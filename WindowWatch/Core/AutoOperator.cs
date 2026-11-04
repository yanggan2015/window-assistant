using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WindowWatch.Core;

/// <summary>向目标窗口注入文本/按键/点击（模拟键鼠）。</summary>
public static class AutoOperator
{
    /// <summary>把目标窗口置前并还原。</summary>
    public static bool Activate(WindowTarget target)
    {
        var h = target.Hwnd;
        if (h == IntPtr.Zero || !NativeMethods.IsWindow(h)) return false;
        NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(h);
        return NativeMethods.GetForegroundWindow() == h;
    }

    /// <summary>输入 Unicode 文本。</summary>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new NativeMethods.INPUT[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wScan = text[i],
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE
                    }
                }
            };
        }
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>发送单个虚拟键（如 Enter/Tab/Esc）。</summary>
    public static void SendKey(ushort vk, bool ctrl = false)
    {
        var list = new System.Collections.Generic.List<NativeMethods.INPUT>();
        if (ctrl) list.Add(Key(NativeMethods.VK_CONTROL, false));
        list.Add(Key(vk, false));
        list.Add(Key(vk, true));
        if (ctrl) list.Add(Key(NativeMethods.VK_CONTROL, true));
        NativeMethods.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>组合快捷键 Ctrl+C / Ctrl+V 等。</summary>
    public static void SendShortcut(ushort letter)
    {
        SendKey(letter, ctrl: true);
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        };
    }

    /// <summary>在屏幕绝对坐标点击左键。</summary>
    public static void ClickAt(int screenX, int screenY, int delayMs = 120)
    {
        var sw = System.Windows.SystemParameters.PrimaryScreenWidth;
        var sh = System.Windows.SystemParameters.PrimaryScreenHeight;
        var absX = (int)(screenX * 65535.0 / sw);
        var absY = (int)(screenY * 65535.0 / sh);

        var down = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = absX, dy = absY,
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        NativeMethods.SendInput(1, new[] { down }, Marshal.SizeOf<NativeMethods.INPUT>());
        Thread.Sleep(Math.Max(0, delayMs));

        var ld = down; ld.U.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK;
        var lu = down; lu.U.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK;
        NativeMethods.SendInput(1, new[] { ld }, Marshal.SizeOf<NativeMethods.INPUT>());
        NativeMethods.SendInput(1, new[] { lu }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>目标窗口客户区坐标 → 屏幕坐标。</summary>
    public static bool ClientToScreen(WindowTarget target, int clientX, int clientY, out int sx, out int sy)
    {
        sx = target.X + clientX; sy = target.Y + clientY;
        return true;
    }
}
