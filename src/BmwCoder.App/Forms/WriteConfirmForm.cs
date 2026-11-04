using System;
using System.Drawing;
using System.Windows.Forms;

namespace BmwCoder.App.Forms;

public sealed class WriteConfirmForm : Form
{
    private readonly TextBox _txtConfirmation;
    private readonly CheckBox _chkBackup;
    private readonly Button _btnConfirmWrite;
    private readonly Button _btnCancel;

    public WriteConfirmForm(string ecuName, byte ecuAddress, ushort did, int diffCount, string checksumName)
    {
        Text = "CONFIRM CODING BLOCK WRITE — SAFETY WARNING";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 360);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 6,
            ColumnCount = 1
        };

        var lblWarning = new Label
        {
            Text = "WARNING: WRITING TO ECU MEMORY\n\n" +
                   $"Target ECU:   {ecuName} (0x{ecuAddress:X2})\n" +
                   $"Block DID:    0x{did:X4}\n" +
                   $"Modified:     {diffCount} byte(s)\n" +
                   $"Checksum:     {checksumName}\n\n" +
                   "Writing incorrect values can cause ECU malfunction or require flashing recovery.\n" +
                   "An automatic backup of the current block has been saved to disk.",
            ForeColor = Color.DarkRed,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Top
        };

        _chkBackup = new CheckBox
        {
            Text = "I confirm a backup snapshot exists and I accept full responsibility.",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 10)
        };

        var lblPrompt = new Label
        {
            Text = "Type \"WRITE\" in uppercase below to unlock the Write button:",
            AutoSize = true
        };

        _txtConfirmation = new TextBox
        {
            Width = 200,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            CharacterCasing = CharacterCasing.Upper
        };

        var panelButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 15, 0, 0)
        };

        _btnConfirmWrite = new Button
        {
            Text = "Write to ECU",
            BackColor = Color.Crimson,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Enabled = false,
            DialogResult = DialogResult.OK,
            Width = 140,
            Height = 36
        };

        _btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = 36
        };

        _txtConfirmation.TextChanged += (s, e) => UpdateButtonState();
        _chkBackup.CheckedChanged += (s, e) => UpdateButtonState();

        panelButtons.Controls.Add(_btnCancel);
        panelButtons.Controls.Add(_btnConfirmWrite);

        table.Controls.Add(lblWarning, 0, 0);
        table.Controls.Add(_chkBackup, 0, 1);
        table.Controls.Add(lblPrompt, 0, 2);
        table.Controls.Add(_txtConfirmation, 0, 3);
        table.Controls.Add(panelButtons, 0, 4);

        Controls.Add(table);
        CancelButton = _btnCancel;
    }

    private void UpdateButtonState()
    {
        _btnConfirmWrite.Enabled = _chkBackup.Checked && _txtConfirmation.Text.Trim() == "WRITE";
    }
}
