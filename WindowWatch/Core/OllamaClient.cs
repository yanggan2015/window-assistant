using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WindowWatch.Core;

/// <summary>本地 Ollama 视觉模型客户端。</summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly OllamaConfig _cfg;

    public OllamaClient(OllamaConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, cfg.TimeoutSec)) };
    }

    /// <summary>名称匹配：容忍省略 ":latest" 标签（如 minicpm-v 视为 minicpm-v:latest）。</summary>
    public static bool ModelMatches(string listed, string wanted)
    {
        var a = (listed ?? "").Trim();
        var b = (wanted ?? "").Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.StartsWith(b + ":", StringComparison.OrdinalIgnoreCase)) return true;
        if (b.StartsWith(a + ":", StringComparison.OrdinalIgnoreCase)) return true;
        var an = a.Contains(':') ? a : a + ":latest";
        var bn = b.Contains(':') ? b : b + ":latest";
        return an.Equals(bn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>健康检查：返回模型列表（空 = 未安装/未启动）。</summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_cfg.Host.TrimEnd('/')}/api/tags", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) list.Add(n.GetString() ?? "");
            return list;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接 Ollama（{_cfg.Host}）：{ex.Message}", ex);
        }
    }

    /// <summary>用视觉模型分析图片，返回建议文本。</summary>
    public async Task<string> GenerateAsync(
        string model,
        string prompt,
        IEnumerable<string> imagesBase64,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["images"] = new List<string>(imagesBase64),
            ["stream"] = false,
            ["options"] = new Dictionary<string, object> { ["num_ctx"] = 4096 }
        };
        var payload = JsonSerializer.Serialize(body);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync($"{_cfg.Host.TrimEnd('/')}/api/generate", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"请求 Ollama 失败：{ex.Message}", ex);
        }

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama 返回 {(int)resp.StatusCode}：{Trim(text, 300)}");
        }
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
        {
            var emsg = err.GetString() ?? "";
            if (emsg.Contains("not found", StringComparison.OrdinalIgnoreCase) || emsg.Contains("does not support images", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"模型 {model} 未安装或不是视觉模型，请先 ollama pull {model}");
            throw new InvalidOperationException($"Ollama 错误：{Trim(emsg, 300)}");
        }
        if (doc.RootElement.TryGetProperty("response", out var r))
            return (r.GetString() ?? "").Trim();
        return "";
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
