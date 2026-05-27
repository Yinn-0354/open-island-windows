using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenIsland.App.Services;
using OpenIsland.Core;

namespace OpenIsland.App.Views;

public partial class SettingsWindow : Window
{
    private readonly WorkspaceSettings _settings;
    private readonly ObservableCollection<string> _draft = new();
    private readonly ObservableCollection<ModelProfile> _modelProfiles = new();
    private ModelProfile? _selectedPreset;
    private string? _editingId; // 非空 = 正在编辑该 Id 的已存档案（保存时复用其 Id，不新建重复项）

    public SettingsWindow(WorkspaceSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var w in _settings.Workspaces) _draft.Add(w);
        WorkspacesList.ItemsSource = _draft;

        foreach (var p in _settings.ModelProfiles) _modelProfiles.Add(p);
        ModelsList.ItemsSource = _modelProfiles;
        PresetCombo.ItemsSource = ModelPresets.ThirdParty;
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

    // ── 模型 / Providers ──

    /// <summary>选中一个内置预设：自动填名称/地址/默认模型；API Key 留给用户填。</summary>
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ModelProfile p) return;
        _selectedPreset = p;
        _editingId = null; // 选预设 = 新建模式
        NameBox.Text = p.Name;
        BaseUrlBox.Text = p.BaseUrl ?? "";
        ModelBox.Text = p.Model ?? "";
        ApiKeyBox.Password = "";
    }

    /// <summary>选中已有模型 = 进入编辑模式：回填表单并记住其 Id（保存时复用，不新建重复项）。</summary>
    private void Models_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelsList.SelectedItem is not ModelProfile p) return;
        _editingId = p.Id;
        _selectedPreset = p; // 以被编辑档为基底，保留其 ApiKeyEnvName / 角色模型
        NameBox.Text = p.Name;
        BaseUrlBox.Text = p.BaseUrl ?? "";
        ModelBox.Text = p.Model ?? "";
        ApiKeyBox.Password = p.ApiKey ?? "";
    }

    /// <summary>添加/保存一个第三方模型档案（立即持久化）。</summary>
    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var baseUrl = BaseUrlBox.Text?.Trim() ?? "";
        var key = ApiKeyBox.Password?.Trim() ?? "";
        var model = ModelBox.Text?.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(key))
        {
            MessageBox.Show(this, "请填写名称、地址和 API Key。", "Open Island",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 选了预设 / 在编辑已存档就以其为基底（带上角色模型 Haiku/Sonnet/Opus + key 字段名），再用表单覆盖。
        var keyEnv = _selectedPreset?.ApiKeyEnvName ?? "ANTHROPIC_AUTH_TOKEN";
        var baseProfile = _selectedPreset ?? new ModelProfile { Kind = ModelKind.ThirdParty };
        // 编辑模式复用原 Id；新建才生成新 Id —— 避免每次保存都堆出重复 profile。
        var id = _editingId ?? ("user-" + Guid.NewGuid().ToString("N")[..8]);
        var profile = baseProfile with
        {
            Id = id,
            Name = name,
            Kind = ModelKind.ThirdParty,
            BaseUrl = baseUrl,
            ApiKeyEnvName = keyEnv,
            ApiKey = key,
            Model = string.IsNullOrEmpty(model) ? null : model
        };

        _settings.AddOrUpdateModelProfile(profile);
        // UI 集合：编辑则就地替换，新建才追加。
        var idx = -1;
        for (int i = 0; i < _modelProfiles.Count; i++)
            if (_modelProfiles[i].Id == id) { idx = i; break; }
        if (idx >= 0) _modelProfiles[idx] = profile; else _modelProfiles.Add(profile);

        NameBox.Text = "";
        BaseUrlBox.Text = "";
        ModelBox.Text = "";
        ApiKeyBox.Password = "";
        PresetCombo.SelectedItem = null;
        _selectedPreset = null;
        _editingId = null;
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is ModelProfile p)
        {
            _settings.RemoveModelProfile(p.Id);
            _modelProfiles.Remove(p);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 工作区走草稿，保存时落盘；模型增删已即时持久化。
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
