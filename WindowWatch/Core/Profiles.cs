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
            "你是串口调试助手。分析这张截图：\n" +
            "1) 提取新出现的数据行/日志/命令；\n" +
            "2) 判断是否有错误、异常、待处理的请求或需要响应的提示；\n" +
            "3) 用中文简洁输出三部分：【发生了什么】【是否需要处理】【建议动作】。\n" +
            "建议动作用短句，如：发送重启命令、确认应答、清屏。"),
        new("wechat", "微信/聊天",
            "这是微信/聊天窗口截图。分析：\n" +
            "1) 总结最新几条消息与对方意图；\n" +
            "2) 给出自然、得体的回复建议（可用一句话）；\n" +
            "3) 输出【消息摘要】【对方意图】【回复建议】。"),
        new("browser", "浏览器/网页",
            "这是浏览器截图。分析：\n" +
            "1) 总结当前页面关键信息（标题/主要内容/关键按钮）；\n" +
            "2) 指出下一步可做的操作。\n" +
            "输出【页面摘要】【下一步建议】。"),
        new("doc", "文档/Word",
            "这是文档窗口截图。分析：\n" +
            "1) 概述当前可见内容与大致位置；\n" +
            "2) 给出写作/编辑方面的建议。\n" +
            "输出【文档概况】【编辑建议】。"),
        new("media", "电影/视频",
            "这是视频播放画面。只做极简提示：当前画面大致在演什么，是否有值得注意的字幕信息。若变化不大请简短回应。"),
        new("general", "通用",
            "分析这张窗口截图：\n" +
            "1) 概括画面内容；\n" +
            "2) 指出是否有异常、新变化或需要注意的信息；\n" +
            "3) 给出建议。\n" +
            "输出【画面概况】【异常/变化】【建议】。"),
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
}
