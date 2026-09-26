using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TradingLauncher
{
    public static class CredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Trading.Binance.Hmac.v1");
        public static string FilePath(string root) { return Path.Combine(root, "private_state", "binance-credentials.dpapi"); }
        public static void Save(string root, string key, string secret)
        {
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, string> { {"key", key}, {"secret", secret} }));
            try
            {
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                string path = FilePath(root);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static Dictionary<string, object> Load(string root)
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath(root)), Entropy, DataProtectionScope.CurrentUser);
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
    }

    internal sealed class CredentialsDialog : Form
    {
        public CredentialsDialog(string root)
        {
            Text = "Binance API 설정";
            ClientSize = new Size(680, 474);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = UiTheme.Font(10F);
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            var eyebrow = UiTheme.Label("안전한 계좌 연결", 9F, UiTheme.Accent, FontStyle.Bold);
            eyebrow.Location = new Point(30, 25);
            var title = UiTheme.Label("Binance API 설정", 24F, UiTheme.Text, FontStyle.Bold);
            title.Location = new Point(27, 49);
            var description = new Label { Text = "USDⓈ-M 선물용 HMAC API 키를 입력하세요.\n읽기·선물 거래 권한을 사용하며, 출금 권한은 필요 없습니다.", Location = new Point(30, 103), Size = new Size(620, 52), ForeColor = UiTheme.Muted, Font = UiTheme.Font(10F) };
            var keyCaption = UiTheme.Label("API 키", 9F, UiTheme.Text, FontStyle.Bold);
            keyCaption.Location = new Point(30, 171);
            var secretCaption = UiTheme.Label("시크릿 키", 9F, UiTheme.Text, FontStyle.Bold);
            secretCaption.Location = new Point(30, 258);
            var keyField = UiTheme.Card();
            keyField.Dock = DockStyle.None;
            keyField.SetBounds(30, 197, 620, 44);
            var secretField = UiTheme.Card();
            secretField.Dock = DockStyle.None;
            secretField.SetBounds(30, 284, 620, 44);
            var key = new TextBox { UseSystemPasswordChar = true, Location = new Point(14, 11), Width = 590, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = UiTheme.Font(11F), TabIndex = 0 };
            var secret = new TextBox { UseSystemPasswordChar = true, Location = new Point(14, 11), Width = 590, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = UiTheme.Font(11F), TabIndex = 1 };
            keyField.Controls.Add(key);
            secretField.Controls.Add(secret);
            var note = UiTheme.Label("키는 이 Windows 사용자 계정으로 암호화되어 로컬에 저장됩니다.", 9F, UiTheme.Muted, FontStyle.Regular);
            note.Location = new Point(30, 345);
            var cancel = new ModernButton { Location = new Point(402, 405), DialogResult = DialogResult.Cancel, TabIndex = 3 };
            var save = new ModernButton { Location = new Point(518, 405), TabIndex = 2 };
            UiTheme.StyleButton(cancel, "취소", 104, false);
            UiTheme.StyleButton(save, "암호화 저장", 132, true);
            Controls.AddRange(new Control[] { eyebrow, title, description, keyCaption, keyField, secretCaption, secretField, note, cancel, save });
            save.Click += delegate
            {
                if (key.Text.Trim().Length < 16 || secret.Text.Trim().Length < 16)
                { MessageBox.Show(this, "API Key와 Secret Key를 모두 입력하세요."); return; }
                try
                {
                    CredentialStore.Save(root, key.Text.Trim(), secret.Text.Trim());
                    key.Clear(); secret.Clear(); DialogResult = DialogResult.OK; Close();
                }
                catch (Exception error) { MessageBox.Show(this, "암호화 저장 실패: " + error.GetType().Name); }
            };
            AcceptButton = save;
            CancelButton = cancel;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiTheme.DarkTitleBar(this);
        }
    }

    public sealed class LivePanel : UserControl
    {
        private readonly string root;
        private readonly Button settings = new ModernButton(), connect = new ModernButton(), disconnect = new ModernButton(), start = new ModernButton(), stop = new ModernButton();
        private readonly Label status = new Label(), info = new Label();
        private readonly Label connectionBadge = new Label(), emptyPositions = new Label(), emptyLog = new Label();
        private readonly Label[] values = new Label[3];
        private readonly DataGridView positions = new DataGridView();
        private readonly TextBox log = new TextBox();
        private readonly Timer watchdog = new Timer();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private Process worker;
        private string accountId;
        private HistoryForm historyWindow;
        private bool closing, staleStopSent;
        private string lastFault;
        private DateTime lastMessage;
        public event EventHandler ShutdownCompleted;
        public bool HasWorker { get { return worker != null; } }
        public bool StartEnabled { get { return start.Enabled; } }
        public string WalletText { get { return values[0].Text; } }
        public string StatusText { get { return status.Text; } }

        public LivePanel(string projectRoot)
        {
            root = projectRoot;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(10F);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0), ColumnCount = 1, RowCount = 4, BackColor = UiTheme.Background, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(layout);
            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = UiTheme.Label("USDⓈ-M 선물", 22F, UiTheme.Text, FontStyle.Bold);
            title.Location = new Point(0, 0);
            var subtitle = UiTheme.Label("시장 차트 · 실계좌 자산 · 자동매매", 8F, UiTheme.Muted, FontStyle.Regular);
            subtitle.Location = new Point(238, 19);
            header.Controls.AddRange(new Control[] { title, subtitle });
            connectionBadge.Size = new Size(180, 30);
            connectionBadge.TextAlign = ContentAlignment.MiddleRight;
            connectionBadge.Font = UiTheme.Font(9F, FontStyle.Bold);
            connectionBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(connectionBadge);
            header.Resize += delegate { connectionBadge.Location = new Point(Math.Max(0, header.ClientSize.Width - 180), 5); };
            SetConnectionBadge("연결 안 됨", UiTheme.Muted);
            var historyButton = new ModernButton { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            UiTheme.StyleButton(historyButton, "거래 내역 · 분석", 170, false);
            historyButton.Height = 34;
            header.Controls.Add(historyButton);
            header.Resize += delegate { historyButton.Location = new Point(Math.Max(0, header.ClientSize.Width - 365), 3); };
            historyButton.Click += delegate
            {
                if (historyWindow == null || historyWindow.IsDisposed) historyWindow = new HistoryForm(root, accountId);
                if (!historyWindow.Visible) historyWindow.Show(FindForm());
                historyWindow.Activate();
            };
            layout.Controls.Add(header, 0, 0);
            SetupButton(settings, "API 설정", 124, false);
            SetupButton(connect, "실계좌 연결", 254, true);
            SetupButton(disconnect, "연결 해제", 124, false);
            SetupButton(start, "실거래 시작", 124, true);
            SetupButton(stop, "신규 진입 중지", 124, false);
            settings.Click += delegate { using (var dialog = new CredentialsDialog(root)) dialog.ShowDialog(this); };
            connect.Click += delegate { ConnectAccount(); };
            disconnect.Click += delegate { BeginShutdown(); };
            start.Click += delegate { start.Enabled = false; SetConnectionBadge("시작 조건 확인 중", UiTheme.Warning); status.ForeColor = UiTheme.Muted; status.Text = "실거래 시작 조건과 완료봉을 확인하는 중입니다…"; Send(new Dictionary<string, object> { {"action", "start"} }); };
            stop.Click += delegate { Send(new Dictionary<string, object> { {"action", "stop"} }); };

            var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            string[] names = { "실계좌 지갑", "가용 증거금", "미실현 손익" };
            string[] notes = { "USDT  ·  지갑 잔고", "USDT  ·  신규 진입 가능 잔고", "USDT  ·  현재 포지션 손익" };
            for (int i = 0; i < 3; i++)
            {
                metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
                values[i] = new Label { Text = "—", ForeColor = UiTheme.Muted };
                var card = UiTheme.Metric(names[i], values[i], notes[i]);
                card.Padding = new Padding(14, 8, 14, 6);
                card.Margin = new Padding(0, 0, i == 2 ? 0 : 8, 8);
                metrics.Controls.Add(card, i, 0);
            }
            layout.Controls.Add(metrics, 0, 1);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 286));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var market = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 8, 0) };
            market.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            market.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            market.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            var chart = new MarketChart(root) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            market.Controls.Add(chart, 0, 0);
            main.Controls.Add(market, 0, 0);

            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 320));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            foreach (float height in new float[] { 40, 44, 28, 44 }) actions.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (Button action in new Button[] { settings, connect, disconnect, start, stop }) action.Dock = DockStyle.Fill;
            settings.Margin = start.Margin = new Padding(0, 0, 4, 6);
            disconnect.Margin = stop.Margin = new Padding(4, 0, 0, 6);
            connect.Margin = new Padding(0, 0, 0, 6);
            actions.Controls.Add(settings, 0, 0);
            actions.Controls.Add(disconnect, 1, 0);
            actions.Controls.Add(connect, 0, 1);
            actions.SetColumnSpan(connect, 2);
            var automationTitle = UiTheme.Label("자동매매", 10F, UiTheme.Text, FontStyle.Bold);
            automationTitle.Margin = new Padding(0, 4, 0, 0);
            actions.Controls.Add(automationTitle, 0, 2);
            actions.SetColumnSpan(automationTitle, 2);
            actions.Controls.Add(start, 0, 3);
            actions.Controls.Add(stop, 1, 3);
            info.Dock = DockStyle.Fill;
            info.Text = "진입 비중  가용 증거금 10%\n최소 주문 미달 시 필요한 만큼 초과 허용\n레버리지  BTC 10배 / XRP·SOL 5배\n시작 이후 완료되는 4시간봉부터 진입\n같은 종목의 수동 거래는 피하세요.";
            info.Font = UiTheme.Font(8F);
            info.ForeColor = UiTheme.Muted;
            info.Margin = new Padding(0, 5, 0, 0);
            actions.Controls.Add(info, 0, 4);
            actions.SetColumnSpan(info, 2);
            var actionSection = UiTheme.Section("계좌 연결", "", actions);
            actionSection.Margin = new Padding(0, 0, 0, 8);
            sidebar.Controls.Add(actionSection, 0, 0);
            main.Controls.Add(sidebar, 1, 0);
            layout.Controls.Add(main, 0, 2);
            positions.Dock = DockStyle.Fill;
            UiTheme.StyleGrid(positions);
            foreach (string name in new string[] { "종목", "포지션 수량", "평균 진입가", "미실현 손익", "프로그램 관리" }) positions.Columns.Add(name, name);
            positions.Columns[0].FillWeight = 85;
            positions.Columns[4].FillWeight = 112;
            for (int i = 1; i <= 3; i++) positions.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            var positionsHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
            positionsHost.Controls.Add(positions);
            emptyPositions.TextAlign = ContentAlignment.MiddleCenter;
            emptyPositions.Font = UiTheme.Font(9F);
            emptyPositions.ForeColor = UiTheme.Muted;
            emptyPositions.BackColor = UiTheme.Surface;
            positionsHost.Controls.Add(emptyPositions);
            positionsHost.Resize += delegate { emptyPositions.SetBounds(0, positions.ColumnHeadersHeight + 1, positionsHost.ClientSize.Width, Math.Max(0, positionsHost.ClientSize.Height - positions.ColumnHeadersHeight - 1)); };
            SetEmptyPositions(false);
            var positionSection = UiTheme.Section("보유 포지션", "거래소 계좌 기준", positionsHost);
            positionSection.Margin = Padding.Empty;
            market.Controls.Add(positionSection, 0, 1);
            UiTheme.StyleLog(log);
            var logHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
            logHost.Controls.Add(log);
            emptyLog.Text = "연결과 매매 활동이\n여기에 표시됩니다.";
            emptyLog.Dock = DockStyle.Fill;
            emptyLog.Font = UiTheme.Font(8F);
            emptyLog.ForeColor = UiTheme.Muted;
            emptyLog.BackColor = UiTheme.Surface;
            emptyLog.Padding = new Padding(2, 5, 0, 0);
            logHost.Controls.Add(emptyLog);
            emptyLog.BringToFront();
            var logSection = UiTheme.Section("활동 로그", "", logHost);
            logSection.Margin = Padding.Empty;
            sidebar.Controls.Add(logSection, 0, 1);
            status.Dock = DockStyle.Fill; status.Padding = new Padding(2, 7, 0, 0); status.Font = UiTheme.Font(8F); status.ForeColor = UiTheme.Muted;
            status.Margin = Padding.Empty;
            status.Text = "연결 안 됨 · API 설정 후 실계좌 연결을 누르세요. 연결만으로 주문하지 않습니다.";
            layout.Controls.Add(status, 0, 3);
            ResetControls();
            watchdog.Interval = 1000;
            watchdog.Tick += delegate
            {
                if (HasWorker && !closing && DateTime.UtcNow - lastMessage > TimeSpan.FromSeconds(30))
                {
                    ClearAccount(); start.Enabled = false;
                    SetConnectionBadge("응답 지연", UiTheme.Warning); status.ForeColor = UiTheme.Warning;
                    status.Text = "연결 응답 지연. 계좌 값은 표시하지 않으며 신규 진입 중지를 요청했습니다.";
                    if (!staleStopSent) { staleStopSent = true; Send(new Dictionary<string, object> { {"action", "stop"} }); }
                }
            };
            watchdog.Start();
        }

        private void SetupButton(Button button, string label, int width, bool primary)
        {
            UiTheme.StyleButton(button, label, width, primary);
            button.Margin = new Padding(0, 0, 10, 0);
        }
        private void SetConnectionBadge(string text, Color color)
        {
            connectionBadge.Text = "●  " + text;
            connectionBadge.ForeColor = color;
        }
        private void SetEmptyPositions(bool connected)
        {
            emptyPositions.Text = connected ? "보유 중인 포지션이 없습니다.\n포지션이 생기면 이곳에 표시됩니다." : "연결된 계좌가 없습니다.\n실계좌에 연결하면 보유 포지션을 확인할 수 있어요.";
            emptyPositions.Visible = positions.Rows.Count == 0;
            if (emptyPositions.Visible) emptyPositions.BringToFront();
        }
        private void ResetControls()
        {
            settings.Enabled = connect.Enabled = !HasWorker;
            start.Enabled = false;
            stop.Enabled = disconnect.Enabled = HasWorker && !closing;
        }
        private void ClearAccount()
        {
            foreach (Label label in values) { label.Text = "—"; label.ForeColor = UiTheme.Muted; }
            positions.Rows.Clear();
            SetEmptyPositions(false);
        }
        private void AppendLog(string message)
        {
            emptyLog.Visible = false;
            UiTheme.PrependLog(log, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        }
        private void OnUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        private void ConnectAccount()
        {
            if (HasWorker) return;
            try
            {
                if (!File.Exists(CredentialStore.FilePath(root)))
                {
                    using (var dialog = new CredentialsDialog(root)) if (dialog.ShowDialog(this) != DialogResult.OK) return;
                }
                var credentials = CredentialStore.Load(root);
                var info = new ProcessStartInfo(Path.Combine(root, ".venv", "Scripts", "python.exe"), "-B -u -X utf8 -m trading.live") {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                worker = new Process { StartInfo = info, EnableRaisingEvents = true };
                worker.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) OnUi(delegate { Receive(e.Data); }); };
                worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) OnUi(delegate { AppendLog("연결 프로세스 오류: " + e.Data); }); };
                worker.Exited += delegate { OnUi(WorkerExited); };
                closing = staleStopSent = false;
                lastFault = null;
                lastMessage = DateTime.UtcNow;
                if (!worker.Start()) throw new InvalidOperationException("연결 프로세스를 실행하지 못했습니다.");
                worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
                Send(new Dictionary<string, object> { {"action", "connect"}, {"api_key", credentials["key"]}, {"api_secret", credentials["secret"]} });
                credentials.Clear();
                ResetControls(); ClearAccount(); SetConnectionBadge("연결 확인 중", UiTheme.Warning); status.ForeColor = UiTheme.Muted; status.Text = "Binance 실계좌 인증과 잔고를 확인하고 있습니다…";
            }
            catch (Exception error)
            {
                AppendLog("연결 준비 실패: " + error.GetType().Name);
                SetConnectionBadge("연결 실패", UiTheme.Negative); status.ForeColor = UiTheme.Negative;
                status.Text = "연결하지 못했습니다. API 설정과 실행 환경을 확인하세요.";
                if (worker != null)
                {
                    try { worker.StandardInput.Close(); }
                    catch (InvalidOperationException) { worker.Dispose(); worker = null; }
                }
                ResetControls();
            }
        }
        private void Send(Dictionary<string, object> message)
        {
            if (worker == null) return;
            try { worker.StandardInput.WriteLine(json.Serialize(message)); worker.StandardInput.Flush(); }
            catch (Exception) { start.Enabled = false; SetConnectionBadge("통신 오류", UiTheme.Negative); status.ForeColor = UiTheme.Negative; status.Text = "연결 프로세스와 통신하지 못했습니다. 거래소 포지션을 확인하세요."; }
        }
        public void Receive(string line)
        {
            try
            {
                var value = json.Deserialize<Dictionary<string, object>>(line);
                if ((string)value["type"] == "history")
                {
                    if (value.ContainsKey("account_id")) accountId = Convert.ToString(value["account_id"]);
                    AppendLog(Convert.ToString(value["message"]));
                    return;
                }
                if ((string)value["type"] == "log") { AppendLog((string)value["message"]); return; }
                if ((string)value["type"] != "status") return;
                lastMessage = DateTime.UtcNow; staleStopSent = false;
                bool connected = Convert.ToBoolean(value["connected"]), running = Convert.ToBoolean(value["running"]);
                if (value.ContainsKey("account_id")) accountId = Convert.ToString(value["account_id"]);
                lastFault = connected ? null : Convert.ToString(value["message"]);
                ClearAccount();
                if (connected)
                {
                    string[] fields = { "wallet", "available", "unrealized" };
                    for (int i = 0; i < 3; i++)
                    {
                        decimal amount = Convert.ToDecimal(value[fields[i]], CultureInfo.InvariantCulture);
                        values[i].Text = amount.ToString("N2", CultureInfo.InvariantCulture);
                        values[i].ForeColor = i == 2 && amount != 0 ? (amount > 0 ? UiTheme.Positive : UiTheme.Negative) : UiTheme.Text;
                    }
                    foreach (object item in (System.Collections.IEnumerable)value["positions"])
                    {
                        var row = (Dictionary<string, object>)item;
                        int index = positions.Rows.Add(row["symbol"], row["quantity"], row["entry"], row["unrealized"], Convert.ToBoolean(row["managed"]) ? (value.ContainsKey("management_enabled") && Convert.ToBoolean(value["management_enabled"]) ? "관리 중" : "재시작 대기") : "외부 포지션");
                        decimal profit;
                        if (decimal.TryParse(Convert.ToString(row["unrealized"], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out profit))
                            positions.Rows[index].Cells[3].Style.ForeColor = profit > 0 ? UiTheme.Positive : profit < 0 ? UiTheme.Negative : UiTheme.Text;
                        positions.Rows[index].Cells[4].Style.ForeColor = Convert.ToBoolean(row["managed"]) ? UiTheme.Accent : UiTheme.Muted;
                    }
                    positions.ClearSelection();
                }
                SetEmptyPositions(connected);
                SetConnectionBadge(connected ? running ? "자동매매 실행 중" : "실계좌 연결됨" : "연결 실패 / 지연", connected ? UiTheme.Positive : UiTheme.Warning);
                status.ForeColor = connected ? UiTheme.Muted : UiTheme.Warning;
                start.Enabled = connected && !running && HasWorker && !closing;
                status.Text = (connected ? running ? "실계좌 연결됨 · 자동매매 실행 중" : "실계좌 연결됨 · 신규 진입 중지" : "연결 실패 / 지연 · 신규 진입 중지") + "  |  " + DateTime.Now.ToString("HH:mm:ss") + "\n" + value["message"];
            }
            catch (Exception)
            {
                ClearAccount(); start.Enabled = false; SetConnectionBadge("응답 오류", UiTheme.Negative); status.ForeColor = UiTheme.Negative; status.Text = "계좌 응답 형식 오류. 신규 진입을 중지합니다.";
                Send(new Dictionary<string, object> { {"action", "stop"} });
            }
        }
        public void BeginShutdown()
        {
            if (!HasWorker || closing) return;
            closing = true; ResetControls();
            SetConnectionBadge("연결 해제 중", UiTheme.Warning); status.ForeColor = UiTheme.Warning;
            status.Text = "진입을 중지하고 진행 중인 주문 처리를 마친 뒤 연결을 해제합니다. 등록된 TP/SL은 거래소에 유지됩니다.";
            Send(new Dictionary<string, object> { {"action", "shutdown"} });
        }
        private void WorkerExited()
        {
            if (worker != null) { worker.Dispose(); worker = null; }
            closing = false; ClearAccount(); ResetControls();
            SetConnectionBadge("연결 안 됨", UiTheme.Muted); status.ForeColor = UiTheme.Muted;
            status.Text = string.IsNullOrEmpty(lastFault) ? "연결 해제됨. 기존 포지션과 거래소에 등록된 TP/SL은 유지됩니다." : "연결 해제됨 · 마지막 오류: " + lastFault + "\nBinance에서 포지션과 TP/SL 등록 상태를 확인하세요.";
            if (ShutdownCompleted != null) ShutdownCompleted(this, EventArgs.Empty);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { watchdog.Dispose(); if (HasWorker) BeginShutdown(); }
            base.Dispose(disposing);
        }
    }
}
