using System;
using System.Threading;
using System.Windows;

namespace WindowWatch;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "WindowWatch_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：提示并退出新实例
            try { System.Windows.Forms.MessageBox.Show("窗哨已在运行。请使用系统托盘图标或 Ctrl+Alt+W 呼出。", "窗哨"); } catch { }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        CreateTray();
    }

    private void CreateTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "窗哨 WindowWatch (Ctrl+Alt+W 呼出)",
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var show = new System.Windows.Forms.ToolStripMenuItem("显示/隐藏工具条");
            show.Click += (_, __) => ToggleWindow();
            var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
            exit.Click += (_, __) => Shutdown();
            menu.Items.Add(show);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, __) => ToggleWindow();
        }
        catch { /* 托盘创建失败不阻断程序 */ }
    }

    private void ToggleWindow()
    {
        if (MainWindow is MainWindow w) w.ShowHideToggle();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
