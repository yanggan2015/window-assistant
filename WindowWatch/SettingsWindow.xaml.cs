using System;
using System.Windows;
using WindowWatch.Core;

namespace WindowWatch;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _src;
    private readonly AppConfig _edit;

    public SettingsWindow(AppConfig cfg)
    {
        InitializeComponent();
        _src = cfg;
        _edit = new AppConfig
        {
            Ollama = new OllamaConfig { Host = cfg.Ollama.Host, Model = cfg.Ollama.Model, TimeoutSec = cfg.Ollama.TimeoutSec },
            Capture = new CaptureConfig
            {
                PollIntervalMs = cfg.Capture.PollIntervalMs,
                DHashThreshold = cfg.Capture.DHashThreshold,
                CooldownSec = cfg.Capture.CooldownSec,
                StableFrames = cfg.Capture.StableFrames,
                PixelDiffThreshold = cfg.Capture.PixelDiffThreshold,
                MaxEdgePx = cfg.Capture.MaxEdgePx
            },
            Dock = new DockConfig
            {
                Edge = cfg.Dock.Edge, AutoHide = cfg.Dock.AutoHide,
                AttachToTarget = cfg.Dock.AttachToTarget, StripPx = cfg.Dock.StripPx,
                FreeMove = cfg.Dock.FreeMove
            },
            Automation = new AutomationConfig { Enabled = cfg.Automation.Enabled, ConfirmEach = cfg.Automation.ConfirmEach },
            Ai = new AiConfig { MaxConcurrent = cfg.Ai.MaxConcurrent, AnalyzeDiffRegion = cfg.Ai.AnalyzeDiffRegion }
        };

        HostBox.Text = _edit.Ollama.Host;
        ModelBox.Text = _edit.Ollama.Model;
        PollBox.Text = _edit.Capture.PollIntervalMs.ToString();
        DHashBox.Text = _edit.Capture.DHashThreshold.ToString();
        CooldownBox.Text = _edit.Capture.CooldownSec.ToString();
        StableBox.Text = _edit.Capture.StableFrames.ToString();

        EdgeCombo.SelectedIndex = _edit.Dock.Edge switch
        {
            "Left" => 1, "Top" => 2, "Bottom" => 3, _ => 0
        };
        AutoHideBox.IsChecked = _edit.Dock.AutoHide;
        AttachBox.IsChecked = _edit.Dock.AttachToTarget;
        FreeMoveBox.IsChecked = _edit.Dock.FreeMove;

        AutoExecBox.IsChecked = _edit.Automation.Enabled;
        ConfirmBox.IsChecked = _edit.Automation.ConfirmEach;
    }

    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = HostBox.Text.Trim();
            var model = ModelBox.Text.Trim();
            var client = new OllamaClient(new OllamaConfig { Host = host, Model = model, TimeoutSec = 8 });
            var models = await client.ListModelsAsync();
            var hit = models.Find(m => m.Equals(model, StringComparison.OrdinalIgnoreCase));
            MessageBox.Show(this,
                hit != null
                    ? $"连接成功。模型 {hit} 已就绪。"
                    : $"连接成功，但未找到模型「{model}」。现有模型：{(models.Count == 0 ? "（无）" : string.Join(", ", models))}",
                "Ollama 校验", MessageBoxButton.OK,
                hit != null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"连接失败：{ex.Message}", "Ollama 校验", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollBox.Text, out var poll) || poll < 100) { PollBox.Text = "800"; poll = 800; }
        if (!int.TryParse(DHashBox.Text, out var dh) || dh < 0) { DHashBox.Text = "8"; dh = 8; }
        if (!int.TryParse(CooldownBox.Text, out var cool) || cool < 0) { CooldownBox.Text = "10"; cool = 10; }
        if (!int.TryParse(StableBox.Text, out var stable) || stable < 1) { StableBox.Text = "2"; stable = 2; }

        _edit.Ollama.Host = string.IsNullOrWhiteSpace(HostBox.Text) ? "http://localhost:11434" : HostBox.Text.Trim();
        _edit.Ollama.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "qwen2.5vl:7b" : ModelBox.Text.Trim();
        _edit.Capture.PollIntervalMs = poll;
        _edit.Capture.DHashThreshold = dh;
        _edit.Capture.CooldownSec = cool;
        _edit.Capture.StableFrames = stable;

        _edit.Dock.Edge = EdgeCombo.SelectedIndex switch { 1 => "Left", 2 => "Top", 3 => "Bottom", _ => "Right" };
        _edit.Dock.AutoHide = AutoHideBox.IsChecked == true;
        _edit.Dock.AttachToTarget = AttachBox.IsChecked == true;
        _edit.Dock.FreeMove = FreeMoveBox.IsChecked == true;
        if (_edit.Dock.FreeMove) _edit.Dock.AttachToTarget = false; // 自由移动时不吸附

        _edit.Automation.Enabled = AutoExecBox.IsChecked == true;
        _edit.Automation.ConfirmEach = ConfirmBox.IsChecked == true;

        _src.Ollama = _edit.Ollama;
        _src.Capture = _edit.Capture;
        _src.Dock = _edit.Dock;
        _src.Automation = _edit.Automation;
        _src.Ai = _edit.Ai;
        _src.Save();

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
