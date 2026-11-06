using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace WindowWatch.Core;

/// <summary>枚举桌面顶层可见窗口。</summary>
public static class WindowPicker
{
    public static List<WindowItem> Enumerate()
    {
        var result = new List<WindowItem>();
        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            var title = NativeMethods.GetWindowTitle(hWnd).Trim();
            var cls = NativeMethods.GetWindowClass(hWnd);

            // 过滤系统/工具窗口
            if (string.IsNullOrEmpty(cls)) return true;
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return true;
            var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            // 过滤不可见样式窗口
            var style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
            if ((style & NativeMethods.WS_VISIBLE) == 0) return true;

            if (!NativeMethods.GetWindowRect(hWnd, out var rc)) return true;
            if (rc.Width <= 40 || rc.Height <= 40) return true; // 太小忽略

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = "";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { proc = ""; }

            result.Add(new WindowItem
            {
                Hwnd = hWnd,
                Title = title,
                ClassName = cls,
                ProcessName = proc,
                Width = rc.Width,
                Height = rc.Height
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static WindowTarget ToTarget(WindowItem item)
    {
        return new WindowTarget
        {
            Hwnd = item.Hwnd,
            Title = item.Title,
            ClassName = item.ClassName,
            ProcessName = item.ProcessName
        };
    }

    /// <summary>
    /// 按保存的进程/标题/类名在现有窗口中找到匹配项（用于重启后重挂接）。
    /// 注意：部分程序的窗口类名带每进程随机后缀（如 WindowsForms10..._r3_ad1），
    /// 因此类名只做前缀匹配，并按"全匹配 → 标题+类族 → 进程+标题 → 唯一标题"分级回退。
    /// </summary>
    public static WindowItem? FindWindowBy(WindowTarget saved)
    {
        var list = Enumerate();
        string sp = saved.ProcessName ?? "", sc = saved.ClassName ?? "", st = saved.Title ?? "";

        bool ProcOk(WindowItem w) => string.IsNullOrWhiteSpace(sp)
            || w.ProcessName.Equals(sp, StringComparison.OrdinalIgnoreCase);
        bool TitleOk(WindowItem w) => string.IsNullOrWhiteSpace(st) || st.Length < 2
            || w.Title.Contains(st, StringComparison.OrdinalIgnoreCase);
        bool ClassOk(WindowItem w) => string.IsNullOrWhiteSpace(sc)
            || w.ClassName.Equals(sc, StringComparison.OrdinalIgnoreCase)
            || CommonPrefix(w.ClassName, sc);

        var hit = list.Where(w => ProcOk(w) && TitleOk(w) && ClassOk(w)).FirstOrDefault();
        if (hit != null) return hit;

        hit = list.Where(w => TitleOk(w) && ClassOk(w)).FirstOrDefault();
        if (hit != null) return hit;

        hit = list.Where(w => ProcOk(w) && TitleOk(w)).FirstOrDefault();
        if (hit != null) return hit;

        // 标题唯一时兜底（标题足够长才用，避免误配）
        var byTitle = list.Where(w => st.Length >= 4 && TitleOk(w)).ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    /// <summary>类名族相同（忽略每进程随机后缀）。</summary>
    private static bool CommonPrefix(string a, string b)
    {
        const int n = 18;   // "WindowsForms10.Wind" 这类公共前缀足够区分控件族
        if (a.Length < n || b.Length < n) return false;
        return a.AsSpan(0, n).Equals(b.AsSpan(0, n), StringComparison.OrdinalIgnoreCase);
    }
}
