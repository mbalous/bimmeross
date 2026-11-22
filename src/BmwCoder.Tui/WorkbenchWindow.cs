using System.Globalization;
using System.Diagnostics;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using BmwCoder.Transport;
using Terminal.Gui;

namespace BmwCoder.Tui;

public sealed class WorkbenchWindow : Toplevel
{
    private readonly Workbench _workbench;
    private readonly string _settingsPath;
    private readonly Label _header;
    private readonly Label _smallTerminal;
    private readonly FrameView _body;
    private readonly ListView _items;
    private readonly TextField _search;
    private readonly TextView _details;
    private readonly TextView _logs;
    private readonly Label _status;
    private readonly Label _workflow;
    private readonly object _activityTimer;
    private int _activityFrame;
    private string _screen = "Vehicle";
    private bool _closed;
    private EcuInfo[] _visibleEcus = Array.Empty<EcuInfo>();
    private XmlField[] _visibleFields = Array.Empty<XmlField>();
    private CafField[] _visibleCafFields = Array.Empty<CafField>();
    private CodingParameter[] _visibleParameters = Array.Empty<CodingParameter>();
    private DiagnosticDefinition[] _visibleDefinitions = Array.Empty<DiagnosticDefinition>();
    private DiagnosticReading[] _visibleDiagnostics = Array.Empty<DiagnosticReading>();
    private BackupEntry[] _visibleRestoreEntries = Array.Empty<BackupEntry>();
    private string[] _visibleLogs = Array.Empty<string>();
    public string CurrentScreen => _screen;

    public WorkbenchWindow(Workbench workbench, string settingsPath)
    {
        _workbench = workbench;
        _settingsPath = settingsPath;
        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_Vehicle", new[]
            {
                new MenuItem("_Show ECUs", "", () => ShowScreen("Vehicle")),
                new MenuItem("_Find gateway (F5)", "", FindGateway),
                new MenuItem("_Connect (F2)", "", () => Start(_workbench.ConnectAsync)),
                new MenuItem("_Discover ECUs (F3)", "", DiscoverEcus),
                new MenuItem("D_isconnect", "", () => Start(_workbench.DisconnectAsync)),
                new MenuItem("_Read DID", "", ReadDid),
                new MenuItem("Read _faults", "", ReadFaults),
                new MenuItem("Check ECU _response", "", ProbeEcu),
                new MenuItem("Read _DME report", "", ReadDmeReport),
                new MenuItem("Read-only re_quest", "", ReadRequest),
                new MenuItem("Load ODX _DIDs", "", LoadDiagnosticData),
                new MenuItem("Read selected ODX DI_D", "", ReadSelectedDefinition),
                new MenuItem("Configure ISTA fault te_xts", "", () => Start(_workbench.ConfigureFaultTextsAsync)),
                new MenuItem("Show diagnostic resu_lts", "", () => ShowScreen("Diagnostics")),
                new MenuItem("E_xport diagnostic results", "", () => SaveFile("Diagnostic results JSON", path => _workbench.ExportDiagnosticReadingsAsync(path))),
                new MenuItem("Export selected _payload", "", ExportDiagnosticPayload),
                new MenuItem("Copy selected CLI _command", "", CopyDiagnosticCommand),
                new MenuItem("C_lear diagnostic results", "", () => Start(_workbench.ClearDiagnosticReadingsAsync)),
                new MenuItem("_Quit (Ctrl+Q)", "", RequestQuit)
            }),
            new MenuBarItem("_Coding", new[]
            {
                new MenuItem("_Capabilities", "", () => ShowScreen("Coding")),
                new MenuItem("_Read coding / decode", "", ReadCoding),
                new MenuItem("_Open NCD / decode", "", OpenNcd),
                new MenuItem("_Show parameters", "", () => ShowScreen("Parameters")),
                new MenuItem("Export read _NCD", "", () => SaveFile("New original NCD", path => _workbench.ExportReadCodingAsync(path))),
                new MenuItem("Read CAF _definition", "", () => OpenFile("CAF definition", path => _workbench.ImportCafAsync(path))),
                new MenuItem("Show CAF _fields", "", () => ShowScreen("CAF")),
                new MenuItem("_Import file (raw preview)", "", () => OpenFile("Coding file", path => _workbench.ImportCodingAsync(path))),
                new MenuItem("E_xport original bytes", "", () => SaveFile("New coding file", path => _workbench.ExportCodingAsync(path))),
                new MenuItem("Open selected DID as raw _draft", "", OpenRawDraft),
                new MenuItem("_Show raw draft", "", () => StartRawDraft(_workbench.ShowRawDraftAsync)),
                new MenuItem("Edit raw _byte", "", EditRawDraft),
                new MenuItem("Re_vert raw draft", "", () => StartRawDraft(_workbench.RevertRawDraftAsync)),
                new MenuItem("_Verify legacy checksum", "", () => StartRawDraft(_workbench.VerifyRawChecksumAsync)),
                new MenuItem("_Apply legacy checksum offline", "", () => StartRawDraft(_workbench.ApplyRawChecksumAsync)),
                new MenuItem("Export raw d_raft", "", () => SaveFile("Raw block draft", path => _workbench.ExportRawDraftAsync(path))),
                new MenuItem("Write raw block (disabled)", "", () => StartRawDraft(_workbench.RequestRawWriteAsync)),
                new MenuItem("Legacy block read (disabled)", "", () => StartRawDraft(_workbench.RequestLegacyBlockReadAsync))
            }),
            new MenuBarItem("_FA/VO", new[]
            {
                new MenuItem("_Show fields", "", () => ShowScreen("FA/VO")),
                new MenuItem("_Import XML", "", () => OpenFile("FA XML", path => _workbench.ImportFaAsync(path))),
                new MenuItem("_Load synthetic FA", "", () => Start(_workbench.LoadDemoFaAsync)),
                new MenuItem("_Edit selected field", "", EditFa),
                new MenuItem("_Diff", "", () => Start(_workbench.ShowFaAsync)),
                new MenuItem("E_xport XML", "", () => SaveFile("New FA XML", path => _workbench.ExportFaAsync(path)))
            }),
            new MenuBarItem("_Backups", new[]
            {
                new MenuItem("_Snapshot suggested DIDs", "", BackupSuggested),
                new MenuItem("_Inspect directory", "", () => PromptPath("Backup directory", path => _workbench.InspectBackupAsync(path))),
                new MenuItem("_Review restore", "", ReviewRestore),
                new MenuItem("Restore _selected snapshot", "", RestoreSelected),
                new MenuItem("_Open backup folder", "", OpenBackupFolder)
            }),
            new MenuBarItem("_Settings", new[]
            {
                new MenuItem("_Connection", "", ConnectionSettings),
                new MenuItem("Diagnostic _timeout", "", DiagnosticTimeout),
                new MenuItem("_Local tool paths", "", ToolPaths),
                new MenuItem("_Inspect installations", "", () => Start(_workbench.InspectInstallationsAsync)),
                new MenuItem("_Save settings", "", () => Start(() => _workbench.SaveSettingsAsync(_settingsPath)))
            }),
            new MenuBarItem("_Help", new[]
            {
                new MenuItem("_About / limitations", "", () => ShowScreen("Help")),
                new MenuItem("Show _logs", "", () => ShowScreen("Logs")),
                new MenuItem("_Cancel operation (F4)", "", _workbench.Cancel)
            })
        });
        var accent = new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(Color.BrightCyan, Color.Blue),
            Focus = Terminal.Gui.Attribute.Make(Color.White, Color.Blue),
            HotNormal = Terminal.Gui.Attribute.Make(Color.White, Color.Blue),
            HotFocus = Terminal.Gui.Attribute.Make(Color.White, Color.Blue)
        };
        _header = new Label("") { X = 0, Y = 1, Width = Dim.Fill(), Height = 2, ColorScheme = accent };
        _body = new FrameView("Vehicle") { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(6) };
        var searchLabel = new Label("Search:") { X = 0, Y = 0 };
        _search = new TextField("") { X = 8, Y = 0, Width = Dim.Fill() };
        _search.TextChanged += _ => RefreshState();
        _workflow = new Label("") { X = 0, Y = 2, Width = Dim.Fill(), Height = 1 };
        _items = new ListView { X = 0, Y = 4, Width = Dim.Percent(35), Height = Dim.Fill() };
        _details = new TextView { X = Pos.Right(_items), Y = 4, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        _body.Add(searchLabel, _search, _workflow, _items, _details);
        _items.SelectedItemChanged += _ => ShowSelection();
        _items.OpenSelectedItem += _ =>
        {
            if (_screen == "FA/VO") EditFa();
            else if (_screen == "DIDs") ReadSelectedDefinition();
        };
        _logs = new TextView { X = 0, Y = Pos.Bottom(_body), Width = Dim.Fill(), Height = 4, ReadOnly = true, WordWrap = false, CanFocus = false };
        _status = new Label("") { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, ColorScheme = accent };
        var keys = new StatusBar(new[]
        {
            new StatusItem(Key.F2, "~F2~ Connect", () => Start(_workbench.ConnectAsync)),
            new StatusItem(Key.F3, "~F3~ Discover ECUs", DiscoverEcus),
            new StatusItem(Key.F5, "~F5~ Find gateway", FindGateway),
            new StatusItem(Key.F4, "~F4~ Cancel", _workbench.Cancel),
            new StatusItem(Key.CtrlMask | Key.Q, "~Ctrl+Q~ Quit", RequestQuit)
        });
        _smallTerminal = new Label("Minimum terminal size: 80 x 24. Resize the terminal; Ctrl+Q exits.")
        { X = 0, Y = 3, Width = Dim.Fill(), Height = 3, Visible = false };
        Add(menu, _header, _body, _logs, _status, keys, _smallTerminal);
        LayoutComplete += _ => UpdateSize();
        _workbench.Changed += OnChanged;
        _activityTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(150), _ =>
        {
            if (_closed) return false;
            if (_workbench.IsBusy)
            {
                _activityFrame++;
                RefreshStatus();
            }
            return true;
        });
        RefreshState();
    }

    public void ShowScreen(string screen)
    {
        _screen = screen;
        RefreshState();
        _items.SetFocus();
    }

    public void Filter(string search) => _search.Text = search;

    public override bool ProcessKey(KeyEvent keyEvent)
    {
        switch (keyEvent.Key)
        {
            case Key.F2: Start(_workbench.ConnectAsync); return true;
            case Key.F3: DiscoverEcus(); return true;
            case Key.F4: _workbench.Cancel(); return true;
            case Key.F5: FindGateway(); return true;
            case Key.CtrlMask | Key.Q: RequestQuit(); return true;
            default: return base.ProcessKey(keyEvent);
        }
    }

    public void RefreshState()
    {
        _header.Text = $"BimmerOSS | {(_workbench.IsDemo ? "DEMO (no sockets)" : "LIVE")} | READ-ONLY PREVIEW\n" +
            $"{(_workbench.IsConnected ? "Connected" : "Disconnected")} | VIN: {_workbench.Vin ?? "(unknown)"} | FDL writes: unavailable";
        RefreshStatus();
        _body.Title = _screen == "Vehicle" ? $"Vehicle / ECUs ({_workbench.Ecus.Count})" : _screen;
        _workflow.Text = _screen == "Vehicle"
            ? (_workbench.IsDemo
                ? "DEMO: F2 Connect  >  F3 Discover ECUs"
                : "GET STARTED: F5 Find gateway  >  F2 Connect  >  F3 Discover ECUs")
            : "Tab: move focus  |  Enter: select  |  Alt: menus";
        int selected = _items.SelectedItem;
        string search = _search.Text?.ToString() ?? "";
        if (_screen == "Vehicle")
        {
            _visibleEcus = _workbench.Ecus.Where(e => $"{e.Address:X2} {e.Name}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleEcus.Select(e => $"0x{e.Address:X2} {e.Name} {(e.Responds ? "online" : "no response")}").ToList());
        }
        else if (_screen == "FA/VO")
        {
            _visibleFields = (_workbench.Fa?.Fields ?? Array.Empty<XmlField>())
                .Where(f => $"{f.Path} {f.Value}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleFields.Select(f => $"{f.Id}: {f.Path.Split('/').Last()} = {f.Value}").ToList());
        }
        else if (_screen == "Parameters")
        {
            _visibleParameters = (_workbench.Decoded?.Parameters ?? Array.Empty<CodingParameter>())
                .Where(p => $"{p.Id} {p.Name} {p.Value} {string.Join(" ", p.Interpretations)}"
                    .Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleParameters.Select(p => $"{p.Name ?? p.Id} = {p.Value ?? "(unavailable)"}").ToList());
        }
        else if (_screen == "CAF")
        {
            _visibleCafFields = (_workbench.Caf?.Fields ?? Array.Empty<CafField>())
                .Where(f => $"{f.Path} {f.Value}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleCafFields.Select(f => $"{f.Path.Split('/').Last()} = {f.Value}").ToList());
        }
        else if (_screen == "DIDs")
        {
            _visibleDefinitions = _workbench.DiagnosticDefinitions
                .Where(d => $"{d.Did:X4} {d.Name}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleDefinitions.Select(d => $"0x{d.Did:X4} {d.Name}").ToList());
        }
        else if (_screen == "Diagnostics")
        {
            _visibleDiagnostics = _workbench.DiagnosticReadings
                .Where(r => $"{r.Ecu:X2} {r.Did:X4} {r.Name} {r.Status}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleDiagnostics.Select(r => $"0x{r.Ecu:X2} {r.Name} [{r.Status}]").ToList());
        }
        else if (_screen == "Restore")
        {
            _visibleRestoreEntries = (_workbench.RestoreInspection?.Manifest.Entries ?? new List<BackupEntry>())
                .Where(e => e != null && $"{e.EcuAddress:X2} {e.Did:X4} {e.EcuName}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleRestoreEntries.Select(e => $"0x{e.EcuAddress:X2}/0x{e.Did:X4} {e.EcuName}").ToList());
        }
        else if (_screen == "Logs")
        {
            _visibleLogs = _workbench.Logs.Where(line => line.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            _items.SetSource(_visibleLogs.ToList());
        }
        else
            _items.SetSource(new List<string> { _screen == "Help" ? "Read-only preview" : "No validated FDL backend" });
        if (_items.Source.Count > 0)
            _items.SelectedItem = Math.Clamp(selected, 0, _items.Source.Count - 1);
        _details.Text = _screen switch
        {
            "Coding" => _workbench.CodingSummary,
            "CAF" => _workbench.CafSummary,
            "Parameters" => _workbench.DecodedSummary,
            "Help" => "Terminal.Gui 1.19 / shared BimmerOSS libraries\n\n" +
                "Tab / Shift+Tab: focus. Alt+menu key: menu. Enter: select/edit.\n" +
                "F5: find gateway; F2: connect; F3: discover ECUs; F4: cancel; Ctrl+Q: exit.\n\n" +
                "Live read-only mode is the default; connections and gateway discovery are explicit. Use --demo for synthetic data without sockets.\n" +
                "Import an FA XML document, select FA/VO > Show fields, then Edit or Enter.\n" +
                "FA edits are offline and syntax-checked only. Exports never overwrite existing files.\n\n" +
                PreviewCapabilities.FaReadUnavailable + "\n\n" + PreviewCapabilities.CodingUnavailable,
            "Vehicle" when _workbench.Status == "Disconnected - read-only preview" =>
                "WELCOME TO BIMMEROSS\n\n" +
                (_workbench.IsDemo
                    ? "Synthetic demo: press F2 to connect, then F3 to discover demo ECUs."
                    : "1. Press F5 to find a gateway on your ENET interface, or set a host in Settings > Connection.\n" +
                      "2. Press F2 to connect and verify the VIN.\n" +
                      "3. Press F3 to discover ECU candidates.\n\n" +
                      "Finding a gateway sends a bounded discovery broadcast; it does not connect.") +
                "\n\nRead-only preview: vehicle coding writes are unavailable.",
            _ => _workbench.Details
        };
        if (_screen is "DIDs" or "Diagnostics" or "Restore" or "Logs")
            ShowSelection();
        _logs.Text = string.Join("\n", _workbench.Logs.TakeLast(4));
        SetNeedsDisplay();
    }

    private void RefreshStatus()
    {
        _status.Text = _workbench.IsBusy
            ? $" {("|/-\\"[_activityFrame % 4])}  {_workbench.Status}  |  F4 cancels (disconnects)"
            : $" {_workbench.Status}";
        _status.SetNeedsDisplay();
    }

    private void DiscoverEcus()
    {
        _screen = "Vehicle";
        _search.Text = "";
        Start(_workbench.DiscoverAsync);
    }

    private void OnChanged()
    {
        if (!_closed)
            Application.MainLoop?.Invoke(() => { if (!_closed) RefreshState(); });
    }

    private void UpdateSize()
    {
        bool small = Bounds.Width < 80 || Bounds.Height < 24;
        _smallTerminal.Visible = small;
        _body.Visible = !small;
        _logs.Visible = !small;
    }

    private void ShowSelection()
    {
        if (_screen == "Vehicle" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleEcus.Length)
        {
            var ecu = _visibleEcus[_items.SelectedItem];
            _details.Text = $"{ecu}\n{ecu.Description}\nSource: {ecu.DiscoverySource}\nName source: {ecu.NameSource}\n" +
                $"Probe: {ecu.ProbeError ?? "no error"}\n\nReads only. ECU name is not a verified coding profile.";
        }
        if (_screen == "FA/VO" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleFields.Length)
        {
            var field = _visibleFields[_items.SelectedItem];
            _details.Text = $"OFFLINE FA XML\n{field.Path}\nValue: {field.Value}\n\nEnter edits this field. XML syntax only; no vehicle semantic validation.";
        }
        if (_screen == "CAF" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleCafFields.Length)
        {
            var field = _visibleCafFields[_items.SelectedItem];
            _details.Text = $"READ-ONLY CAF DEFINITION\n{field.Path}\nValue: {field.Value}\n\n" +
                "Generic definition fields, not decoded NCD values or validated writable parameters.";
        }
        if (_screen == "Parameters" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleParameters.Length)
        {
            var parameter = _visibleParameters[_items.SelectedItem];
            _details.Text = $"READ-ONLY CODING SNAPSHOT\n{_workbench.Decoded?.CafFile}\n" +
                $"ID: {parameter.Id}\nName: {parameter.Name ?? "(unknown/trimmed)"}\n" +
                $"Current units: {parameter.Value ?? "(unavailable: no current SDK value)"}\n" +
                $"Interpretation: {(parameter.Interpretations.Count == 0 ? "(unknown)" : string.Join(", ", parameter.Interpretations))}\n" +
                $"Storage: {parameter.Start} - {parameter.End}\nMask: {parameter.Mask}\n\nNo editing or writing is enabled.";
        }
        if (_screen == "DIDs" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleDefinitions.Length)
        {
            var definition = _visibleDefinitions[_items.SelectedItem];
            _details.Text = $"ECU 0x{_workbench.DiagnosticDefinitionEcu:X2} / DID 0x{definition.Did:X4}\n{definition.Name}\n\n" +
                "ODX names and layouts are candidates, not a verified ECU variant. Enter reads this DID.";
        }
        if (_screen == "Diagnostics" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleDiagnostics.Length)
            _details.Text = Workbench.Format(_visibleDiagnostics[_items.SelectedItem]);
        if (_screen == "Restore" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleRestoreEntries.Length)
        {
            var entry = _visibleRestoreEntries[_items.SelectedItem];
            _details.Text = $"RESTORE REVIEW - LIVE RESTORE DISABLED\nECU 0x{entry.EcuAddress:X2} / DID 0x{entry.Did:X4}\n" +
                $"Name: {entry.EcuName}\nFile: {entry.FileName}\nBytes: {entry.SizeBytes}\nSHA-256: {entry.Sha256}\n" +
                $"Backup VIN: {_workbench.RestoreInspection?.Manifest.Vin}\n" +
                $"Connected VIN: {_workbench.Vin ?? "(not verified)"}\n" +
                $"Integrity: {(_workbench.RestoreInspection?.IsValid == true ? "verified (not ECU compatibility)" : "FAILED")}\n" +
                "A target-specific backend and safety qualification are required before any restore.";
        }
        if (_screen == "Logs" && _items.SelectedItem >= 0 && _items.SelectedItem < _visibleLogs.Length)
            _details.Text = _visibleLogs[_items.SelectedItem];
    }

    private void ReadCoding()
    {
        try
        {
            byte ecu = SelectedEcu();
            string? caf = Prompt("Read coding / decode", "Exact CAF filename (blank = auto when ECU has one CAF)", "");
            if (caf == null)
                return;
            _screen = "Parameters";
            _search.Text = "";
            Start(() => _workbench.ReadCodingAsync(ecu, string.IsNullOrWhiteSpace(caf) ? null : caf.Trim()));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Read coding", ex.Message, "OK");
        }
    }

    private void OpenNcd()
    {
        using var dialog = new OpenDialog("Open trusted NCD", "Offline parameter decoding; exact CAF required")
        { AllowsMultipleSelection = false, CanChooseDirectories = false };
        Application.Run(dialog);
        string? path = dialog.FilePath?.ToString();
        if (dialog.Canceled || string.IsNullOrWhiteSpace(path))
            return;
        string? caf = Prompt("Decode NCD", "Exact CAF filename including version", _workbench.Decoded?.CafFile ?? _workbench.Caf?.Name ?? "");
        if (string.IsNullOrWhiteSpace(caf))
            return;
        _screen = "Parameters";
        _search.Text = "";
        Start(() => _workbench.DecodeCodingAsync(path, caf.Trim()));
    }

    private async void Start(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            if (!_closed)
                MessageBox.ErrorQuery("Operation failed", ex.Message, "OK");
        }
        if (!_closed)
            RefreshState();
    }

    private void RequestQuit()
    {
        _workbench.Cancel();
        Application.RequestStop();
    }

    private byte SelectedEcu()
    {
        if (_screen != "Vehicle" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleEcus.Length)
            throw new InvalidOperationException("Select Vehicle > Show ECUs and choose a discovered ECU first.");
        return _visibleEcus[_items.SelectedItem].Address;
    }

    private void ReadDid()
    {
        try
        {
            byte ecu = SelectedEcu();
            string? value = Prompt("Read DID", "DID (four hex digits)", "F190");
            if (value == null)
                return;
            if (value.Length != 4 || !ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort did))
                throw new FormatException("Enter exactly four hexadecimal digits.");
            Start(() => _workbench.ReadDidAsync(ecu, did));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Read DID", ex.Message, "OK");
        }
    }

    private void FindGateway()
    {
        try
        {
            if (_workbench.IsBusy)
                throw new InvalidOperationException("Wait for the current operation or press F4 to cancel.");
            if (_workbench.IsDemo)
            {
                MessageBox.Query("Demo mode", "No discovery packets are sent in demo mode. Use --live to select an ENET interface.", "OK");
                return;
            }
            var networks = EnetDiscovery.GetInterfaces();
            if (networks.Length == 0)
                throw new InvalidOperationException("No active IPv4 network interface was found.");
            using var dialog = new Dialog("Select the ENET network interface", 70, 15);
            var list = new ListView(networks.Select(n => n.ToString()).ToList())
            { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(3) };
            EnetInterface? selected = null;
            var accept = new Button("Find", true);
            accept.Clicked += () => { selected = networks[list.SelectedItem]; Application.RequestStop(); };
            var cancel = new Button("Cancel");
            cancel.Clicked += () => Application.RequestStop();
            dialog.Add(list);
            dialog.AddButton(accept);
            dialog.AddButton(cancel);
            Application.Run(dialog);
            if (selected != null)
                Start(() => _workbench.FindGatewayAsync(selected));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Find gateway", ex.Message, "OK");
        }
    }

    private void ReadFaults()
    {
        try
        {
            byte ecu = SelectedEcu();
            Start(() => _workbench.ReadFaultsAsync(ecu));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Read faults", ex.Message, "OK");
        }
    }

    private void ProbeEcu()
    {
        try
        {
            byte ecu = SelectedEcu();
            Start(() => _workbench.ProbeAsync(ecu));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Check ECU", ex.Message, "OK");
        }
    }

    private void ReadDmeReport()
    {
        ShowScreen("Vehicle");
        Start(_workbench.ReadDmeReportAsync);
    }

    private void LoadDiagnosticData()
    {
        try
        {
            byte ecu = SelectedEcu();
            ShowScreen("DIDs");
            Start(() => _workbench.LoadDiagnosticDataAsync(ecu));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Load ODX DIDs", ex.Message, "OK");
        }
    }

    private void ReadSelectedDefinition()
    {
        try
        {
            if (_screen != "DIDs" || _workbench.DiagnosticDefinitionEcu is not byte ecu ||
                _items.SelectedItem < 0 || _items.SelectedItem >= _visibleDefinitions.Length)
                throw new InvalidOperationException("Load ODX DIDs and select a definition first.");
            ushort did = _visibleDefinitions[_items.SelectedItem].Did;
            ShowScreen("Vehicle");
            Start(() => _workbench.ReadDidAsync(ecu, did));
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Read ODX DID", ex.Message, "OK");
        }
    }

    private void ExportDiagnosticPayload()
    {
        if (_screen != "Diagnostics" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleDiagnostics.Length)
        {
            MessageBox.ErrorQuery("Export diagnostic payload", "Show diagnostic results and select a reading first.", "OK");
            return;
        }
        var reading = _visibleDiagnostics[_items.SelectedItem];
        SaveFile("Diagnostic payload", path => _workbench.ExportDiagnosticPayloadAsync(reading, path));
    }

    private void CopyDiagnosticCommand()
    {
        try
        {
            if (_screen != "Diagnostics" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleDiagnostics.Length)
                throw new InvalidOperationException("Show diagnostic results and select a reading first.");
            string command = _workbench.DiagnosticCommand(_visibleDiagnostics[_items.SelectedItem]);
            if (!Clipboard.TrySetClipboardData(command))
                throw new InvalidOperationException("Clipboard is unavailable in this terminal.");
            MessageBox.Query("CLI command", "Copied using the endpoint recorded when the reading was made.", "OK");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            MessageBox.ErrorQuery("CLI command", ex.Message, "OK");
        }
    }

    private void BackupSuggested()
    {
        if (MessageBox.Query("Snapshot suggested DIDs",
            "Read suggested DIDs from responding ECUs? This is not a full-vehicle or validated coding backup.", "Read", "Cancel") != 0)
            return;
        ShowScreen("Vehicle");
        Start(_workbench.BackupSuggestedAsync);
    }

    private void OpenBackupFolder()
    {
        try
        {
            string path = _workbench.BackupDirectory;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.ErrorQuery("Backup folder", ex.Message, "OK");
        }
    }

    private void ReviewRestore()
    {
        string? path = Prompt("Review restore", "Backup directory", "");
        if (string.IsNullOrWhiteSpace(path))
            return;
        ShowScreen("Restore");
        Start(() => _workbench.PrepareRestoreAsync(path));
    }

    private void RestoreSelected()
    {
        if (_screen != "Restore" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleRestoreEntries.Length)
        {
            MessageBox.ErrorQuery("Restore", "Review a backup and select a snapshot first.", "OK");
            return;
        }
        Start(() => _workbench.RequestRestoreAsync(_visibleRestoreEntries[_items.SelectedItem]));
    }

    private void OpenRawDraft()
    {
        if (_screen != "Diagnostics" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleDiagnostics.Length)
        {
            MessageBox.ErrorQuery("Raw block draft", "Show diagnostic results and select a successful DID reading first.", "OK");
            return;
        }
        var reading = _visibleDiagnostics[_items.SelectedItem];
        ShowScreen("Raw block");
        Start(() => _workbench.OpenRawDraftAsync(reading));
    }

    private void EditRawDraft()
    {
        string? offsetText = Prompt("Edit raw block offline", "Byte offset (hex)", "0");
        if (offsetText == null)
            return;
        string? valueText = Prompt("Edit raw block offline", "New byte value (two hex digits)", "00");
        if (valueText == null)
            return;
        if (!int.TryParse(offsetText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset) ||
            valueText.Length != 2 || !byte.TryParse(valueText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
        {
            MessageBox.ErrorQuery("Edit raw block", "Enter a hexadecimal offset and exactly two hexadecimal digits for the byte.", "OK");
            return;
        }
        ShowScreen("Raw block");
        Start(() => _workbench.EditRawDraftAsync(offset, value));
    }

    private void StartRawDraft(Func<Task> action)
    {
        ShowScreen("Raw block");
        Start(action);
    }

    private void ReadRequest()
    {
        try
        {
            string? address = Prompt("Read-only request", "ECU address (two hex digits)", "10");
            if (address == null)
                return;
            if (address.Length != 2 || !byte.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ecu))
                throw new FormatException("Enter exactly two hexadecimal digits for the ECU address.");
            string? input = Prompt("Read-only request", "22 <DID>, 19 02 <mask>, or 3E 00 (hex)", "22 F1 90");
            if (input == null)
                return;
            byte[] request = Convert.FromHexString(string.Concat(input.Where(c => !char.IsWhiteSpace(c))));
            ShowScreen("Vehicle");
            Start(() => _workbench.ReadRequestAsync(ecu, request));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            MessageBox.ErrorQuery("Read-only request", ex.Message, "OK");
        }
    }

    private void EditFa()
    {
        if (_workbench.IsBusy)
        {
            MessageBox.ErrorQuery("Busy", "Wait for the current operation or press F4 to cancel.", "OK");
            return;
        }
        if (_screen != "FA/VO" || _items.SelectedItem < 0 || _items.SelectedItem >= _visibleFields.Length)
        {
            MessageBox.ErrorQuery("FA editor", "Import XML, then select FA/VO > Show fields and choose a field.", "OK");
            return;
        }
        var field = _visibleFields[_items.SelectedItem];
        string? value = Prompt("Edit FA offline - syntax only", field.Path, field.Value);
        if (value != null)
            Start(() => _workbench.EditFaAsync(field.Id, value));
    }

    private void ConnectionSettings()
    {
        if (_workbench.IsBusy)
        {
            MessageBox.ErrorQuery("Busy", "Wait for the current operation or press F4 to cancel.", "OK");
            return;
        }
        string? host = Prompt("Connection", "Host (connection starts only with F2)", _workbench.Settings.Host);
        if (host == null)
            return;
        string? port = Prompt("Connection", "TCP port", _workbench.Settings.Port.ToString(CultureInfo.InvariantCulture));
        if (port == null)
            return;
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(port, out int number) || number is < 1 or > 65535)
        {
            MessageBox.ErrorQuery("Connection", "A host and port between 1 and 65535 are required.", "OK");
            return;
        }
        _workbench.Settings.Host = host;
        _workbench.Settings.Port = number;
    }

    private void DiagnosticTimeout()
    {
        string? value = Prompt("Diagnostic timeout", "Per-request timeout in milliseconds (100-30000)",
            _workbench.RequestTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        if (value == null)
            return;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds))
        {
            MessageBox.ErrorQuery("Diagnostic timeout", "Enter a whole number of milliseconds.", "OK");
            return;
        }
        Start(() => _workbench.SetRequestTimeoutAsync(milliseconds));
    }

    private void ToolPaths()
    {
        if (_workbench.IsBusy)
        {
            MessageBox.ErrorQuery("Busy", "Wait for the current operation or press F4 to cancel.", "OK");
            return;
        }
        using var dialog = new LocalToolsDialog(_workbench.Settings, _settingsPath, LocalToolDiscovery.Detect(_workbench.Settings));
        Application.Run(dialog);
        RefreshState();
    }

    private void OpenFile(string title, Func<string, Task> action)
    {
        using var dialog = new OpenDialog(title, "Local files only") { AllowsMultipleSelection = false, CanChooseDirectories = false };
        Application.Run(dialog);
        if (!dialog.Canceled)
        {
            string? path = dialog.FilePath?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return;
            _screen = title switch { "FA XML" => "FA/VO", "CAF definition" => "CAF", _ => "Coding" };
            Start(() => action(path));
        }
    }

    private void SaveFile(string title, Func<string, Task> action) => PromptPath(title + " (must not exist)", action);

    private void PromptPath(string title, Func<string, Task> action)
    {
        string? path = Prompt(title, "Full path", "");
        if (!string.IsNullOrWhiteSpace(path))
            Start(() => action(path));
    }

    private static string? Prompt(string title, string label, string initial)
    {
        using var dialog = new Dialog(title, 70, 10);
        var prompt = new Label(label) { X = 1, Y = 1, Width = Dim.Fill(1), Height = 2 };
        var input = new TextField(initial) { X = 1, Y = 3, Width = Dim.Fill(1) };
        string? result = null;
        var accept = new Button("OK", true);
        accept.Clicked += () => { result = input.Text.ToString(); Application.RequestStop(); };
        var cancel = new Button("Cancel");
        cancel.Clicked += () => Application.RequestStop();
        dialog.Add(prompt, input);
        dialog.AddButton(accept);
        dialog.AddButton(cancel);
        input.SetFocus();
        Application.Run(dialog);
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            _workbench.Changed -= OnChanged;
            Application.MainLoop?.RemoveTimeout(_activityTimer);
        }
        base.Dispose(disposing);
    }
}
