using System;
using System.Drawing;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal sealed class SelectField : PickerField
    {
        private readonly string[] items;
        private readonly OptionList options;
        private int selectedIndex;
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value < 0 || value >= items.Length) throw new ArgumentOutOfRangeException("value");
                if (value == selectedIndex) return;
                selectedIndex = value; Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public string SelectedItem
        {
            get { return items[selectedIndex]; }
            set { int index = Array.IndexOf(items, value); if (index >= 0) SelectedIndex = index; }
        }
        public SelectField(params string[] items)
        {
            if (items == null || items.Length == 0) throw new ArgumentException("At least one option is required.", "items");
            this.items = (string[])items.Clone();
            options = new OptionList(this.items);
            options.SelectionChanged += delegate { SelectedIndex = options.SelectedIndex; CloseDropDown(); Focus(); };
            SetPopupContent(options);
        }
        protected override void PrepareDropDown() { options.SetSelection(selectedIndex); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Home || keyData == Keys.End)
            {
                SelectedIndex = keyData == Keys.Home ? 0 : keyData == Keys.End ? items.Length - 1 : Math.Max(0, Math.Min(items.Length - 1, selectedIndex + (keyData == Keys.Up ? -1 : 1)));
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, SelectedItem, Font, new Rectangle(10, 0, Width - 36, Height), Enabled ? UiTheme.Text : UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class OptionList : Control
    {
        private readonly string[] items;
        private int active;
        private const int Row = 34, Inset = 5;
        public int SelectedIndex { get; private set; }
        public event EventHandler SelectionChanged;
        public OptionList(string[] items)
        {
            this.items = items; Size = new Size(160, Row * items.Length + Inset * 2);
            BackColor = UiTheme.Surface; ForeColor = UiTheme.Text; Font = UiTheme.Font(9F); Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            AccessibleName = "선택 목록";
        }
        public void SetSelection(int index) { SelectedIndex = active = index; Invalidate(); }
        private void Choose(int index)
        {
            SelectedIndex = index;
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = (e.Y - Inset) / Row;
            if (e.Y >= Inset && index >= 0 && index < items.Length && active != index) { active = index; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { active = SelectedIndex; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); int index = (e.Y - Inset) / Row;
            if (e.Button == MouseButtons.Left && e.Y >= Inset && index >= 0 && index < items.Length) Choose(index);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.Enter || key == Keys.Space || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) active = Math.Max(0, Math.Min(items.Length - 1, active + (e.KeyCode == Keys.Up ? -1 : 1)));
            else if (e.KeyCode == Keys.Home || e.KeyCode == Keys.End) active = e.KeyCode == Keys.Home ? 0 : items.Length - 1;
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Choose(active);
            else { base.OnKeyDown(e); return; }
            Invalidate(); e.Handled = e.SuppressKeyPress = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle box = new Rectangle(Inset, Inset + i * Row, Width - Inset * 2, Row);
                if (i == active) using (var path = UiTheme.Round(box, UiTheme.CornerRadius)) using (var fill = new SolidBrush(UiTheme.Elevated)) e.Graphics.FillPath(fill, path);
                TextRenderer.DrawText(e.Graphics, items[i], Font, new Rectangle(box.Left + 8, box.Top, box.Width - 34, box.Height), i == SelectedIndex ? UiTheme.Accent : UiTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (i == SelectedIndex)
                    using (var pen = new Pen(UiTheme.Accent, 1.5F)) e.Graphics.DrawLines(pen, new Point[] { new Point(box.Right - 19, box.Top + 17), new Point(box.Right - 16, box.Top + 20), new Point(box.Right - 10, box.Top + 13) });
            }
        }
    }
}
