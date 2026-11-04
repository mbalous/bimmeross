using System.Globalization;
using System.Text.Json;
using BmwCoder.Coding;
using BmwCoder.Core;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;

namespace BmwCoder.App.Forms;

public sealed class DiagnosticsForm : Form
{
    private readonly CodingSession _session;
    private readonly AppSettings _settings;
    private readonly TextBox _ecu = new() { Text = "0x12", Width = 65, Name = "DiagnosticEcu" };
    private readonly ComboBox _module = new() { Width = 390, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Value", Name = "DiagnosticModule" };
    private readonly ComboBox _did = new() { Width = 390, DropDownStyle = ComboBoxStyle.DropDown, Name = "DiagnosticDid" };
    private readonly TextBox _request = new() { Text = "22 F1 90", Width = 260 };
    private readonly NumericUpDown _timeout = new() { Minimum = 100, Maximum = 30000, Increment = 500, Value = 5000, Width = 80 };
    private readonly TextBox _psdzPath = new() { Width = 390, Name = "DiagnosticPsdzPath" };
    private readonly TextBox _series = new() { Width = 120, Name = "DiagnosticSeries" };
    private readonly TextBox _istaPath = new() { Width = 365, Name = "DiagnosticIstaPath" };
    private readonly Label _target = new() { AutoSize = true, MaximumSize = new Size(400, 0), Name = "DiagnosticTarget" };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        Name = "DiagnosticResults"
    };
    private readonly TextBox _json = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 10),
        Name = "DiagnosticJson"
    };
    private readonly TextBox _cli = new() { Dock = DockStyle.Fill, ReadOnly = true, Name = "DiagnosticCli" };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, Name = "DiagnosticStatus", Text = "Read-only: no session changes, security access, writes, resets, or fault clearing." };
    private readonly Button _cancel = new() { Text = "Cancel read", AutoSize = true, Enabled = false };
    private readonly List<Control> _inputs = new();
    private readonly List<DiagnosticReading> _readings = new();
    private CancellationTokenSource? _operation;
    private bool _closeAfterCancel;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public DiagnosticsForm(CodingSession session, AppSettings settings, byte ecu = 0x12, IReadOnlyList<EcuInfo>? discoveredEcus = null)
    {
        _session = session;
        _settings = settings;
        _ecu.Text = $"0x{ecu:X2}";
        Text = "Read-only ECU Diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1280, 820);
        MinimumSize = new Size(1100, 720);
        Font = new Font("Segoe UI", 9.5f);

        var actions = new TabControl { Dock = DockStyle.Fill, Name = "DiagnosticActions" };
        var quickTab = new TabPage("Quick reads");
        var explorerTab = new TabPage("ECU explorer");
        actions.TabPages.AddRange(new[] { quickTab, explorerTab });
        if (ecu != 0x12) actions.SelectedTab = explorerTab;
        var quick = CreateColumn();
        quickTab.Controls.Add(quick);
        var vehicleSection = CreateSection("Vehicle identity - gateway (0x10)", out var vehicle);
        vehicle.Controls.Add(Explain("Read the car's VIN from its central gateway. No ECU or data-item selection is needed."));
        AddAction(vehicle, "Read vehicle VIN", async token =>
            AddReading(await _session.ReadDiagnosticDidAsync(CodingSession.GatewayAddress, CodingSession.VinDid, token)), "ReadVehicleVin");
        quick.Controls.Add(vehicleSection);

        var dmeSection = CreateSection("Engine electronics - DME (0x12)", out var dme);
        dme.Controls.Add(Explain("DME is the engine control module. This report always reads DME at address 0x12, regardless of selections in ECU explorer."));
        dme.Controls.Add(Explain("Includes VIN, serial number, manufacturing date, operating time, programming counters, calibration, software inventory, session state and faults (11 reads)."));
        AddAction(dme, "Read DME report", async token =>
        {
            await _session.ReadDmeReportAsync(new Progress<DiagnosticReading>(AddReading), token);
        }, "ReadDmeReport");
        quick.Controls.Add(dmeSection);
        quick.Controls.Add(Explain("Results appear on the right. Reads do not clear faults or change vehicle settings."));

        var explorer = CreateColumn();
        explorerTab.Controls.Add(explorer);
        var moduleSection = CreateSection("1. Choose a control module (ECU)", out var module);
        module.Controls.Add(Explain("An ECU is a vehicle control module. Discovery uses the gateway's installed-address list. ODX names are candidates for an address, not confirmed variants."));
        module.Controls.Add(_module);
        var addressRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        addressRow.Controls.Add(new Label { Text = "Diagnostic address (hex):", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        addressRow.Controls.Add(_ecu);
        module.Controls.Add(addressRow);
        module.Controls.Add(_target);
        explorer.Controls.Add(moduleSection);
        var choices = discoveredEcus is { Count: > 0 }
            ? discoveredEcus.Select(e => (e.Address, e.Name))
            : EcuCatalog.Entries.Select(e => (e.Address, e.Name));
        foreach (var entry in choices)
            _module.Items.Add(new KeyValuePair<byte?, string>(entry.Address, $"{entry.Name} (0x{entry.Address:X2})"));
        _module.Items.Add(new KeyValuePair<byte?, string>(null, "Other module - enter its hex address"));
        _module.SelectedIndexChanged += (_, _) =>
        {
            var choice = (KeyValuePair<byte?, string>)_module.SelectedItem!;
            _ecu.ReadOnly = choice.Key.HasValue;
            if (choice.Key is byte address)
                _ecu.Text = $"0x{address:X2}";
            UpdateTarget();
            RefreshDids();
        };
        _ecu.TextChanged += (_, _) => { UpdateTarget(); RefreshDids(); };
        int selectedModule = _module.Items.Cast<KeyValuePair<byte?, string>>().ToList().FindIndex(e => e.Key == ecu);
        _module.SelectedIndex = selectedModule >= 0 ? selectedModule : _module.Items.Count - 1;

        var readSection = CreateSection("2. Read from the selected module", out var selected);
        selected.Controls.Add(Explain("Only the actions in this section use the module chosen above. Quick reads have their own fixed targets."));
        var readTabs = new TabControl { Width = 390, Height = 280, Name = "DiagnosticReadTabs" };
        var itemTab = new TabPage("Data item");
        var checksTab = new TabPage("Checks");
        var requestTab = new TabPage("Raw request");
        var definitionsTab = new TabPage("ODX data");
        var faultTextTab = new TabPage("ISTA texts");
        readTabs.TabPages.AddRange(new[] { itemTab, checksTab, requestTab, definitionsTab, faultTextTab });
        selected.Controls.Add(readTabs);
        explorer.Controls.Add(readSection);

        var items = CreateColumn();
        itemTab.Controls.Add(items);
        items.Controls.Add(Explain("Data item (DID): choose a named item, or enter its hex identifier. The button below reads this item from the selected module."));
        _did.Width = 365;
        items.Controls.Add(_did);
        AddAction(items, "Read selected data item", async token =>
            AddReading(await _session.ReadDiagnosticDidAsync(ParseEcu(), ParseDid(), token)), "ReadSelectedDid");
        items.Controls.Add(Explain("With ODX loaded, names/layouts come from the selected module's candidate definitions. Without definitions, DME-oriented presets are suggestions only; support is not guaranteed."));
        var checks = CreateColumn();
        checksTab.Controls.Add(checks);
        checks.Controls.Add(Explain("These checks use only the selected module. No data-item selection is required."));
        checks.Controls.Add(Explain("Read faults retrieves failure/pending/confirmed/history records (status mask AF). Tests that are merely incomplete are not faults. Nothing is cleared."));
        AddAction(checks, "Read module faults", async token =>
            AddReading(await _session.ReadFaultMemoryAsync(ParseEcu(), token)), "ReadModuleFaults");
        checks.Controls.Add(Explain("Check responds sends a TesterPresent request to see whether this module answers."));
        AddAction(checks, "Check module responds", async token =>
            AddReading(await _session.ProbeDiagnosticEcuAsync(ParseEcu(), token)), "ProbeModule");

        var raw = CreateColumn();
        requestTab.Controls.Add(raw);
        raw.Controls.Add(Explain("Advanced: send the hex request below to the selected module. This does not use the data-item dropdown."));
        raw.Controls.Add(_request);
        AddAction(raw, "Send to selected module", async token =>
        {
            string hex = string.Concat(_request.Text.Where(c => !char.IsWhiteSpace(c)));
            AddReading(await _session.ReadOnlyRequestAsync(ParseEcu(), Convert.FromHexString(hex), token));
        }, "SendReadOnlyRequest");
        raw.Controls.Add(Explain("Only read requests are allowed: 22 <DID>, 19 02 <status mask>, or 3E 00. AF selects failures/history; FF also includes test-status-only records. Writes are blocked."));

        var metadata = CreateColumn();
        definitionsTab.Controls.Add(metadata);
        metadata.Controls.Add(Explain("Offline data for all reads: loads ECU name/address candidates and ODX decoding layouts. It does not contact the car or enable writes."));
        metadata.Controls.Add(Explain("PSdZData root, dataset version, or odx\\src folder:"));
        _psdzPath.Width = 365;
        _psdzPath.Text = settings.PsdzDataPath;
        metadata.Controls.Add(_psdzPath);
        metadata.Controls.Add(Explain("Main series (must match this vehicle):"));
        _series.Text = settings.PsdzSeries;
        metadata.Controls.Add(_series);
        var browse = new Button { Text = "Browse for PSdZData...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select psdzdata (contains mainseries)", SelectedPath = _psdzPath.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _psdzPath.Text = dialog.SelectedPath;
        };
        metadata.Controls.Add(browse);
        AddAction(metadata, "Load selected ODX data", async token =>
        {
            var path = _psdzPath.Text.Trim();
            string series = _series.Text.Trim();
            byte ecu = ParseEcu();
            var data = await Task.Run(() =>
            {
                var loaded = PsdzDiagnosticData.Load(path, series);
                loaded.GetDids(ecu);
                return loaded;
            }, token);
            _session.DiagnosticData = data;
            RefreshDids();
            UpdateTarget();
            _settings.PsdzDataPath = path;
            _settings.PsdzSeries = series;
            _settings.Save();
            _status.Text = $"Loaded {series} ODX data. Rediscover in the main window to refresh ECU names. Support is not guaranteed.";
            readTabs.SelectedTab = itemTab;
        }, "LoadDiagnosticOdx", online: false);
        metadata.Controls.Add(Explain("Uses the newest installed version of the selected series. Missing/unsupported layouts keep raw bytes with warnings; no units are invented."));

        var faultTexts = CreateColumn();
        faultTextTab.Controls.Add(faultTexts);
        faultTexts.Controls.Add(Explain("Offline descriptions for all fault reads, including DME report. Exact-code matches come from ISTA English documents; ECU-variant applicability is not verified."));
        faultTexts.Controls.Add(Explain("ISTA folder or SQLiteDBs folder:"));
        _istaPath.Text = settings.IstaPath;
        faultTexts.Controls.Add(_istaPath);
        var browseIsta = new Button { Text = "Browse for ISTA...", AutoSize = true };
        browseIsta.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select ISTA or SQLiteDBs", SelectedPath = _istaPath.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _istaPath.Text = dialog.SelectedPath;
        };
        faultTexts.Controls.Add(browseIsta);
        AddAction(faultTexts, "Use ISTA fault texts", token =>
        {
            string path = _istaPath.Text.Trim();
            _session.FaultDescriptions = new IstaFaultLookup(path);
            _settings.IstaPath = path;
            _settings.Save();
            _status.Text = "ISTA fault texts configured. Read faults again; missing or ambiguous descriptions remain unresolved.";
            return Task.CompletedTask;
        }, "LoadDiagnosticIsta", online: false);
        faultTexts.Controls.Add(Explain("Uses the indexed English text database read-only. This is not a complete ECU-specific fault catalog; no neighboring-code descriptions are guessed."));

        var settingsSection = CreateSection("Read settings - applies to all online actions", out var settingsBody);
        settingsSection.Dock = DockStyle.Bottom;
        var settingsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(420, 0) };
        settingsRow.Controls.Add(new Label { Text = "Timeout per request (ms):", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        settingsRow.Controls.Add(_timeout);
        settingsRow.Controls.Add(_cancel);
        settingsBody.Controls.Add(settingsRow);
        _cancel.Click += (_, _) => _operation?.Cancel();
        _inputs.AddRange(new Control[] { _ecu, _module, _did, _request, _timeout, _psdzPath, _series, _istaPath, browse, browseIsta });

        RefreshDids();
        _grid.Columns.Add("Name", "Reading / field");
        _grid.Columns.Add("Value", "Value");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns["Name"].FillWeight = 90;
        _grid.Columns["Value"].FillWeight = 200;
        _grid.Columns["Status"].FillWeight = 35;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.SelectionChanged += (_, _) => UpdateSelected();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var decodedTab = new TabPage("Decoded results");
        decodedTab.Controls.Add(_grid);
        var jsonTab = new TabPage("Raw + decoded JSON");
        jsonTab.Controls.Add(_json);
        tabs.TabPages.Add(decodedTab);
        tabs.TabPages.Add(jsonTab);

        var exports = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        AddLocalButton(exports, "Export all results (JSON)...", ExportJson);
        AddLocalButton(exports, "Save selected data (.bin)...", SavePayload);
        AddLocalButton(exports, "Copy selected CLI command", () =>
        {
            if (_cli.Text.Length == 0)
                throw new InvalidOperationException("Select a result first.");
            Clipboard.SetText(_cli.Text);
        });
        AddLocalButton(exports, "Clear results", () =>
        {
            _readings.Clear();
            _grid.Rows.Clear();
            UpdateJson();
            _cli.Clear();
        });
        var commandPanel = new GroupBox { Text = "CLI equivalent for the selected result", Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(8) };
        commandPanel.Controls.Add(_cli);
        var results = new GroupBox { Text = "Results - each reading shows its actual ECU and data item", Dock = DockStyle.Fill, Padding = new Padding(8, 22, 8, 8) };
        results.Controls.Add(tabs);
        results.Controls.Add(commandPanel);
        results.Controls.Add(exports);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1250, 700), SplitterDistance = 460, Panel1MinSize = 440, Panel2MinSize = 480 };
        split.Panel1.Controls.Add(actions);
        split.Panel1.Controls.Add(settingsSection);
        split.Panel2.Controls.Add(results);
        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8),
            Text = $"Connected to {_settings.Host}:{_settings.Port}. Choose a quick read, or explore one ECU.\nRead-only: no session changes, security access, writes, resets, or fault clearing."
        };
        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(heading);
        UpdateJson();
    }

    private static FlowLayoutPanel CreateColumn() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(5)
    };

    private static GroupBox CreateSection(string title, out FlowLayoutPanel body)
    {
        var section = new GroupBox { Text = title, Width = 420, MinimumSize = new Size(420, 0), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 10, 8, 8), Margin = new Padding(0, 0, 0, 12) };
        body = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        section.Controls.Add(body);
        return section;
    }

    private static Label Explain(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(365, 0),
        Margin = new Padding(3, 4, 3, 8)
    };

    private void UpdateTarget()
    {
        string address = _ecu.Text.Trim();
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) address = address[2..];
        _target.Text = byte.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ecu)
            ? $"Explorer target: {_session.DiagnosticData?.GetEcuName(ecu) ?? EcuCatalog.FindByAddress(ecu)?.Name ?? "Unknown module"} (0x{ecu:X2})."
            : "Enter a valid hex address (00 to FF) for the explorer target.";
    }

    private void RefreshDids()
    {
        if (!byte.TryParse(_ecu.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ecu)) return;
        try
        {
            var definitions = _session.DiagnosticData?.GetDids(ecu);
            _did.Items.Clear();
            foreach (var definition in definitions is { Count: > 0 } ? definitions : DiagnosticCatalog.DmeReads)
                _did.Items.Add(definition);
            _did.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or FormatException or NotSupportedException)
        {
            _did.Items.Clear();
            _did.Text = "";
            _status.Text = $"ODX definitions unavailable: {ex.Message} Enter a DID manually; raw replies remain available.";
        }
    }

    private void AddAction(FlowLayoutPanel panel, string text, Func<CancellationToken, Task> action, string? name = null, bool online = true)
    {
        var button = new Button { Text = text, AutoSize = true, Name = name ?? text };
        button.Click += async (_, _) => await RunAsync(action, online);
        panel.Controls.Add(button);
        _inputs.Add(button);
    }

    private void AddLocalButton(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex); }
        };
        panel.Controls.Add(button);
        _inputs.Add(button);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool online)
    {
        if (_operation != null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        TimeSpan previousTimeout = _session.Connection.Timeout;
        _session.Connection.Timeout = TimeSpan.FromMilliseconds((double)_timeout.Value);
        // The report has eleven independently bounded reads; leave room for reconnects.
        operation.CancelAfter(TimeSpan.FromMilliseconds((double)_timeout.Value * 12));
        foreach (var input in _inputs) input.Enabled = false;
        _cancel.Enabled = true;
        _status.Text = online ? "Reading... Cancel is available; all operations are read-only." : "Loading offline diagnostic data...";
        try
        {
            await action(operation.Token);
            if (_status.Text.StartsWith("Reading", StringComparison.Ordinal))
                _status.Text = "Finished. Select a result to copy its CLI command or save its raw payload.";
        }
        catch (OperationCanceledException)
        {
            if (online) _session.Close();
            _status.Text = online ? "Read cancelled or command deadline exceeded. Partial results are preserved." : "Offline data loading cancelled.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _session.Connection.Timeout = previousTimeout;
            _operation = null;
            foreach (var input in _inputs) input.Enabled = true;
            _cancel.Enabled = false;
            if (_closeAfterCancel) Close();
        }
    }

    private byte ParseEcu()
    {
        string value = _ecu.Text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private ushort ParseDid()
    {
        if (_did.SelectedItem is DiagnosticDefinition definition && _did.Text == definition.ToString())
            return definition.Did;
        string value = _did.Text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ushort.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private void AddReading(DiagnosticReading reading)
    {
        if (IsDisposed) return;
        _readings.Add(reading);
        string prefix = $"0x{reading.Ecu:X2} / {reading.Name}";
        AddRow(prefix, reading.Error ?? (reading.Values.Count == 0 ? reading.PayloadHex : ""), reading);
        foreach (var pair in reading.Values)
            AddRow($"  {pair.Key}", pair.Value, reading);
        if (reading.DecoderSource != null)
            AddRow("  Decoder source", reading.DecoderSource, reading);
        foreach (var fault in reading.Faults)
        {
            string description = fault.Description ?? fault.DescriptionStatus switch
            {
                "not_found" => "No exact indexed ISTA description found.",
                "ambiguous" => "Conflicting ISTA descriptions; no interpretation selected.",
                "lookup_error" => "ISTA lookup failed; see warning.",
                "test_status_only" => "Test-status record only; no failure/history bits are set.",
                _ => "No description source configured."
            };
            AddRow($"  {(fault.HasFailureEvidence ? "DTC" : "Test status")} {fault.Code}", $"{description}\nStatus 0x{fault.Status:X2}; Failed={fault.TestFailed}; Pending={fault.Pending}; Confirmed={fault.Confirmed}; Failed since clear={fault.TestFailedSinceClear}; Test incomplete this cycle={fault.TestNotCompletedThisCycle}; Warning indicator={fault.WarningIndicatorRequested}", reading);
            if (fault.DescriptionSource != null)
                AddRow("    ISTA source", $"{fault.DescriptionSource}; document {fault.SourceDocumentId ?? "(no unique match)"}; code match only, variant unverified.", reading);
        }
        foreach (var component in reading.Software)
            AddRow($"  Software {component.Id}", $"Class 0x{component.ProcessClass:X2}; version {component.Version}", reading);
        foreach (string warning in reading.Warnings)
            AddRow("  WARNING", warning, reading);
        UpdateJson();
        UpdateSelected();
    }

    private void AddRow(string name, string value, DiagnosticReading reading)
    {
        int index = _grid.Rows.Add(name, value, reading.Status);
        _grid.Rows[index].Tag = reading;
        if (reading.Status != "ok") _grid.Rows[index].DefaultCellStyle.ForeColor = Color.DarkRed;
    }

    private DiagnosticReading? SelectedReading => _grid.CurrentRow?.Tag as DiagnosticReading;

    private void UpdateSelected()
    {
        var reading = SelectedReading;
        _cli.Text = reading == null ? "" :
            $"bmwcoder raw --host \"{_settings.Host}\" --port {_settings.Port} --ecu 0x{reading.Ecu:X2} --pdu \"{reading.RequestHex}\" --json";
    }

    private void UpdateJson() => _json.Text = JsonSerializer.Serialize(new
    {
        schema = "bmwcoder/diagnostics/1",
        host = _settings.Host,
        port = _settings.Port,
        read_only = true,
        readings = _readings
    }, JsonOptions);

    private void ExportJson()
    {
        if (_readings.Count == 0) throw new InvalidOperationException("Read diagnostic data first.");
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, _json.Text);
    }

    private void SavePayload()
    {
        var reading = SelectedReading;
        if (reading == null || reading.Status != "ok")
            throw new InvalidOperationException("Select a successful reading first.");
        using var dialog = new SaveFileDialog { Filter = "Binary (*.bin)|*.bin", FileName = $"ecu-{reading.Ecu:X2}-{reading.RequestHex}.bin" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllBytes(dialog.FileName, Convert.FromHexString(reading.PayloadHex));
    }

    private void ShowError(Exception ex)
    {
        _status.Text = $"Error: {ex.Message}";
        MessageBox.Show(this, ex.Message, "Diagnostics error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_operation != null)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _operation.Cancel();
        }
        base.OnFormClosing(e);
    }
}
