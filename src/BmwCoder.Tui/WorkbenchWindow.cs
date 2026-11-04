using System.Globalization;
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
    private string _screen = "Vehicle";
    private bool _closed;
    private EcuInfo[] _visibleEcus = Array.Empty<EcuInfo>();
    private XmlField[] _visibleFields = Array.Empty<XmlField>();
    private CafField[] _visibleCafFields = Array.Empty<CafField>();
    private CodingParameter[] _visibleParameters = Array.Empty<CodingParameter>();
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
                new MenuItem("_Find gateway", "", FindGateway),
                new MenuItem("_Connect (F2)", "", () => Start(_workbench.ConnectAsync)),
                new MenuItem("_Discover (F3)", "", () => Start(_workbench.DiscoverAsync)),
                new MenuItem("D_isconnect", "", () => Start(_workbench.DisconnectAsync)),
                new MenuItem("_Read DID", "", ReadDid),
                new MenuItem("Read _faults", "", ReadFaults),
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
                new MenuItem("E_xport original bytes", "", () => SaveFile("New coding file", path => _workbench.ExportCodingAsync(path)))
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
                new MenuItem("_Inspect directory", "", () => PromptPath("Backup directory", path => _workbench.InspectBackupAsync(path)))
            }),
            new MenuBarItem("_Settings", new[]
            {
                new MenuItem("_Connection", "", ConnectionSettings),
                new MenuItem("_Local tool paths", "", ToolPaths),
                new MenuItem("_Inspect installations", "", () => Start(_workbench.InspectInstallationsAsync)),
                new MenuItem("_Save settings", "", () => Start(() => _workbench.SaveSettingsAsync(_settingsPath)))
            }),
            new MenuBarItem("_Help", new[]
            {
                new MenuItem("_About / limitations", "", () => ShowScreen("Help")),
                new MenuItem("_Cancel operation (F4)", "", _workbench.Cancel)
            })
        });
        _header = new Label("") { X = 0, Y = 1, Width = Dim.Fill(), Height = 2 };
        _body = new FrameView("Vehicle") { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(6) };
        var searchLabel = new Label("Search:") { X = 0, Y = 0 };
        _search = new TextField("") { X = 8, Y = 0, Width = Dim.Fill() };
        _search.TextChanged += _ => RefreshState();
        _items = new ListView { X = 0, Y = 2, Width = Dim.Percent(35), Height = Dim.Fill() };
        _details = new TextView { X = Pos.Right(_items), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        _body.Add(searchLabel, _search, _items, _details);
        _items.SelectedItemChanged += _ => ShowSelection();
        _items.OpenSelectedItem += _ => { if (_screen == "FA/VO") EditFa(); };
        _logs = new TextView { X = 0, Y = Pos.Bottom(_body), Width = Dim.Fill(), Height = 4, ReadOnly = true, WordWrap = false, CanFocus = false };
        _status = new Label("") { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        var keys = new StatusBar(new[]
        {
            new StatusItem(Key.F2, "~F2~ Connect", () => Start(_workbench.ConnectAsync)),
            new StatusItem(Key.F3, "~F3~ Discover", () => Start(_workbench.DiscoverAsync)),
            new StatusItem(Key.F4, "~F4~ Cancel", _workbench.Cancel),
            new StatusItem(Key.CtrlMask | Key.Q, "~Ctrl+Q~ Quit", RequestQuit)
        });
        _smallTerminal = new Label("Minimum terminal size: 80 x 24. Resize the terminal; Ctrl+Q exits.")
        { X = 0, Y = 3, Width = Dim.Fill(), Height = 3, Visible = false };
        Add(menu, _header, _body, _logs, _status, keys, _smallTerminal);
        LayoutComplete += _ => UpdateSize();
        _workbench.Changed += OnChanged;
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
            case Key.F3: Start(_workbench.DiscoverAsync); return true;
            case Key.F4: _workbench.Cancel(); return true;
            case Key.CtrlMask | Key.Q: RequestQuit(); return true;
            default: return base.ProcessKey(keyEvent);
        }
    }

    public void RefreshState()
    {
        _header.Text = $"BimmerOSS | {(_workbench.IsDemo ? "DEMO (no sockets)" : "LIVE")} | READ-ONLY PREVIEW\n" +
            $"{(_workbench.IsConnected ? "Connected" : "Disconnected")} | VIN: {_workbench.Vin ?? "(unknown)"} | FDL writes: unavailable";
        _status.Text = _workbench.Status;
        _body.Title = _screen;
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
                "F2: connect; F3: discover; F4: cancel; Ctrl+Q: exit.\n\n" +
                "Default mode is synthetic demo. Use --live for explicit read-only vehicle connections.\n" +
                "Import an FA XML document, select FA/VO > Show fields, then Edit or Enter.\n" +
                "FA edits are offline and syntax-checked only. Exports never overwrite existing files.\n\n" +
                PreviewCapabilities.FaReadUnavailable + "\n\n" + PreviewCapabilities.CodingUnavailable,
            _ => _workbench.Details
        };
        _logs.Text = string.Join("\n", _workbench.Logs.TakeLast(4));
        SetNeedsDisplay();
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

    private void ToolPaths()
    {
        if (_workbench.IsBusy)
        {
            MessageBox.ErrorQuery("Busy", "Wait for the current operation or press F4 to cancel.", "OK");
            return;
        }
        string? psdz = Prompt("Local tools", "PSdZData directory", _workbench.Settings.PsdzDataPath);
        if (psdz == null)
            return;
        string? esys = Prompt("Local tools", "E-Sys directory", _workbench.Settings.EsysPath);
        if (esys == null)
            return;
        string? ista = Prompt("Local tools", "ISTA directory", _workbench.Settings.IstaPath);
        if (ista == null)
            return;
        string? java = Prompt("Local tools", "64-bit Java 8 java.exe (explicit path)", _workbench.Settings.JavaPath);
        if (java == null)
            return;
        _workbench.Settings.PsdzDataPath = psdz;
        _workbench.Settings.EsysPath = esys;
        _workbench.Settings.IstaPath = ista;
        _workbench.Settings.JavaPath = java;
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
        }
        base.Dispose(disposing);
    }
}
