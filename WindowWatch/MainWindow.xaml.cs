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
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"{DateTime.Now:HH:mm:ss} cfg={AppConfig.ConfigPath} exists={System.IO.File.Exists(AppConfig.ConfigPath)} sessions={_cfg.Sessions.Count} model={_cfg.Ollama.Model}\r\n");
        }
        catch { }
        _expandedW = 330; _expandedH = 520;
        try
        {
            var dpiInfo = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var selfRc);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"{DateTime.Now:HH:mm:ss} dpi={dpiInfo.DpiScaleX:F2} selfRect={selfRc.Left},{selfRc.Top},{selfRc.Right - selfRc.Left}x{selfRc.Bottom - selfRc.Top} virtual={NativeMethods.GetSystemMetrics(78)}x{NativeMethods.GetSystemMetrics(79)} actual={ActualWidth:F0}x{ActualHeight:F0}\r\n");
        }
        catch { }
        _engine = new MonitorEngine(_cfg);
        _engine.Log += OnLog;
        _engine.AnalysisCompleted += OnAnalysis;

        // 重启后按保存的窗口信息重挂接监控会话
        int saved = _cfg.Sessions.Count, restored = 0;
        foreach (var sc in _cfg.Sessions)
        {
            var w = WindowPicker.FindWindowBy(sc.Window);
            if (w != null)
            {
                var sess = _engine.AddSession(WindowPicker.ToTarget(w), sc.Profile, sc.PromptOverride);
                restored++;
                OnLog(sess, $"已恢复监控：{w.Title}");
            }
            else
            {
                LogLine($"未能恢复（窗口已关闭）：{sc.Window.Title}");
            }
        }
        if (saved > 0)
        {
            // 诊断：记录枚举到的候选窗口，便于排查"配置了却不生效"
            try
            {
                var cands = WindowPicker.Enumerate();
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} saved={saved} restored={restored} candidates={cands.Count}\r\n");
            }
            catch { }
        }

        _engine.Start();

        _pinned = true;   // 启动后工具条固定显示，直到用户手动收缩
        if (_cfg.Dock.FreeMove)
        {
            // 自由模式：恢复上次位置，否则浮动到屏幕内一个可见位置
            if (_cfg.Dock.FreeX.HasValue && _cfg.Dock.FreeY.HasValue)
            {
                Left = _cfg.Dock.FreeX.Value;
                Top = _cfg.Dock.FreeY.Value;
            }
            else
            {
                var (sw, _) = ScreenSize();
                Left = Math.Max(8, sw - _expandedW - 16);
                Top = 80;
            }
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
            var has = models.Exists(m => OllamaClient.ModelMatches(m, _cfg.Ollama.Model));
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
        // 正在拖动时不做任何停靠定位，避免抢位置
        if (_dragging) return;

        // 自由移动模式：不贴边、不吸附、不自动隐藏，用户自行拖动位置
        // 注意：吸附（AttachToTarget）优先级更高，勾了吸附就不再走自由移动
        if (_cfg.Dock.FreeMove && !_cfg.Dock.AttachToTarget)
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

        if (_cfg.Dock.AttachToTarget)
            FollowTarget();   // 吸附：展开/收缩都贴着目标窗口跟随
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

    /// <summary>吸附被监控窗口：每 tick 取目标的实时矩形（物理像素），换算成 DIP 后贴边跟随。</summary>
    private void FollowTarget()
    {
        var s = _engine?.Sessions.Count > 0 ? _engine!.Sessions[0] : null;
        if (s == null || !s.Target.IsValid)
        {
            if (_expanded) PlaceExpanded(); else PlaceStrip();
            return;
        }

        var hwnd = s.Target.Hwnd;
        // 目标最小化 / 取不到矩形 → 回退到屏幕贴边，避免窗口飞到屏幕外
        if (NativeMethods.IsIconic(hwnd) ||
            !NativeMethods.GetWindowRect(hwnd, out var rc) ||
            rc.Right <= rc.Left || rc.Bottom <= rc.Top)
        {
            if (_expanded) PlaceExpanded(); else PlaceStrip();
            return;
        }

        // 物理像素 → WPF DIP（本机 150% 缩放，必须换算，否则位置偏 1.5 倍）
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double dpiX = Math.Max(0.5, dpi.DpiScaleX);
        double dpiY = Math.Max(0.5, dpi.DpiScaleY);
        double tLeft = rc.Left / dpiX, tTop = rc.Top / dpiY;
        double tW = (rc.Right - rc.Left) / dpiX, tH = (rc.Bottom - rc.Top) / dpiY;

        var (sw, sh) = ScreenSize();
        double off = Math.Max(2, _cfg.Dock.DockOffsetPx);
        bool vertical = _cfg.Dock.Edge is "Left" or "Right";

        double w, h;
        if (_expanded) { w = _expandedW; h = _expandedH; }
        else if (vertical) { w = Math.Max(4, _cfg.Dock.StripPx); h = Clamp(tH * 0.6, 120, sh); }
        else { h = Math.Max(4, _cfg.Dock.StripPx); w = Clamp(tW * 0.6, 120, sw); }

        double left, top;
        switch (_cfg.Dock.Edge)
        {
            case "Left": left = tLeft - w - off; top = tTop + (tH - h) / 2; break;
            case "Top": left = tLeft + (tW - w) / 2; top = tTop - h - off; break;
            case "Bottom": left = tLeft + (tW - w) / 2; top = tTop + tH + off; break;
            default: left = tLeft + tW + off; top = tTop + (tH - h) / 2; break;
        }

        // 贴在目标外侧但超出屏幕时，翻到另一侧
        if (_cfg.Dock.Edge is "Left" or "Right")
        {
            if (left < 0) left = tLeft + tW + off;
            if (left + w > sw) left = tLeft - w - off;
            left = Clamp(left, 0, Math.Max(0, sw - w));
            top = Clamp(top, 0, Math.Max(0, sh - h));
        }
        else
        {
            if (top < 0) top = tTop + tH + off;
            if (top + h > sh) top = tTop - h - off;
            top = Clamp(top, 0, Math.Max(0, sh - h));
            left = Clamp(left, 0, Math.Max(0, sw - w));
        }

        Left = left; Top = top;
        Width = w; Height = h;
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
        Dispatcher.Invoke(() => SessionLabel.Text = s.DisplayName);
        LogLine(msg);
    }

    private void LogLine(string msg)
    {
        Dispatcher.Invoke(() =>
        {
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

    /// <summary>标题栏拖动：按下开始，移动改位置，松开完成。任意模式下都可用，拖动后自动进入自由位置。</summary>
    private System.Windows.Point _dragStartScreen;
    private double _dragWinLeft, _dragWinTop;
    private double _dpiX = 1, _dpiY = 1;
    private bool _dragging, _dragMoved;

    private void OnTitleMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        _dragging = true; _dragMoved = false;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _dpiX = Math.Max(0.5, dpi.DpiScaleX);
        _dpiY = Math.Max(0.5, dpi.DpiScaleY);
        var c = GetCursorPoint();                       // 物理像素（GetCursorPos）
        _dragStartScreen = new System.Windows.Point(c.X, c.Y);
        _dragWinLeft = Left; _dragWinTop = Top;
        ((System.Windows.UIElement)sender).CaptureMouse();
    }

    private void OnTitleMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var c = GetCursorPoint();
        // 物理像素差 → 换算为 DIP（WPF Left/Top 单位），保证 1:1 跟手
        double dx = (c.X - _dragStartScreen.X) / _dpiX;
        double dy = (c.Y - _dragStartScreen.Y) / _dpiY;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _dragMoved = true;
        Left = _dragWinLeft + dx;
        Top = _dragWinTop + dy;
    }

    private void OnTitleMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var el = (System.Windows.UIElement)sender;
        if (el.IsMouseCaptured) el.ReleaseMouseCapture();
        if (_dragMoved && !_cfg.Dock.FreeMove)
        {
            // 拖动了 → 自动进入自由位置，避免被贴边定时器拉回
            _cfg.Dock.FreeMove = true;
            _cfg.Dock.AttachToTarget = false;   // 手动拖动=自由摆放，取消吸附
        }
        if (_dragMoved)
        {
            // 记录自由位置，重启后恢复
            _cfg.Dock.FreeX = (int)Left;
            _cfg.Dock.FreeY = (int)Top;
            SaveConfig();
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
