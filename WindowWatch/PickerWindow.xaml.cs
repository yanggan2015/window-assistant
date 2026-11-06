using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowWatch.Core;

namespace WindowWatch;

public partial class PickerWindow : Window
{
    private readonly AppConfig _cfg;

    public WindowTarget? SelectedTarget { get; private set; }
    public string SelectedProfile { get; private set; } = "general";
    public string? SelectedPromptOverride { get; private set; }

    public PickerWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        foreach (var p in Profiles.List) ProfileCombo.Items.Add(p.Name);
        ProfileCombo.SelectedIndex = 0;
        Refresh();
    }

    private void Refresh()
    {
        WindowList.Items.Clear();
        foreach (var w in WindowPicker.Enumerate())
            WindowList.Items.Add(w);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>选中窗口时按进程名/标题自动匹配画像（微信/串口/浏览器/文档/视频），可再手动改。</summary>
    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowItem item) return;
        var key = GuessProfile(item.ProcessName + " " + item.Title + " " + item.ClassName);
        for (int i = 0; i < Profiles.List.Count; i++)
        {
            if (Profiles.List[i].Key == key) { ProfileCombo.SelectedIndex = i; break; }
        }
    }

    private static string GuessProfile(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("wechat") || t.Contains("weixin") || t.Contains("微信")) return "wechat";
        if (t.Contains("securecrt") || t.Contains("putty") || t.Contains("mobaxterm") || t.Contains("xshell")
            || t.Contains("windterm") || t.Contains("terminal") || t.Contains("串口") || t.Contains("comtool")) return "serial";
        if (t.Contains("chrome") || t.Contains("edge") || t.Contains("firefox") || t.Contains("浏览器") || t.Contains("browser")) return "browser";
        if (t.Contains("winword") || t.Contains("word") || t.Contains("wps") || t.Contains("文档")) return "doc";
        if (t.Contains("vlc") || t.Contains("potplayer") || t.Contains("mpv") || t.Contains("播放") || t.Contains("视频")) return "media";
        return "general";
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowList.SelectedItem != null) Finish();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        if (WindowList.SelectedItem is not WindowItem item) return;
        SelectedTarget = WindowPicker.ToTarget(item);
        SelectedProfile = Profiles.List[ProfileCombo.SelectedIndex].Key;
        SelectedPromptOverride = string.IsNullOrWhiteSpace(PromptBox.Text) ? null : PromptBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
