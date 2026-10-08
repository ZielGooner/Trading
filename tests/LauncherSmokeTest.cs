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
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr source);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder name);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, ushort[] glyphs, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    private static void VerifyNativeFont(Font font)
    {
        using (var input = new TextBox { Font = font })
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            Require(dc != IntPtr.Zero, "Could not create a native font probe");
            IntPtr previous = SelectObject(dc, SendMessage(input.Handle, 0x31, IntPtr.Zero, IntPtr.Zero));
            try
            {
                var name = new System.Text.StringBuilder(64);
                Require(GetTextFace(dc, name.Capacity, name) > 0 && name.ToString() == "Pretendard", "Native text input fell back to another font: " + name);
                string sample = "거래 내역 분석 기간 심볼 long short 0123456789,.%+-";
                var glyphs = new ushort[sample.Length];
                Require(GetGlyphIndices(dc, sample, sample.Length, glyphs, 1) != uint.MaxValue, "Native font glyph inspection failed");
                foreach (ushort glyph in glyphs) Require(glyph != ushort.MaxValue, "Pretendard is missing a UI text or numeric glyph");
            }
            finally { SelectObject(dc, previous); DeleteDC(dc); }
        }
    }
    private static void RequireUiFont(Control control)
    {
        Require(control.Font.Name == "Pretendard", "UI control did not inherit the bundled font: " + control.GetType().Name);
        foreach (Control child in control.Controls) RequireUiFont(child);
    }
    private static void VerifyTypography(LauncherForm form)
    {
        RequireUiFont(form); VerifyNativeFont(form.Font);
        using (var bold = new Font(form.Font.FontFamily, 10F, FontStyle.Bold, GraphicsUnit.Point)) VerifyNativeFont(bold);
        var assembly = typeof(LauncherForm).Assembly;
        using (Stream stream = assembly.GetManifestResourceStream("Aurex.Fonts.OFL.txt"))
        {
            Require(stream != null, "Font copyright/license is missing from the executable");
            using (var reader = new StreamReader(stream))
            {
                string license = reader.ReadToEnd();
                Require(license.Contains("Kil Hyung-jin") && license.Contains("SIL OPEN FONT LICENSE Version 1.1"), "Bundled font license is incomplete");
                string accompanying = Path.Combine(Path.GetDirectoryName(assembly.Location), "Trading.font-license.txt");
                Require(File.Exists(accompanying) && File.ReadAllText(accompanying) == license, "Readable font license is not included beside the executable");
            }
        }
    }
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
            // DrawToBitmap reverses sibling painting order. Render the actual
            // front overlay separately so the preview matches its z-order.
            Control popup = FindNamed(form, "PickerPopup");
            if (popup != null && popup.Visible)
            {
                using (var popupBitmap = new Bitmap(popup.Width, popup.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    popup.DrawToBitmap(popupBitmap, new Rectangle(Point.Empty, popupBitmap.Size));
                    Point clientOrigin = form.PointToScreen(Point.Empty);
                    graphics.DrawImageUnscaled(popupBitmap, popup.Left + clientOrigin.X - form.Left, popup.Top + clientOrigin.Y - form.Top);
                }
            }
            bitmap.Save(path);
        }
    }
    private static void VerifyQuestionTransport(string root)
    {
        var info = ReviewWorker.CreateStartInfo(root);
        info.Arguments = "-B -u -X utf8 -c \"import sys,json; from trading.review import parse_request; value=parse_request(sys.stdin.buffer.readline()); print(json.dumps(value,ensure_ascii=True))\"";
        string question = "한글 질문 🚀📉 🧑‍💻 e\u0301\r\n두 번째 줄";
        var serializer = new JavaScriptSerializer();
        using (var process = new Process { StartInfo = info })
        {
            Require(process.Start(), "Unicode transport probe did not start");
            try
            {
                string wire = ReviewWorker.SerializeRequest(new Dictionary<string, object> { {"question", question} });
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
    private static void VerifyListTransport(string root)
    {
        string account = Guid.NewGuid().ToString("N").Substring(0, 24);
        string database = Path.Combine(root, "private_state", "history-" + account + ".sqlite3");
        Require(!File.Exists(database), "The isolated list-test account already exists");
        Dictionary<string, object> response = null;
        var serializer = new JavaScriptSerializer();
        using (var worker = new ReviewWorker())
        {
            var request = new Dictionary<string, object> { {"action", "list"}, {"account", account}, {"start_ms", 1790780400000L}, {"end_ms", 1790866800000L}, {"symbol", "XRPUSDT"}, {"page", 10}, {"view", "fills"} };
            var task = worker.RunAsync(root, request, delegate(string line) { response = serializer.Deserialize<Dictionary<string, object>>(line); }, null);
            PumpUntil(delegate { return task.IsCompleted; }, "Read-only history pipe timed out");
            Require(task.GetAwaiter().GetResult() == 0 && response != null && Convert.ToString(response["type"]) == "result", "History list protocol did not return a result");
            Require(Convert.ToInt32(response["total"]) == 0 && Convert.ToInt32(response["page"]) == 1 && Convert.ToInt32(response["page_size"]) == 20 && !worker.IsBusy, "History list paging or process cleanup failed");
        }
        Require(!File.Exists(database), "A read-only history query created an account database");
    }
    private static DataGridView FindGrid(Control parent)
    {
        DataGridView grid = parent as DataGridView;
        if (grid != null) return grid;
        foreach (Control child in parent.Controls) { grid = FindGrid(child); if (grid != null) return grid; }
        return null;
    }
    private static void MoveMouse(MarketChart chart, int index, double vertical)
    {
        RectangleF plot = chart.PricePlot;
        int x = (int)(plot.Left + plot.Width * (index + .5) / chart.CandleCount);
        int y = (int)(plot.Top + plot.Height * vertical);
        typeof(Control).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(chart.ChartSurface, new object[] { new MouseEventArgs(MouseButtons.None, 0, x, y, 0) });
        Application.DoEvents();
    }
    private static void LeaveMouse(MarketChart chart)
    {
        typeof(Control).GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(chart.ChartSurface, new object[] { EventArgs.Empty });
        Application.DoEvents();
    }
    private static void VerifyHover(MarketChart chart, FakeChartFeed feed)
    {
        MoveMouse(chart, 3, .35);
        Require(chart.HoveredCandleIndex == 3 && !double.IsNaN(chart.HoverPrice), "Mouse did not select the expected candle/price");
        ChartCandle selected = feed.Candles[3];
        string selectedVolume = selected.Volume.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
        Require(chart.QuoteText.Contains("시 ") && chart.QuoteText.Contains("고 ") && chart.QuoteText.Contains("저 ") && chart.QuoteText.Contains("종 ") && chart.QuoteText.Contains("거래량 " + selectedVolume), "Hover values are missing from the top OHLC strip");
        string hovered = chart.QuoteText;
        var updated = new List<ChartCandle>(feed.Candles);
        ChartCandle last = updated[23];
        updated[23] = new ChartCandle { Time = last.Time, CloseTime = last.CloseTime, Open = last.Open, High = last.High + 2, Low = last.Low, Close = last.Close + 1, Volume = 789, Closed = false };
        feed.Emit(feed.Frame(ChartFeedState.Live, updated, false, "모의 호버 중 갱신"));
        PumpUntil(delegate { return chart.LastVolume == 789; }, "Live feed stopped while hovering");
        Require(chart.QuoteText == hovered, "A live update overwrote the hovered historical candle");
        LeaveMouse(chart);
        Require(chart.HoveredCandleIndex == -1 && double.IsNaN(chart.HoverPrice) && chart.QuoteText.Contains("거래량 789.00"), "Mouse leave did not restore the latest values");
        MoveMouse(chart, 23, .65);
        Require(chart.HoveredCandleIndex == 23 && chart.QuoteText.Contains("거래량 789.00"), "Last candle hover is incorrect");
        typeof(Control).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(chart.ChartSurface, new object[] { new MouseEventArgs(MouseButtons.None, 0, chart.ChartSurface.Width - 1, 10, 0) });
        Require(chart.HoveredCandleIndex == -1, "Price-axis margin selected a candle");
        MoveMouse(chart, 5, .5);
        chart.SelectInterval("1h");
        Require(chart.HoveredCandleIndex == -1, "Timeframe change retained old hover data");
        PumpUntil(delegate { return chart.CandleCount == 24; }, "Chart did not return after hover selection test");
        chart.SelectInterval("4h");
        PumpUntil(delegate { return chart.CandleCount == 24; }, "Chart did not return to default interval");
    }
    private static Dictionary<string, object> HistoryFixture(int first, int rows, int total, int page)
    {
        var records = new List<object>();
        for (int i = 0; i < rows; i++) records.Add(new Dictionary<string, object> {
            {"time", 1791414000000L - (first + i) * 3600000L}, {"symbol", i % 2 == 0 ? "BTCUSDT" : "XRPUSDT"}, {"direction", i % 2 == 0 ? "long" : "short"},
            {"entry", i % 2 == 0 ? 62000.0 : .61}, {"exit", i % 2 == 0 ? 62100.0 : .60}, {"quantity", .01}, {"fees", .5}, {"fee_asset", "USDT"},
            {"gross", i % 2 == 0 ? 10.5 : -3.25}, {"status", "화면 검증용 예시"} });
        return new Dictionary<string, object> { {"type", "result"}, {"view", "trades"}, {"rows", records}, {"total", total}, {"page", page}, {"pages", (total + 19) / 20}, {"synced_at", null}, {"sync_error", null} };
    }
    private static void VerifyTabs(LauncherForm form, LivePanel live)
    {
        int windows = Application.OpenForms.Count;
        var serializer = new JavaScriptSerializer();
        Require(form.Text == "AUREX" && form.Icon != null && form.SelectedPage == "main", "AUREX title, icon or default tab is missing");
        form.SelectPage("history"); Application.DoEvents();
        Require(form.Transactions.Visible && !live.Visible && Application.OpenForms.Count == windows, "History opened another window");
        form.Transactions.Receive(serializer.Serialize(HistoryFixture(0, 20, 45, 1)));
        Require(form.Transactions.RowCount == 20 && form.Transactions.TotalCount == 45 && form.Transactions.CurrentPage == 1, "First history page did not show 20 rows");
        Require(Convert.ToString(form.Transactions.HistoryGrid.Rows[0].Cells[3].Value) == "long" && Convert.ToString(form.Transactions.HistoryGrid.Rows[1].Cells[3].Value) == "short", "History direction labels are incorrect");
        form.Transactions.Receive(serializer.Serialize(HistoryFixture(40, 5, 45, 3)));
        Require(form.Transactions.RowCount == 5 && form.Transactions.CurrentPage == 3 && Convert.ToInt32(form.Transactions.HistoryGrid.Rows[0].Cells[0].Value) == 41, "Last history page or continuous numbering failed");
        form.Transactions.SetFilters(new DateTime(2026, 10, 1), new DateTime(2026, 10, 8), "XRPUSDT");
        Require(form.Transactions.RowCount == 0 && form.Transactions.CurrentPage == 1, "Changing history filters retained mismatched rows");
        form.SelectPage("analysis"); Application.DoEvents();
        Require(form.Analysis.Visible && !form.Transactions.Visible && Application.OpenForms.Count == windows, "Analysis opened another window");
        form.Analysis.QuestionText = "이 대화는 탭을 바꿔도 유지됩니다";
        form.SelectPage("main"); form.SelectPage("analysis");
        Require(form.Analysis.QuestionText.Contains("유지"), "Tab switch lost analysis draft");
        form.SelectPage("main");
        Require(live.Visible && Application.OpenForms.Count == windows, "Main tab did not return to the same window");
        RequireLiveInactive(live, "Navigation");
    }
    private static Control FindNamed(Control parent, string name)
    {
        if (parent.Name == name) return parent;
        foreach (Control child in parent.Controls) { Control found = FindNamed(child, name); if (found != null) return found; }
        return null;
    }
    private static void OpenPicker(Control field)
    {
        field.GetType().GetMethod("ShowDropDown").Invoke(field, null); Application.DoEvents();
        Require(PickerOpen(field), field.Name + " did not open");
        Form form = field.FindForm(); Control popup = FindNamed(form, "PickerPopup");
        Require(popup != null && popup.Visible && popup.Controls[0].Visible && form.Controls.GetChildIndex(popup) == 0 && form.ClientRectangle.Contains(popup.Bounds), "Picker is hidden, clipped or behind the page");
    }
    private static void ClosePicker(Control field) { field.GetType().GetMethod("CloseDropDown").Invoke(field, null); Application.DoEvents(); }
    private static bool PickerOpen(Control field) { return (bool)field.GetType().GetProperty("IsDropDownOpen").GetValue(field, null); }
    private static void PressKey(Control control, Keys key)
    {
        Message message = Message.Create(control.Handle, 0x100, (IntPtr)key, IntPtr.Zero);
        if (!Application.FilterMessage(ref message) && !control.PreProcessMessage(ref message))
            control.GetType().GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(control, new object[] { new KeyEventArgs(key) });
        Application.DoEvents();
    }
    private static void ClickAt(Control control, int x, int y)
    {
        control.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(control, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        Application.DoEvents();
    }
    private static DateTime PickerDate(Control field) { return (DateTime)field.GetType().GetProperty("Value").GetValue(field, null); }
    private static void VerifyPickers(LauncherForm form)
    {
        form.SelectPage("history"); Application.DoEvents();
        int windows = Application.OpenForms.Count;
        string[] names = { "HistoryStartDate", "HistoryEndDate", "HistorySymbol", "HistoryView" };
        foreach (string name in names)
        {
            Control field = FindNamed(form.Transactions, name);
            Require(field != null, "Missing themed picker " + name);
            for (int i = 0; i < 25; i++)
            {
                OpenPicker(field);
                Require(Application.OpenForms.Count == windows, "Picker created a separate window");
                Message outsideClick = Message.Create(form.Transactions.HistoryGrid.Handle, 0x201, IntPtr.Zero, IntPtr.Zero);
                Require(!Application.FilterMessage(ref outsideClick), "Outside click was swallowed");
                Application.DoEvents();
                Require(!PickerOpen(field) && FindNamed(form, "PickerPopup") == null, "Outside click retained a popup");
            }
            OpenPicker(field); form.SelectPage("analysis"); Application.DoEvents();
            Require(!PickerOpen(field) && FindNamed(form, "PickerPopup") == null, "Tab switch retained a popup: " + name);
            form.SelectPage("history"); OpenPicker(field);
            Control content = FindNamed(form, "PickerPopup").Controls[0];
            Message escape = Message.Create(content.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero);
            Require(Application.FilterMessage(ref escape) && !PickerOpen(field), "Escape did not dismiss the picker");
            OpenPicker(field); field.Enabled = false;
            Require(!PickerOpen(field), "Disabled picker retained its popup"); field.Enabled = true;
        }
        Control start = FindNamed(form.Transactions, names[0]), end = FindNamed(form.Transactions, names[1]), symbol = FindNamed(form.Transactions, names[2]), view = FindNamed(form.Transactions, names[3]);
        form.Transactions.SetFilters(new DateTime(2024, 2, 28), new DateTime(2024, 3, 31), "");
        OpenPicker(start); Control calendar = FindNamed(form, "PickerPopup").Controls[0];
        PressKey(calendar, Keys.Right); PressKey(calendar, Keys.Enter);
        Require(PickerDate(start) == new DateTime(2024, 2, 29) && !PickerOpen(start), "Leap day selection failed");
        OpenPicker(start); PressKey(calendar, Keys.PageDown); PressKey(calendar, Keys.End); PressKey(calendar, Keys.Enter);
        Require(PickerDate(start) == new DateTime(2024, 3, 31), "Calendar month navigation failed");
        form.Transactions.SetFilters(new DateTime(2026, 12, 31), new DateTime(2027, 1, 31), "");
        OpenPicker(start); ClickAt(calendar, 247, 26); ClickAt(calendar, 210, 94);
        Require(PickerDate(start) == new DateTime(2027, 1, 1) && !PickerOpen(start), "Mouse selection across a year boundary failed");
        OpenPicker(start); ClickAt(calendar, 120, 316);
        Require(PickerDate(start) == DateTime.Today, "Today button failed");
        OpenPicker(start); OpenPicker(end);
        Require(!PickerOpen(start) && PickerOpen(end), "Two popups were open at once"); ClosePicker(end);
        TextBox input = (TextBox)start.Controls[0]; DateTime previousDate = PickerDate(start);
        input.Text = "2026.02.30"; OpenPicker(start);
        Require(PickerDate(start) == previousDate, "Invalid typed date replaced a valid date"); ClosePicker(start);
        input.Text = "2026.10.01"; input.Focus();
        object[] command = { Message.Create(input.Handle, 0x100, (IntPtr)Keys.Enter, IntPtr.Zero), Keys.Enter };
        start.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(start, command);
        Require(PickerDate(start) == new DateTime(2026, 10, 1) && !PickerOpen(start), "Enter did not commit the typed date");
        OpenPicker(symbol); Control choices = FindNamed(form, "PickerPopup").Controls[0]; ClickAt(choices, 40, 90);
        Require(Convert.ToString(symbol.GetType().GetProperty("SelectedItem").GetValue(symbol, null)) == "XRPUSDT" && !PickerOpen(symbol), "Symbol selection failed");
        OpenPicker(view); choices = FindNamed(form, "PickerPopup").Controls[0]; PressKey(choices, Keys.End); PressKey(choices, Keys.Enter);
        Require(form.Transactions.HistoryGrid.Columns[4].HeaderText == "체결가" && !PickerOpen(view), "Trade view selection did not update the history");
        OpenPicker(start);
        form.GetType().GetMethod("OnDeactivate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { EventArgs.Empty });
        Require(!PickerOpen(start), "Application deactivation retained a popup");
        OpenPicker(start); Size originalSize = form.ClientSize; form.ClientSize = new Size(originalSize.Width + 1, originalSize.Height);
        Require(!PickerOpen(start), "Window resize retained a popup"); form.ClientSize = originalSize;
        for (int i = 0; i < names.Length; i++)
        {
            using (var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(1100, 800) })
            {
                var history = new TransactionsPanel(Path.GetTempPath()) { AutoLoad = false, Dock = DockStyle.Fill };
                host.Controls.Add(history); host.Show(); Application.DoEvents();
                Control field = FindNamed(history, names[i]); OpenPicker(field); host.Dispose(); Application.DoEvents();
                Require(field.IsDisposed && !PickerOpen(field), "Closing the window retained a live popup");
            }
        }
        form.Transactions.SetFilters(new DateTime(2026, 10, 1), new DateTime(2026, 10, 8), "");
        view.GetType().GetProperty("SelectedIndex").SetValue(view, 0, null);
        form.SelectPage("main");
    }
    private static void VerifyAnalysisPeriod(LauncherForm form)
    {
        form.SelectPage("analysis"); Application.DoEvents();
        Control field = FindNamed(form.Analysis, "AnalysisPeriodDays");
        Require(field != null && field.GetType().Name == "NumberField", "Analysis still uses the default numeric input");
        TextBox input = (TextBox)field.Controls[0];
        form.Analysis.QuestionText = "기간 변경 전 질문";
        input.Focus(); input.Text = "43"; PressKey(input, Keys.Enter);
        Require(form.Analysis.PeriodDays == 43 && form.Analysis.QuestionText == "" && form.Analysis.ReportText.Contains("기간을 변경"), "Typed period did not reset the analysis context");
        ClickAt(field, field.Width - 13, 8);
        Require(form.Analysis.PeriodDays == 44, "Period up arrow failed");
        ClickAt(field, field.Width - 13, 24);
        Require(form.Analysis.PeriodDays == 43, "Period down arrow failed");
        input.Focus(); PressKey(input, Keys.Up); PressKey(input, Keys.PageUp);
        Require(form.Analysis.PeriodDays == 54, "Period keyboard increment failed");
        PressKey(input, Keys.Down); PressKey(input, Keys.PageDown);
        Require(form.Analysis.PeriodDays == 43, "Period keyboard decrement failed");
        form.Analysis.SetPeriod(365); ClickAt(field, field.Width - 13, 8);
        Require(form.Analysis.PeriodDays == 365, "Period exceeded 365 days");
        form.Analysis.SetPeriod(1); ClickAt(field, field.Width - 13, 24);
        Require(form.Analysis.PeriodDays == 1, "Period dropped below one day");
        input.Focus(); input.Text = "999"; PressKey(input, Keys.Enter);
        Require(form.Analysis.PeriodDays == 365, "Typed period was not bounded");
        input.Text = ""; PressKey(input, Keys.Enter);
        Require(form.Analysis.PeriodDays == 365 && input.Text == "365", "Empty period erased the current selection");
        form.Analysis.SetPeriod(0); Require(form.Analysis.PeriodDays == 1, "SetPeriod no longer clamps its lower bound");
        foreach (int preset in new int[] { 7, 30, 90 })
        {
            Button button = null;
            foreach (Control child in field.Parent.Controls) if (child is Button && child.Text == preset + "일") button = (Button)child;
            Require(button != null, "Missing analysis preset"); button.PerformClick();
            Require(form.Analysis.PeriodDays == preset, "Analysis period preset failed");
        }
        form.SelectPage("main"); form.SelectPage("analysis");
        Require(form.Analysis.PeriodDays == 90, "Tab switch lost the analysis period");
        form.Analysis.SetPeriod(30); form.SelectPage("main");
    }
    private static void RequireRoundedWindow(Control control)
    {
        Require(control != null, "Missing rounded content control");
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Require(GetWindowRgn(control.Handle, region) > 1 && !PtInRegion(region, 0, 0) && PtInRegion(region, control.Width / 2, control.Height / 2), "Native content corners were not clipped: " + control.Name);
        }
        finally { DeleteObject(region); }
    }
    private static void VerifyRoundedLayout(LauncherForm form)
    {
        form.SelectPage("history"); Application.DoEvents();
        foreach (string name in new string[] { "HistoryStartDate", "HistoryEndDate" })
        {
            Control field = FindNamed(form.Transactions, name); TextBox input = (TextBox)field.Controls[0];
            Require(Math.Abs(input.Top - (field.Height - input.Bottom)) <= 1 && !input.AutoSize, "Date input is vertically off center");
            Font originalFont = field.Font; int originalHeight = field.Height;
            using (var scaledFont = new Font(originalFont.FontFamily, 11.25F, FontStyle.Regular, GraphicsUnit.Point))
            {
                field.Font = scaledFont; field.Height = 40;
                Require(Math.Abs(input.Top - (field.Height - input.Bottom)) <= 1 && input.Height >= TextRenderer.MeasureText("2026.10.09", input.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height, "Resized date text is off center or clipped");
                field.Font = originalFont; field.Height = originalHeight;
            }
            RequireRoundedWindow(field);
        }
        RequireRoundedWindow(FindNamed(form.Transactions, "HistoryFilterCard"));
        RequireRoundedWindow(FindNamed(form.Transactions, "HistoryGridCard"));
        Control filter = FindNamed(form.Transactions, "HistoryFilterCard"), title = FindNamed(form.Transactions, "HistoryFilterTitle");
        Require(title.PointToScreen(Point.Empty).Y - filter.PointToScreen(Point.Empty).Y >= 12, "Filter title has no top breathing room");
        form.SelectPage("analysis"); Application.DoEvents();
        Control period = FindNamed(form.Analysis, "AnalysisPeriodDays"); TextBox number = (TextBox)period.Controls[0];
        Require(Math.Abs(number.Top - (period.Height - number.Bottom)) <= 1, "Analysis period is vertically off center");
        RequireRoundedWindow(period);
        ((Button)FindNamed(form.Analysis, "AnalysisChatTab")).PerformClick(); Application.DoEvents();
        foreach (string name in new string[] { "AnalysisContentCard", "ConversationCard", "QuestionInputCard" }) RequireRoundedWindow(FindNamed(form.Analysis, name));
        ((Button)FindNamed(form.Analysis, "AnalysisReportTab")).PerformClick();
        form.SelectPage("main"); Application.DoEvents();
        foreach (string name in new string[] { "MarketChartCard", "PositionsGridCard", "ActivityLogCard" }) RequireRoundedWindow(FindNamed(form, name));
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
            VerifyListTransport(root);
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
                VerifyTypography(form);
                form.Transactions.AutoLoad = false;
                form.Analysis.AutoDetectAccount = false;
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
                VerifyTabs(form, live);
                VerifyPickers(form);
                VerifyAnalysisPeriod(form);
                VerifyRoundedLayout(form);
                chartFeed.Emit(chartFeed.Frame(ChartFeedState.Live, chartFeed.Candles, false, "모의 시세 검증"));
                PumpUntil(delegate { return chart.SourceText.Contains("실시간"); }, "Live stream state did not render");
                chartFeed.AutoLive = true;
                VerifyHover(chart, chartFeed);
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
                    form.Text = "AUREX · 화면 검증용 모의 데이터";
                    SavePreview(form, args[1]);
                    MoveMouse(chart, 8, .45);
                    SavePreview(form, Path.Combine(previewDirectory, "launcher-hover-preview.png"));
                    LeaveMouse(chart);
                    form.SelectPage("history");
                    form.Transactions.SetFilters(new DateTime(2026, 9, 9), new DateTime(2026, 10, 8), "");
                    form.Transactions.Receive(new JavaScriptSerializer().Serialize(HistoryFixture(0, 20, 45, 1)));
                    SavePreview(form, Path.Combine(previewDirectory, "transactions-preview.png"));
                    form.ClientSize = new Size(1100, 800);
                    SavePreview(form, Path.Combine(previewDirectory, "transactions-compact-preview.png"));
                    Control datePicker = FindNamed(form.Transactions, "HistoryStartDate");
                    OpenPicker(datePicker); SavePreview(form, Path.Combine(previewDirectory, "transactions-calendar-preview.png")); ClosePicker(datePicker);
                    Control symbolPicker = FindNamed(form.Transactions, "HistorySymbol");
                    OpenPicker(symbolPicker); SavePreview(form, Path.Combine(previewDirectory, "transactions-symbol-preview.png")); ClosePicker(symbolPicker);
                    Require(form.Transactions.HistoryGrid.DisplayedRowCount(false) == 20, "The compact history viewport does not fit all 20 rows");
                    form.ClientSize = new Size(1280, 880);
                    form.SelectPage("analysis");
                    SavePreview(form, Path.Combine(previewDirectory, "launcher-analysis-preview.png"));
                    form.SelectPage("main");
                    Size originalClientSize = form.ClientSize;
                    form.ClientSize = new Size(1100, 780);
                    SavePreview(form, Path.Combine(previewDirectory, "launcher-compact-preview.png"));
                    form.ClientSize = originalClientSize;
                    RequireLiveInactive(live, "Preview resize");
                    form.Text = "AUREX";
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
                panel.Receive("{\"type\":\"status\",\"connected\":true,\"running\":false,\"management_enabled\":false,\"wallet\":\"1234.56\",\"available\":\"1200\",\"unrealized\":\"-2\",\"positions\":[{\"symbol\":\"BTCUSDT\",\"quantity\":\"-0.010\",\"entry\":\"62000\",\"unrealized\":\"-2\",\"managed\":false},{\"symbol\":\"XRPUSDT\",\"quantity\":\"10\",\"entry\":\"0.6\",\"unrealized\":\"0\",\"managed\":false}],\"message\":\"test\"}");
                Require(panel.WalletText == "1,234.56" && !panel.StartEnabled, "Account rendering or start gate failed");
                DataGridView positions = FindGrid(panel);
                Require(positions != null && positions.Rows.Count == 2 && Convert.ToString(positions.Rows[0].Cells[1].Value) == "short" && Convert.ToString(positions.Rows[1].Cells[1].Value) == "long", "Signed account positions did not show long/short");
                Require(Convert.ToDecimal(positions.Rows[0].Cells[2].Value, System.Globalization.CultureInfo.InvariantCulture) == .01M, "Short position quantity was displayed as negative");
                panel.Receive("{\"type\":\"status\",\"connected\":false,\"running\":false,\"message\":\"offline\"}");
                Require(panel.WalletText == "—" && !panel.StartEnabled, "Disconnected account retained live-looking values");
            }
            using (var history = new AnalysisPanel(Path.Combine(root, "tests")))
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
                Require(history.CanSendQuestion, "Chat input gate failed");
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
                    using (var analysisHost = new Form { ClientSize = new Size(1080, 780), ShowInTaskbar = false, Opacity = 0 })
                    {
                        analysisHost.Controls.Add(history); analysisHost.Show();
                        Application.DoEvents();
                        history.Receive(@"{""type"":""result"",""message"":""화면 검증용 예시 · 실제 계좌 연결 및 주문 없음""}");
                        history.QuestionText = "XRP 전략에서 낙폭을 줄이려면 어떤 조건을 먼저 검증해야 할까?";
                        SavePreview(analysisHost, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "analysis-preview.png"));
                        analysisHost.ClientSize = new Size(964, 662);
                        SavePreview(analysisHost, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "analysis-compact-preview.png"));
                    }
                }
            }
            string credentialRoot = Path.Combine(root, ".ui-update", "test-fixtures", "credentials-" + Guid.NewGuid().ToString("N"));
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
            Console.WriteLine("PASS: Embedded Pretendard Regular/Bold render in native Windows text inputs with Korean/numeric glyph coverage, all UI controls inherit the font, and the full readable font license accompanies the executable.");
            Console.WriteLine("PASS: AUREX main/history/analysis tabs reuse one window and retain the market feed without enabling trading.");
            Console.WriteLine("PASS: Themed calendar and selectors handle 100 outside-click/reopen cycles, tab switches, Escape, disable, resize, deactivation and window disposal; date/month/symbol/view selections are verified.");
            Console.WriteLine("PASS: Themed analysis spin box preserves custom typed periods, mouse and keyboard arrows, 1-365 day bounds, presets, context reset and tab persistence.");
            Console.WriteLine("PASS: Date/period text remains vertically centered, resized date text fits, filter heading has top padding, and native window regions clip all content/input corners.");
            Console.WriteLine("PASS: Chart mouse events update OHLC/volume above the plot, draw axis crosshairs and restore the latest candle on leave.");
            Console.WriteLine("PASS: Offline mock feed verifies four symbols, five intervals, forming candle/volume updates, old-response rejection, stale/offline labels and disposal cancellation.");
            Console.WriteLine("PASS: Launcher loads from an unrelated working directory.");
            Console.WriteLine("PASS: Actual GUI-to-Python JSON pipe preserves Korean, emoji, combining marks and multiline questions.");
            Console.WriteLine("PASS: Chat input, answer display, retry preservation and period reset; no default Enter submission.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
