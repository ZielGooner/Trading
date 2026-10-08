using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TradingLauncher
{
    public static class AurexBrand
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        public static Icon CreateIcon()
        {
            using (var bitmap = new Bitmap(64, 64))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(24, 26, 32));
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Color.FromArgb(240, 185, 11), 6F))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        graphics.DrawLines(pen, new PointF[] { new PointF(12, 51), new PointF(32, 12), new PointF(52, 51) });
                        graphics.DrawLine(pen, 23, 39, 43, 39);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
