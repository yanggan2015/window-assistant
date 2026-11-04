using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowWatch.Core;

/// <summary>窗口目标描述（句柄易失效，故持久化时用进程/标题/类名定位）。</summary>
public sealed class WindowTarget
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Title) ? $"{ProcessName} ({ClassName})" : Title;

    public bool IsValid => Hwnd != IntPtr.Zero && NativeMethods.IsWindow(Hwnd);
}

public sealed class OllamaConfig
{
    public string Host { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5vl:7b";
    public int TimeoutSec { get; set; } = 60;
}

public sealed class CaptureConfig
{
    public int PollIntervalMs { get; set; } = 800;
    public int DHashThreshold { get; set; } = 8;
    public double PixelDiffThreshold { get; set; } = 0.02;
    public int StableFrames { get; set; } = 2;
    public int CooldownSec { get; set; } = 10;
    public int MaxEdgePx { get; set; } = 1280;
}

public sealed class AiConfig
{
    public int MaxConcurrent { get; set; } = 1;
    public bool AnalyzeDiffRegion { get; set; } = true;
}

public sealed class DockConfig
{
    public string Edge { get; set; } = "Right";   // Left / Right / Top / Bottom
    public bool AutoHide { get; set; } = false;   // 默认不自动隐藏，仅在设置中勾选后生效
    public bool AttachToTarget { get; set; } = false;
    public bool FreeMove { get; set; } = false;   // 自由移动：不贴边、不吸附，可拖到任意位置
    public int StripPx { get; set; } = 8;
    public int DockOffsetPx { get; set; } = 8;
}

public sealed class AutomationConfig
{
    public bool Enabled { get; set; } = false;
    public bool ConfirmEach { get; set; } = true;
    public int ClickDelayMs { get; set; } = 120;
}

public sealed class SessionConfig
{
    public WindowTarget Window { get; set; } = new();
    public string Profile { get; set; } = "general";   // serial / wechat / browser / doc / media / general
    public string? PromptOverride { get; set; }
}

public sealed class AppConfig
{
    public OllamaConfig Ollama { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public DockConfig Dock { get; set; } = new();
    public AutomationConfig Automation { get; set; } = new();
    public List<SessionConfig> Sessions { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
        }
        catch { /* 损坏配置回退默认 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 忽略写失败 */ }
    }
}

/// <summary>一次分析的结果。</summary>
public sealed class AnalysisResult
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string SessionName { get; set; } = "";
    public string Suggestion { get; set; } = "";        // AI 原始建议文本
    public string Error { get; set; } = "";              // 非空表示分析失败
    public bool IsError => !string.IsNullOrWhiteSpace(Error);
    public List<string> SuggestedActions { get; set; } = new();
}

/// <summary>窗口枚举列表项。</summary>
public sealed class WindowItem
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Display => string.IsNullOrWhiteSpace(Title) ? $"[无标题] {ProcessName}" : Title;
}
