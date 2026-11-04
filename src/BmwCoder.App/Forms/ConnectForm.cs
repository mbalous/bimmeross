using System;
using System.Drawing;
using System.Windows.Forms;
using BmwCoder.Core.Models;

namespace BmwCoder.App.Forms;

public sealed class ConnectForm : Form
{
    private readonly TextBox _txtHost;
    private readonly TextBox _txtPort;
    private readonly Button _btnConnect;
    private readonly Button _btnCancel;
    private readonly AppSettings _settings;

    public string Host => _txtHost.Text.Trim();
    public int Port => int.TryParse(_txtPort.Text.Trim(), out int p) ? p : 6801;

    public ConnectForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Connect to BMW Vehicle (ENET)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 340);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 7,
            ColumnCount = 2
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65f));

        var lblHost = new Label { Text = "ENET Host IP:", Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _txtHost = new TextBox { Text = string.IsNullOrEmpty(_settings.Host) ? "169.254.16.17" : _settings.Host, Anchor = AnchorStyles.Left | AnchorStyles.Right };

        var lblPort = new Label { Text = "TCP Port (HSFZ):", Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _txtPort = new TextBox { Text = _settings.Port > 0 ? _settings.Port.ToString() : "6801", Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var psdz = new TextBox { Text = settings.PsdzDataPath, Dock = DockStyle.Fill };
        var series = new TextBox { Text = settings.PsdzSeries, Dock = DockStyle.Fill };
        var ista = new TextBox { Text = settings.IstaPath, Dock = DockStyle.Fill };

        var panelButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 15, 0, 0)
        };

        _btnConnect = new Button { Text = "Connect", DialogResult = DialogResult.OK, Width = 90, Height = 32 };
        _btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 32 };

        _btnConnect.Click += (s, e) =>
        {
            _settings.Host = Host;
            _settings.Port = Port;
            _settings.PsdzDataPath = psdz.Text.Trim();
            _settings.PsdzSeries = series.Text.Trim();
            _settings.IstaPath = ista.Text.Trim();
            _settings.Save();
        };

        panelButtons.Controls.Add(_btnCancel);
        panelButtons.Controls.Add(_btnConnect);

        table.Controls.Add(lblHost, 0, 0);
        table.Controls.Add(_txtHost, 1, 0);
        table.Controls.Add(lblPort, 0, 1);
        table.Controls.Add(_txtPort, 1, 1);
        table.Controls.Add(new Label { Text = "PSdZData (optional):", AutoSize = true }, 0, 2);
        table.Controls.Add(psdz, 1, 2);
        table.Controls.Add(new Label { Text = "Main series:", AutoSize = true }, 0, 3);
        table.Controls.Add(series, 1, 3);
        table.Controls.Add(new Label { Text = "ISTA folder (optional):", AutoSize = true }, 0, 4);
        table.Controls.Add(ista, 1, 4);
        table.Controls.Add(new Label { Text = "Select the data series matching your car; names and layouts are loaded at runtime.\nBlank data paths use raw/built-in fallbacks. Data does not enable writes.", AutoSize = true }, 0, 5);
        table.SetColumnSpan(table.GetControlFromPosition(0, 5)!, 2);
        table.Controls.Add(panelButtons, 1, 6);

        Controls.Add(table);
        AcceptButton = _btnConnect;
        CancelButton = _btnCancel;
    }
}
