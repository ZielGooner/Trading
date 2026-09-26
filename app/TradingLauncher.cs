using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TradingLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string key;
            using (SHA256 hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
            using (Mutex mutex = new Mutex(false, "Local\\TradingLauncher_" + key))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    MessageBox.Show("이미 실행 중입니다. 작업 표시줄에서 Trading 창을 확인하세요.", "Trading", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Application.Run(new LauncherForm(root)); }
                catch (Exception error) { MessageBox.Show(error.Message, "Trading 실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed class LauncherForm : Form
    {
        private bool closeWhenFinished;
        private readonly LivePanel live;

        public LauncherForm(string projectRoot)
        {
            string root = Path.GetFullPath(projectRoot);
            Text = "Trading | BTC · XRP · SOL";
            ClientSize = new Size(1280, 880);
            MinimumSize = new Size(1100, 800);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UiTheme.Font(10F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            DoubleBuffered = true;

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(shell);
            shell.Controls.Add(CreateTopBar(), 0, 0);
            live = new LivePanel(root) { Margin = Padding.Empty };
            shell.Controls.Add(live, 0, 1);
            shell.Controls.Add(CreateFooter(), 0, 2);
            live.ShutdownCompleted += delegate { if (closeWhenFinished) Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (live.HasWorker) { e.Cancel = true; closeWhenFinished = true; live.BeginShutdown(); }
            };
            IntPtr unused = Handle;
        }

        private Control CreateTopBar()
        {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
            bar.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            var identity = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 400, WrapContents = false, Padding = new Padding(16, 0, 0, 0), Margin = Padding.Empty };
            var brand = new Panel { Width = 175, Height = 59, Margin = new Padding(0, 0, 20, 0) };
            brand.Controls.Add(new BrandMark { Location = new Point(0, 11) });
            var name = UiTheme.Label("TRADING", 16F, UiTheme.Accent, FontStyle.Bold);
            name.Location = new Point(42, 15);
            brand.Controls.Add(name);
            identity.Controls.Add(brand);
            identity.Controls.Add(new Label { Text = "실계좌", ForeColor = UiTheme.Text, Font = UiTheme.Font(10F, FontStyle.Bold), Size = new Size(94, 59), TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty });
            bar.Controls.Add(identity);
            var modeLabel = new Label { Text = "USDⓈ-M  /  자동매매 워크스페이스", ForeColor = UiTheme.Muted, Font = UiTheme.Font(9F), Dock = DockStyle.Right, Width = 315, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 22, 0) };
            bar.Controls.Add(modeLabel);
            return bar;
        }

        private Control CreateFooter()
        {
            var footer = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var symbols = new Label { Text = "BTC/USDT     XRP/USDT     SOL/USDT", ForeColor = UiTheme.Muted, Font = UiTheme.Font(8F), Dock = DockStyle.Left, Width = 380, Padding = new Padding(16, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
            var local = new Label { Text = "4시간봉  ·  로컬 워크스페이스", ForeColor = UiTheme.Muted, Font = UiTheme.Font(8F), Dock = DockStyle.Right, Width = 260, Padding = new Padding(0, 0, 16, 0), TextAlign = ContentAlignment.MiddleRight };
            footer.Controls.AddRange(new Control[] { symbols, local });
            return footer;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiTheme.DarkTitleBar(this);
        }
    }
}
