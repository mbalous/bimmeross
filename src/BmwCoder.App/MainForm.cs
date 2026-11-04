using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using BmwCoder.App.Forms;
using BmwCoder.Core;
using BmwCoder.Core.Models;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace BmwCoder.App;

public sealed class RichTextBoxSink : ILogEventSink
{
    private readonly RichTextBox _box;

    public RichTextBoxSink(RichTextBox box)
    {
        _box = box;
    }

    public void Emit(LogEvent logEvent)
    {
        if (_box.IsDisposed) return;

        string message = $"[{logEvent.Timestamp:HH:mm:ss}] [{logEvent.Level.ToString().Substring(0, 3).ToUpper()}] {logEvent.RenderMessage()}\n";

        Color color = logEvent.Level switch
        {
            LogEventLevel.Error or LogEventLevel.Fatal => Color.Crimson,
            LogEventLevel.Warning => Color.DarkOrange,
            LogEventLevel.Information => Color.LightSkyBlue,
            _ => Color.Silver
        };

        try
        {
            if (_box.InvokeRequired)
            {
                _box.BeginInvoke(new Action(() => AppendColoredText(message, color)));
            }
            else
            {
                AppendColoredText(message, color);
            }
        }
        catch { }
    }

    private void AppendColoredText(string text, Color color)
    {
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.SelectionColor = color;
        _box.AppendText(text);
        _box.SelectionColor = _box.ForeColor;
        _box.ScrollToCaret();
    }
}

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private ILoggerFactory _loggerFactory = null!;
    private ILogger<MainForm> _logger = null!;
    private CodingSession? _session;

    private readonly ListView _lstEcus;
    private readonly RichTextBox _txtLogs;
    private readonly ToolStripButton _btnConnect;
    private readonly ToolStripButton _btnRefreshEcus;
    private readonly ToolStripButton _btnDiagnostics;
    private readonly ToolStripButton _btnBackupAll;
    private readonly ToolStripButton _btnRestore;
    private readonly ToolStripButton _btnOpenBackupFolder;

    private readonly ToolStripStatusLabel _statusConnection;
    private readonly ToolStripStatusLabel _statusVin;

    private readonly List<EcuInfo> _discoveredEcus = new();

    public MainForm()
    {
        _settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(_settings.PsdzDataPath) && Directory.Exists(@"C:\EC-Apps\ISTA\PSdZ\data\psdzdata"))
            _settings.PsdzDataPath = @"C:\EC-Apps\ISTA\PSdZ\data\psdzdata";
        if (string.IsNullOrWhiteSpace(_settings.IstaPath) && File.Exists(@"C:\EC-Apps\ISTA\SQLiteDBs\xmlvalueprimitive_ENGB.sqlite"))
            _settings.IstaPath = @"C:\EC-Apps\ISTA";
        _settings.Save();

        Text = "BMW F-Series Coding Tool (MVP) — ENET / HSFZ";
        Size = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        // 1. ToolStrip
        var toolStrip = new ToolStrip { ImageScalingSize = new Size(20, 20), RenderMode = ToolStripRenderMode.System };
        _btnConnect = new ToolStripButton("Connect...") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _btnRefreshEcus = new ToolStripButton("Discover ECUs") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnDiagnostics = new ToolStripButton("Read-only Diagnostics...") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnBackupAll = new ToolStripButton("Backup All") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnRestore = new ToolStripButton("Restore...") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnOpenBackupFolder = new ToolStripButton("Open Backups") { DisplayStyle = ToolStripItemDisplayStyle.Text };

        _btnConnect.Click += async (s, e) => await OnConnectClickAsync();
        _btnRefreshEcus.Click += async (s, e) => await DiscoverEcusAsync();
        _btnDiagnostics.Click += (s, e) => OpenDiagnostics();
        _btnBackupAll.Click += async (s, e) => await BackupAllAsync();
        _btnRestore.Click += async (s, e) => await RestoreWithBusyStateAsync();
        _btnOpenBackupFolder.Click += (s, e) => OpenBackupFolder();

        toolStrip.Items.Add(_btnConnect);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_btnRefreshEcus);
        toolStrip.Items.Add(_btnDiagnostics);
        toolStrip.Items.Add(_btnBackupAll);
        toolStrip.Items.Add(_btnRestore);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_btnOpenBackupFolder);

        // 2. Split Container (Left: ECUs, Right: Logs)
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 460
        };

        // Left Pane: ECU List
        _lstEcus = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            ShowItemToolTips = true
        };
        _lstEcus.Columns.Add("Addr", 55);
        _lstEcus.Columns.Add("Name / ODX candidates", 190);
        _lstEcus.Columns.Add("Probe status", 135);
        _lstEcus.Columns.Add("RTT (ms)", 75);
        _lstEcus.Columns.Add("Writable", 75);
        _lstEcus.Columns.Add("Description", 150);
        _lstEcus.Columns.Add("Inventory source", 200);
        _lstEcus.Columns.Add("Probe details", 300);
        _lstEcus.DoubleClick += (s, e) => OpenSelectedEcu();

        split.Panel1.Controls.Add(_lstEcus);

        // Right Pane: Logs
        _txtLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9.5f, FontStyle.Regular)
        };
        split.Panel2.Controls.Add(_txtLogs);

        // Configure Serilog with RichTextBox sink + file sink
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BmwCoder", "logs");
        Directory.CreateDirectory(logDir);
        string logFilePath = Path.Combine(logDir, "session-.log");

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new RichTextBoxSink(_txtLogs))
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
        _loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(serilogLogger, dispose: true);
        _logger = _loggerFactory.CreateLogger<MainForm>();

        _logger.LogInformation("BMW F-Series Coding Tool initialized.");
        _logger.LogInformation("Executable: {Executable}", Application.ExecutablePath);
        _logger.LogInformation("Core build: {BuildId}", typeof(CodingSession).Assembly.ManifestModule.ModuleVersionId);

        // 3. StatusStrip
        var statusStrip = new StatusStrip();
        _statusConnection = new ToolStripStatusLabel("Disconnected") { BorderSides = ToolStripStatusLabelBorderSides.Right };
        _statusVin = new ToolStripStatusLabel("VIN: None") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        statusStrip.Items.Add(_statusConnection);
        statusStrip.Items.Add(_statusVin);

        Controls.Add(split);
        Controls.Add(toolStrip);
        Controls.Add(statusStrip);
    }

    private async Task OnConnectClickAsync()
    {
        if (_session != null)
        {
            _session.Close();
            _session.Dispose();
            _session = null;
            _btnConnect.Text = "Connect...";
            _btnRefreshEcus.Enabled = false;
            _btnDiagnostics.Enabled = false;
            _btnBackupAll.Enabled = false;
            _btnRestore.Enabled = false;
            _statusConnection.Text = "Disconnected";
            _statusVin.Text = "VIN: None";
            _logger.LogInformation("Disconnected from vehicle.");
            return;
        }

        using var connectForm = new ConnectForm(_settings);
        if (connectForm.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            _btnConnect.Enabled = false;
            var connection = new HsfzConnection(_loggerFactory.CreateLogger<HsfzConnection>());
            var uds = new UdsClient(connection, logger: _loggerFactory.CreateLogger<UdsClient>());
            _session = new CodingSession(connection, uds, logger: _loggerFactory.CreateLogger<CodingSession>());
            if (!string.IsNullOrWhiteSpace(_settings.PsdzDataPath))
            {
                _session.DiagnosticData = await Task.Run(() => PsdzDiagnosticData.Load(_settings.PsdzDataPath, _settings.PsdzSeries));
                _logger.LogInformation("ODX data: {Source}. Names are address candidates, not verified ECU variants.", _session.DiagnosticData.SourceDirectory);
            }
            if (!string.IsNullOrWhiteSpace(_settings.IstaPath))
                _session.FaultDescriptions = new IstaFaultLookup(_settings.IstaPath);
            await _session.ConnectAsync(connectForm.Host, connectForm.Port);

            _btnConnect.Text = "Disconnect";
            _btnRefreshEcus.Enabled = true;
            _btnBackupAll.Enabled = true;
            _btnRestore.Enabled = true;
            _statusConnection.Text = $"Connected ({connectForm.Host}:{connectForm.Port})";

            // Trigger discovery automatically on connect
            await DiscoverEcusAsync();
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            _btnDiagnostics.Enabled = false;
            MessageBox.Show(this, $"Failed to connect: {ex.Message}", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            _btnConnect.Enabled = true;
        }
    }

    private async Task DiscoverEcusAsync()
    {
        if (_session == null) return;

        _btnRefreshEcus.Enabled = false;
        _btnDiagnostics.Enabled = false;
        _btnConnect.Enabled = false;
        _btnBackupAll.Enabled = false;
        _btnRestore.Enabled = false;
        _lstEcus.Enabled = false;
        _lstEcus.Items.Clear();
        _discoveredEcus.Clear();

        try
        {
            Cursor = Cursors.WaitCursor;
            var progress = new Progress<(EcuInfo Info, int Current, int Total)>(p =>
            {
                var item = new ListViewItem($"0x{p.Info.Address:X2}");
                item.SubItems.Add(p.Info.Name);
                item.SubItems.Add(p.Info.Responds ? p.Info.ProbeError == null ? "Responding" : "Responding (NRC)" : "Listed; no probe reply");
                item.SubItems.Add(p.Info.RttMs?.ToString() ?? "-");
                item.SubItems.Add(p.Info.IsWritable ? "Yes" : "Locked");
                item.SubItems.Add(p.Info.Description);
                item.SubItems.Add(p.Info.DiscoverySource);
                item.SubItems.Add(p.Info.ProbeError ?? "TesterPresent accepted");
                item.ToolTipText = $"{p.Info.Name}\n{p.Info.DiscoverySource}\nName source: {p.Info.NameSource}\n{p.Info.ProbeError}";

                if (p.Info.Responds)
                {
                    item.ForeColor = Color.DarkGreen;
                    item.Font = new Font(_lstEcus.Font, FontStyle.Bold);
                }
                else
                {
                    item.ForeColor = Color.Gray;
                }

                _lstEcus.Items.Add(item);
            });

            var ecus = await _session.DiscoverEcusAsync(progress, probeTimeout: TimeSpan.FromMilliseconds(500));
            _discoveredEcus.AddRange(ecus);
            _statusConnection.Text = $"Connected; {ecus.Count} listed, {ecus.Count(e => e.Responds)} responding";

            if (!string.IsNullOrEmpty(_session.CurrentVin))
            {
                _statusVin.Text = $"VIN: {_session.CurrentVin}";
                _settings.LastVin = _session.CurrentVin;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ECU Discovery failed: {ex.Message}", "Discovery Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            _btnRefreshEcus.Enabled = true;
            _btnDiagnostics.Enabled = true;
            _btnConnect.Enabled = true;
            _btnBackupAll.Enabled = true;
            _btnRestore.Enabled = true;
            _lstEcus.Enabled = true;
        }
    }

    private void OpenDiagnostics(byte? address = null)
    {
        if (_session == null) return;
        byte ecu = address ?? (_lstEcus.SelectedIndices.Count > 0 &&
            _lstEcus.SelectedIndices[0] < _discoveredEcus.Count
            ? _discoveredEcus[_lstEcus.SelectedIndices[0]].Address : (byte)0x12);
        using var diagnostics = new DiagnosticsForm(_session, _settings, ecu, _discoveredEcus);
        diagnostics.ShowDialog(this);
    }

    private void OpenSelectedEcu()
    {
        if (_lstEcus.SelectedIndices.Count == 0 || _session == null) return;

        int index = _lstEcus.SelectedIndices[0];
        if (index < 0 || index >= _discoveredEcus.Count) return;

        var ecu = _discoveredEcus[index];
        if (!ecu.Responds)
        {
            MessageBox.Show(this, $"{ecu.Name} (0x{ecu.Address:X2}) is currently offline.", "ECU Offline", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ecu.IsWritable)
        {
            OpenDiagnostics(ecu.Address);
            return;
        }

        using var editor = new BlockEditorForm(_session, ecu);
        editor.ShowDialog(this);
    }

    private async Task BackupAllAsync()
    {
        if (_session == null) return;

        var onlineEcus = _discoveredEcus.FindAll(e => e.Responds && e.SuggestedDids.Length > 0);
        if (onlineEcus.Count == 0)
        {
            MessageBox.Show(this, "No online ECUs with configured coding DIDs were found.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnBackupAll.Enabled = false;
        _btnDiagnostics.Enabled = false;
        _btnRefreshEcus.Enabled = false;
        _btnConnect.Enabled = false;
        _btnRestore.Enabled = false;
        _lstEcus.Enabled = false;
        try
        {
            Cursor = Cursors.WaitCursor;
            int backedUpCount = 0;

            foreach (var ecu in onlineEcus)
            {
                foreach (var did in ecu.SuggestedDids)
                {
                    try
                    {
                        await _session.ReadBlockAsync(ecu.Address, did);
                        backedUpCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not backup {Ecu} DID 0x{Did:X4}: {Message}", ecu.Name, did, ex.Message);
                    }
                }
            }

            MessageBox.Show(this, $"Full backup completed! Successfully saved {backedUpCount} block(s).", "Backup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            Cursor = Cursors.Default;
            _btnBackupAll.Enabled = true;
            _btnDiagnostics.Enabled = true;
            _btnRefreshEcus.Enabled = true;
            _btnConnect.Enabled = true;
            _btnRestore.Enabled = true;
            _lstEcus.Enabled = true;
        }
    }

    private async Task RestoreAsync()
    {
        if (_session == null) return;

        using var fbd = new FolderBrowserDialog
        {
            SelectedPath = _session.BackupManager.BaseDirectory,
            Description = "Select a backup session folder to restore"
        };

        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        var manifest = _session.BackupManager.LoadManifest(fbd.SelectedPath);
        if (manifest == null || manifest.Entries.Count == 0)
        {
            MessageBox.Show(this, "Selected folder does not contain a valid manifest.json backup.", "Invalid Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Found {manifest.Entries.Count} block(s) in backup for VIN {manifest.Vin} ({manifest.Timestamp:g}).\n" +
            "Do you want to proceed with restore? You will be prompted per block.",
            "Confirm Restore",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        int restored = 0;
        foreach (var entry in manifest.Entries)
        {
            var block = _session.BackupManager.LoadBlock(fbd.SelectedPath, entry.EcuAddress, entry.Did);
            if (block == null) continue;

            var res = MessageBox.Show(this,
                $"Restore {entry.EcuName} (0x{entry.EcuAddress:X2}) DID 0x{entry.Did:X4}?\n" +
                $"File SHA256: {entry.Sha256}",
                "Confirm Block Restore",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (res == DialogResult.Cancel) break;
            if (res == DialogResult.No) continue;

            try
            {
                await _session.WriteBlockAsync(block);
                restored++;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to restore {entry.EcuName}: {ex.Message}", "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        MessageBox.Show(this, $"Restore session finished. {restored} block(s) restored.", "Restore Finished", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RestoreWithBusyStateAsync()
    {
        _btnConnect.Enabled = false;
        _btnRefreshEcus.Enabled = false;
        _btnDiagnostics.Enabled = false;
        _btnBackupAll.Enabled = false;
        _btnRestore.Enabled = false;
        _lstEcus.Enabled = false;
        try
        {
            await RestoreAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnConnect.Enabled = true;
            _btnRefreshEcus.Enabled = _session != null;
            _btnDiagnostics.Enabled = _session != null;
            _btnBackupAll.Enabled = _session != null;
            _btnRestore.Enabled = _session != null;
            _lstEcus.Enabled = true;
        }
    }

    private void OpenBackupFolder()
    {
        string path = _session?.BackupManager.BaseDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BmwCoder", "backups");

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _session?.Close();
        _session?.Dispose();
        _loggerFactory.Dispose();
        base.OnFormClosing(e);
    }
}
