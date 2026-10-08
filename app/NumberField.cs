using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal sealed class NumberField : InputField
    {
        private readonly TextBox text = new TextBox();
        private readonly int minimum, maximum;
        private int value, hovered;
        public event EventHandler ValueChanged;
        public int Value
        {
            get { CommitText(); return value; }
            set
            {
                int next = Math.Max(minimum, Math.Min(maximum, value));
                bool changed = this.value != next; this.value = next;
                text.Text = next.ToString(CultureInfo.InvariantCulture);
                if (changed && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        public NumberField(int minimum, int maximum, int initial)
        {
            if (minimum < 0 || maximum < minimum) throw new ArgumentOutOfRangeException("minimum");
            this.minimum = minimum; this.maximum = maximum; Width = 90;
            text.BorderStyle = BorderStyle.None; text.BackColor = UiTheme.Elevated; text.ForeColor = UiTheme.Text;
            text.Font = Font; text.TextAlign = HorizontalAlignment.Left;
            text.MaxLength = maximum.ToString(CultureInfo.InvariantCulture).Length; text.AccessibleName = "분석 기간 일수";
            text.Leave += delegate { CommitText(); };
            text.KeyPress += delegate(object sender, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9')) e.Handled = true; };
            text.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) Adjust(e.KeyCode == Keys.Up ? 1 : -1);
                else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) Adjust(e.KeyCode == Keys.PageUp ? 10 : -10);
                else if (e.KeyCode == Keys.Enter) CommitText();
                else if (e.KeyCode == Keys.Escape) text.Text = value.ToString(CultureInfo.InvariantCulture);
                else return;
                e.Handled = e.SuppressKeyPress = true;
            };
            text.MouseWheel += delegate(object sender, MouseEventArgs e) { HandleWheel(e); };
            Controls.Add(text); Value = initial; ArrangeText();
        }
        private void CommitText()
        {
            int entered;
            if (int.TryParse(text.Text, NumberStyles.None, CultureInfo.InvariantCulture, out entered)) Value = entered;
            else text.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        private void Adjust(int step) { CommitText(); Value = step > 0 ? value + Math.Min(step, maximum - value) : value + Math.Max(step, minimum - value); }
        private void ArrangeText() { UiTheme.CenterInputText(text, ClientSize, 10, 34); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeText(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (text != null) { text.Font = Font; ArrangeText(); } }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Enter && text.ContainsFocus) { CommitText(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        private int HitArrow(Point point) { return point.X >= Width - 27 && ClientRectangle.Contains(point) ? point.Y < Height / 2 ? 1 : -1 : 0; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int next = HitArrow(e.Location); if (next != hovered) { hovered = next; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hovered = 0; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (!Enabled || e.Button != MouseButtons.Left) return;
            text.Focus(); int step = HitArrow(e.Location); if (step != 0) Adjust(step);
        }
        private void HandleWheel(MouseEventArgs e)
        {
            if (!Enabled || !ContainsFocus || e.Delta == 0) return;
            Adjust(e.Delta > 0 ? 1 : -1);
            var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
        }
        protected override void OnMouseWheel(MouseEventArgs e) { HandleWheel(e); base.OnMouseWheel(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int left = Width - 27, center = left + 13;
            if (Enabled && hovered != 0)
                using (var fill = new SolidBrush(UiTheme.Border)) e.Graphics.FillRectangle(fill, left + 1, hovered > 0 ? 2 : Height / 2, 24, Height / 2 - 2);
            using (var pen = new Pen(UiTheme.Border))
            {
                e.Graphics.DrawLine(pen, left, 6, left, Height - 7);
                e.Graphics.DrawLine(pen, left + 5, Height / 2, Width - 6, Height / 2);
            }
            for (int direction = 1; direction >= -1; direction -= 2)
            {
                int y = direction > 0 ? Height / 4 : Height * 3 / 4;
                using (var pen = new Pen(!Enabled ? UiTheme.Border : hovered == direction ? UiTheme.Accent : UiTheme.Muted, 1.5F))
                    e.Graphics.DrawLines(pen, new Point[] { new Point(center - 3, y + direction), new Point(center, y - direction * 2), new Point(center + 3, y + direction) });
            }
        }
    }
}
