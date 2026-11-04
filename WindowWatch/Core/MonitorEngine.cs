using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowWatch.Core;

/// <summary>一个被监控窗口的会话。</summary>
public sealed class MonitorSession
{
    public WindowTarget Target { get; set; } = new();
    public string ProfileKey { get; set; } = "general";
    public string? PromptOverride { get; set; }
    public bool Paused { get; set; }
    public int EventCount { get; internal set; }
    public DateTime LastFired { get; internal set; } = DateTime.MinValue;
    public bool IsAnalyzing { get; internal set; }
    public string DisplayName => Target.Display;

    // 内部状态
    internal ulong? PrevHash;
    internal int ChangedStreak;
    internal AnalysisResult? LastResult;
}

/// <summary>监控引擎：编排截图→变化检测→AI分析→结果下发。</summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly OllamaClient _ollama;
    private readonly List<MonitorSession> _sessions = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _analysisSlots;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();

    public event Action<MonitorSession, AnalysisResult>? AnalysisCompleted;
    public event Action<MonitorSession, string>? Log;
    public event Action<MonitorSession>? EventDetected;

    public IReadOnlyList<MonitorSession> Sessions
    {
        get { lock (_lock) return _sessions.ToArray(); }
    }

    public MonitorEngine(AppConfig cfg)
    {
        _cfg = cfg;
        _ollama = new OllamaClient(cfg.Ollama);
        _analysisSlots = new SemaphoreSlim(Math.Max(1, cfg.Ai.MaxConcurrent));
    }

    public MonitorSession AddSession(WindowTarget target, string profile, string? promptOverride)
    {
        var s = new MonitorSession { Target = target, ProfileKey = profile, PromptOverride = promptOverride };
        lock (_lock) _sessions.Add(s);
        return s;
    }

    public void RemoveSession(MonitorSession s)
    {
        lock (_lock) _sessions.Remove(s);
    }

    public void Start()
    {
        foreach (var s in Sessions) _loops.Add(RunLoop(s, _cts.Token));
    }

    public async Task AnalyzeNow(MonitorSession s)
    {
        await AnalyzeAsync(s, _cts.Token).ConfigureAwait(false);
    }

    private async Task RunLoop(MonitorSession s, CancellationToken ct)
    {
        var interval = Math.Max(100, _cfg.Capture.PollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (s.Paused || !s.Target.IsValid)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    continue;
                }

                using var bmp = WindowCapture.Capture(s.Target, _cfg.Capture.MaxEdgePx);
                if (bmp == null)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    continue;
                }

                var hash = WindowCapture.DHash(bmp);
                var changed = false;
                if (s.PrevHash.HasValue)
                {
                    var d = WindowCapture.Hamming(s.PrevHash.Value, hash);
                    changed = d >= _cfg.Capture.DHashThreshold;
                }
                s.PrevHash = hash;

                if (changed)
                {
                    s.ChangedStreak++;
                    if (s.ChangedStreak >= Math.Max(1, _cfg.Capture.StableFrames)
                        && (DateTime.Now - s.LastFired).TotalSeconds >= _cfg.Capture.CooldownSec)
                    {
                        s.LastFired = DateTime.Now;
                        s.ChangedStreak = 0;
                        s.EventCount++;
                        EventDetected?.Invoke(s);
                        // 不阻塞循环：异步分析，但并发受信号量限制
                        _ = Task.Run(() => AnalyzeAsync(s, ct), ct);
                    }
                }
                else
                {
                    s.ChangedStreak = 0;
                }

                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke(s, $"监控循环错误：{ex.Message}");
                try { await Task.Delay(interval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task AnalyzeAsync(MonitorSession s, CancellationToken ct)
    {
        await _analysisSlots.WaitAsync(ct).ConfigureAwait(false);
        s.IsAnalyzing = true;
        try
        {
            var result = new AnalysisResult { Time = DateTime.Now, SessionName = s.DisplayName };
            try
            {
                if (!s.Target.IsValid) throw new InvalidOperationException("目标窗口已关闭");
                using var bmp = WindowCapture.Capture(s.Target, _cfg.Capture.MaxEdgePx);
                if (bmp == null) throw new InvalidOperationException("无法截取窗口");

                var prompt = Profiles.BuildPrompt(s.ProfileKey, s.PromptOverride);
                var b64 = BitmapToBase64(bmp);
                var suggestion = await _ollama.GenerateAsync(_cfg.Ollama.Model, prompt, new[] { b64 }, ct).ConfigureAwait(false);
                result.Suggestion = suggestion;
                ParseActions(result);
            }
            catch (OperationCanceledException) { result.Error = "分析已取消"; }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log?.Invoke(s, $"分析失败：{ex.Message}");
            }

            s.LastResult = result;
            AnalysisCompleted?.Invoke(s, result);
        }
        finally
        {
            s.IsAnalyzing = false;
            _analysisSlots.Release();
        }
    }

    private static void ParseActions(AnalysisResult r)
    {
        // 简单启发式：从建议文本中摘取含关键动词的短句作为可执行动作
        if (string.IsNullOrWhiteSpace(r.Suggestion)) return;
        var lines = r.Suggestion.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var clean = line.TrimStart('•', '-', '*', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.', '、', '：', ':');
            clean = clean.Trim();
            if (clean.Length < 2 || clean.Length > 80) continue;
            if (clean.Contains("【")) continue; // 跳过小节标题
            r.SuggestedActions.Add(clean);
            if (r.SuggestedActions.Count >= 5) break;
        }
    }

    private static string BitmapToBase64(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return Convert.ToBase64String(ms.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
        _analysisSlots.Dispose();
    }
}
