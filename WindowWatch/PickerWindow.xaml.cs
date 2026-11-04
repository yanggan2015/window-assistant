using System;
using System.Windows;
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
