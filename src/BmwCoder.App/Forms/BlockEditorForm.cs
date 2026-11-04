using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BmwCoder.App.Controls;
using BmwCoder.Coding;
using BmwCoder.Core;
using BmwCoder.Core.Models;

namespace BmwCoder.App.Forms;

public sealed class BlockEditorForm : Form
{
    private readonly CodingSession _session;
    private readonly EcuInfo _ecu;

    private readonly ComboBox _cmbDid;
    private readonly Button _btnRead;
    private readonly Button _btnVerifyChecksum;
    private readonly Button _btnApplyChecksum;
    private readonly Button _btnRevert;
    private readonly Button _btnSaveToFile;
    private readonly Button _btnWrite;

    private readonly HexEditorControl _hexEditor;
    private readonly Label _lblChecksumStatus;
    private readonly Label _lblDiffCount;

    private CodingBlock? _originalBlock;
    private CodingBlock? _currentBlock;

    public BlockEditorForm(CodingSession session, EcuInfo ecu)
    {
        _session = session;
        _ecu = ecu;

        Text = $"Coding Block Editor — [{_ecu.Name}] (0x{_ecu.Address:X2})";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(880, 600);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        // Top Toolbar panel
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 45,
            Padding = new Padding(10, 8, 10, 5)
        };

        var lblDid = new Label { Text = "DID (Hex):", AutoSize = true, Margin = new Padding(0, 5, 5, 0) };
        _cmbDid = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDown };
        foreach (var did in _ecu.SuggestedDids)
        {
            _cmbDid.Items.Add($"0x{did:X4}");
        }
        if (_cmbDid.Items.Count > 0) _cmbDid.SelectedIndex = 0;
        else _cmbDid.Text = "0x3000";

        _btnRead = new Button { Text = "Read Block", Width = 95, Height = 28 };
        _btnVerifyChecksum = new Button { Text = "Verify Checksum", Width = 120, Height = 28, Enabled = false };
        _btnApplyChecksum = new Button { Text = "Apply Checksum", Width = 120, Height = 28, Enabled = false };
        _btnRevert = new Button { Text = "Revert", Width = 75, Height = 28, Enabled = false };
        _btnSaveToFile = new Button { Text = "Save to File", Width = 95, Height = 28, Enabled = false };
        _btnWrite = new Button
        {
            Text = "Write to ECU",
            Width = 110,
            Height = 28,
            BackColor = Color.Crimson,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Enabled = false
        };

        _btnRead.Click += async (s, e) => await ReadBlockAsync();
        _btnVerifyChecksum.Click += (s, e) => VerifyChecksum();
        _btnApplyChecksum.Click += (s, e) => ApplyChecksum();
        _btnRevert.Click += (s, e) => RevertData();
        _btnSaveToFile.Click += (s, e) => SaveToFile();
        _btnWrite.Click += async (s, e) => await WriteBlockAsync();

        topPanel.Controls.Add(lblDid);
        topPanel.Controls.Add(_cmbDid);
        topPanel.Controls.Add(_btnRead);
        topPanel.Controls.Add(_btnVerifyChecksum);
        topPanel.Controls.Add(_btnApplyChecksum);
        topPanel.Controls.Add(_btnRevert);
        topPanel.Controls.Add(_btnSaveToFile);
        topPanel.Controls.Add(_btnWrite);

        // Center Hex Editor
        _hexEditor = new HexEditorControl { Dock = DockStyle.Fill };
        _hexEditor.DataChanged += (s, e) => OnDataModified();

        // Bottom Status panel
        var bottomPanel = new StatusStrip();
        _lblChecksumStatus = new Label { Text = "Checksum: Not loaded", AutoSize = true, Margin = new Padding(10, 0, 20, 0) };
        _lblDiffCount = new Label { Text = "Modified: 0 bytes", AutoSize = true };

        var statusHost = new ToolStripControlHost(_lblChecksumStatus);
        var diffHost = new ToolStripControlHost(_lblDiffCount);
        bottomPanel.Items.Add(statusHost);
        bottomPanel.Items.Add(diffHost);

        Controls.Add(_hexEditor);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);
    }

    private ushort ParseSelectedDid()
    {
        string text = _cmbDid.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(2);
        }

        if (ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort result))
        {
            return result;
        }

        return 0x3000;
    }

    private async Task ReadBlockAsync()
    {
        ushort did = ParseSelectedDid();
        _btnRead.Enabled = false;

        try
        {
            Cursor = Cursors.WaitCursor;
            var block = await _session.ReadBlockAsync(_ecu.Address, did);
            _originalBlock = block.Clone();
            _currentBlock = block;

            _hexEditor.SetData(_currentBlock.RawData, _originalBlock.RawData);

            _btnVerifyChecksum.Enabled = true;
            _btnApplyChecksum.Enabled = true;
            _btnRevert.Enabled = true;
            _btnSaveToFile.Enabled = true;

            VerifyChecksum();
            UpdateDiffCount();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to read coding block: {ex.Message}", "Read Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            _btnRead.Enabled = true;
        }
    }

    private void OnDataModified()
    {
        UpdateDiffCount();
        VerifyChecksum();
    }

    private void UpdateDiffCount()
    {
        if (_originalBlock == null || _currentBlock == null)
        {
            _lblDiffCount.Text = "Modified: 0 bytes";
            return;
        }

        int diffs = 0;
        int minLen = Math.Min(_originalBlock.RawData.Length, _currentBlock.RawData.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (_originalBlock.RawData[i] != _currentBlock.RawData[i]) diffs++;
        }
        diffs += Math.Abs(_originalBlock.RawData.Length - _currentBlock.RawData.Length);

        _lblDiffCount.Text = $"Modified: {diffs} byte(s)";
    }

    private void VerifyChecksum()
    {
        if (_currentBlock == null) return;

        bool hasChecksum = _session.ChecksumRegistry.HasChecksum(_currentBlock.EcuAddress, _currentBlock.Did);
        if (!hasChecksum)
        {
            _lblChecksumStatus.Text = "Checksum: UNKNOWN (Writing locked)";
            _lblChecksumStatus.ForeColor = Color.DarkGoldenrod;
            _btnWrite.Enabled = false;
            return;
        }

        var def = _session.ChecksumRegistry.GetDefinition(_currentBlock.EcuAddress, _currentBlock.Did);
        string name = def?.Checksum.Name ?? "Registered";
        bool valid = _session.ChecksumRegistry.Verify(_currentBlock);

        if (valid)
        {
            _lblChecksumStatus.Text = $"Checksum: VALID ({name})";
            _lblChecksumStatus.ForeColor = Color.DarkGreen;
            _btnWrite.Enabled = _ecu.IsWritable;
        }
        else
        {
            _lblChecksumStatus.Text = $"Checksum: INVALID ({name})";
            _lblChecksumStatus.ForeColor = Color.DarkRed;
            _btnWrite.Enabled = false;
        }
    }

    private void ApplyChecksum()
    {
        if (_currentBlock == null) return;

        try
        {
            _session.ChecksumRegistry.Apply(_currentBlock);
            _hexEditor.Invalidate();
            VerifyChecksum();
            UpdateDiffCount();
            MessageBox.Show(this, "Checksum recalculated and applied successfully.", "Checksum Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to apply checksum: {ex.Message}", "Checksum Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RevertData()
    {
        if (_originalBlock == null || _currentBlock == null) return;

        Array.Copy(_originalBlock.RawData, _currentBlock.RawData, _originalBlock.RawData.Length);
        _hexEditor.SetData(_currentBlock.RawData, _originalBlock.RawData);
        VerifyChecksum();
        UpdateDiffCount();
    }

    private void SaveToFile()
    {
        if (_currentBlock == null) return;

        using var sfd = new SaveFileDialog
        {
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = $"{_ecu.Name}_0x{_currentBlock.Did:X4}.bin"
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllBytes(sfd.FileName, _currentBlock.RawData);
            MessageBox.Show(this, "Block saved to file successfully.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task WriteBlockAsync()
    {
        if (_currentBlock == null) return;

        int diffs = 0;
        if (_originalBlock != null)
        {
            diffs = _currentBlock.RawData.Zip(_originalBlock.RawData, (a, b) => a != b).Count(t => t);
        }

        var def = _session.ChecksumRegistry.GetDefinition(_currentBlock.EcuAddress, _currentBlock.Did);
        string checksumName = def?.Checksum.Name ?? "None";

        using var confirmForm = new WriteConfirmForm(_ecu.Name, _ecu.Address, _currentBlock.Did, diffs, checksumName);
        if (confirmForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _btnWrite.Enabled = false;
        try
        {
            Cursor = Cursors.WaitCursor;
            await _session.WriteBlockAsync(_currentBlock);

            _originalBlock = _currentBlock.Clone();
            _hexEditor.SetData(_currentBlock.RawData, _originalBlock.RawData);
            UpdateDiffCount();

            MessageBox.Show(this, "Coding block written and post-write verification passed successfully!", "Write Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Write failed: {ex.Message}", "Write Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            _btnWrite.Enabled = true;
        }
    }
}
