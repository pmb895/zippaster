# ZipPaster

A Windows tray app that pastes US ZIP codes (and their city and state) into any
web form via a global hotkey, and tracks which ZIPs you have already used on
each website.

Click into a form field in any browser, press `Ctrl+Alt+Z`, and the next unused
ZIP appears. It marks itself used and advances to the next one.

## How it works

| Hotkey | Pastes | Advances? |
| --- | --- | --- |
| `Ctrl+Alt+Z` | ZIP code (`78701`) | Yes — marks used, moves to the next unused ZIP |
| `Ctrl+Alt+C` | City of that ZIP (`Austin`) | No |
| `Ctrl+Alt+S` | State (`TX` or `Texas`) | No |

All three read the **same currently selected row**, so you control which field
gets what. All are rebindable under *Tools → Settings*.

Because it works by simulating a paste into whatever window has focus, it works
on any website in any browser — and in Excel, Notepad, or anything else with a
text field. There is no browser extension and no per-site setup.

## Features

- **41,195 US ZIP codes** bundled in the app — all 50 states, DC, and the five
  inhabited territories. No internet connection required, ever.
- **Projects** — one per website. Each tracks its own used-ZIP set, so the same
  ZIP can be used on many sites independently.
- **Filtering** — by state, by city, by ZIP prefix, or free-text city search.
  "Hide used" narrows to what is left. Sorting by clicking any column header.
- **Undo** the last paste, toggle any ZIP by hand, reset or export a project.
- Clipboard contents are saved and restored around every paste.

## Requirements

Windows 10 or 11, 64-bit. Nothing else — the installer bundles the .NET runtime.

**Do not run ZipPaster as administrator.** Windows blocks a program running as
administrator from sending keystrokes to a normally-launched browser (a security
feature called UIPI), so an elevated ZipPaster silently fails to paste.

## Installing

Run `ZipPaster-Setup-1.0.0.exe` and follow the prompts. It installs per-user and
needs no administrator rights.

### Expect a security warning on first run

The app is **not code-signed**, so Windows SmartScreen shows *"Windows protected
your PC"*. Click **More info → Run anyway**.

Some antivirus products also flag it. This is a heuristic false positive:
ZipPaster works by simulating keyboard input, which is the same technique
keyloggers use, so scanners are suspicious of it. Clearing both warnings properly
requires a code signing certificate (roughly $200/year).

## Where your data lives

    %LOCALAPPDATA%\ZipPaster\data.json

Projects and used-ZIP history are stored here as plain JSON. Uninstalling
deliberately leaves this file in place so an upgrade or reinstall keeps your
progress. Delete the folder by hand to start completely fresh.

## Building from source

Requires the .NET 10 SDK, Python 3 (for the data step only), and Inno Setup 6
(for the installer only).

```powershell
# 1. Build the ZIP dataset (only needed to refresh the data)
pip install truststore          # only on networks that inspect TLS
python tools\build_zipdata.py

# 2. Build and package
.\installer\build.ps1
```

`build.ps1` publishes a self-contained single-file exe, runs the self-test as a
release gate, and produces `installer\output\ZipPaster-Setup-<version>.exe`.
Without Inno Setup installed it stops after the portable exe and says so.

### Refreshing the ZIP data

`tools/build_zipdata.py` downloads the current GeoNames postal code export,
normalizes it, and writes `src/ZipPaster/Resources/us_zipcodes.csv.gz`, which is
embedded in the executable. GeoNames updates roughly daily; ZIP/city/state
assignments change slowly, so refreshing once or twice a year is plenty.

The script merges six GeoNames files: `US` (which covers only the 50 states and
DC) plus `PR`, `VI`, `GU`, `AS` and `MP` for the territories, which are published
separately and would otherwise be missing.

### Self-test

```powershell
ZipPaster.exe --selftest C:\path\to\report.txt
```

Verifies the dataset, filtering, persistence, and — most importantly — that a
paste actually reaches a **separate process**, that the clipboard is restored
afterwards, and that the type-characters fallback works. It pastes into a
throwaway window the app launches itself, never into your own applications.

Useful for diagnosing "it does not paste" on someone else's machine.

## Data attribution

ZIP code data from [GeoNames](https://www.geonames.org/), used under the
[Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/)
licence, which permits redistribution inside this installer.

Note that GeoNames stores place names without punctuation — `O'Fallon` is held as
`O Fallon`, `Wilkes-Barre` as `Wilkes Barre`, and `St. Louis` as `Saint Louis`.
The search box is punctuation-insensitive to compensate, so typing the name the
way it is normally spelled still finds it.
