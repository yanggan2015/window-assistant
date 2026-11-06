using System;
using System.Collections.Generic;

namespace WindowWatch.Core;

/// <summary>内置窗口画像（提示词预设）。</summary>
public static class Profiles
{
    public sealed record ProfileInfo(string Key, string Name, string Prompt);

    private static readonly List<ProfileInfo> All = new()
    {
        new("serial", "串口/终端",
            "你是串口日志助手。只输出两行，不要分析过程、不要复述原文、不要客套：\n" +
            "【新内容】<最新一行日志/数据的关键信息，30字内>\n" +
            "【动作】<需要人工做什么，没有就写“无需处理”，20字内>"),
        new("wechat", "微信/聊天",
            "这是聊天窗口截图。只看最后一条对方消息，然后**只输出两行**：\n" +
            "【对方】<直接抄最后一条消息的发送者或开头几个字，不要总结，20字内>\n" +
            "【回复】<一句可直接发送的话，25字内>\n" +
            "规则：用图中对话的语言作答；不要编号、不要解释、不要复述整段聊天、不要写“用户：”；" +
            "如果没有对话内容，两行都写“无”。"),
        new("browser", "浏览器/网页",
            "这是浏览器截图。只输出两行，不要分析过程：\n" +
            "【页面】<当前页面在做什么/关键信息，30字内>\n" +
            "【下一步】<最值得做的一步操作，20字内>"),
        new("doc", "文档/Word",
            "这是文档截图。只输出两行：\n" +
            "【内容】<当前可见内容一句话概括，30字内>\n" +
            "【建议】<一条具体可执行的编辑建议，25字内>"),
        new("media", "电影/视频",
            "这是视频播放画面。只输出一行，不要分析过程：<当前画面在一句话内说了什么（25字内）；无字幕信息就概括画面>"),
        new("general", "通用",
            "只输出两行，不要分析过程、不要标题堆叠、不要客套：\n" +
            "【要点】<画面里最关键的一句话，30字内>\n" +
            "【建议】<一条可执行的动作或结论，没有就写“无需处理”，20字内>"),
    };

    public static IReadOnlyList<ProfileInfo> List => All;

    public static ProfileInfo Get(string key)
    {
        foreach (var p in All) if (p.Key == key) return p;
        return All[^1]; // general
    }

    /// <summary>组装最终提示词（会话自定义优先）。</summary>
    public static string BuildPrompt(string profileKey, string? overridePrompt)
    {
        if (!string.IsNullOrWhiteSpace(overridePrompt)) return overridePrompt!;
        return Get(profileKey).Prompt;
    }

    /// <summary>微信第一遍：只抄录对方最后一条消息（小模型一次干两件事必翻车）。</summary>
    public const string WechatOcrPrompt =
        "抄录任务：把聊天窗口里最底部那条来自对方的消息原样抄下来。\n" +
        "只输出这一句原文，不要序号、不要引号、不要解释、不要总结。\n" +
        "如果看不到任何对话，只输出两个字：无";

    /// <summary>微信第二遍：纯文本生成可直接发送的一条回复。</summary>
    public static string WechatReplyPrompt(string peerMessage) =>
        "你在微信上收到一条消息：\n\"" + peerMessage + "\"\n" +
        "请只输出一条可以直接发出去的回复，30字以内，语气自然、口语化。\n" +
        "不要解释你的思路，不要写“回复：”，不要加引号，不要分点。";

    /// <summary>判断一行文本是不是模型在复述提示词/规则（这种内容绝不能显示给用户）。</summary>
    public static bool IsJunk(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        var s = line.Trim();
        if (s.Length <= 1) return true;
        foreach (var k in new[] { "规则", "不要解释", "不要编号", "逐字抄录", "抄录任务", "用图中", "回答中", "作为AI", "作为一个AI", "无法识别", "看不清" })
            if (s.Contains(k, StringComparison.Ordinal)) return true;
        if (s.Equals("无", StringComparison.Ordinal) || s.Equals("未知", StringComparison.Ordinal)) return false;
        return false;
    }

    /// <summary>去掉模型爱加的引号/前缀，得到可以原样发送或展示的一行。</summary>
    public static string CleanLine(string? s, int maxLen)
    {
        if (s == null) return "";
        var line = s.Replace("\r", "").Replace("\n", " ").Trim();
        line = line.Trim('"', '“', '”', '\'', '：', ':', '、', ' ');
        foreach (var p in new[] { "对方：", "对方:", "我：", "我:", "回复：", "回复:", "用户：", "用户:", "原文：", "原文:" })
            if (line.StartsWith(p, StringComparison.Ordinal)) line = line.Substring(p.Length).Trim();
        if (line.Length > maxLen)
        {
            var cut = line.Substring(0, maxLen);
            var lastSpace = cut.LastIndexOf(' ');
            // 英文单词被截断时退到最近的空格，避免出现 "I was dri" 这种半截词
            if (lastSpace > maxLen / 2) cut = cut.Substring(0, lastSpace);
            line = cut.TrimEnd() + (cut.EndsWith("。") || cut.EndsWith("！") || cut.EndsWith("？") ? "" : "…");
        }
        return line;
    }

    private static readonly string[] NoisePrefix =
    {
        "根据图片", "从这张", "这张截图", "该截图", "图片显示", "画面显示", "分析如下", "以下是我"
    };

    /// <summary>
    /// 兜底压缩：小模型常自作多情写分析过程，这里只保留真正的结论行，避免界面出现大段废话。
    /// </summary>
    public static string Compact(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var kept = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var noise = false;
            foreach (var p in NoisePrefix) if (line.StartsWith(p, StringComparison.Ordinal)) { noise = true; break; }
            if (noise) continue;
            if (line.Length > 120) line = line.Substring(0, 120);
            kept.Add(line);
            if (kept.Count >= maxLines) break;
        }
        return kept.Count == 0 ? text.Trim() : string.Join("\n", kept);
    }

    /// <summary>
    /// 微信画像专用：把模型输出强行收敛成两行【对方】【回复】。
    /// 小模型经常添标题、解释或"用户：…"，直接取标记内容；标记缺失时退化为"首行=对方，末行=回复"。
    /// </summary>
    public static (string Peer, string Reply) SplitWechat(string text)
    {
        var cleaned = text.Replace("\r", "");
        string peer = "", reply = "";

        var iPeer = cleaned.IndexOf("【对方】", StringComparison.Ordinal);
        var iReply = cleaned.IndexOf("【回复】", StringComparison.Ordinal);

        if (iPeer >= 0 && iReply > iPeer)
            peer = cleaned.Substring(iPeer + 4, iReply - iPeer - 4);
        if (iReply >= 0)
            reply = cleaned.Substring(iReply + 4);

        string FirstLine(string s) => s.Replace("\n", "").Trim().Trim('"', '“', '”', '：', ':', ' ');

        peer = FirstLine(peer);
        reply = FirstLine(reply);

        if (reply.Length == 0)
        {
            // 没有标记：取最后一行非空文本作为可发送的话
            var lines = cleaned.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                l = l.TrimStart('•', '-', '*', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.', '、', ' ');
                if (l.Length > 0 && !l.StartsWith("【", StringComparison.Ordinal))
                {
                    reply = FirstLine(l);
                    break;
                }
            }
        }
        if (peer.Length == 0 && reply.Length > 0) peer = "对方发来一条消息";

        if (reply.Length == 0) return ("无", "无");
        // 去掉模型爱加的主角前缀，保证"回复"本身可以直接发送
        foreach (var p in new[] { "对方：", "对方:", "我：", "我:", "回复：", "回复:", "用户：", "用户:" })
            if (reply.StartsWith(p, StringComparison.Ordinal)) reply = reply.Substring(p.Length).Trim();
        if (peer.Length > 40) peer = peer.Substring(0, 40);
        if (reply.Length > 60) reply = reply.Substring(0, 60);
        return (peer, reply);
    }
}
