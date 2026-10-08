using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal sealed class DateField : PickerField
    {
        private readonly TextBox text = new TextBox();
        private readonly CalendarView calendar = new CalendarView();
        private DateTime value = DateTime.Today;
        public event EventHandler ValueChanged;
        public DateTime Value
        {
            get { CommitText(); return value; }
            set { SetValue(value.Date); }
        }
        public DateField()
        {
            Width = 144;
            text.BorderStyle = BorderStyle.None; text.BackColor = UiTheme.Elevated; text.ForeColor = UiTheme.Text;
            text.Font = Font; text.MaxLength = 10; text.AccessibleName = "날짜 yyyy.MM.dd";
            text.Text = value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
            text.Leave += delegate { CommitText(); };
            Controls.Add(text); SetPopupContent(calendar);
            calendar.DateSelected += delegate { SetValue(calendar.SelectedDate); CloseDropDown(); text.Focus(); };
            ArrangeText();
        }
        private void CommitText()
        {
            DateTime date;
            if (DateTime.TryParseExact(text.Text, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) SetValue(date);
            else text.Text = value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        }
        private void SetValue(DateTime date)
        {
            bool changed = value != date; value = date; text.Text = value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
            if (changed && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
        protected override void PrepareDropDown() { CommitText(); calendar.SelectedDate = value; }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (text.ContainsFocus && keyData == Keys.Enter) { CommitText(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeText(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (text != null) { text.Font = Font; ArrangeText(); } }
        private void ArrangeText() { UiTheme.CenterInputText(text, ClientSize, 32, 25); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Enabled ? UiTheme.Muted : UiTheme.Border, 1.2F))
            {
                int y = Height / 2 - 6;
                e.Graphics.DrawRectangle(pen, 11, y, 12, 12);
                e.Graphics.DrawLine(pen, 11, y + 4, 23, y + 4);
                e.Graphics.DrawLine(pen, 14, y - 2, 14, y + 2);
                e.Graphics.DrawLine(pen, 20, y - 2, 20, y + 2);
            }
        }
    }
}
