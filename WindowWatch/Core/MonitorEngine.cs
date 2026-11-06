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
    internal ulong? BaselineHash;
    internal byte[,]? BaselineSmall;
    internal int ChangedStreak;
    internal DateTime LastWarn = DateTime.MinValue;
    internal DateTime LastBeat = DateTime.MinValue;
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
    private volatile bool _running;

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
        // 运行中新增的窗口，必须同时启动它的监控循环，否则只有手动分析、不会自动连续分析
        if (_running) _loops.Add(RunLoop(s, _cts.Token));
        return s;
    }

    public void RemoveSession(MonitorSession s)
    {
        lock (_lock) _sessions.Remove(s);
    }

    public void Start()
    {
        _running = true;
        foreach (var s in Sessions) _loops.Add(RunLoop(s, _cts.Token));
    }

    public async Task AnalyzeNow(MonitorSession s)
    {
        await AnalyzeAsync(s, _cts.Token).ConfigureAwait(false);
    }

    private async Task RunLoop(MonitorSession s, CancellationToken ct)
    {
        var interval = Math.Max(100, _cfg.Capture.PollIntervalMs);
        Log?.Invoke(s, $"开始监控：{s.DisplayName}（轮询 {interval}ms）");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                lock (_lock) { if (!_sessions.Contains(s)) break; }   // 会话被移除则结束循环
                if (s.Paused || !s.Target.IsValid)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    continue;
                }

                using var bmp = WindowCapture.Capture(s.Target, _cfg.Capture.MaxEdgePx);
                if (bmp == null)
                {
                    if ((DateTime.Now - s.LastWarn).TotalSeconds > 30)
                    {
                        s.LastWarn = DateTime.Now;
                        Log?.Invoke(s, "截图失败：目标窗口可能已最小化或被遮挡，稍后重试");
                    }
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    continue;
                }

                var hash = WindowCapture.DHash(bmp);
                var small = WindowCapture.SampleGray(bmp, 320);
                var changed = false;
                var d = 0;
                var ratio = 0.0;

                // 基准 = "上次已分析过的那一帧"，而不是上一帧。
                // 逐帧比较会漏掉"变一帧就停"的场景（内容每几秒才更新一次，永远凑不齐连续多帧变化）。
                if (s.BaselineHash.HasValue)
                {
                    d = WindowCapture.Hamming(s.BaselineHash.Value, hash);
                    // dHash 捕捉整体布局变化；像素占比捕捉"只多了一两行小字"这类细微变化
                    ratio = s.BaselineSmall != null ? WindowCapture.DiffRatio(s.BaselineSmall, small) : 0;
                    changed = d >= _cfg.Capture.DHashThreshold || ratio > _cfg.Capture.PixelDiffThreshold;
                }
                else
                {
                    s.BaselineHash = hash;
                    s.BaselineSmall = small;
                }

                if (changed)
                {
                    s.ChangedStreak++;
                    if (s.ChangedStreak >= Math.Max(1, _cfg.Capture.StableFrames)
                        && !s.IsAnalyzing                                   // 上一轮还没分析完就不叠加新任务，避免堆积过期结果
                        && (DateTime.Now - s.LastFired).TotalSeconds >= _cfg.Capture.CooldownSec)
                    {
                        s.LastFired = DateTime.Now;
                        s.ChangedStreak = 0;
                        s.EventCount++;
                        // 以触发分析的这一帧作为新基准：同一内容不重复分析
                        s.BaselineHash = hash;
                        s.BaselineSmall = small;
                        Log?.Invoke(s, $"检测到内容变化（dHash {d}，像素 {ratio * 100:F1}%）→ 自动分析中…");
                        EventDetected?.Invoke(s);
                        // 不阻塞循环：异步分析，但并发受信号量限制
                        _ = Task.Run(() => AnalyzeAsync(s, ct), ct);
                    }
                }
                else
                {
                    s.ChangedStreak = 0;
                    // 分析进行中不要推进基准，否则会漏掉分析期间到达的新消息
                    if (!s.IsAnalyzing)
                    {
                        s.BaselineHash = hash;
                        s.BaselineSmall = small;
                    }
                }

                // 心跳：只写文件不刷界面，避免事件日志被"监控中"淹没
                if ((DateTime.Now - s.LastBeat).TotalSeconds > 30)
                {
                    s.LastBeat = DateTime.Now;
                    // 调试落盘：记录实际截到的画面与判据数值（排查"截错区域/截图空白"）
                    try
                    {
                        var dbg = System.IO.Path.Combine(AppContext.BaseDirectory, "debug");
                        System.IO.Directory.CreateDirectory(dbg);
                        bmp.Save(System.IO.Path.Combine(dbg, $"capture_{s.Target.Hwnd}.png"), System.Drawing.Imaging.ImageFormat.Png);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(dbg, "monitor.log"),
                            $"{DateTime.Now:HH:mm:ss} hwnd={s.Target.Hwnd} size={bmp.Width}x{bmp.Height} d={d} ratio={ratio:F4} events={s.EventCount}\r\n");
                    }
                    catch { }
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
                string suggestion;
                if (string.IsNullOrWhiteSpace(s.PromptOverride) && s.ProfileKey == "wechat")
                {
                    // 微信：先抄录对方最后一句，再据此生成一句可发送的回复（两遍，避免小模型一次输出一堆废话）
                    var peer = "";
                    for (int attempt = 0; attempt < 2 && (peer.Length == 0 || Profiles.IsJunk(peer)); attempt++)
                    {
                        var ocr = await _ollama.GenerateAsync(_cfg.Ollama.Model, Profiles.WechatOcrPrompt, new[] { b64 }, ct).ConfigureAwait(false);
                        var firstLine = ocr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
                        peer = Profiles.CleanLine(firstLine, 40);
                    }
                    var gotPeer = !Profiles.IsJunk(peer) && peer != "无";
                    var reply = "无";
                    if (gotPeer)
                    {
                        var raw = await _ollama.GenerateAsync(_cfg.Ollama.Model, Profiles.WechatReplyPrompt(peer), Array.Empty<string>(), ct).ConfigureAwait(false);
                        var line = raw.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
                        reply = Profiles.CleanLine(line, 40) is { Length: > 0 } r && !Profiles.IsJunk(r) ? r : "无";
                    }
                    if (!gotPeer) peer = "无";
                    suggestion = $"【对方】{peer}\n【回复】{reply}";
                    result.SuggestedActions.Clear();
                    result.SuggestedActions.Add(reply);
                }
                else
                {
                    suggestion = await _ollama.GenerateAsync(_cfg.Ollama.Model, prompt, new[] { b64 }, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(s.PromptOverride))
                        suggestion = Profiles.Compact(suggestion, 4);
                }
                result.Suggestion = suggestion;
                if (!(string.IsNullOrWhiteSpace(s.PromptOverride) && s.ProfileKey == "wechat")) ParseActions(result);   // 微信画像只需要"一条可发送回复"这一动作
                try
                {
                    var dbg = System.IO.Path.Combine(AppContext.BaseDirectory, "debug");
                    System.IO.Directory.CreateDirectory(dbg);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(dbg, "analysis.log"),
                        $"{DateTime.Now:HH:mm:ss} profile={s.ProfileKey} len={suggestion.Length}\r\n---\r\n{suggestion}\r\n---\r\n");
                }
                catch { }
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
