using ZipPaster.Data;
using ZipPaster.Interop;

namespace ZipPaster.UI;

public sealed class MainForm : Form
{
    private readonly ZipCatalog _catalog;
    private readonly AppState _state;
    private readonly HotkeyManager _hotkeys = new();

    private readonly ComboBox _projectCombo = new();
    private readonly Label _progressLabel = new();
    private readonly Button _undoButton = new();

    private readonly TextBox _searchBox = new();
    private readonly ComboBox _stateCombo = new();
    private readonly ComboBox _cityCombo = new();
    private readonly CheckBox _hideUsedCheck = new();

    private readonly DataGridView _grid = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _hintLabel = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 200 };

    private List<ZipRow> _view = [];
    private bool _suppressFilterEvents;
    private bool _reallyClosing;

    public MainForm(ZipCatalog catalog, AppState state)
    {
        _catalog = catalog;
        _state = state;

        BuildUi();
        WireEvents();

        LoadProjects();
        LoadStates();
        RestoreFilterFromSettings();
        ApplyFilter(preserveSelection: false);
        ApplyHotkeys();
        UpdateProgress();
    }

    private Project ActiveProject => _state.ActiveProject ?? _state.Projects[0];

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        Text = "ZipPaster";
        MinimumSize = new Size(760, 480);
        Size = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Get();

        // ---- menu -------------------------------------------------------
        var menu = new MenuStrip();

        var projectMenu = new ToolStripMenuItem("&Project");
        projectMenu.DropDownItems.Add("&New Project...", null, (_, _) => NewProject());
        projectMenu.DropDownItems.Add("&Rename...", null, (_, _) => RenameProject());
        projectMenu.DropDownItems.Add("&Delete...", null, (_, _) => DeleteProject());
        projectMenu.DropDownItems.Add(new ToolStripSeparator());
        projectMenu.DropDownItems.Add("&Reset Used ZIPs...", null, (_, _) => ResetProject());
        projectMenu.DropDownItems.Add("&Export Used ZIPs...", null, (_, _) => ExportUsed());
        projectMenu.DropDownItems.Add(new ToolStripSeparator());
        projectMenu.DropDownItems.Add("E&xit", null, (_, _) => { _reallyClosing = true; Close(); });

        var toolsMenu = new ToolStripMenuItem("&Tools");
        toolsMenu.DropDownItems.Add("&Settings...", null, (_, _) => OpenSettings());

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("&About ZipPaster", null, (_, _) => ShowAbout());

        menu.Items.AddRange([projectMenu, toolsMenu, helpMenu]);

        // ---- project bar ------------------------------------------------
        var projectBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 4) };

        var projectLabel = new Label
        {
            Text = "Project:",
            AutoSize = true,
            Location = new Point(8, 13),
        };

        _projectCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _projectCombo.Location = new Point(64, 9);
        _projectCombo.Width = 240;

        var newButton = new Button { Text = "New", Location = new Point(312, 8), Width = 60 };
        newButton.Click += (_, _) => NewProject();

        _undoButton.Text = "Undo Last";
        _undoButton.Location = new Point(378, 8);
        _undoButton.Width = 90;
        _undoButton.Click += (_, _) => UndoLast();

        _progressLabel.AutoSize = true;
        _progressLabel.Location = new Point(482, 13);
        _progressLabel.ForeColor = SystemColors.GrayText;

        projectBar.Controls.AddRange([projectLabel, _projectCombo, newButton, _undoButton, _progressLabel]);

        // ---- filter bar -------------------------------------------------
        var filterBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 4, 8, 8) };

        var searchLabel = new Label { Text = "Search:", AutoSize = true, Location = new Point(8, 13) };

        _searchBox.Location = new Point(64, 9);
        _searchBox.Width = 180;
        _searchBox.PlaceholderText = "ZIP or city";

        var stateLabel = new Label { Text = "State:", AutoSize = true, Location = new Point(256, 13) };

        _stateCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _stateCombo.Location = new Point(300, 9);
        _stateCombo.Width = 190;

        var cityLabel = new Label { Text = "City:", AutoSize = true, Location = new Point(500, 13) };

        _cityCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _cityCombo.Location = new Point(538, 9);
        _cityCombo.Width = 170;

        _hideUsedCheck.Text = "Hide used";
        _hideUsedCheck.AutoSize = true;
        _hideUsedCheck.Location = new Point(722, 12);

        var clearButton = new Button { Text = "Clear", Location = new Point(812, 8), Width = 60 };
        clearButton.Click += (_, _) => ClearFilters();

        filterBar.Controls.AddRange(
            [searchLabel, _searchBox, stateLabel, _stateCombo, cityLabel, _cityCombo, _hideUsedCheck, clearButton]);

        // ---- grid -------------------------------------------------------
        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn
            {
                Name = "used", HeaderText = "", FillWeight = 6,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                SortMode = DataGridViewColumnSortMode.NotSortable,
            },
            new DataGridViewTextBoxColumn { Name = "zip", HeaderText = "ZIP", FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "city", HeaderText = "City", FillWeight = 34 },
            new DataGridViewTextBoxColumn { Name = "state", HeaderText = "State", FillWeight = 24 },
            new DataGridViewTextBoxColumn { Name = "county", HeaderText = "County", FillWeight = 22 },
        ]);

        foreach (DataGridViewColumn column in _grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Toggle used/unused", null, (_, _) => ToggleUsedOnSelection());
        contextMenu.Items.Add("Copy ZIP", null, (_, _) => CopySelection(r => r.Zip));
        contextMenu.Items.Add("Copy City", null, (_, _) => CopySelection(r => r.City));
        _grid.ContextMenuStrip = contextMenu;

        // ---- status bar -------------------------------------------------
        var status = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _hintLabel.ForeColor = SystemColors.GrayText;
        status.Items.AddRange([_statusLabel, _hintLabel]);

        // ---- tray -------------------------------------------------------
        _tray.Icon = AppIcon.Get();
        _tray.Text = "ZipPaster";
        _tray.Visible = true;

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show ZipPaster", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => { _reallyClosing = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;

        // Order matters: docked controls fill in reverse order of addition.
        Controls.Add(_grid);
        Controls.Add(filterBar);
        Controls.Add(projectBar);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void WireEvents()
    {
        _projectCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressFilterEvents || _projectCombo.SelectedItem is not ProjectItem item)
                return;

            _state.Settings.ActiveProjectId = item.Project.Id;
            _state.Save();
            ApplyFilter(preserveSelection: false);
            UpdateProgress();
        };

        // Typing re-filters 41k rows; debounce so each keystroke is not a full pass.
        _searchBox.TextChanged += (_, _) =>
        {
            if (_suppressFilterEvents) return;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter(preserveSelection: false);
        };

        _stateCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressFilterEvents) return;
            LoadCities();
            ApplyFilter(preserveSelection: false);
        };

        _cityCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressFilterEvents) return;
            ApplyFilter(preserveSelection: false);
        };

        _hideUsedCheck.CheckedChanged += (_, _) =>
        {
            if (_suppressFilterEvents) return;
            ApplyFilter(preserveSelection: false);
        };

        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellFormatting += OnCellFormatting;
        _grid.SelectionChanged += (_, _) => UpdateStatus();
        _grid.ColumnHeaderMouseClick += OnColumnHeaderClick;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) ToggleUsedOnSelection();
        };

        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _state.Settings.MinimizeToTray)
                Hide();
        };

        FormClosing += OnFormClosing;
    }

    // -------------------------------------------------------------- data

    private sealed record ProjectItem(Project Project)
    {
        public override string ToString() => Project.Name;
    }

    private void LoadProjects()
    {
        _suppressFilterEvents = true;
        try
        {
            _projectCombo.Items.Clear();
            foreach (var project in _state.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                _projectCombo.Items.Add(new ProjectItem(project));

            string activeId = ActiveProject.Id;
            for (int i = 0; i < _projectCombo.Items.Count; i++)
            {
                if (((ProjectItem)_projectCombo.Items[i]!).Project.Id == activeId)
                {
                    _projectCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void LoadStates()
    {
        _suppressFilterEvents = true;
        try
        {
            _stateCombo.Items.Clear();
            _stateCombo.Items.Add("All states");
            foreach (var s in _catalog.States)
                _stateCombo.Items.Add(s);
            _stateCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void LoadCities()
    {
        _suppressFilterEvents = true;
        try
        {
            object? previous = _cityCombo.SelectedIndex > 0 ? _cityCombo.SelectedItem : null;

            _cityCombo.Items.Clear();
            _cityCombo.Items.Add("All cities");

            string stateCode = SelectedStateCode();
            if (!string.IsNullOrEmpty(stateCode))
            {
                var cities = _catalog.Rows
                    .Where(r => r.StateCode == stateCode)
                    .Select(r => r.City)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

                foreach (string city in cities)
                    _cityCombo.Items.Add(city);
            }

            // City list is only meaningful within one state; 18k nationwide
            // entries would be unusable in a dropdown.
            _cityCombo.Enabled = !string.IsNullOrEmpty(stateCode);

            int index = previous is null ? 0 : Math.Max(0, _cityCombo.Items.IndexOf(previous));
            _cityCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private string SelectedStateCode() =>
        _stateCombo.SelectedItem is ZipCatalog.StateInfo info ? info.Code : "";

    private string SelectedCity() =>
        _cityCombo.SelectedIndex > 0 ? (string)_cityCombo.SelectedItem! : "";

    private void RestoreFilterFromSettings()
    {
        _suppressFilterEvents = true;
        try
        {
            _hideUsedCheck.Checked = _state.Settings.HideUsed;

            string saved = _state.Settings.LastStateFilter;
            if (!string.IsNullOrEmpty(saved))
            {
                for (int i = 1; i < _stateCombo.Items.Count; i++)
                {
                    if (((ZipCatalog.StateInfo)_stateCombo.Items[i]!).Code == saved)
                    {
                        _stateCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        LoadCities();
    }

    private void ApplyFilter(bool preserveSelection)
    {
        string? keepZip = preserveSelection ? CurrentRow()?.Zip : null;

        var filter = new ZipFilter(
            Search: _searchBox.Text,
            StateCode: SelectedStateCode(),
            City: SelectedCity(),
            HideUsed: _hideUsedCheck.Checked,
            Sort: _state.Settings.Sort,
            Descending: _state.Settings.SortDescending);

        _view = filter.Apply(_catalog.Rows, ActiveProject.UsedZips);

        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _grid.Invalidate();

        if (_view.Count > 0)
        {
            int index = 0;
            if (keepZip is not null)
            {
                int found = _view.FindIndex(r => r.Zip == keepZip);
                if (found >= 0) index = found;
            }

            SetCurrentIndex(index);
        }

        UpdateStatus();
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if ((uint)e.RowIndex >= (uint)_view.Count)
            return;

        ZipRow row = _view[e.RowIndex];

        e.Value = _grid.Columns[e.ColumnIndex].Name switch
        {
            "used" => ActiveProject.UsedZips.Contains(row.Zip) ? "✓" : "",
            "zip" => row.Zip,
            "city" => row.City,
            "state" => $"{row.StateName} ({row.StateCode})",
            "county" => row.County,
            _ => "",
        };
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if ((uint)e.RowIndex >= (uint)_view.Count)
            return;

        // Used rows stay visible but recede, so progress is obvious at a glance.
        if (ActiveProject.UsedZips.Contains(_view[e.RowIndex].Zip))
        {
            e.CellStyle!.ForeColor = SystemColors.GrayText;
            e.CellStyle.BackColor = Color.FromArgb(246, 246, 246);
        }
    }

    private void OnColumnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        ZipSort? sort = _grid.Columns[e.ColumnIndex].Name switch
        {
            "zip" => ZipSort.Zip,
            "city" => ZipSort.City,
            "state" => ZipSort.State,
            _ => null,
        };

        if (sort is null)
            return;

        if (_state.Settings.Sort == sort)
            _state.Settings.SortDescending = !_state.Settings.SortDescending;
        else
        {
            _state.Settings.Sort = sort.Value;
            _state.Settings.SortDescending = false;
        }

        ApplyFilter(preserveSelection: true);
    }

    // ----------------------------------------------------------- hotkeys

    private void ApplyHotkeys()
    {
        var bindings = new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.Zip] = Hotkey.Parse(_state.Settings.ZipHotkey),
            [HotkeyAction.City] = Hotkey.Parse(_state.Settings.CityHotkey),
            [HotkeyAction.State] = Hotkey.Parse(_state.Settings.StateHotkey),
        };

        var failed = _hotkeys.Register(bindings);

        _hintLabel.Text =
            $"{_state.Settings.ZipHotkey} ZIP  |  {_state.Settings.CityHotkey} City  |  {_state.Settings.StateHotkey} State";

        if (failed.Count > 0)
        {
            string names = string.Join(", ", failed);
            MessageBox.Show(
                $"These hotkeys are already in use by another program and could not be registered: {names}."
                + $"{Environment.NewLine}{Environment.NewLine}Pick different ones under Tools > Settings.",
                "ZipPaster",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        ZipRow? row = CurrentRow();
        if (row is null)
        {
            _tray.ShowBalloonTip(2000, "ZipPaster", "No ZIP selected.", ToolTipIcon.Warning);
            return;
        }

        string value = e.Action switch
        {
            HotkeyAction.Zip => row.Zip,
            HotkeyAction.City => row.City,
            HotkeyAction.State => _state.Settings.StateAsAbbreviation ? row.StateCode : row.StateName,
            _ => row.Zip,
        };

        try
        {
            await Paster.SendAsync(value, _state.Settings.PasteMode);
        }
        catch (PasteBlockedException ex)
        {
            _tray.ShowBalloonTip(5000, "ZipPaster - paste blocked", ex.Message, ToolTipIcon.Error);
            return;
        }

        // Only a ZIP paste consumes a ZIP; city and state are supplementary
        // fields on the same form and must not advance or mark anything.
        if (e.Action == HotkeyAction.Zip && _state.Settings.AutoAdvance)
        {
            _state.MarkUsed(ActiveProject, row.Zip);
            _state.Save();
            AdvanceToNextUnused();
            UpdateProgress();
            _grid.Invalidate();
        }

        if (_state.Settings.NotifyOnPaste)
            _tray.ShowBalloonTip(1000, "ZipPaster", $"Pasted {value}", ToolTipIcon.Info);

        UpdateStatus();
    }

    /// <summary>
    /// Moves to the next unused ZIP in the current view, wrapping to the start
    /// so a mid-list selection still finds the remaining ZIPs.
    /// </summary>
    private void AdvanceToNextUnused()
    {
        if (_view.Count == 0)
            return;

        // "Hide used" removes rows underneath us, so the current index already
        // points at the next candidate once the view is rebuilt.
        if (_hideUsedCheck.Checked)
        {
            int keep = CurrentIndex();
            ApplyFilter(preserveSelection: false);
            if (_view.Count > 0)
                SetCurrentIndex(Math.Min(keep, _view.Count - 1));
            return;
        }

        int start = CurrentIndex();
        var used = ActiveProject.UsedZips;

        for (int offset = 1; offset <= _view.Count; offset++)
        {
            int index = (start + offset) % _view.Count;
            if (!used.Contains(_view[index].Zip))
            {
                SetCurrentIndex(index);
                return;
            }
        }

        _tray.ShowBalloonTip(3000, "ZipPaster",
            "Every ZIP in the current view has been used for this project.", ToolTipIcon.Info);
    }

    private int CurrentIndex() =>
        _grid.CurrentCell?.RowIndex is int i && i >= 0 && i < _view.Count ? i : 0;

    private ZipRow? CurrentRow() =>
        _view.Count == 0 ? null : _view[CurrentIndex()];

    private void SetCurrentIndex(int index)
    {
        if (_view.Count == 0)
            return;

        index = Math.Clamp(index, 0, _view.Count - 1);

        // Assigning CurrentCell does not activate the window, so the browser
        // keeps focus while we walk the list.
        _grid.CurrentCell = _grid.Rows[index].Cells[1];

        int firstVisible = _grid.FirstDisplayedScrollingRowIndex;
        int visibleCount = _grid.DisplayedRowCount(false);

        if (index < firstVisible || index >= firstVisible + visibleCount)
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, index - visibleCount / 2);
    }

    // ------------------------------------------------------------ actions

    private void UndoLast()
    {
        string? zip = _state.UndoLast(ActiveProject);
        if (zip is null)
        {
            _statusLabel.Text = "Nothing to undo.";
            return;
        }

        _state.Save();
        ApplyFilter(preserveSelection: false);

        int index = _view.FindIndex(r => r.Zip == zip);
        if (index >= 0)
            SetCurrentIndex(index);

        UpdateProgress();
        _grid.Invalidate();
        _statusLabel.Text = $"Undid {zip} - it is available again.";
    }

    private void ToggleUsedOnSelection()
    {
        ZipRow? row = CurrentRow();
        if (row is null)
            return;

        var project = ActiveProject;

        if (project.UsedZips.Remove(row.Zip))
        {
            project.History.RemoveAll(z => z == row.Zip);
        }
        else
        {
            _state.MarkUsed(project, row.Zip);
        }

        _state.Save();

        if (_hideUsedCheck.Checked)
            ApplyFilter(preserveSelection: true);
        else
            _grid.Invalidate();

        UpdateProgress();
    }

    private void CopySelection(Func<ZipRow, string> selector)
    {
        if (CurrentRow() is { } row)
            Clipboard.SetText(selector(row));
    }

    private void NewProject()
    {
        string? name = Prompt.Show(this, "New project", "Name this project (for example, the website you are filling in):", "");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var project = AppState.NewProject(name.Trim());
        _state.Projects.Add(project);
        _state.Settings.ActiveProjectId = project.Id;
        _state.Save();

        LoadProjects();
        ApplyFilter(preserveSelection: false);
        UpdateProgress();
    }

    private void RenameProject()
    {
        var project = ActiveProject;
        string? name = Prompt.Show(this, "Rename project", "New name:", project.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        project.Name = name.Trim();
        _state.Save();
        LoadProjects();
    }

    private void DeleteProject()
    {
        if (_state.Projects.Count == 1)
        {
            MessageBox.Show("You need at least one project.", "ZipPaster",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var project = ActiveProject;
        var answer = MessageBox.Show(
            $"Delete \"{project.Name}\" and its {project.UsedZips.Count:N0} used-ZIP marks?"
            + $"{Environment.NewLine}{Environment.NewLine}This cannot be undone.",
            "ZipPaster",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
            return;

        _state.Projects.Remove(project);
        _state.Settings.ActiveProjectId = _state.Projects[0].Id;
        _state.Save();

        LoadProjects();
        ApplyFilter(preserveSelection: false);
        UpdateProgress();
    }

    private void ResetProject()
    {
        var project = ActiveProject;
        if (project.UsedZips.Count == 0)
            return;

        var answer = MessageBox.Show(
            $"Clear all {project.UsedZips.Count:N0} used marks in \"{project.Name}\"?",
            "ZipPaster",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
            return;

        project.UsedZips.Clear();
        project.History.Clear();
        _state.Save();

        ApplyFilter(preserveSelection: false);
        UpdateProgress();
    }

    private void ExportUsed()
    {
        var project = ActiveProject;

        using var dialog = new SaveFileDialog
        {
            Title = "Export used ZIPs",
            Filter = "CSV file (*.csv)|*.csv|Text file (*.txt)|*.txt",
            FileName = $"{SanitizeFileName(project.Name)}-used.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var byZip = _catalog.Rows.ToDictionary(r => r.Zip, StringComparer.Ordinal);
        var lines = new List<string> { "zip,city,state" };

        lines.AddRange(project.UsedZips
            .OrderBy(z => z, StringComparer.Ordinal)
            .Select(zip => byZip.TryGetValue(zip, out var row)
                ? $"{row.Zip},\"{row.City}\",{row.StateCode}"
                : zip));

        File.WriteAllLines(dialog.FileName, lines);
        _statusLabel.Text = $"Exported {project.UsedZips.Count:N0} ZIPs.";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name;
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_state.Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _state.Save();
        ApplyHotkeys();
        ApplyFilter(preserveSelection: true);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "ZipPaster 1.0" + Environment.NewLine + Environment.NewLine
            + $"{_catalog.Rows.Count:N0} US ZIP codes across {_catalog.States.Count} states and territories."
            + Environment.NewLine + Environment.NewLine
            + "ZIP code data from GeoNames (www.geonames.org), used under the"
            + Environment.NewLine
            + "Creative Commons Attribution 4.0 licence."
            + Environment.NewLine + Environment.NewLine
            + $"Your projects are stored in:{Environment.NewLine}{_state.FilePath}",
            "About ZipPaster",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ClearFilters()
    {
        _suppressFilterEvents = true;
        try
        {
            _searchBox.Clear();
            _stateCombo.SelectedIndex = 0;
            _hideUsedCheck.Checked = false;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        LoadCities();
        ApplyFilter(preserveSelection: false);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------- status

    private void UpdateProgress()
    {
        var project = ActiveProject;
        int total = _catalog.Rows.Count;
        int used = project.UsedZips.Count;
        _progressLabel.Text = $"{used:N0} of {total:N0} used  ({(double)used / total:P1})";
        _undoButton.Enabled = project.History.Count > 0;
    }

    private void UpdateStatus()
    {
        ZipRow? row = CurrentRow();

        _statusLabel.Text = row is null
            ? $"No matches  |  {_view.Count:N0} shown"
            : $"Next: {row.Zip}  {row.City}, {row.StateCode}   |   {_view.Count:N0} shown";

        _tray.Text = row is null ? "ZipPaster" : $"ZipPaster - {row.Zip} {row.City}";
    }

    // ------------------------------------------------------------ closing

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // The X button hides to tray; hotkeys must keep working. Only an
        // explicit Exit (or Windows shutting down) really closes.
        if (!_reallyClosing
            && e.CloseReason == CloseReason.UserClosing
            && _state.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _state.Settings.LastStateFilter = SelectedStateCode();
        _state.Settings.HideUsed = _hideUsedCheck.Checked;
        _state.Save();

        _tray.Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeys.Dispose();
            _tray.Dispose();
            _searchDebounce.Dispose();
        }

        base.Dispose(disposing);
    }
}
