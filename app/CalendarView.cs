using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal sealed class CalendarView : Control
    {
        private DateTime selected = DateTime.Today, focused = DateTime.Today;
        private DateTime month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        private int hovered = -1;
        private const int Cell = 36, Inset = 12, GridTop = 76;
        public event EventHandler DateSelected;
        public DateTime SelectedDate
        {
            get { return selected; }
            set { selected = focused = value.Date; month = new DateTime(value.Year, value.Month, 1); hovered = -1; Invalidate(); }
        }
        public CalendarView()
        {
            Size = new Size(276, 340); BackColor = UiTheme.Surface; ForeColor = UiTheme.Text; Font = UiTheme.Font(9F);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; AccessibleName = "날짜 선택 달력";
        }
        private Rectangle PreviousMonth { get { return new Rectangle(Inset, 10, 32, 32); } }
        private Rectangle NextMonth { get { return new Rectangle(Width - Inset - 32, 10, 32, 32); } }
        private Rectangle TodayButton { get { return new Rectangle(Inset, 303, Width - Inset * 2, 27); } }
        private Rectangle CellBounds(int index) { return new Rectangle(Inset + index % 7 * Cell, GridTop + index / 7 * Cell, Cell, Cell); }
        private int HitCell(Point point)
        {
            if (point.X < Inset || point.X >= Inset + 7 * Cell || point.Y < GridTop || point.Y >= GridTop + 6 * Cell) return -1;
            return (point.Y - GridTop) / Cell * 7 + (point.X - Inset) / Cell;
        }
        private void MoveMonth(int delta)
        {
            if (delta < 0 && month.Year == 1 && month.Month == 1 || delta > 0 && month.Year == 9999 && month.Month == 12) return;
            month = month.AddMonths(delta);
            focused = month.AddDays(Math.Min(focused.Day, DateTime.DaysInMonth(month.Year, month.Month)) - 1);
            hovered = -1; Invalidate();
        }
        private void Choose(DateTime date)
        {
            selected = focused = date;
            if (DateSelected != null) DateSelected(this, EventArgs.Empty);
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int cell = HitCell(e.Location);
            int next = cell >= 0 ? cell : PreviousMonth.Contains(e.Location) ? 42 : NextMonth.Contains(e.Location) ? 43 : TodayButton.Contains(e.Location) ? 44 : -1;
            if (next != hovered) { hovered = next; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
            if (PreviousMonth.Contains(e.Location)) MoveMonth(-1);
            else if (NextMonth.Contains(e.Location)) MoveMonth(1);
            else if (TodayButton.Contains(e.Location)) Choose(DateTime.Today);
            else { int index = HitCell(e.Location); if (index >= 0) { DateTime date; if (TryDate(index, out date)) Choose(date); } }
        }
        private bool TryDate(int index, out DateTime date)
        {
            long ticks = month.Ticks + (index - (int)month.DayOfWeek) * TimeSpan.TicksPerDay;
            if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) { date = month; return false; }
            date = new DateTime(ticks); return true;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End
                || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Enter || key == Keys.Space || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int days = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Up ? -7 : e.KeyCode == Keys.Down ? 7 : 0;
            if (days != 0)
            {
                if (focused.Ticks + days * TimeSpan.TicksPerDay >= 0 && focused.Ticks + days * TimeSpan.TicksPerDay <= DateTime.MaxValue.Ticks)
                { focused = focused.AddDays(days); month = new DateTime(focused.Year, focused.Month, 1); Invalidate(); }
            }
            else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) MoveMonth(e.KeyCode == Keys.PageUp ? -1 : 1);
            else if (e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            { focused = month.AddDays(e.KeyCode == Keys.Home ? 0 : DateTime.DaysInMonth(month.Year, month.Month) - 1); Invalidate(); }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Choose(focused);
            else { base.OnKeyDown(e); return; }
            e.Handled = e.SuppressKeyPress = true;
        }
        private static void FillRound(Graphics graphics, Rectangle rectangle, Color color)
        {
            using (var path = UiTheme.Round(rectangle, UiTheme.CornerRadius)) using (var fill = new SolidBrush(color)) graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var headingFont = UiTheme.Font(10F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, month.ToString("yyyy년 M월", CultureInfo.InvariantCulture), headingFont, new Rectangle(45, 10, Width - 90, 32), UiTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Rectangle[] arrows = { PreviousMonth, NextMonth };
            for (int i = 0; i < arrows.Length; i++)
            {
                if (hovered == 42 + i) FillRound(e.Graphics, arrows[i], UiTheme.Elevated);
                int x = arrows[i].Left + 16, y = arrows[i].Top + 16, direction = i == 0 ? 1 : -1;
                using (var pen = new Pen(UiTheme.Muted, 1.5F)) e.Graphics.DrawLines(pen, new Point[] { new Point(x + direction * 2, y - 5), new Point(x - direction * 3, y), new Point(x + direction * 2, y + 5) });
            }
            string[] weekdays = { "일", "월", "화", "수", "목", "금", "토" };
            for (int i = 0; i < 7; i++) TextRenderer.DrawText(e.Graphics, weekdays[i], Font, new Rectangle(Inset + i * Cell, 49, Cell, 24), UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            for (int i = 0; i < 42; i++)
            {
                DateTime date; if (!TryDate(i, out date)) continue;
                Rectangle box = Rectangle.Inflate(CellBounds(i), -3, -3);
                bool chosen = date == selected;
                if (chosen || i == hovered) FillRound(e.Graphics, box, chosen ? UiTheme.Accent : UiTheme.Elevated);
                if (!chosen && (date == DateTime.Today || Focused && date == focused))
                    using (var path = UiTheme.Round(box, UiTheme.CornerRadius)) using (var pen = new Pen(UiTheme.Accent)) e.Graphics.DrawPath(pen, path);
                Color color = chosen ? UiTheme.Background : date.Month == month.Month ? UiTheme.Text : UiTheme.Muted;
                TextRenderer.DrawText(e.Graphics, date.Day.ToString(CultureInfo.InvariantCulture), Font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawLine(pen, Inset, 296, Width - Inset, 296);
            if (hovered == 44) FillRound(e.Graphics, TodayButton, UiTheme.Elevated);
            TextRenderer.DrawText(e.Graphics, "오늘 · " + DateTime.Today.ToString("MM.dd", CultureInfo.InvariantCulture), Font, TodayButton, UiTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
