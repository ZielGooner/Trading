using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TradingLauncher
{
    // Keep pickers inside the existing window. A closed overlay stays owned by
    // its field, avoiding ToolStripDropDown's close/dispose re-entry entirely.
    internal sealed class FieldPopup : IMessageFilter, IDisposable
    {
        private static FieldPopup active;
        private readonly Control owner, content;
        private readonly SurfacePanel panel = new SurfacePanel();
        private readonly List<Control> ancestors = new List<Control>();
        private Form host;
        private bool disposed;
        public bool Visible { get { return host != null; } }
        public FieldPopup(Control owner, Control content)
        {
            this.owner = owner; this.content = content;
            panel.Name = "PickerPopup"; panel.BackColor = UiTheme.Surface; panel.Padding = new Padding(1);
            panel.Size = new Size(content.Width + 2, content.Height + 2);
            content.Dock = DockStyle.Fill; panel.Controls.Add(content);
            owner.VisibleChanged += OwnerChanged; owner.EnabledChanged += OwnerChanged;
        }
        public void Show()
        {
            if (disposed || Visible || !owner.Visible || !owner.Enabled) return;
            if (active != null) active.Close();
            host = owner.FindForm(); if (host == null) return;
            Point point = host.PointToClient(owner.PointToScreen(new Point(0, owner.Height + 5)));
            int x = Math.Max(0, Math.Min(point.X, host.ClientSize.Width - panel.Width));
            int y = point.Y + panel.Height <= host.ClientSize.Height ? point.Y : point.Y - owner.Height - panel.Height - 10;
            panel.Location = new Point(x, Math.Max(0, y));
            host.Controls.Add(panel); panel.Show(); panel.BringToFront();
            for (Control parent = owner.Parent; parent != null; parent = parent.Parent)
            {
                ancestors.Add(parent); parent.VisibleChanged += OwnerChanged; parent.EnabledChanged += OwnerChanged;
            }
            host.Deactivate += HostChanged; host.SizeChanged += HostChanged;
            active = this; Application.AddMessageFilter(this); content.Focus(); owner.Invalidate();
        }
        public void Close()
        {
            if (!Visible) return;
            Form previous = host; host = null;
            if (active == this) active = null;
            Application.RemoveMessageFilter(this);
            foreach (Control parent in ancestors) { parent.VisibleChanged -= OwnerChanged; parent.EnabledChanged -= OwnerChanged; }
            ancestors.Clear();
            previous.Deactivate -= HostChanged; previous.SizeChanged -= HostChanged;
            panel.Hide(); previous.Controls.Remove(panel);
            if (!owner.IsDisposed) owner.Invalidate();
        }
        private void OwnerChanged(object sender, EventArgs e) { if (!owner.Visible || !owner.Enabled) Close(); }
        private void HostChanged(object sender, EventArgs e) { Close(); }
        private static bool Inside(Control target, Control container)
        {
            for (Control current = target; current != null; current = current.Parent) if (current == container) return true;
            return false;
        }
        public bool PreFilterMessage(ref Message message)
        {
            if (!Visible) return false;
            const int KeyDown = 0x100, LeftDown = 0x201, RightDown = 0x204, MiddleDown = 0x207, NonClientLeftDown = 0xA1;
            if (message.Msg == KeyDown && (Keys)message.WParam.ToInt32() == Keys.Escape)
            { Close(); owner.Focus(); return true; }
            if (message.Msg == KeyDown && (Keys)message.WParam.ToInt32() == Keys.Tab) Close();
            else if (message.Msg == LeftDown || message.Msg == RightDown || message.Msg == MiddleDown || message.Msg == NonClientLeftDown)
            {
                Control target = Control.FromChildHandle(message.HWnd);
                if (!Inside(target, panel) && !Inside(target, owner)) Close();
            }
            return false;
        }
        public void Dispose()
        {
            if (disposed) return;
            Close(); disposed = true;
            owner.VisibleChanged -= OwnerChanged; owner.EnabledChanged -= OwnerChanged;
            panel.Dispose();
        }
    }

    internal abstract class PickerField : InputField
    {
        private FieldPopup popup;
        public bool IsDropDownOpen { get { return popup != null && popup.Visible; } }
        protected override bool Highlighted { get { return IsDropDownOpen; } }
        protected void SetPopupContent(Control content) { popup = new FieldPopup(this, content); }
        protected abstract void PrepareDropDown();
        public void ShowDropDown() { if (!Enabled || popup == null || IsDisposed) return; PrepareDropDown(); popup.Show(); }
        public void CloseDropDown() { if (popup != null) popup.Close(); }
        protected override void OnClick(EventArgs e)
        {
            if (IsDropDownOpen) CloseDropDown(); else ShowDropDown();
            base.OnClick(e);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Down) || keyData == Keys.Space || keyData == Keys.Enter)
            { ShowDropDown(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { ShowDropDown(); e.Handled = e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Enabled ? UiTheme.Muted : UiTheme.Border, 1.5F))
            {
                int x = Width - 16, y = Height / 2;
                e.Graphics.DrawLines(pen, IsDropDownOpen ? new Point[] { new Point(x - 3, y + 1), new Point(x, y - 2), new Point(x + 3, y + 1) }
                    : new Point[] { new Point(x - 3, y - 1), new Point(x, y + 2), new Point(x + 3, y - 1) });
            }
        }
        protected override void Dispose(bool disposing) { if (disposing && popup != null) popup.Dispose(); base.Dispose(disposing); }
    }
}
