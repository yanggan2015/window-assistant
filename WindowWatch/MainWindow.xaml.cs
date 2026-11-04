using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowWatch.Core;

namespace WindowWatch;

public partial class MainWindow : Window
{
    private AppConfig _cfg = new();
    private MonitorEngine? _engine;
    private DispatcherTimer _dockTimer;
    private bool _expanded = true;
    private bool _pinned = true;      // 手动呼出后固定住，不自动隐藏
    private int _leaveTicks;
    private int _expandedW, _expandedH;
    private const int AUTO_HIDE_IDLE_TICKS = 64;  // ~9s 鼠标离开后才自动隐藏
    private const int HOTKEY_ID = 0x8877;
    private System.Windows.Interop.HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _dockTimer.Tick += OnDockTick;
    }

    /// <summary>托盘/热键调用：展开并固定住可操作；已展开则收缩。</summary>
    public void ShowHideToggle()
    {
        if (!_expanded)
        {
            _pinned = true;          // 手动呼出 → 固定住，不自动隐藏
            Expand();
            Show();
            Activate();
            Topmost = true;
        }
        else
        {
            _pinned = false;
            Collapse();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x57); // Ctrl+Alt+W
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ShowHideToggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cfg = AppConfig.Load();
        _expandedW = 330; _expandedH = 520;
        _engine = new MonitorEngine(_cfg);
        _engine.Log += OnLog;
        _engine.AnalysisCompleted += OnAnalysis;

        // 重启后按保存的窗口信息重挂接监控会话
        foreach (var sc in _cfg.Sessions)
        {
            var w = WindowPicker.FindWindowBy(sc.Window);
            if (w != null)
                _engine.AddSession(WindowPicker.ToTarget(w), sc.Profile, sc.PromptOverride);
        }

        _engine.Start();

        _pinned = true;   // 启动后工具条固定显示，直到用户手动收缩
        if (_cfg.Dock.FreeMove)
        {
            // 自由模式：浮动到屏幕内一个可见位置，不贴边
            var (sw, _) = ScreenSize();
            Left = Math.Max(8, sw - _expandedW - 16);
            Top = 80;
        }
        else PlaceExpanded();
        _ = CheckOllamaAsync();
        _dockTimer.Start();
    }

    private async System.Threading.Tasks.Task CheckOllamaAsync()
    {
        try
        {
            var client = new OllamaClient(_cfg.Ollama);
            var models = await client.ListModelsAsync();
            var has = models.Exists(m => m.Equals(_cfg.Ollama.Model, StringComparison.OrdinalIgnoreCase));
            SetStatus(has
                ? $"Ollama 已连接 · 模型 {_cfg.Ollama.Model} 就绪"
                : $"Ollama 已连接，但未找到模型 {_cfg.Ollama.Model}，请在设置中修改或执行 ollama pull");
        }
        catch (Exception ex)
        {
            SetStatus($"Ollama 未就绪：{ex.Message}（请先安装并启动 Ollama）");
        }
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }

    // ---------- 停靠 / 自动隐藏 ----------
    private void OnDockTick(object? sender, EventArgs e)
    {
        // 自由移动模式：不贴边、不吸附、不自动隐藏，用户自行拖动位置
        if (_cfg.Dock.FreeMove)
        {
            if (!_expanded) { _pinned = true; Expand(); }
            return;
        }

        var cursor = GetCursorPoint();
        var bounds = new System.Drawing.Rectangle((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight);
        bounds.Inflate(40, 40);   // 增大悬停触发区，靠边即展开
        bool over = bounds.Contains(cursor.X, cursor.Y);

        if (over)
        {
            _leaveTicks = 0;
            if (!_expanded) Expand();   // 悬停靠边把手 → 展开（自动模式）
        }
        else if (_expanded && !_pinned)
        {
            // 仅「未固定」的工具条，且用户勾选了自动隐藏，且鼠标长久离开后才收缩
            if (_cfg.Dock.AutoHide)
            {
                _leaveTicks++;
                if (_leaveTicks >= AUTO_HIDE_IDLE_TICKS) Collapse();
            }
        }

        if (_expanded && _cfg.Dock.AttachToTarget)
            FollowTarget();
        else if (_expanded)
            PlaceExpanded();
        else
            PlaceStrip();
    }

    private void Expand()
    {
        _expanded = true; _leaveTicks = 0;
        MainPanel.Visibility = System.Windows.Visibility.Visible;
        CollapsedTab.Visibility = System.Windows.Visibility.Collapsed;
        Width = _expandedW; Height = _expandedH;
    }

    private void Collapse()
    {
        _expanded = false;
        MainPanel.Visibility = System.Windows.Visibility.Collapsed;
        CollapsedTab.Visibility = System.Windows.Visibility.Visible;
        PlaceStrip();
    }

    private void PlaceExpanded()
    {
        var (sw, sh) = ScreenSize();
        switch (_cfg.Dock.Edge)
        {
            case "Left": Left = 0; Top = (sh - _expandedH) / 2.0; break;
            case "Top": Left = (sw - _expandedW) / 2.0; Top = 0; break;
            case "Bottom": Left = (sw - _expandedW) / 2.0; Top = sh - _expandedH; break;
            default: Left = sw - _expandedW; Top = (sh - _expandedH) / 2.0; break;
        }
        Width = _expandedW; Height = _expandedH;
    }

    private void PlaceStrip()
    {
        var (sw, sh) = ScreenSize();
        int strip = Math.Max(4, _cfg.Dock.StripPx);
        bool vertical = _cfg.Dock.Edge is "Left" or "Right";
        int length = Math.Max(120, (int)(vertical ? sh * 0.5 : sw * 0.3));
        if (vertical)
        {
            switch (_cfg.Dock.Edge)
            {
                case "Left": Left = 0; Top = (sh - length) / 2.0; break;
                default: Left = sw - strip; Top = (sh - length) / 2.0; break;
            }
            Width = strip; Height = length;
        }
        else
        {
            switch (_cfg.Dock.Edge)
            {
                case "Top": Left = (sw - length) / 2.0; Top = 0; break;
                default: Left = (sw - length) / 2.0; Top = sh - strip; break;
            }
            Width = length; Height = strip;
        }
    }

    private void FollowTarget()
    {
        var s = _engine?.Sessions.Count > 0 ? _engine!.Sessions[0] : null;
        if (s == null || !s.Target.IsValid) { PlaceExpanded(); return; }
        var t = s.Target;
        var (sw, sh) = ScreenSize();
        int off = Math.Max(2, _cfg.Dock.DockOffsetPx);
        switch (_cfg.Dock.Edge)
        {
            case "Left": Left = Math.Max(0, t.X - _expandedW - off); Top = Clamp(t.Y + (t.Height - _expandedH) / 2, 0, sh - _expandedH); break;
            case "Top": Left = Clamp(t.X + (t.Width - _expandedW) / 2, 0, sw - _expandedW); Top = Math.Max(0, t.Y - _expandedH - off); break;
            case "Bottom": Left = Clamp(t.X + (t.Width - _expandedW) / 2, 0, sw - _expandedW); Top = Math.Min(sh - _expandedH, t.Y + t.Height + off); break;
            default: Left = Math.Min(sw - _expandedW, t.X + t.Width + off); Top = Clamp(t.Y + (t.Height - _expandedH) / 2, 0, sh - _expandedH); break;
        }
    }

    private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

    private static (double w, double h) ScreenSize()
        => (System.Windows.SystemParameters.VirtualScreenWidth, System.Windows.SystemParameters.VirtualScreenHeight);

    private static System.Drawing.Point GetCursorPoint()
    {
        NativeMethods.GetCursorPos(out var p);
        return new System.Drawing.Point(p.X, p.Y);
    }

    // ---------- 引擎事件 -> UI ----------
    private void OnLog(MonitorSession s, string msg)
    {
        Dispatcher.Invoke(() =>
        {
            SessionLabel.Text = s.DisplayName;
            LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (LogList.Items.Count > 60) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
    }

    private void OnAnalysis(MonitorSession s, AnalysisResult r)
    {
        Dispatcher.Invoke(() =>
        {
            SessionLabel.Text = s.DisplayName;
            if (r.IsError)
            {
                ResultText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                ResultText.Text = $"分析失败：{r.Error}";
                ActionList.ItemsSource = null;
            }
            else
            {
                ResultText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
                ResultText.Text = r.Suggestion;
                ActionList.ItemsSource = r.SuggestedActions.Count > 0 ? r.SuggestedActions : null;
            }
            StatusText.Text = r.IsError
                ? $"分析失败：{r.Error}"
                : $"事件 #{s.EventCount} · {s.DisplayName} · {r.Time:HH:mm:ss}";
        });
    }

    // ---------- 按钮 ----------
    private void OnAddWindow(object sender, RoutedEventArgs e)
    {
        var picker = new PickerWindow(_cfg);
        var ok = picker.ShowDialog() == true;
        if (ok && picker.SelectedTarget != null)
        {
            var s = _engine!.AddSession(picker.SelectedTarget, picker.SelectedProfile, picker.SelectedPromptOverride);
            OnLog(s, $"开始监控：{s.DisplayName}（{Profiles.Get(picker.SelectedProfile).Name}）");
            SaveConfig();
        }
    }

    private async void OnAnalyzeNow(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _engine.Sessions.Count == 0) return;
        SetStatus("正在立即分析…");
        await _engine.AnalyzeNow(_engine.Sessions[0]);
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _engine.Sessions.Count == 0) return;
        var s = _engine.Sessions[0];
        s.Paused = !s.Paused;
        BtnPause.Content = s.Paused ? "继续" : "暂停";
        OnLog(s, s.Paused ? "已暂停监控" : "已恢复监控");
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _engine.Sessions.Count == 0) return;
        var btn = (Button)sender;
        var text = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(text)) return;

        var s = _engine.Sessions[0];
        if (!_cfg.Automation.Enabled)
        {
            MessageBox.Show(this, "自动操作未开启。请在设置中打开「允许自动执行」，或先复制建议手动操作。",
                "安全提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Clipboard.SetText(text);
            OnLog(s, $"已复制动作文本：{text}");
            return;
        }
        if (_cfg.Automation.ConfirmEach &&
            MessageBox.Show(this, $"将要向「{s.DisplayName}」输入并回车：\n\n{text}\n\n确认执行？",
                "动作确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        if (!AutoOperator.Activate(s.Target))
        {
            OnLog(s, "无法激活目标窗口，已取消动作");
            return;
        }
        System.Threading.Thread.Sleep(_cfg.Automation.ClickDelayMs);
        AutoOperator.SendText(text);
        AutoOperator.SendKey(NativeMethods.VK_RETURN);
        OnLog(s, $"已向 {s.DisplayName} 输入动作：{text}");
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_cfg);
        if (w.ShowDialog() == true)
        {
            SaveConfig();
            _ = CheckOllamaAsync();
            if (!_cfg.Dock.FreeMove)
                PlaceExpanded();
            else
            {
                _pinned = true;
                Expand();
            }
        }
    }

    /// <summary>标题栏拖动（仅自由移动模式生效）。</summary>
    private void OnHeaderDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_cfg.Dock.FreeMove && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void SaveConfig()
    {
        if (_engine == null) return;
        _cfg.Sessions.Clear();
        foreach (var s in _engine.Sessions)
            _cfg.Sessions.Add(new SessionConfig
            {
                Window = s.Target,
                Profile = s.ProfileKey,
                PromptOverride = s.PromptOverride
            });
        _cfg.Save();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_source != null && _source.Handle != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_source.Handle, HOTKEY_ID);
        SaveConfig();
        _dockTimer.Stop();
        _engine?.Dispose();
    }
}
