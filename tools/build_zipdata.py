"""Build the bundled US ZIP code dataset for ZipPaster.

Downloads the GeoNames US postal code export, normalizes it, dedupes to one row
per ZIP, and writes a gzipped CSV that ships inside the app as an embedded
resource.

Source: https://download.geonames.org/export/zip/US.zip
License: Creative Commons Attribution 4.0 (CC BY 4.0) -- redistribution is
permitted with attribution, which is why this dataset can be bundled in the
installer. See the About box and installer license page.

Run manually to refresh the data (GeoNames updates roughly daily; a couple of
refreshes a year is plenty for ZIP/city/state):

    python tools/build_zipdata.py

Requires `truststore` (pip install truststore) on networks that inspect TLS --
corporate proxies and some antivirus products substitute their own CA, which
Python's bundled certificate set rejects even though Windows itself trusts it.
truststore makes Python validate against the Windows certificate store instead.
"""

from __future__ import annotations

import csv
import gzip
import io
import re
import ssl
import sys
import urllib.request
import zipfile
from pathlib import Path

GEONAMES_BASE = "https://download.geonames.org/export/zip/"

# GeoNames splits the US postal system across several country files: US.zip
# holds the 50 states + DC only, and each inhabited territory ships separately.
# Puerto Rico alone is ~180 real, form-valid ZIP codes, so all five are merged
# in -- pulling only US.zip silently loses them.
SOURCE_COUNTRIES = ("US", "PR", "VI", "GU", "AS", "MP")

OUT_PATH = (
    Path(__file__).resolve().parent.parent
    / "src"
    / "ZipPaster"
    / "Resources"
    / "us_zipcodes.csv.gz"
)

# GeoNames postal code column indexes (tab-delimited, no header).
COL_COUNTRY = 0
COL_POSTAL = 1
COL_PLACE = 2
COL_ADMIN_NAME1 = 3  # state name, e.g. "Texas"
COL_ADMIN_CODE1 = 4  # state abbreviation, e.g. "TX"
COL_ADMIN_NAME2 = 5  # county name
COL_LAT = 9
COL_LON = 10

# 50 states + DC + the five inhabited territories. Anything outside this set is
# dropped, which as of the 2026-08 export removes exactly two groups:
#   - 511 rows with a blank state code: overseas military APO/FPO/DPO ZIPs whose
#     "city" is a literal like "APO AA". Not valid input for a US web form.
#   - 2 rows coded MH (Marshall Islands), a freely associated state rather than
#     a US territory.
# Both are intentional exclusions, not data errors.
VALID_STATES = {
    "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
    "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
    "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
    "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
    "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
    "DC", "PR", "VI", "GU", "AS", "MP",
}

# Territory rows sometimes arrive with an empty admin_name1; fill it in so the
# "paste full state name" setting always has something to paste.
STATE_NAME_FALLBACK = {
    "DC": "District of Columbia",
    "PR": "Puerto Rico",
    "VI": "Virgin Islands",
    "GU": "Guam",
    "AS": "American Samoa",
    "MP": "Northern Mariana Islands",
}

ZIP_RE = re.compile(r"^\d{5}$")

# A truncated download would otherwise ship silently. The US export has held
# around 41k records for years; anything far outside that is a broken build.
MIN_EXPECTED = 39_000
MAX_EXPECTED = 43_000

# GeoNames place names are already title-cased and largely correct: only ~117 of
# ~18,600 distinct names need any change. So this normalizer is deliberately
# CONSERVATIVE -- it fixes the few demonstrably-wrong patterns and otherwise
# leaves the source string untouched. An aggressive title-caser does more damage
# than good here, mangling "North Las Vegas" -> "North las Vegas",
# "Bayou La Batre" -> "Bayou la Batre", and "Coeur d'Alene" -> "Coeur D'Alene".

# Lowercased mid-name. Only genuine English/Romance prepositions that US place
# names really do lowercase ("Marina del Rey", "City of Industry", "Point of
# Rocks"). Notably absent: la/las/los/y/en -- in US names these are part of the
# proper noun ("Las Vegas", "La Jolla") and must stay capitalized.
LOWER_WORDS = {"of", "the", "de", "del", "and", "on", "upon", "at", "by", "in"}

# Always fully capitalized.
UPPER_WORDS = {
    "NE", "NW", "SE", "SW", "US", "APO", "FPO", "DPO", "AFB", "JBSA",
    "AE", "AA", "AP", "MCBH",  # MCBH = Marine Corps Base Hawaii; must precede the Mc- rule
}

# "Mcallen" -> "McAllen". GeoNames flattens the intercap on Mc-names.
MC_RE = re.compile(r"^Mc([a-z])(.*)$")


def fix_word(word: str, index: int) -> str:
    upper = word.upper()
    if upper in UPPER_WORDS:
        return upper

    if index > 0 and word.lower() in LOWER_WORDS:
        return word.lower()

    mc = MC_RE.match(word)
    if mc:
        return "Mc" + mc.group(1).upper() + mc.group(2)

    # Recover from an all-caps or all-lowercase source word; anything already
    # mixed-case is trusted as-is so "d'Alene" and "DeWitt" survive intact.
    if len(word) > 1 and (word.isupper() or word.islower()) and word.isalpha():
        return word.capitalize()

    return word


def title_case_city(name: str) -> str:
    """Normalize a place name, trusting GeoNames casing except where it's wrong."""
    name = " ".join(name.split())
    if not name:
        return name
    return " ".join(fix_word(w, i) for i, w in enumerate(name.split(" ")))


def make_ssl_context() -> ssl.SSLContext:
    """Prefer the Windows certificate store so TLS-inspecting proxies work."""
    try:
        import truststore
    except ImportError:
        return ssl.create_default_context()
    return truststore.SSLContext(ssl.PROTOCOL_TLS_CLIENT)


def download(country: str) -> bytes:
    url = f"{GEONAMES_BASE}{country}.zip"
    print(f"Downloading {url} ...")
    req = urllib.request.Request(
        url, headers={"User-Agent": "ZipPaster-build-script/1.0"}
    )
    try:
        with urllib.request.urlopen(req, timeout=120, context=make_ssl_context()) as resp:
            data = resp.read()
    except urllib.error.URLError as exc:
        if isinstance(exc.reason, ssl.SSLCertVerificationError):
            raise SystemExit(
                "TLS certificate verification failed. This network appears to "
                "inspect HTTPS traffic.\nInstall truststore so Python uses the "
                "Windows certificate store:\n\n    pip install truststore\n"
            ) from exc
        raise

    print(f"  received {len(data) / 1024:.0f} KB")
    return data


def extract_rows(archive: bytes, country: str):
    with zipfile.ZipFile(io.BytesIO(archive)) as zf:
        with zf.open(f"{country}.txt") as fh:
            text = io.TextIOWrapper(fh, encoding="utf-8", newline="")
            for line in text:
                line = line.rstrip("\n").rstrip("\r")
                if line:
                    yield line.split("\t")


def build() -> int:
    seen: dict[str, list[str]] = {}
    total = 0
    skipped_bad_zip = 0
    skipped_bad_state = 0
    duplicates = 0

    for country in SOURCE_COUNTRIES:
        archive = download(country)
        kept_before = len(seen)

        for cols in extract_rows(archive, country):
            total += 1
            if len(cols) <= COL_LON:
                continue

            zip_code = cols[COL_POSTAL].strip()
            if not ZIP_RE.match(zip_code):
                skipped_bad_zip += 1
                continue

            if country == "US":
                # In US.txt the admin1 columns carry the state: code "TX",
                # name "Texas", and admin2 carries the county.
                state_code = cols[COL_ADMIN_CODE1].strip().upper()
                state_name = cols[COL_ADMIN_NAME1].strip()
                county = cols[COL_ADMIN_NAME2].strip()
            else:
                # Territory files are shaped differently: the territory itself is
                # the country, so admin_code1 holds a numeric municipality code
                # ("001"), not a state abbreviation. The state comes from the
                # country code, and the county equivalent is whichever admin name
                # is meaningful -- admin2 for VI/GU ("St. Croix", "Guam"),
                # admin1 for PR/MP municipalities ("Adjuntas", "Rota"). AS/VI/GU
                # echo the country code back in admin1 ("Vi", "Gu"), which is
                # noise and gets dropped.
                state_code = country
                state_name = STATE_NAME_FALLBACK.get(country, country)
                county = cols[COL_ADMIN_NAME2].strip()
                if not county:
                    admin1 = cols[COL_ADMIN_NAME1].strip()
                    county = "" if admin1.upper() == country else admin1

            if state_code not in VALID_STATES:
                skipped_bad_state += 1
                continue

            # One row per ZIP, first occurrence wins. The US export happens to
            # already be unique per ZIP (this collapses 0 rows today), unlike some
            # other GeoNames country files. Kept as a guard so a future export that
            # splits a ZIP across place names still yields one canonical city.
            if zip_code in seen:
                duplicates += 1
                continue

            city = title_case_city(cols[COL_PLACE].strip())
            if not city:
                continue

            if not state_name:
                state_name = STATE_NAME_FALLBACK.get(state_code, state_code)

            seen[zip_code] = [
                zip_code,
                city,
                state_code,
                state_name,
                county,
                cols[COL_LAT].strip(),
                cols[COL_LON].strip(),
            ]

        print(f"  {country}: +{len(seen) - kept_before} ZIPs")

    count = len(seen)
    print(f"\n  parsed {total} raw rows across {len(SOURCE_COUNTRIES)} files")
    print(f"  skipped {skipped_bad_zip} non-5-digit, {skipped_bad_state} invalid-state")
    print(f"  collapsed {duplicates} duplicate place rows")
    print(f"  kept {count} unique ZIP codes")

    if not (MIN_EXPECTED <= count <= MAX_EXPECTED):
        print(
            f"ERROR: record count {count} outside expected range "
            f"{MIN_EXPECTED}-{MAX_EXPECTED}. Refusing to write a suspect dataset.",
            file=sys.stderr,
        )
        return 1

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    # mtime=0 keeps the output byte-identical between runs on unchanged input,
    # so rebuilding does not churn the repo.
    with gzip.GzipFile(OUT_PATH, "wb", compresslevel=9, mtime=0) as gz:
        with io.TextIOWrapper(gz, encoding="utf-8", newline="") as text:
            writer = csv.writer(text, lineterminator="\n")
            writer.writerow(
                ["zip", "city", "state_code", "state_name", "county", "lat", "lon"]
            )
            for zip_code in sorted(seen):
                writer.writerow(seen[zip_code])

    size_kb = OUT_PATH.stat().st_size / 1024
    print(f"Wrote {OUT_PATH} ({size_kb:.1f} KB)")

    # Spot-check a few well-known ZIPs so a silently mis-parsed file is obvious.
    for probe in ("00501", "00926", "10001", "78701", "90210", "96910", "96950", "99950"):
        row = seen.get(probe)
        print(f"  {probe} -> {row[1]}, {row[2]}" if row else f"  {probe} -> MISSING")

    return 0


if __name__ == "__main__":
    sys.exit(build())
