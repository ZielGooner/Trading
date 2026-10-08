using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace TradingLauncher
{
    internal static class Typography
    {
        private static readonly FontResources resources = new FontResources();
        static Typography() { AppDomain.CurrentDomain.ProcessExit += delegate { resources.Dispose(); }; }
        public static Font Create(float size, FontStyle style) { return new Font(resources.Family, size, style, GraphicsUnit.Point); }

        private sealed class FontResources : IDisposable
        {
            [DllImport("gdi32.dll")]
            private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, out uint count);
            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool RemoveFontMemResourceEx(IntPtr handle);
            private readonly PrivateFontCollection collection = new PrivateFontCollection();
            private readonly List<IntPtr> buffers = new List<IntPtr>(), handles = new List<IntPtr>();
            public FontFamily Family { get; private set; }
            private bool disposed;
            public FontResources()
            {
                try
                {
                    Add("Pretendard-Regular.ttf"); Add("Pretendard-Bold.ttf");
                    foreach (FontFamily family in collection.Families)
                    {
                        if (family.Name == "Pretendard") Family = family;
                        else family.Dispose();
                    }
                    if (Family == null || !Family.IsStyleAvailable(FontStyle.Regular) || !Family.IsStyleAvailable(FontStyle.Bold))
                        throw new InvalidOperationException("내장 Pretendard 글꼴을 읽지 못했습니다.");
                }
                catch { Dispose(); throw; }
            }
            private void Add(string name)
            {
                using (Stream stream = typeof(Typography).Assembly.GetManifestResourceStream("Aurex.Fonts." + name))
                {
                    if (stream == null) throw new InvalidOperationException("내장 글꼴이 없습니다: " + name);
                    byte[] bytes = new byte[checked((int)stream.Length)];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int count = stream.Read(bytes, offset, bytes.Length - offset);
                        if (count == 0) throw new EndOfStreamException("내장 글꼴이 불완전합니다: " + name);
                        offset += count;
                    }
                    IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length); buffers.Add(memory);
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                    collection.AddMemoryFont(memory, bytes.Length);
                    // WinForms TextRenderer/text boxes use GDI, while FontFamily
                    // uses GDI+. Register with both, privately for this process.
                    uint added;
                    IntPtr handle = AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, out added);
                    if (handle == IntPtr.Zero) throw new InvalidOperationException("Windows에서 내장 글꼴을 읽지 못했습니다: " + name);
                    handles.Add(handle);
                }
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (Family != null) Family.Dispose();
                collection.Dispose();
                foreach (IntPtr handle in handles) RemoveFontMemResourceEx(handle);
                // GDI+ keeps the memory backing a private family alive until the
                // family and collection are released; keep it for the UI lifetime.
                foreach (IntPtr buffer in buffers) Marshal.FreeCoTaskMem(buffer);
                handles.Clear(); buffers.Clear();
            }
        }
    }
}
