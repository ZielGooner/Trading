using System;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.IO;
using System.Windows.Forms;
using TradingLauncher;

internal static class LauncherSmokeTest
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static LivePanel FindLivePanel(Control parent)
    {
        LivePanel panel = parent as LivePanel;
        if (panel != null) return panel;
        foreach (Control child in parent.Controls)
        {
            panel = FindLivePanel(child);
            if (panel != null) return panel;
        }
        return null;
    }
    private static void RequireLiveInactive(LivePanel panel, string action)
    {
        Require(panel != null && !panel.HasWorker && !panel.StartEnabled && panel.WalletText == "—",
            action + " unexpectedly connected or enabled live trading");
    }
    private static void RequireLiveOnlyUi(Control parent)
    {
        Require(!parent.Text.Contains("백테스트"), "Backtest UI remains in the live account window");
        foreach (Control child in parent.Controls) RequireLiveOnlyUi(child);
    }
    private static MarketChart FindMarketChart(Control parent)
    {
        MarketChart chart = parent as MarketChart;
        if (chart != null) return chart;
        foreach (Control child in parent.Controls)
        {
            chart = FindMarketChart(child);
            if (chart != null) return chart;
        }
        return null;
    }
    private static void SavePreview(Form form, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        form.PerformLayout();
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path);
        }
    }
    private static void VerifyQuestionTransport(string root)
    {
        var info = HistoryForm.CreateWorkerStartInfo(root);
        info.Arguments = "-B -u -X utf8 -c \"import sys,json; from trading.review import parse_request; value=parse_request(sys.stdin.buffer.readline()); print(json.dumps(value,ensure_ascii=True))\"";
        string question = "한글 질문 🚀📉 🧑‍💻 e\u0301\r\n두 번째 줄";
        var serializer = new JavaScriptSerializer();
        using (var process = new Process { StartInfo = info })
        {
            Require(process.Start(), "Unicode transport probe did not start");
            try
            {
                string wire = HistoryForm.SerializeRequest(new Dictionary<string, object> { {"question", question} });
                foreach (char character in wire) Require(character <= 127, "GUI request depends on the Windows code page");
                process.StandardInput.WriteLine(wire);
                process.StandardInput.Close();
                Require(process.WaitForExit(10000), "Unicode transport probe timed out");
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                Require(process.ExitCode == 0, "Unicode transport failed: " + errors);
                var value = serializer.Deserialize<Dictionary<string, object>>(output);
                Require((string)value["question"] == question, "Korean or emoji changed between GUI and Python");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetFullPath(args[0]);
            VerifyQuestionTransport(root);
            // A shortcut or Explorer may launch from an unrelated working directory.
            Environment.CurrentDirectory = Path.GetTempPath();
            using (var form = new LauncherForm(root))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                RequireLiveOnlyUi(form);
                LivePanel live = FindLivePanel(form);
                RequireLiveInactive(live, "Application launch");
                Require(live.Visible, "Live account page is not visible on launch");
                Require(form.AcceptButton == null, "Enter key has an unexpected default account action");
                MarketChart chart = FindMarketChart(live);
                Require(chart != null, "Historical market chart is missing");
                foreach (string symbol in new string[] { "BTCUSDT", "XRPUSDT", "SOLUSDT" })
                {
                    chart.SelectSymbol(symbol);
                    Require(chart.SelectedSymbol == symbol && chart.CandleCount > 0, "Stored candles did not load for " + symbol);
                    Require(chart.SourceText.Contains("실시간 아님"), "Historical prices were not labeled as non-live");
                    RequireLiveInactive(live, "Historical symbol selection");
                }
                chart.SelectSymbol("BTCUSDT");
                if (args.Length > 1)
                {
                    string previewDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
                    SavePreview(form, args[1]);
                    Size originalClientSize = form.ClientSize;
                    form.ClientSize = new Size(1100, 780);
                    SavePreview(form, Path.Combine(previewDirectory, "launcher-compact-preview.png"));
                    form.ClientSize = originalClientSize;
                    RequireLiveInactive(live, "Preview resize");
                }
            }
            using (var missingChart = new MarketChart(Path.Combine(root, "tests")))
            {
                Require(missingChart.CandleCount == 0 && missingChart.SourceText.Contains("실시간 아님"), "Missing historical data displayed substitute prices");
            }
            using (var panel = new LivePanel(root))
            {
                Require(!panel.HasWorker && !panel.StartEnabled && panel.WalletText == "—", "Live trading was enabled without connection");
                panel.Receive("{\"type\":\"status\",\"connected\":true,\"running\":false,\"management_enabled\":false,\"wallet\":\"1234.56\",\"available\":\"1200\",\"unrealized\":\"-2\",\"positions\":[],\"message\":\"test\"}");
                Require(panel.WalletText == "1,234.56" && !panel.StartEnabled, "Account rendering or start gate failed");
                panel.Receive("{\"type\":\"status\",\"connected\":false,\"running\":false,\"message\":\"offline\"}");
                Require(panel.WalletText == "—" && !panel.StartEnabled, "Disconnected account retained live-looking values");
            }
            using (var history = new HistoryForm(Path.Combine(root, "tests")))
            {
                Require(history.PeriodDays == 30 && !history.IsBusy, "History default period or worker state failed");
                history.SetPeriod(7);
                Require(history.PeriodDays == 7, "7-day period selection failed");
                history.SetPeriod(365);
                Require(history.PeriodDays == 365, "Custom history period failed");
                history.Receive(@"{""type"":""result"",""report"":""검증된 통계와 피드백"",""message"":""완료""}");
                Require(history.ReportText.Contains("피드백"), "Feedback display failed");
                history.Receive(@"{""type"":""error"",""message"":""연결 오류""}");
                Require(!history.ReportText.Contains("검증된 통계"), "Analysis failure retained an old report");
                Require(!history.CanSendQuestion, "Empty chat submission is enabled");
                history.Receive(@"{""type"":""result"",""account"":""0123456789abcdef01234567""}");
                history.QuestionText = "XRP의 수수료 부담은?";
                Require(history.CanSendQuestion && history.AcceptButton == null, "Chat input gate or Enter behavior failed");
                history.Receive(@"{""type"":""result"",""action"":""chat"",""conversation_id"":""0123456789abcdef0123456789abcdef"",""question"":""XRP의 수수료 부담은?"",""answer"":""현재 기간의 수수료와 거래 횟수를 함께 살펴보세요."",""message"":""대화 저장됨""}");
                Require(history.ChatText.Contains("거래 횟수") && history.QuestionText == "", "Chat answer display or input reset failed");
                history.QuestionText = "그 방법을 설명해줘";
                history.Receive(@"{""type"":""error"",""action"":""chat"",""message"":""연결 오류""}");
                Require(history.ChatText.Contains("거래 횟수") && history.QuestionText.Contains("설명"), "Chat failure lost prior conversation or retry input");
                history.SetPeriod(7);
                Require(!history.ChatText.Contains("거래 횟수") && history.QuestionText == "", "Period switch retained prior conversation");
                history.ResetConversation();
                if (args.Length > 1)
                {
                    history.ShowInTaskbar = false; history.Opacity = 0; history.Show();
                    Application.DoEvents();
                    history.Receive(@"{""type"":""result"",""message"":""화면 검증용 예시 · 실제 계좌 연결 및 주문 없음""}");
                    history.QuestionText = "XRP 전략에서 낙폭을 줄이려면 어떤 조건을 먼저 검증해야 할까?";
                    SavePreview(history, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "history-preview.png"));
                    history.ClientSize = new Size(964, 662);
                    SavePreview(history, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "history-compact-preview.png"));
                }
            }
            string credentialRoot = Path.Combine(Path.GetTempPath(), "TradingCredentialTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                CredentialStore.Save(credentialRoot, "TEST-KEY-ONLY-NOT-REAL", "TEST-SECRET-ONLY-NOT-REAL");
                var decoded = CredentialStore.Load(credentialRoot);
                Require((string)decoded["key"] == "TEST-KEY-ONLY-NOT-REAL", "DPAPI round trip failed");
                Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(CredentialStore.FilePath(credentialRoot))).Contains("TEST-SECRET"), "Credentials were stored as plaintext");
            }
            finally
            {
                string credentialFile = CredentialStore.FilePath(credentialRoot);
                if (File.Exists(credentialFile)) File.Delete(credentialFile);
                if (File.Exists(credentialFile + ".tmp")) File.Delete(credentialFile + ".tmp");
                if (Directory.Exists(Path.Combine(credentialRoot, "private_state"))) Directory.Delete(Path.Combine(credentialRoot, "private_state"));
                if (Directory.Exists(credentialRoot)) Directory.Delete(credentialRoot);
            }
            Console.WriteLine("PASS: Live account display, disconnected display gate, no automatic trading on launch, Windows DPAPI encryption.");
            Console.WriteLine("PASS: Single live account window without backtest controls or a default Enter-key action.");
            Console.WriteLine("PASS: BTC/XRP/SOL stored chart selection, non-live labels, and missing-data display without connecting an account.");
            Console.WriteLine("PASS: Launcher loads from an unrelated working directory.");
            Console.WriteLine("PASS: Actual GUI-to-Python JSON pipe preserves Korean, emoji, combining marks and multiline questions.");
            Console.WriteLine("PASS: Chat input, answer display, retry preservation and period reset; no default Enter submission.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
