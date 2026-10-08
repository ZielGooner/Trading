using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TradingLauncher
{
    public sealed class AnalysisPanel : UserControl
    {
        private readonly string root;
        private string account;
        private readonly NumberField days = new NumberField(1, 365, 30);
        private readonly Button sync = new ModernButton(), excel = new ModernButton(), analyze = new ModernButton();
        private readonly Button cancel = new ModernButton(), login = new ModernButton(), folder = new ModernButton();
        private readonly Label status = new Label(), accountLabel = new Label();
        private readonly TextBox report = new TextBox(), conversation = new TextBox(), question = new TextBox();
        private readonly Button send = new ModernButton(), newChat = new ModernButton();
        private readonly Panel analysisTab = new Panel(), chatTab = new Panel();
        private readonly Button analysisView = new ModernButton(), chatView = new ModernButton();
        private readonly FlowLayoutPanel period;
        private string conversationId, activeAction;
        public string ChatText { get { return conversation.Text; } }
        public string QuestionText { get { return question.Text; } set { question.Text = value; } }
        public bool CanSendQuestion { get { return send.Enabled; } }
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly ReviewWorker worker = new ReviewWorker();
        private bool gotResult;
        private string lastExcel;
        public event EventHandler OperationCompleted;
        public int PeriodDays { get { return days.Value; } }
        public string ReportText { get { return report.Text; } }
        public bool IsBusy { get { return worker.IsBusy; } }
        public bool AutoDetectAccount { get; set; }
        public AnalysisPanel(string projectRoot, string currentAccount = null)
        {
            root = Path.GetFullPath(projectRoot);
            account = currentAccount;
            AutoDetectAccount = true;
            Text = "분석";
            Dock = DockStyle.Fill;
            Font = UiTheme.Font(10F);
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(layout);
            var title = UiTheme.Label("거래 분석", 22F, UiTheme.Text, FontStyle.Bold);
            layout.Controls.Add(title, 0, 0);
            period = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Font = UiTheme.Font(9F) };
            period.Controls.Add(new Label { Text = "최근", AutoSize = true, ForeColor = UiTheme.Muted, Padding = new Padding(0, 9, 4, 0) });
            days.Name = "AnalysisPeriodDays"; days.Margin = new Padding(0, 2, 3, 0);
            period.Controls.Add(days);
            period.Controls.Add(new Label { Text = "일", AutoSize = true, ForeColor = UiTheme.Muted, Padding = new Padding(0, 9, 14, 0) });
            foreach (int preset in new int[] { 7, 30, 90 })
            {
                int selected = preset;
                var button = new ModernButton();
                UiTheme.StyleButton(button, preset.ToString() + "일", 65, false);
                button.Height = 32; button.Margin = new Padding(0, 2, 8, 0);
                button.Click += delegate { days.Value = selected; };
                period.Controls.Add(button);
            }
            accountLabel.AutoSize = true; accountLabel.ForeColor = UiTheme.Muted;
            accountLabel.Padding = new Padding(15, 8, 0, 0);
            period.Controls.Add(accountLabel);
            layout.Controls.Add(period, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            UiTheme.StyleButton(sync, "내역 새로고침", 142, false);
            UiTheme.StyleButton(excel, "엑셀 저장·열기", 142, false);
            UiTheme.StyleButton(analyze, "Codex 분석", 132, true);
            UiTheme.StyleButton(cancel, "분석 취소", 108, false);
            UiTheme.StyleButton(login, "Codex 로그인", 130, false);
            UiTheme.StyleButton(folder, "저장 폴더", 108, false);
            actions.Controls.AddRange(new Control[] { sync, excel, analyze, cancel, login, folder });
            layout.Controls.Add(actions, 0, 2);
            status.Dock = DockStyle.Fill; status.ForeColor = UiTheme.Muted;
            status.Text = "엑셀 내역과 기간별 전략 분석을 준비합니다.";
            layout.Controls.Add(status, 0, 3);
            report.Dock = DockStyle.Fill; report.Multiline = true; report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Both; report.WordWrap = true; report.BorderStyle = BorderStyle.None;
            report.BackColor = UiTheme.Surface; report.ForeColor = UiTheme.Text; report.Font = UiTheme.Font(10F);
            report.Text = "실제 체결과 수수료·펀딩을 저장합니다.\r\n전략과 연결된 완료 거래가 쌓이면 기간별 피드백을 받을 수 있습니다.";
            var views = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            views.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            views.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            views.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var viewButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            UiTheme.StyleButton(analysisView, "분석 보고서", 135, true);
            UiTheme.StyleButton(chatView, "Codex 대화", 135, false);
            analysisView.Name = "AnalysisReportTab"; chatView.Name = "AnalysisChatTab";
            viewButtons.Controls.AddRange(new Control[] { analysisView, chatView });
            views.Controls.Add(viewButtons, 0, 0);
            var content = UiTheme.Card(); content.Name = "AnalysisContentCard"; content.Padding = new Padding(1);
            analysisTab.Dock = chatTab.Dock = DockStyle.Fill;
            analysisTab.Padding = chatTab.Padding = new Padding(12);
            analysisTab.BackColor = chatTab.BackColor = UiTheme.Surface;
            analysisTab.Controls.Add(report);
            content.Controls.Add(analysisTab); content.Controls.Add(chatTab);
            views.Controls.Add(content, 0, 1);
            analysisView.Click += delegate { ShowConversation(false); };
            chatView.Click += delegate { ShowConversation(true); question.Focus(); };
            var chatLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            chatLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            chatLayout.Controls.Add(new Label { Text = "Astra · Extra High  |  선택 기간의 거래내역·현재 전략과 대화를 함께 참고합니다.",
                Dock = DockStyle.Fill, ForeColor = UiTheme.Muted }, 0, 0);
            conversation.Dock = DockStyle.Fill; conversation.Multiline = true; conversation.ReadOnly = true;
            conversation.ScrollBars = ScrollBars.Vertical; conversation.BorderStyle = BorderStyle.None;
            conversation.BackColor = UiTheme.Surface; conversation.ForeColor = UiTheme.Text; conversation.Font = UiTheme.Font(10F);
            var conversationBox = UiTheme.TextArea(conversation, UiTheme.Background); conversationBox.Name = "ConversationCard";
            chatLayout.Controls.Add(conversationBox, 0, 1);
            var composer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 10, 0, 0) };
            composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            composer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            question.Multiline = true; question.AcceptsReturn = true; question.MaxLength = 4000;
            question.Dock = DockStyle.Fill; question.ScrollBars = ScrollBars.Vertical;
            question.BackColor = UiTheme.Background; question.ForeColor = UiTheme.Text;
            question.Font = UiTheme.Font(10F); question.AccessibleName = "Codex에게 보낼 질문";
            var questionBox = UiTheme.TextArea(question, UiTheme.Background); questionBox.Name = "QuestionInputCard";
            composer.Controls.Add(questionBox, 0, 0);
            var chatButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            UiTheme.StyleButton(send, "질문 보내기", 118, true); send.Height = 32;
            UiTheme.StyleButton(newChat, "새 대화", 118, false); newChat.Height = 30;
            chatButtons.Controls.AddRange(new Control[] { send, newChat }); composer.Controls.Add(chatButtons, 1, 0);
            var hint = new Label { Text = "Enter 줄바꿈 · Ctrl+Enter 전송 · 최대 4,000자 · API 키를 입력하지 마세요.",
                Dock = DockStyle.Fill, Font = UiTheme.Font(8F), ForeColor = UiTheme.Muted };
            composer.Controls.Add(hint, 0, 1); composer.SetColumnSpan(hint, 2);
            chatLayout.Controls.Add(composer, 0, 2); chatTab.Controls.Add(chatLayout);
            layout.Controls.Add(views, 0, 4);
            ShowConversation(false);
            ResetConversation();
            var note = new Label { Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Font = UiTheme.Font(9F),
                Text = "분석·대화는 Astra Extra High로 실행됩니다. 질문과 거래 통계·현재 전략을 Codex에 전달합니다.\n답변과 대화는 로컬에 저장되며, 전략과 실제 주문을 자동으로 변경하지 않습니다.", Padding = new Padding(0, 10, 0, 0) };
            layout.Controls.Add(note, 0, 5);
            sync.Click += async delegate { await RunOperation("sync", false); };
            excel.Click += async delegate { await RunOperation("export", true); };
            analyze.Click += async delegate { await RunOperation("analyze", false); };
            cancel.Click += delegate { CancelOperation(); };
            send.Click += async delegate { await RunOperation("chat", false); };
            newChat.Click += delegate { ResetConversation(); question.Focus(); };
            question.TextChanged += delegate { UpdateButtons(); };
            question.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true; e.Handled = true;
                    if (CanSendQuestion) await RunOperation("chat", false);
                }
            };
            days.ValueChanged += delegate
            {
                ResetConversation();
                report.Text = "기간을 변경했습니다. 내역 새로고침 또는 Codex 분석을 눌러 확인하세요.";
            };
            folder.Click += delegate
            {
                string path = account == null ? Path.Combine(root, "records") : Path.Combine(root, "records", account);
                Directory.CreateDirectory(path); OpenPath(path);
            };
            login.Click += delegate { Login(); };
            UpdateButtons();
        }
        public void RequestCancellation() { CancelOperation(); }
        public void SetAccount(string value)
        {
            if (IsBusy) return;
            if (!string.IsNullOrEmpty(value) && account != value)
            {
                account = value; ResetConversation(); report.Text = "계정이 변경되었습니다. Codex 분석을 눌러 확인하세요.";
            }
            try { IdentifyAccount(); } catch (Exception error) { status.Text = "계정 확인 실패: " + error.Message; }
            UpdateButtons();
        }

        private void IdentifyAccount()
        {
            if (!AutoDetectAccount) return;
            string previousAccount = account;
            account = ReviewWorker.IdentifyAccount(root, account);
            if (previousAccount != account)
            {
                ResetConversation(); report.Text = "계정이 변경되었습니다. 선택 계정의 내역을 다시 확인하세요.";
            }
        }
        private void ShowConversation(bool showChat)
        {
            chatTab.Visible = showChat; analysisTab.Visible = !showChat;
            UiTheme.StyleButton(analysisView, "분석 보고서", 135, !showChat);
            UiTheme.StyleButton(chatView, "Codex 대화", 135, showChat);
        }
        public void ResetConversation()
        {
            if (IsBusy) return;
            conversationId = null;
            conversation.Text = "질문을 입력하세요. 예: XRP 수수료 부담을 줄이려면 어떤 조건을 검증해야 할까?\r\n"
                + "선택 기간에 해당하는 최근 분석 보고서도 함께 참고합니다. 기간·계정을 바꾸면 새 대화를 시작합니다.\r\n";
            question.Clear();
        }
        private void AppendConversation(string text)
        {
            conversation.AppendText("\r\n" + text + "\r\n");
            conversation.SelectionStart = conversation.TextLength; conversation.ScrollToCaret();
        }
        private void UpdateButtons()
        {
            bool idle = !IsBusy;
            sync.Enabled = login.Enabled = days.Enabled = idle;
            period.Enabled = idle;
            send.Enabled = idle && account != null && !string.IsNullOrWhiteSpace(question.Text);
            question.ReadOnly = !idle; newChat.Enabled = idle;
            excel.Enabled = analyze.Enabled = idle && account != null;
            cancel.Enabled = !idle;
            accountLabel.Text = account == null ? "계정 선택 전" : "계정 " + account.Substring(0, 8);
        }
        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show("파일을 열지 못했습니다: " + error.Message); }
        }
        private void Login()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string directory = Path.Combine(local, "OpenAI", "Codex", "bin");
                string[] found = Directory.GetFiles(directory, "codex.exe", SearchOption.AllDirectories);
                if (found.Length == 0) throw new FileNotFoundException("Codex CLI를 찾지 못했습니다.");
                Array.Sort(found, delegate(string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                // The user requested an interactive login by pressing this button.
                Process.Start(new ProcessStartInfo(found[0], "login") { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });
                status.Text = "열린 로그인 창에서 ChatGPT 계정으로 로그인한 뒤 Codex 분석을 다시 누르세요.";
            }
            catch (Exception error) { status.Text = "로그인 실행 실패: " + error.Message; }
        }
        private void CancelOperation()
        {
            if (!IsBusy) return;
            worker.Cancel(); status.Text = "진행 중인 저장·분석 작업을 취소하고 있습니다.";
        }
        public void SetPeriod(int value) { days.Value = value; }
        public void Receive(string line)
        {
            var value = json.Deserialize<Dictionary<string, object>>(line);
            string type = Convert.ToString(value["type"]);
            if (type == "progress") { status.Text = Convert.ToString(value["message"]); return; }
            if (type == "error")
            {
                gotResult = true;
                status.Text = Convert.ToString(value["message"]);
                if (activeAction == "chat" || (value.ContainsKey("action") && Convert.ToString(value["action"]) == "chat"))
                    AppendConversation("[답변 실패] " + status.Text + "\r\n입력한 질문을 확인한 뒤 다시 전송할 수 있습니다.");
                else report.Text = "작업을 완료하지 못했습니다.\r\n\r\n" + status.Text;
                return;
            }
            if (type != "result") return;
            gotResult = true;
            if (value.ContainsKey("account")) account = Convert.ToString(value["account"]);
            if (value.ContainsKey("excel")) lastExcel = Convert.ToString(value["excel"]);
            if (value.ContainsKey("answer"))
            {
                conversationId = Convert.ToString(value["conversation_id"]);
                AppendConversation("[나]\r\n" + Convert.ToString(value["question"]) +
                    "\r\n\r\n[Codex · Astra Extra High]\r\n" + Convert.ToString(value["answer"]));
                if (value.ContainsKey("context_note") && !string.IsNullOrEmpty(Convert.ToString(value["context_note"])))
                    AppendConversation(Convert.ToString(value["context_note"]));
                question.Clear(); ShowConversation(true);
            }
            if (value.ContainsKey("report")) report.Text = Convert.ToString(value["report"]).Replace("\n", "\r\n");
            status.Text = value.ContainsKey("message") ? Convert.ToString(value["message"]) : "작업 완료";
            if (value.ContainsKey("path") && value["path"] != null && !value.ContainsKey("answer")) status.Text += " · 피드백 파일 저장됨";
            UpdateButtons();
        }
        private async Task RunOperation(string action, bool openExcel)
        {
            if (IsBusy) return;
            Dictionary<string, object> request = null;
            try
            {
                IdentifyAccount();
                activeAction = action;
                if (action == "chat" && string.IsNullOrWhiteSpace(question.Text)) return;
                request = new Dictionary<string, object> { {"action", action}, {"days", PeriodDays}, {"account", account} };
                if (action == "chat")
                {
                    request["question"] = question.Text;
                    request["conversation_id"] = conversationId;
                    ShowConversation(true);
                }
                if (action == "sync")
                {
                    if (!File.Exists(CredentialStore.FilePath(root))) throw new InvalidOperationException("실계좌 화면에서 API를 먼저 설정하세요.");
                    var credentials = CredentialStore.Load(root);
                    request["api_key"] = credentials["key"]; request["api_secret"] = credentials["secret"];
                    credentials.Clear();
                }
                if (account == null && action != "sync") throw new InvalidOperationException("API 설정과 내역 새로고침을 먼저 실행하세요.");
                gotResult = false;
                status.Text = action == "chat" ? "Astra Extra High 답변을 준비합니다…" :
                    action == "analyze" ? "저장 내역을 기준으로 Codex 분석을 준비합니다…" : "거래 내역을 확인하고 있습니다…";
                if (action == "analyze")
                {
                    ShowConversation(false);
                    report.Text = "Astra Extra High 분석 중입니다. 완료되면 결과가 여기에 표시됩니다.";
                }
                int exitCode = await worker.RunAsync(root, request, Receive, UpdateButtons);
                if (IsDisposed) return;
                if (!gotResult) throw new InvalidOperationException("처리 결과를 받지 못했습니다. 실행 환경을 확인하세요.");
                if (exitCode == 0 && openExcel && lastExcel != null && File.Exists(lastExcel)) OpenPath(lastExcel);
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    status.Text = "작업 실패: " + error.Message;
                    if (action == "chat") AppendConversation("[답변 실패] " + status.Text);
                    else report.Text = status.Text;
                }
            }
            finally
            {
                if (request != null) request.Clear();
                activeAction = null;
                if (!IsDisposed)
                {
                    UpdateButtons(); if (action == "chat") question.Focus();
                    if (OperationCompleted != null) OperationCompleted(this, EventArgs.Empty);
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) worker.Dispose(); base.Dispose(disposing); }
    }
}

