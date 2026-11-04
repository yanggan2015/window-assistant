using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>按保存的进程/标题/类名在现有窗口中找到匹配项（用于重启后重挂接）。</summary>
    public static WindowItem? FindWindowBy(WindowTarget saved)
    {
        foreach (var w in Enumerate())
        {
            bool proc = string.IsNullOrWhiteSpace(saved.ProcessName)
                || w.ProcessName.Equals(saved.ProcessName, StringComparison.OrdinalIgnoreCase);
            bool cls = string.IsNullOrWhiteSpace(saved.ClassName)
                || w.ClassName.Equals(saved.ClassName, StringComparison.OrdinalIgnoreCase);
            bool title = string.IsNullOrWhiteSpace(saved.Title)
                || saved.Title.Length < 2
                || w.Title.Contains(saved.Title, StringComparison.OrdinalIgnoreCase);
            if (proc && cls && title) return w;
        }
        return null;
    }
}
