using System;
using System.Drawing;
using System.Windows.Forms;

namespace BmwCoder.App.Controls;

/// <summary>
/// Owner-drawn hex editor control optimized for coding blocks up to 64 KB.
/// Displays byte offsets, hex representation, ASCII view, and highlights diffs against baseline.
/// </summary>
public sealed class HexEditorControl : Control
{
    private byte[] _data = Array.Empty<byte>();
    private byte[]? _baseline;
    private int _selectedIndex = -1;
    private bool _highNibbleSelected = true;

    private readonly Font _font;
    private readonly int _lineHeight;
    private readonly int _charWidth;

    private readonly VScrollBar _vScrollBar;

    public event EventHandler? DataChanged;
    public event EventHandler? SelectionChanged;

    public byte[] Data => _data;
    public int SelectedIndex => _selectedIndex;

    public HexEditorControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.Selectable, true);

        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);
        _font = new Font("Consolas", 10f, FontStyle.Regular);

        using (var g = CreateGraphics())
        {
            var size = g.MeasureString("W", _font);
            _charWidth = (int)Math.Ceiling(size.Width);
            _lineHeight = (int)Math.Ceiling(size.Height);
        }

        _vScrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Visible = false
        };
        _vScrollBar.Scroll += (s, e) => Invalidate();
        Controls.Add(_vScrollBar);
    }

    public void SetData(byte[] data, byte[]? baseline = null)
    {
        _data = data ?? Array.Empty<byte>();
        _baseline = baseline;
        _selectedIndex = _data.Length > 0 ? 0 : -1;
        _highNibbleSelected = true;

        UpdateScrollBars();
        Invalidate();
    }

    private void UpdateScrollBars()
    {
        int totalRows = (_data.Length + 15) / 16;
        int visibleRows = Math.Max(1, ClientSize.Height / _lineHeight);

        if (totalRows > visibleRows)
        {
            _vScrollBar.Visible = true;
            _vScrollBar.Maximum = Math.Max(0, totalRows - visibleRows);
            _vScrollBar.LargeChange = visibleRows;
        }
        else
        {
            _vScrollBar.Visible = false;
            _vScrollBar.Value = 0;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        int startRow = _vScrollBar.Visible ? _vScrollBar.Value : 0;
        int visibleRows = (ClientSize.Height / _lineHeight) + 1;
        int totalRows = (_data.Length + 15) / 16;
        int endRow = Math.Min(totalRows, startRow + visibleRows);

        int hexStartX = 10 * _charWidth + 10;
        int asciiStartX = hexStartX + (16 * 3 + 2) * _charWidth;

        using var offsetBrush = new SolidBrush(Color.FromArgb(120, 120, 120));
        using var textBrush = new SolidBrush(ForeColor);
        using var diffBrush = new SolidBrush(Color.FromArgb(255, 140, 0));
        using var diffBgBrush = new SolidBrush(Color.FromArgb(80, 50, 0));
        using var selBgBrush = new SolidBrush(Color.FromArgb(0, 100, 200));

        for (int row = startRow; row < endRow; row++)
        {
            int y = (row - startRow) * _lineHeight + 4;
            int rowOffset = row * 16;

            // 1. Draw Offset Header (e.g. 00000010:)
            g.DrawString($"{rowOffset:X8}:", _font, offsetBrush, 5, y);

            // 2. Draw 16 hex bytes
            for (int col = 0; col < 16; col++)
            {
                int byteIndex = rowOffset + col;
                if (byteIndex >= _data.Length) break;

                byte b = _data[byteIndex];
                bool isDiff = _baseline != null && byteIndex < _baseline.Length && b != _baseline[byteIndex];
                bool isSelected = byteIndex == _selectedIndex;

                int byteX = hexStartX + (col * 3 + (col >= 8 ? 1 : 0)) * _charWidth;
                var byteRect = new Rectangle(byteX - 1, y, _charWidth * 2 + 2, _lineHeight);

                if (isSelected)
                {
                    g.FillRectangle(selBgBrush, byteRect);
                }
                else if (isDiff)
                {
                    g.FillRectangle(diffBgBrush, byteRect);
                }

                var byteBrush = isDiff ? diffBrush : textBrush;
                g.DrawString($"{b:X2}", _font, byteBrush, byteX, y);

                // 3. Draw ASCII representation
                int asciiX = asciiStartX + col * _charWidth;
                var asciiRect = new Rectangle(asciiX, y, _charWidth, _lineHeight);
                if (isSelected)
                {
                    g.FillRectangle(selBgBrush, asciiRect);
                }

                char c = (b >= 32 && b <= 126) ? (char)b : '.';
                g.DrawString(c.ToString(), _font, byteBrush, asciiX, y);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        int startRow = _vScrollBar.Visible ? _vScrollBar.Value : 0;
        int clickedRow = ((e.Y - 4) / _lineHeight) + startRow;

        int hexStartX = 10 * _charWidth + 10;
        int clickedCol = -1;

        for (int col = 0; col < 16; col++)
        {
            int byteX = hexStartX + (col * 3 + (col >= 8 ? 1 : 0)) * _charWidth;
            if (e.X >= byteX && e.X <= byteX + _charWidth * 3)
            {
                clickedCol = col;
                _highNibbleSelected = (e.X <= byteX + _charWidth);
                break;
            }
        }

        if (clickedCol >= 0)
        {
            int index = clickedRow * 16 + clickedCol;
            if (index >= 0 && index < _data.Length)
            {
                _selectedIndex = index;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down => true,
            _ => base.IsInputKey(keyData)
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _data.Length) return;

        if (e.KeyCode == Keys.Left)
        {
            if (!_highNibbleSelected)
            {
                _highNibbleSelected = true;
            }
            else if (_selectedIndex > 0)
            {
                _selectedIndex--;
                _highNibbleSelected = false;
            }
            Invalidate();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Right)
        {
            if (_highNibbleSelected)
            {
                _highNibbleSelected = false;
            }
            else if (_selectedIndex < _data.Length - 1)
            {
                _selectedIndex++;
                _highNibbleSelected = true;
            }
            Invalidate();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            if (_selectedIndex >= 16) _selectedIndex -= 16;
            Invalidate();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Down)
        {
            if (_selectedIndex + 16 < _data.Length) _selectedIndex += 16;
            Invalidate();
            e.Handled = true;
            return;
        }

        // Hex editing: 0-9, A-F
        int hexValue = -1;
        if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) hexValue = e.KeyCode - Keys.D0;
        else if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9) hexValue = e.KeyCode - Keys.NumPad0;
        else if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.F) hexValue = 10 + (e.KeyCode - Keys.A);

        if (hexValue >= 0)
        {
            byte current = _data[_selectedIndex];
            if (_highNibbleSelected)
            {
                _data[_selectedIndex] = (byte)((hexValue << 4) | (current & 0x0F));
                _highNibbleSelected = false;
            }
            else
            {
                _data[_selectedIndex] = (byte)((current & 0xF0) | (hexValue & 0x0F));
                _highNibbleSelected = true;
                if (_selectedIndex < _data.Length - 1) _selectedIndex++;
            }

            DataChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
