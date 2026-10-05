using System;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using TradingLauncher;

internal static class LauncherSmokeTest
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void PumpUntil(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Application.DoEvents();
        Require(condition(), message);
    }
    private sealed class FakeChartFeed : IChartFeed
    {
        public event EventHandler<ChartFeedEventArgs> Updated;
        public bool AutoLive, FailInitial, DeferResponses, Disposed, PendingCancelled;
        public long Generation;
        public string Symbol, Interval;
        public readonly HashSet<string> RequestedPairs = new HashSet<string>();
        public IList<ChartCandle> Candles;
        public int SubscriberCount { get { return Updated == null ? 0 : Updated.GetInvocationList().Length; } }
        private bool pending;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public long Select(string symbol, string interval)
        {
            if (Disposed) throw new ObjectDisposedException("FakeChartFeed");
            Symbol = symbol; Interval = interval; Generation++;
            RequestedPairs.Add(symbol + ":" + interval);
            pending = true;
            if (DeferResponses) return Generation;
            if (FailInitial)
            {
                Emit(Frame(ChartFeedState.Reconnecting, null, true, "모의 시세 조회 실패"));
                pending = false;
                return Generation;
            }
            Candles = MakeCandles(symbol, interval);
            Emit(Frame(ChartFeedState.Connecting, Candles, true, "모의 시세 검증"));
            if (AutoLive) Emit(Frame(ChartFeedState.Live, Candles, false, "모의 시세 검증"));
            pending = false;
            return Generation;
        }
        public ChartFeedEventArgs Frame(ChartFeedState state, IList<ChartCandle> candles, bool snapshot, string message)
        {
            return new ChartFeedEventArgs { Symbol = Symbol, Interval = Interval, Generation = Generation,
                State = state, Candles = candles, LastEventTime = snapshot ? 0 : (long)(DateTime.UtcNow - Epoch).TotalMilliseconds,
                ReceivedUtc = DateTime.UtcNow, Message = message, IsSnapshot = snapshot };
        }
        public void Emit(ChartFeedEventArgs frame)
        {
            EventHandler<ChartFeedEventArgs> handler = Updated;
            if (handler != null) handler(this, frame);
        }
        public Action DeferredEmission(ChartFeedEventArgs frame)
        {
            EventHandler<ChartFeedEventArgs> handler = Updated;
            return delegate { if (handler != null) handler(this, frame); };
        }
        public void Dispose() { PendingCancelled = pending; Disposed = true; }
        private static long Seconds(string interval)
        {
            switch (interval) { case "15m": return 900; case "1h": return 3600; case "4h": return 14400;
                case "1d": return 86400; case "1w": return 604800; default: throw new ArgumentException("interval"); }
        }
        private static IList<ChartCandle> MakeCandles(string symbol, string interval)
        {
            long now = (long)(DateTime.UtcNow - Epoch).TotalSeconds, step = Seconds(interval);
            long anchor = interval == "1w" ? 345600 : 0; // Binance UTC Monday week boundary.
            long current = ((now - anchor) / step) * step + anchor;
            double basis = symbol == "BTCUSDT" ? 40000 : symbol == "XRPUSDT" ? 1 : symbol == "SOLUSDT" ? 120 : 30;
            var rows = new List<ChartCandle>();
            for (int i = 0; i < 24; i++)
            {
                long time = current - (23 - i) * step;
                double open = basis * (1 + i * .0001), close = open * 1.0002;
                rows.Add(new ChartCandle { Time = time, CloseTime = (time + step) * 1000 - 1,
                    Open = open, High = close * 1.001, Low = open * .999, Close = close,
                    Volume = 100 + i, Closed = i < 23 });
            }
            return rows;
        }
    }
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
            var chartFeed = new FakeChartFeed();
            using (var form = new LauncherForm(root, chartFeed))
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
                Require(chart != null, "Public market chart is missing");
                PumpUntil(delegate { return chart.CandleCount == 24; }, "Initial snapshot did not render");
                Require(chart.SelectedSymbol == "BTCUSDT" && chart.SelectedInterval == "4h", "Initial chart selection is wrong");
                Require(chart.SourceText.Contains("모의 시세 검증"), "Mock source was not identified");
                Require(chart.SourceText.Contains("연결"), "Snapshot without a live stream was not marked connecting");
                chartFeed.Emit(chartFeed.Frame(ChartFeedState.Live, chartFeed.Candles, false, "모의 시세 검증"));
                PumpUntil(delegate { return chart.SourceText.Contains("실시간"); }, "Live stream state did not render");
                chartFeed.AutoLive = true;
                string[] symbols = { "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT" };
                string[] intervals = { "15m", "1h", "4h", "1d", "1w" };
                foreach (string symbol in symbols)
                {
                    chart.SelectSymbol(symbol);
                    foreach (string interval in intervals)
                    {
                        chart.SelectInterval(interval);
                        double expectedClose = chartFeed.Candles[23].Close;
                        PumpUntil(delegate { return chart.SelectedSymbol == symbol && chart.SelectedInterval == interval && chart.CandleCount == 24 && Math.Abs(chart.LastClose - expectedClose) < .0000001; }, "Pair did not render: " + symbol + " " + interval);
                        Require(chartFeed.RequestedPairs.Contains(symbol + ":" + interval), "Interval was not requested from the feed");
                        Require(chart.SourceText.Contains("UTC"), "Public candle time was not identified as UTC");
                        RequireLiveInactive(live, "Public chart selection");
                    }
                }
                var updated = new List<ChartCandle>(chartFeed.Candles);
                ChartCandle last = updated[23];
                updated[23] = new ChartCandle { Time = last.Time, CloseTime = last.CloseTime,
                    Open = last.Open, High = last.High + 4, Low = last.Low, Close = last.Close + 3,
                    Volume = last.Volume * 2, Closed = false };
                ChartFeedEventArgs previousSelection = chartFeed.Frame(ChartFeedState.Live, updated, false, "모의 이전 선택 응답");
                chartFeed.Emit(chartFeed.Frame(ChartFeedState.Live, updated, false, "모의 진행봉 갱신"));
                PumpUntil(delegate { return Math.Abs(chart.LastClose - updated[23].Close) < .0000001 && chart.LastVolume == updated[23].Volume; }, "Forming weekly candle price/volume did not update");
                Require(chart.CandleCount == 24 && !chart.LastCandleClosed && chart.SourceText.Contains("진행"), "A forming candle was treated as completed");
                chart.SelectSymbol("XRPUSDT"); chart.SelectInterval("1h");
                double selectedClose = chartFeed.Candles[23].Close;
                PumpUntil(delegate { return Math.Abs(chart.LastClose - selectedClose) < .0000001; }, "New selection did not render before delayed response");
                chartFeed.Emit(previousSelection);
                var wrongPair = chartFeed.Frame(ChartFeedState.Live, updated, false, "모의 잘못된 종목 응답");
                wrongPair.Symbol = "BTCUSDT";
                chartFeed.Emit(wrongPair);
                Application.DoEvents();
                Require(chart.SelectedSymbol == "XRPUSDT" && chart.SelectedInterval == "1h" && Math.Abs(chart.LastClose - selectedClose) < .0000001, "An old generation or wrong pair overwrote the active chart");
                var stale = chartFeed.Frame(ChartFeedState.Live, chartFeed.Candles, false, "모의 시세 지연");
                stale.ReceivedUtc = DateTime.UtcNow.AddMinutes(-2);
                stale.LastEventTime -= 120000;
                chartFeed.Emit(stale);
                PumpUntil(delegate { return chart.SourceText.Contains("지연"); }, "Stale prices retained a healthy live label");
                chartFeed.Emit(chartFeed.Frame(ChartFeedState.Live, chartFeed.Candles, false, "모의 시세 검증"));
                PumpUntil(delegate { return chart.SourceText.Contains("실시간"); }, "Fresh prices did not recover from stale state");
                chartFeed.Emit(chartFeed.Frame(ChartFeedState.Reconnecting, null, false, "모의 연결 끊김"));
                PumpUntil(delegate { return chart.SourceText.Contains("재연결"); }, "Offline state was not shown");
                chart.SelectSymbol("BTCUSDT");
                chart.SelectInterval("4h");
                PumpUntil(delegate { return chart.SourceText.Contains("실시간") && chart.CandleCount == 24; }, "Chart did not recover after reconnect");
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
                chartFeed.DeferResponses = true;
                chart.SelectInterval("15m");
                Action lateResponse = chartFeed.DeferredEmission(chartFeed.Frame(ChartFeedState.Live, updated, false, "모의 종료 후 응답"));
                form.Dispose();
                Require(chartFeed.Disposed && chartFeed.PendingCancelled && chartFeed.SubscriberCount == 0, "Closing did not cancel and detach the chart feed");
                lateResponse();
                Application.DoEvents();
            }
            using (var missingChart = new MarketChart(new FakeChartFeed { FailInitial = true }))
            {
                IntPtr handle = missingChart.Handle;
                PumpUntil(delegate { return missingChart.SourceText.Contains("조회 실패"); }, "Initial public feed failure was not shown");
                Require(missingChart.CandleCount == 0, "Initial feed failure displayed substitute candles");
            }
            using (var panel = new LivePanel(root, new FakeChartFeed { AutoLive = true }))
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
            Console.WriteLine("PASS: Offline mock feed verifies four symbols, five intervals, forming candle/volume updates, old-response rejection, stale/offline labels and disposal cancellation.");
            Console.WriteLine("PASS: Launcher loads from an unrelated working directory.");
            Console.WriteLine("PASS: Actual GUI-to-Python JSON pipe preserves Korean, emoji, combining marks and multiline questions.");
            Console.WriteLine("PASS: Chat input, answer display, retry preservation and period reset; no default Enter submission.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
