using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowWatch.Core;

/// <summary>窗口截图与变化检测。</summary>
public static class WindowCapture
{
    private const int THUMB = 48; // dHash 缩略图尺寸（48x48）

    /// <summary>截取目标窗口内容位图。PrintWindow 为主，空白时可见窗口回退 BitBlt。</summary>
    public static Bitmap? Capture(WindowTarget target, int maxEdgePx)
    {
        var hwnd = target.Hwnd;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;

        // 更新窗口矩形
        if (NativeMethods.GetWindowRect(hwnd, out var rc) && rc.Width > 0 && rc.Height > 0)
        {
            target.X = rc.Left; target.Y = rc.Top;
            target.Width = rc.Width; target.Height = rc.Height;
        }
        else return null;

        var w = target.Width; var h = target.Height;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            try
            {
                NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally { g.ReleaseHdc(hdc); }

            // PrintWindow 常对 GPU/硬件加速内容返回全黑或全透明，尝试屏幕回退
            if (IsBlank(bmp) && NativeMethods.IsWindowVisible(hwnd))
            {
                using var screen = CaptureScreenRegion(target.X, target.Y, w, h);
                if (screen != null)
                {
                    using var g2 = Graphics.FromImage(bmp);
                    g2.Clear(Color.Transparent);
                    g2.DrawImageUnscaled(screen, 0, 0);
                }
            }

            // 缩放工作图
            if (Math.Max(w, h) > maxEdgePx)
                return Scale(bmp, maxEdgePx);
            return new Bitmap(bmp);
        }
        catch { bmp.Dispose(); return null; }
    }

    private static Bitmap? CaptureScreenRegion(int x, int y, int w, int h)
    {
        var bounds = ScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var cx = Math.Max(x, bounds.X);
        var cy = Math.Max(y, bounds.Y);
        var cw = Math.Min(x + w, bounds.X + bounds.Width) - cx;
        var ch = Math.Min(y + h, bounds.Y + bounds.Height) - cy;
        if (cw <= 0 || ch <= 0) return null;

        var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(cx, cy, 0, 0, new System.Drawing.Size(cw, ch), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { bmp.Dispose(); return null; }
    }

    private static Rectangle ScreenBounds()
    {
        int x = (int)System.Windows.SystemParameters.VirtualScreenLeft;
        int y = (int)System.Windows.SystemParameters.VirtualScreenTop;
        int w = (int)System.Windows.SystemParameters.VirtualScreenWidth;
        int h = (int)System.Windows.SystemParameters.VirtualScreenHeight;
        return new Rectangle(x, y, w, h);
    }

    private static bool IsBlank(Bitmap bmp)
    {
        // 抽样若干点判断是否全黑/全透明
        using var b = new Bitmap(bmp, 32, 32);
        int nonBlack = 0, samples = 0;
        for (int yy = 0; yy < 32; yy += 2)
            for (int xx = 0; xx < 32; xx += 2)
            {
                var c = b.GetPixel(xx, yy);
                samples++;
                if (c.A > 20 && (c.R > 12 || c.G > 12 || c.B > 12)) nonBlack++;
            }
        return samples == 0 || nonBlack < samples / 20; // 少于5%有内容视为空白
    }

    private static Bitmap Scale(Bitmap src, int maxEdgePx)
    {
        double ratio = (double)maxEdgePx / Math.Max(src.Width, src.Height);
        int nw = Math.Max(1, (int)(src.Width * ratio));
        int nh = Math.Max(1, (int)(src.Height * ratio));
        var bmp = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, nw, nh);
        return bmp;
    }

    /// <summary>生成 dHash 指纹（ulong 位向量）。</summary>
    public static ulong DHash(Bitmap bmp)
    {
        using var thumb = new Bitmap(bmp, THUMB, THUMB);
        var gray = ToGray(thumb);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < THUMB; y++)
            for (int x = 0; x < THUMB - 1; x++)
            {
                if (gray[x, y] > gray[x + 1, y]) hash |= 1UL << bit;
                bit++;
            }
        return hash;
    }

    private static byte[,] ToGray(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            var g = new byte[bmp.Width, bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    int idx = y * stride + x * 4;
                    int b = buf[idx], gg = buf[idx + 1], r = buf[idx + 2];
                    g[x, y] = (byte)((r * 299 + gg * 587 + b * 114) / 1000);
                }
            return g;
        }
        finally { bmp.UnlockBits(data); }
    }

    public static int Hamming(ulong a, ulong b)
    {
        var x = a ^ b;
        int c = 0;
        while (x != 0) { c += (int)(x & 1); x >>= 1; }
        return c;
    }

    /// <summary>下采样灰度后统计差异像素占比。</summary>
    public static double PixelDiffRatio(Bitmap a, Bitmap b)
    {
        using var ta = new Bitmap(a, 96, 96);
        using var tb = new Bitmap(b, 96, 96);
        var ga = ToGray(ta); var gb = ToGray(tb);
        int diff = 0, total = 96 * 96;
        for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
                if (Math.Abs(ga[x, y] - gb[x, y]) > 18) diff++;
        return (double)diff / total;
    }
}
