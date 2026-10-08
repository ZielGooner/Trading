using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace TradingLauncher
{
    public sealed class MarketChart : UserControl
    {
        private readonly string[] symbols = { "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT" };
        private readonly string[] intervals = { "15m", "1h", "4h", "1d", "1w" };
        private readonly string[] intervalNames = { "15분", "1시간", "4시간", "1일", "1주일" };
        private readonly Button[] tabs = new Button[4], intervalTabs = new Button[5];
        private readonly Label quote = new Label(), market = new Label(), source = new Label(), timestamps = new Label();
        private readonly CandleCanvas canvas = new CandleCanvas();
        private readonly Timer freshness = new Timer { Interval = 1000 };
        private readonly IChartFeed feed;
        private string selectedSymbol = "BTCUSDT", selectedInterval = "4h";
        private long selectedGeneration;
        private DateTime lastUpdate;
        private bool closing;
        private bool priceLive;
        private string latestTimeText;
        public string QuoteText { get { return quote.Text; } }
        public int HoveredCandleIndex { get { return canvas.HoverIndex; } }
        public double HoverPrice { get { return canvas.HoverPrice; } }
        public Control ChartSurface { get { return canvas; } }
        public RectangleF PricePlot { get { return canvas.PricePlot; } }
        public string SourceText { get { return source.Text + " · " + timestamps.Text; } }
        public int CandleCount { get { return canvas.CandleCount; } }
        public string SelectedSymbol { get { return selectedSymbol; } }
        public string SelectedInterval { get { return selectedInterval; } }
        public double LastClose { get; private set; }
        public double LastVolume { get; private set; }
        public bool LastCandleClosed { get; private set; }

        public MarketChart() : this(new BinanceChartFeed()) { }
        public MarketChart(IChartFeed chartFeed)
        {
            if (chartFeed == null) throw new ArgumentNullException("chartFeed");
            feed = chartFeed;
            feed.Updated += FeedUpdated;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Surface;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(9F);
            Padding = new Padding(16, 8, 12, 6);
            Margin = Padding.Empty;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 32, 30, 42, 21 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 187));
            navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabStrip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            for (int i = 0; i < symbols.Length; i++)
            {
                string symbol = symbols[i];
                tabs[i] = ChartButton(symbol.Replace("USDT", "/USDT"), 92);
                tabs[i].Tag = symbol;
                tabs[i].Click += delegate { SelectSymbol(symbol); };
                tabStrip.Controls.Add(tabs[i]);
            }
            source.Text = "Binance · 연결 중";
            source.Dock = DockStyle.Fill;
            source.Font = UiTheme.Font(8F);
            source.ForeColor = UiTheme.Warning;
            source.TextAlign = ContentAlignment.MiddleRight;
            source.Margin = Padding.Empty;
            navigation.Controls.Add(tabStrip, 0, 0);
            navigation.Controls.Add(source, 1, 0);
            layout.Controls.Add(navigation, 0, 0);
            var intervalStrip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            for (int i = 0; i < intervals.Length; i++)
            {
                string value = intervals[i];
                intervalTabs[i] = ChartButton(intervalNames[i], 62);
                intervalTabs[i].Height = 25;
                intervalTabs[i].Tag = value;
                intervalTabs[i].Click += delegate { SelectInterval(value); };
                intervalStrip.Controls.Add(intervalTabs[i]);
            }
            layout.Controls.Add(intervalStrip, 0, 1);
            var quoteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            quoteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            quoteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            quoteRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            market.Dock = quote.Dock = DockStyle.Fill;
            market.Font = UiTheme.Font(12F, FontStyle.Bold);
            quote.Font = UiTheme.Font(8.5F);
            market.TextAlign = quote.TextAlign = ContentAlignment.MiddleLeft;
            market.Margin = quote.Margin = Padding.Empty;
            quoteRow.Controls.Add(market, 0, 0);
            quoteRow.Controls.Add(quote, 1, 0);
            layout.Controls.Add(quoteRow, 0, 2);
            timestamps.Dock = DockStyle.Fill;
            timestamps.Font = UiTheme.Font(7.5F);
            timestamps.ForeColor = UiTheme.Muted;
            timestamps.Margin = Padding.Empty;
            timestamps.AutoEllipsis = true;
            layout.Controls.Add(timestamps, 0, 3);
            canvas.Dock = DockStyle.Fill;
            canvas.Name = "Candles";
            canvas.HoverChanged += delegate { UpdateQuote(); };
            canvas.Margin = Padding.Empty;
            layout.Controls.Add(canvas, 0, 4);
            freshness.Tick += delegate
            {
                if (lastUpdate != DateTime.MinValue && DateTime.UtcNow - lastUpdate > TimeSpan.FromSeconds(20))
                {
                    source.Text = "시세 지연 · 재연결 중";
                    source.ForeColor = UiTheme.Warning;
                    priceLive = false; UpdateQuote(); quote.ForeColor = UiTheme.Warning;
                }
            };
            freshness.Start();
            SetSelection();
        }

        private static Button ChartButton(string label, int width)
        {
            var button = new ModernButton { TabStop = true };
            UiTheme.StyleButton(button, label, width, false);
            button.Height = 29;
            button.Font = UiTheme.Font(8F, FontStyle.Bold);
            button.Margin = new Padding(0, 0, 4, 0);
            button.FlatAppearance.BorderSize = 0;
            return button;
        }
        public void SelectSymbol(string symbol)
        {
            if (Array.IndexOf(symbols, symbol) < 0 || selectedSymbol == symbol) return;
            selectedSymbol = symbol;
            SetSelection();
        }
        public void SelectInterval(string interval)
        {
            if (Array.IndexOf(intervals, interval) < 0 || selectedInterval == interval) return;
            selectedInterval = interval;
            SetSelection();
        }
        private void SetSelection()
        {
            market.Text = selectedSymbol.Replace("USDT", "/USDT");
            foreach (Button tab in tabs) StyleSelection(tab, (string)tab.Tag == selectedSymbol);
            foreach (Button tab in intervalTabs) StyleSelection(tab, (string)tab.Tag == selectedInterval);
            quote.Text = "—";
            quote.ForeColor = UiTheme.Text;
            LastClose = LastVolume = double.NaN;
            LastCandleClosed = false;
            lastUpdate = DateTime.MinValue;
            source.Text = "Binance · 연결 중";
            source.ForeColor = UiTheme.Warning;
            timestamps.Text = "Binance 선물 시세를 불러오는 중입니다.";
            canvas.SetData(null, selectedSymbol, "시세를 불러오는 중입니다.", selectedInterval);
            if (IsHandleCreated && !closing) selectedGeneration = feed.Select(selectedSymbol, selectedInterval);
        }
        private static void StyleSelection(Button button, bool selected)
        {
            button.ForeColor = selected ? UiTheme.Accent : UiTheme.Muted;
            button.BackColor = selected ? UiTheme.Elevated : UiTheme.Surface;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (feed != null && !closing) selectedGeneration = feed.Select(selectedSymbol, selectedInterval);
        }
        private void FeedUpdated(object sender, ChartFeedEventArgs update)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (closing || IsDisposed || update.Generation != selectedGeneration || update.Symbol != selectedSymbol || update.Interval != selectedInterval) return;
                    ApplyUpdate(update);
                }));
            }
            catch (InvalidOperationException) { }
        }
        private void ApplyUpdate(ChartFeedEventArgs update)
        {
                bool live = update.State == ChartFeedState.Live && DateTime.UtcNow - update.ReceivedUtc <= TimeSpan.FromSeconds(20);
            priceLive = live;
            source.Text = live ? "Binance · 실시간" : update.State == ChartFeedState.Connecting ? "Binance · 연결 중" : update.State == ChartFeedState.Stopped ? "시세 연결 중지" : "시세 지연 · 재연결 중";
            source.ForeColor = live ? UiTheme.Positive : UiTheme.Warning;
            if (update.Candles != null && update.Candles.Count > 0)
            {
                var closed = new List<DisplayCandle>();
                double sum = 0;
                foreach (ChartCandle candle in update.Candles)
                {
                    sum += candle.Close;
                    if (closed.Count >= 20) sum -= closed[closed.Count - 20].Close;
                    closed.Add(new DisplayCandle { Time = candle.Time, Open = candle.Open, High = candle.High, Low = candle.Low, Close = candle.Close, Volume = candle.Volume, MovingAverage = closed.Count >= 19 ? sum / 20 : double.NaN });
                }
                int start = Math.Max(0, closed.Count - 72);
                var visible = closed.GetRange(start, closed.Count - start);
                ChartCandle last = update.Candles[update.Candles.Count - 1];
                LastClose = last.Close;
                LastVolume = last.Volume;
                LastCandleClosed = last.Closed;
                lastUpdate = update.ReceivedUtc;
                latestTimeText = "봉 시작 " + Epoch.AddSeconds(last.Time).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC · " + (last.Closed ? "완료봉" : "진행 중") + " · 갱신 " + update.ReceivedUtc.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC · MA(20)";
                if (!string.IsNullOrEmpty(update.Message)) latestTimeText += " · " + update.Message;
                canvas.SetData(visible, selectedSymbol, null, selectedInterval);
            }
            else if (canvas.CandleCount == 0)
            {
                quote.Text = "—";
                timestamps.Text = string.IsNullOrEmpty(update.Message) ? "시세 연결을 기다리고 있습니다." : update.Message;
                canvas.SetData(null, selectedSymbol, "시세를 받을 수 없습니다.\n자동으로 다시 연결합니다.", selectedInterval);
            }
            else UpdateQuote();
        }
        private void UpdateQuote()
        {
            DisplayCandle candle = canvas.DisplayedCandle;
            if (candle == null) { quote.Text = "—"; return; }
            double change = candle.Close - candle.Open;
            string delta = change.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            string percentage = (candle.Open == 0 ? 0 : change / candle.Open * 100).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            quote.Text = "시 " + FormatPrice(candle.Open, selectedSymbol) + "   고 " + FormatPrice(candle.High, selectedSymbol)
                + "   저 " + FormatPrice(candle.Low, selectedSymbol) + "   종 " + FormatPrice(candle.Close, selectedSymbol)
                + "   " + delta + " (" + percentage + "%)   거래량 " + candle.Volume.ToString("N2", CultureInfo.InvariantCulture);
            quote.ForeColor = priceLive ? change >= 0 ? UiTheme.Positive : UiTheme.Negative : UiTheme.Muted;
            timestamps.Text = canvas.HoverIndex >= 0
                ? "선택 봉 " + Epoch.AddSeconds(candle.Time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC · MA(20) · " + source.Text
                : latestTimeText;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                freshness.Dispose();
                feed.Updated -= FeedUpdated;
                feed.Dispose();
            }
            base.Dispose(disposing);
        }
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static string FormatPrice(double value, string symbol) { return value.ToString(symbol == "XRPUSDT" ? "N4" : "N2", CultureInfo.InvariantCulture); }
        private sealed class DisplayCandle
        {
            public long Time;
            public double Open, High, Low, Close, Volume, MovingAverage;
        }

        private sealed class CandleCanvas : Control
        {
            private List<DisplayCandle> candles;
            private string symbol, interval, emptyText;
            private Point? pointer;
            private double lowBound, highBound, maximumVolume;
            private bool volumeAvailable;
            public int HoverIndex { get; private set; }
            public double HoverPrice { get; private set; }
            public event EventHandler HoverChanged;
            public DisplayCandle DisplayedCandle { get { return CandleCount == 0 ? null : candles[HoverIndex >= 0 && HoverIndex < CandleCount ? HoverIndex : CandleCount - 1]; } }
            public RectangleF PricePlot { get { return PlotRectangle(); } }
            public int CandleCount { get { return candles == null ? 0 : candles.Count; } }
            public CandleCanvas()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = UiTheme.Surface;
                Font = UiTheme.Font(7.5F);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
                HoverIndex = -1; HoverPrice = double.NaN;
            }
            public void SetData(List<DisplayCandle> data, string selected, string message, string timeframe)
            {
                candles = data; symbol = selected; emptyText = message; interval = timeframe;
                if (data == null) pointer = null;
                CacheRange(); UpdateHover(true);
                Invalidate();
            }
            private int VolumeHeight()
            {
                if (!volumeAvailable) return 0;
                return Math.Max(20, Math.Min(49, Height / 6));
            }
            private RectangleF PlotRectangle() { return new RectangleF(4, 7, Math.Max(0, Width - 91), Math.Max(0, Height - 34 - VolumeHeight())); }
            private void CacheRange()
            {
                lowBound = double.MaxValue; highBound = double.MinValue; maximumVolume = 0; volumeAvailable = CandleCount > 0;
                if (CandleCount == 0) return;
                foreach (DisplayCandle candle in candles)
                {
                    lowBound = Math.Min(lowBound, candle.Low); highBound = Math.Max(highBound, candle.High);
                    if (!double.IsNaN(candle.MovingAverage)) { lowBound = Math.Min(lowBound, candle.MovingAverage); highBound = Math.Max(highBound, candle.MovingAverage); }
                    if (candle.Volume < 0) volumeAvailable = false;
                    maximumVolume = Math.Max(maximumVolume, candle.Volume);
                }
                double spread = Math.Max(highBound - lowBound, Math.Max(Math.Abs(highBound) * 0.001, 0.00000001));
                highBound += spread * 0.08; lowBound -= spread * 0.08;
            }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); pointer = e.Location; UpdateHover(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pointer = null; UpdateHover(); }
            protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) { pointer = null; UpdateHover(); } }
            protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateHover(); }
            private void UpdateHover(bool dataChanged = false)
            {
                int previousIndex = HoverIndex;
                HoverIndex = -1; HoverPrice = double.NaN;
                RectangleF plot = PlotRectangle();
                if (pointer.HasValue && CandleCount > 0 && Width >= 180 && Height >= 100 && plot.Width > 0 && plot.Height > 0)
                {
                    Point point = pointer.Value;
                    if (point.X >= plot.Left && point.X < plot.Right && point.Y >= plot.Top && point.Y <= plot.Bottom + VolumeHeight())
                    {
                        HoverIndex = Math.Min(CandleCount - 1, (int)((point.X - plot.Left) / (plot.Width / CandleCount)));
                        if (point.Y <= plot.Bottom)
                        {
                            HoverPrice = highBound - (point.Y - plot.Top) / plot.Height * (highBound - lowBound);
                        }
                    }
                }
                Invalidate();
                if ((dataChanged || previousIndex != HoverIndex) && HoverChanged != null) HoverChanged(this, EventArgs.Empty);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (candles == null || candles.Count == 0)
                {
                    TextRenderer.DrawText(g, emptyText ?? "시세 연결 대기", Font, ClientRectangle, UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                if (Width < 180 || Height < 100) return;
                bool hasVolume = volumeAvailable;
                double low = lowBound, high = highBound;
                int volumeHeight = VolumeHeight();
                var plot = PlotRectangle();
                float cell = plot.Width / candles.Count;
                Func<double, float> y = delegate(double price) { return plot.Bottom - (float)((price - low) / (high - low)) * plot.Height; };
                using (var gridPen = new Pen(Color.FromArgb(100, UiTheme.Border)))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        float lineY = plot.Top + plot.Height * i / 4F;
                        g.DrawLine(gridPen, plot.Left, lineY, plot.Right, lineY);
                        double price = high - (high - low) * i / 4D;
                        TextRenderer.DrawText(g, FormatPrice(price, symbol), Font, new Rectangle((int)plot.Right + 8, (int)lineY - 8, 77, 18), UiTheme.Muted, TextFormatFlags.VerticalCenter);
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        int index = (candles.Count - 1) * i / 4;
                        float x = plot.Left + cell * (index + 0.5F);
                        g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom + volumeHeight);
                        string date = Epoch.AddSeconds(candles[index].Time).ToString(interval == "1w" ? "yy-MM-dd" : interval == "1d" ? "MM-dd" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
                        int labelX = Math.Max(0, Math.Min((int)plot.Right - 68, (int)x - 34));
                        TextRenderer.DrawText(g, date, Font, new Rectangle(labelX, Height - 22, 74, 20), UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                var average = new List<PointF>();
                for (int i = 0; i < candles.Count; i++)
                {
                    DisplayCandle candle = candles[i];
                    float x = plot.Left + cell * (i + 0.5F);
                    Color color = candle.Close >= candle.Open ? UiTheme.Positive : UiTheme.Negative;
                    float bodyWidth = Math.Max(2F, cell * 0.64F);
                    using (var wick = new Pen(color, 1F))
                    using (var body = new SolidBrush(color))
                    {
                        g.DrawLine(wick, x, y(candle.High), x, y(candle.Low));
                        float top = y(Math.Max(candle.Open, candle.Close));
                        g.FillRectangle(body, x - bodyWidth / 2F, top, bodyWidth, Math.Max(1.5F, y(Math.Min(candle.Open, candle.Close)) - top));
                    }
                    if (!double.IsNaN(candle.MovingAverage)) average.Add(new PointF(x, y(candle.MovingAverage)));
                    if (hasVolume && maximumVolume > 0)
                    {
                        float height = (float)(candle.Volume / maximumVolume) * Math.Max(1, volumeHeight - 9);
                        using (var volume = new SolidBrush(Color.FromArgb(100, color)))
                            g.FillRectangle(volume, x - bodyWidth / 2F, plot.Bottom + volumeHeight - height, bodyWidth, height);
                    }
                }
                if (average.Count > 1) using (var ma = new Pen(UiTheme.Accent, 1.4F)) g.DrawLines(ma, average.ToArray());
                DisplayCandle last = candles[candles.Count - 1];
                float lastY = y(last.Close);
                Color lastColor = last.Close >= last.Open ? UiTheme.Positive : UiTheme.Negative;
                using (var line = new Pen(Color.FromArgb(165, lastColor), 1F))
                { line.DashStyle = DashStyle.Dash; g.DrawLine(line, plot.Left, lastY, plot.Right, lastY); }
                var priceBox = new Rectangle((int)plot.Right + 2, (int)lastY - 10, 82, 20);
                using (var fill = new SolidBrush(lastColor)) g.FillRectangle(fill, priceBox);
                TextRenderer.DrawText(g, FormatPrice(last.Close, symbol), Font, priceBox, UiTheme.Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (hasVolume) TextRenderer.DrawText(g, "거래량", Font, new Point(7, (int)plot.Bottom + 2), UiTheme.Muted);
                if (HoverIndex >= 0 && pointer.HasValue)
                {
                    float crossX = plot.Left + cell * (HoverIndex + 0.5F);
                    using (var crosshair = new Pen(UiTheme.Muted, 1F))
                    {
                        crosshair.DashStyle = DashStyle.Dash;
                        g.DrawLine(crosshair, crossX, plot.Top, crossX, plot.Bottom + volumeHeight);
                        if (!double.IsNaN(HoverPrice))
                        {
                            float crossY = pointer.Value.Y;
                            g.DrawLine(crosshair, plot.Left, crossY, plot.Right, crossY);
                            var hoverBox = new Rectangle((int)plot.Right + 2, (int)crossY - 10, 82, 20);
                            using (var fill = new SolidBrush(UiTheme.Border)) g.FillRectangle(fill, hoverBox);
                            TextRenderer.DrawText(g, FormatPrice(HoverPrice, symbol), Font, hoverBox, UiTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        }
                    }
                    string time = Epoch.AddSeconds(candles[HoverIndex].Time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
                    int timeWidth = TextRenderer.MeasureText(time, Font).Width + 12;
                    int timeX = Math.Max(0, Math.Min((int)plot.Right - timeWidth, (int)crossX - timeWidth / 2));
                    var timeBox = new Rectangle(timeX, Height - 23, timeWidth, 22);
                    using (var fill = new SolidBrush(UiTheme.Border)) g.FillRectangle(fill, timeBox);
                    TextRenderer.DrawText(g, time, Font, timeBox, UiTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
