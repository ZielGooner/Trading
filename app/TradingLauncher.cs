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

        public LauncherForm(string projectRoot) : this(projectRoot, null) { }
        public LauncherForm(string projectRoot, IChartFeed chartFeed)
        {
            string root = Path.GetFullPath(projectRoot);
            Text = "Trading | BTC · XRP · SOL · HYPE";
            ClientSize = new Size(1280, 880);
            MinimumSize = new Size(1100, 800);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UiTheme.Font(10F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            DoubleBuffered = true;

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(shell);
            live = new LivePanel(root, chartFeed) { Margin = Padding.Empty };
            shell.Controls.Add(live, 0, 0);
            shell.Controls.Add(CreateFooter(), 0, 1);
            live.ShutdownCompleted += delegate { if (closeWhenFinished) Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (live.HasWorker) { e.Cancel = true; closeWhenFinished = true; live.BeginShutdown(); }
            };
            IntPtr unused = Handle;
        }

        private Control CreateFooter()
        {
            var footer = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var local = new Label { Text = "4시간 실행 · 상위봉 확인 · 진입 2배", ForeColor = UiTheme.Muted, Font = UiTheme.Font(8F), Dock = DockStyle.Right, Width = 280, Padding = new Padding(0, 0, 16, 0), TextAlign = ContentAlignment.MiddleRight };
            footer.Controls.Add(local);
            return footer;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiTheme.DarkTitleBar(this);
        }
    }
}
