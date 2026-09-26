using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal static class UiTheme
    {
        public static readonly Color Background = Color.FromArgb(11, 14, 17);
        public static readonly Color Surface = Color.FromArgb(24, 26, 32);
        public static readonly Color Elevated = Color.FromArgb(30, 35, 41);
        public static readonly Color Border = Color.FromArgb(43, 49, 57);
        public static readonly Color Text = Color.FromArgb(234, 236, 239);
        public static readonly Color Muted = Color.FromArgb(132, 142, 156);
        public static readonly Color Accent = Color.FromArgb(240, 185, 11);
        public static readonly Color Positive = Color.FromArgb(14, 203, 129);
        public static readonly Color Negative = Color.FromArgb(246, 70, 93);
        public static readonly Color Warning = Accent;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void DarkTitleBar(Form form)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try { int enabled = 1; DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public static Font Font(float size) { return Font(size, FontStyle.Regular); }
        public static Font Font(float size, FontStyle style) { return new Font("맑은 고딕", size, style, GraphicsUnit.Point); }
        public static Label Label(string text, float size, Color color, FontStyle style)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, Font = Font(size, style) };
        }
        public static SurfacePanel Card() { return new SurfacePanel { Dock = DockStyle.Fill, BackColor = Surface }; }

        public static void StyleButton(Button button, string text, int width, bool primary)
        {
            button.Text = text;
            button.Size = new Size(width, 40);
            button.Margin = new Padding(0, 0, 8, 0);
            button.Font = Font(9F, FontStyle.Bold);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(252, 213, 53) : Color.FromArgb(43, 49, 57);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(218, 168, 10) : Elevated;
            button.BackColor = primary ? Accent : Elevated;
            button.ForeColor = primary ? Background : Text;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        public static void StyleGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.GridColor = Color.FromArgb(33, 38, 45);
            grid.EnableHeadersVisualStyles = false;
            grid.Font = Font(9F);
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Muted, SelectionBackColor = Surface, SelectionForeColor = Text, Font = Font(8.5F), Padding = new Padding(8, 0, 8, 0) };
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 32;
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Text, SelectionBackColor = Elevated, SelectionForeColor = Text, Padding = new Padding(8, 0, 8, 0) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Surface;
        }

        public static void StyleLog(TextBox log)
        {
            log.Dock = DockStyle.Fill;
            log.Multiline = log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Surface;
            log.ForeColor = Muted;
            log.BorderStyle = BorderStyle.None;
            log.Font = Font(8.5F);
            log.Margin = Padding.Empty;
        }

        public static void PrependLog(TextBox log, string message)
        {
            if (message == null) return;
            string text = message + Environment.NewLine + log.Text;
            if (text.Length > 24000)
            {
                // Retain the newest entries and end on a complete line when possible.
                int end = text.LastIndexOf('\n', 15999);
                text = text.Substring(0, end >= 0 ? end + 1 : 16000);
            }
            log.Text = text;
            log.Select(0, 0);
            log.ScrollToCaret();
        }

        public static Panel Header(string eyebrow, string title, string subtitle)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var overline = Label(eyebrow, 8F, Accent, FontStyle.Bold);
            overline.Location = new Point(0, 0);
            var heading = Label(title, 20F, Text, FontStyle.Bold);
            heading.Location = new Point(-2, 17);
            var description = Label(subtitle, 9F, Muted, FontStyle.Regular);
            description.Location = new Point(0, 56);
            panel.Controls.AddRange(new Control[] { overline, heading, description });
            return panel;
        }

        public static SurfacePanel Metric(string title, Label value, string footnote)
        {
            var panel = Card();
            panel.Padding = new Padding(16, 10, 16, 8);
            var caption = Label(title, 9F, Muted, FontStyle.Regular);
            value.Text = "—";
            value.Font = Font(23F, FontStyle.Bold);
            value.ForeColor = Text;
            value.AutoSize = false;
            value.TextAlign = ContentAlignment.MiddleLeft;
            value.AutoEllipsis = true;
            var note = Label(footnote, 8F, Muted, FontStyle.Regular);
            panel.Controls.AddRange(new Control[] { caption, value, note });
            EventHandler fitValue = delegate
            {
                if (value.ClientSize.Width < 20) return;
                float size = 23F;
                Font fitted = Font(size, FontStyle.Bold);
                while (size > 14F && (TextRenderer.MeasureText(value.Text, fitted).Width > value.ClientSize.Width || TextRenderer.MeasureText(value.Text, fitted).Height > value.ClientSize.Height))
                { fitted.Dispose(); size -= 0.5F; fitted = Font(size, FontStyle.Bold); }
                if (Math.Abs(value.Font.Size - size) > 0.1F)
                { Font old = value.Font; value.Font = fitted; old.Dispose(); }
                else fitted.Dispose();
            };
            EventHandler arrange = delegate
            {
                caption.Location = new Point(panel.Padding.Left, panel.Padding.Top);
                note.Location = new Point(panel.Padding.Left, panel.ClientSize.Height - panel.Padding.Bottom - note.Height);
                int top = caption.Bottom + 4;
                value.SetBounds(panel.Padding.Left, top, Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal), Math.Max(1, note.Top - top - 2));
                fitValue(value, EventArgs.Empty);
            };
            panel.SizeChanged += arrange;
            value.TextChanged += fitValue;
            arrange(panel, EventArgs.Empty);
            return panel;
        }
        public static SurfacePanel Section(string title, string detail, Control body)
        {
            var panel = Card();
            panel.Padding = new Padding(16, 0, 16, 12);
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var head = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var caption = Label(title, 10F, Text, FontStyle.Bold);
            caption.Location = new Point(0, 9);
            var note = Label(detail, 8F, Muted, FontStyle.Regular);
            note.AutoSize = false;
            note.Dock = DockStyle.Right;
            note.Width = 270;
            note.TextAlign = ContentAlignment.MiddleRight;
            note.Visible = !string.IsNullOrEmpty(detail);
            head.Controls.AddRange(new Control[] { caption, note });
            head.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Border)) e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
                using (var pen = new Pen(Accent, 2F)) e.Graphics.DrawLine(pen, 0, head.Height - 2, Math.Min(38, caption.Width), head.Height - 2);
            };
            body.Dock = DockStyle.Fill;
            body.Margin = Padding.Empty;
            rows.Controls.Add(head, 0, 0); rows.Controls.Add(body, 0, 1);
            panel.Controls.Add(rows);
            return panel;
        }

        internal static GraphicsPath Round(Rectangle rectangle, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
            path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SurfacePanel : Panel
    {
        public SurfacePanel() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiTheme.Background : Parent.BackColor);
            if (Width < 22 || Height < 22) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 5))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(UiTheme.Border))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path); }
        }
    }

    internal sealed class ModernButton : Button
    {
        private bool hovered;
        public ModernButton() { DoubleBuffered = true; SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiTheme.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? UiTheme.Surface : hovered ? FlatAppearance.MouseOverBackColor : BackColor;
            using (var path = UiTheme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 5))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(!Enabled ? UiTheme.Border : FlatAppearance.BorderColor))
            {
                e.Graphics.FillPath(brush, path);
                if (FlatAppearance.BorderSize > 0 || !Enabled) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(94, 102, 115), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), UiTheme.Accent, fill);
        }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark() { Size = new Size(36, 36); DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(UiTheme.Accent))
            using (var pen = new Pen(UiTheme.Accent, 2F))
            {
                e.Graphics.DrawLine(pen, 8, 15, 8, 32); e.Graphics.FillRectangle(brush, 4, 20, 8, 7);
                e.Graphics.DrawLine(pen, 19, 7, 19, 28); e.Graphics.FillRectangle(brush, 15, 12, 8, 11);
                e.Graphics.DrawLine(pen, 30, 2, 30, 21); e.Graphics.FillRectangle(brush, 26, 5, 8, 10);
            }
        }
    }

    internal sealed class NavigationButton : Button
    {
        public bool Selected { get; set; }
        private bool hovered;
        public NavigationButton()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = UiTheme.Font(10F, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(UiTheme.Surface);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Selected || hovered ? UiTheme.Accent : UiTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Selected) using (var pen = new Pen(UiTheme.Accent, 3F)) e.Graphics.DrawLine(pen, Width / 2 - 15, Height - 2, Width / 2 + 15, Height - 2);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), UiTheme.Accent, UiTheme.Surface);
        }
    }
}
