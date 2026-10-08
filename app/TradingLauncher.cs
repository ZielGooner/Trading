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
                    MessageBox.Show("이미 실행 중입니다. 작업 표시줄에서 AUREX 창을 확인하세요.", "AUREX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Application.Run(new LauncherForm(root)); }
                catch (Exception error) { MessageBox.Show(error.Message, "AUREX 실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed class LauncherForm : Form
    {
        private bool closeWhenFinished;
        private readonly LivePanel live;
        private readonly AnalysisPanel analysis;
        private readonly TransactionsPanel transactions;
        private readonly Panel pages = new Panel();
        private readonly NavigationButton[] navigation = new NavigationButton[3];
        private readonly Label connection = new Label();
        public string SelectedPage { get; private set; }
        public TransactionsPanel Transactions { get { return transactions; } }
        public AnalysisPanel Analysis { get { return analysis; } }

        public LauncherForm(string projectRoot) : this(projectRoot, null) { }
        public LauncherForm(string projectRoot, IChartFeed chartFeed)
        {
            string root = Path.GetFullPath(projectRoot);
            Text = "AUREX";
            Icon = AurexBrand.CreateIcon();
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
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(shell);
            live = new LivePanel(root, chartFeed) { Margin = Padding.Empty };
            transactions = new TransactionsPanel(root) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            analysis = new AnalysisPanel(root) { Margin = Padding.Empty };
            pages.Dock = DockStyle.Fill; pages.Margin = Padding.Empty;
            pages.Controls.Add(live); pages.Controls.Add(transactions); pages.Controls.Add(analysis);
            shell.Controls.Add(CreateNavigation(), 0, 0);
            shell.Controls.Add(pages, 0, 1);
            shell.Controls.Add(CreateFooter(), 0, 2);
            live.ConnectionChanged += delegate { connection.Text = live.ConnectionText; connection.ForeColor = live.ConnectionColor; };
            live.HistoryUpdated += delegate { transactions.MarkLedgerUpdated(live.AccountId); };
            connection.Text = live.ConnectionText; connection.ForeColor = live.ConnectionColor;
            live.ShutdownCompleted += delegate { TryFinishClose(); };
            analysis.OperationCompleted += delegate { TryFinishClose(); };
            transactions.OperationCompleted += delegate { TryFinishClose(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (live.HasWorker || analysis.IsBusy || transactions.IsBusy)
                {
                    e.Cancel = true; closeWhenFinished = true;
                    live.BeginShutdown(); analysis.RequestCancellation(); transactions.RequestCancellation();
                }
            };
            SelectPage("main");
            IntPtr unused = Handle;
        }

        private Control CreateNavigation()
        {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
            var strip = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 350, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(12, 0, 0, 0) };
            string[] names = { "메인", "거래 내역", "분석" }, keys = { "main", "history", "analysis" };
            for (int i = 0; i < names.Length; i++)
            {
                string key = keys[i];
                navigation[i] = new NavigationButton { Text = names[i], Width = i == 1 ? 118 : 88, Height = 47, Margin = Padding.Empty };
                navigation[i].Click += delegate { SelectPage(key); };
                strip.Controls.Add(navigation[i]);
            }
            connection.Dock = DockStyle.Right; connection.Width = 230;
            connection.TextAlign = ContentAlignment.MiddleRight; connection.Padding = new Padding(0, 0, 20, 0);
            connection.Font = UiTheme.Font(9F); connection.ForeColor = UiTheme.Muted;
            bar.Controls.Add(strip); bar.Controls.Add(connection);
            return bar;
        }

        public void SelectPage(string page)
        {
            int index = page == "main" ? 0 : page == "history" ? 1 : page == "analysis" ? 2 : -1;
            if (index < 0) throw new ArgumentException("Unknown page", "page");
            SelectedPage = page;
            live.Visible = index == 0; transactions.Visible = index == 1; analysis.Visible = index == 2;
            for (int i = 0; i < navigation.Length; i++) { navigation[i].Selected = i == index; navigation[i].Invalidate(); }
            if (index == 1) { transactions.BringToFront(); transactions.ActivatePage(live.AccountId); }
            if (index == 2) { analysis.BringToFront(); analysis.SetAccount(live.AccountId); }
            if (index == 0) live.BringToFront();
        }

        private void TryFinishClose()
        {
            if (closeWhenFinished && !live.HasWorker && !analysis.IsBusy && !transactions.IsBusy) Close();
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
