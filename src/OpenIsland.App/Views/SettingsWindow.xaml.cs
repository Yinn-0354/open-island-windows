using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using OpenIsland.App.Services;

namespace OpenIsland.App.Views;

public partial class SettingsWindow : Window
{
    private readonly WorkspaceSettings _settings;
    private readonly ObservableCollection<string> _draft = new();

    public SettingsWindow(WorkspaceSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        foreach (var w in _settings.Workspaces) _draft.Add(w);
        WorkspacesList.ItemsSource = _draft;
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 内置的 OpenFolderDialog（无须 WinForms 依赖）
        var dlg = new OpenFolderDialog
        {
            Title = "选择工作区目录",
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FolderName?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(path)) return;
        if (_draft.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _draft.Add(path);
    }

    private void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspacesList.SelectedItem is string s) _draft.Remove(s);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetWorkspaces(_draft);
        TrySetDialogResult(true);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        TrySetDialogResult(false);
        Close();
    }

    /// <summary>
    /// 设置 DialogResult 仅当窗口是用 ShowDialog() 打开的（IsModal）。Show() 模式下设
    /// DialogResult 会抛 InvalidOperationException，旧版导致用户点保存整个 app 崩。
    /// </summary>
    private void TrySetDialogResult(bool value)
    {
        try { DialogResult = value; } catch (InvalidOperationException) { /* 非 dialog 模式 */ }
    }
}
