using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _initializing = true; // 构造期设 LanguageCombo 选中项不触发切换
    private bool _recordingHotkey;     // 正在录制截图快捷键

    public SettingsWindow(WorkspaceSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var w in _settings.Workspaces) _draft.Add(w);
        WorkspacesList.ItemsSource = _draft;

        foreach (var p in _settings.ModelProfiles) _modelProfiles.Add(p);
        ModelsList.ItemsSource = _modelProfiles;
        PresetCombo.ItemsSource = ModelPresets.ThirdParty;

        // 语言下拉对齐当前设置（auto=0 / zh=1 / en=2）
        LanguageCombo.SelectedIndex = _settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
        HotkeyBox.Content = _settings.ScreenshotHotkey;
        // 毛玻璃区块对齐当前设置（_initializing 保护中，赋值不会触发 Glass_Changed 重复落盘）
        GlassCheck.IsChecked = _settings.GlassEnabled;
        GlassSlider.Value = _settings.GlassOpacity;
        GlassVal.Text = _settings.GlassOpacity + "%";
        _initializing = false;
    }

    /// <summary>毛玻璃开关变化：只动 glassEnabled（与滑块解耦 —— 每次 Set 都是一次完整落盘
    /// + Changed 全量广播（热键重绑等），不能把没变的值也跟着写一遍）。</summary>
    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var on = GlassCheck.IsChecked == true;
        if (on != _settings.GlassEnabled) _settings.SetGlassEnabled(on);
    }

    /// <summary>不透明度滑块：标签即时跟手；落盘去抖 250ms —— 拖动会跨多个 tick，每个 tick
    /// 直接 Save 会造成几十次 DPAPI 序列化落盘 + Changed 广播（热键反复解绑/重绑）。</summary>
    private System.Windows.Threading.DispatcherTimer? _glassDebounce;
    private void GlassSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var v = (int)GlassSlider.Value;
        GlassVal.Text = v + "%";
        if (_glassDebounce == null)
        {
            _glassDebounce = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            _glassDebounce.Tick += (_, _) =>
            {
                _glassDebounce!.Stop();
                var val = (int)GlassSlider.Value;
                if (val != _settings.GlassOpacity) _settings.SetGlassOpacity(val);
            };
        }
        _glassDebounce.Stop();
        _glassDebounce.Start();
    }

    // ── 区域截图快捷键录制 ──

    /// <summary>点击后进入录制模式：下一组"修饰键 + 主键"会被记录为新快捷键。</summary>
    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyBox.Content = Loc.Get("Settings_Screenshot_Press");
        HotkeyBox.Focus();
    }

    /// <summary>录制中捕获按键：等到按下非修饰主键时合成 "Ctrl+Q" 形式并持久化（需至少一个修饰键）。</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc 取消录制，恢复原值
        if (key == Key.Escape)
        {
            _recordingHotkey = false;
            HotkeyBox.Content = _settings.ScreenshotHotkey;
            return;
        }
        // 仅按下修饰键时继续等待主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var keyName = KeyName(key);
        if (keyName == null) return; // 不支持的键，继续等

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            // 必须含至少一个修饰键（纯字母键会和打字冲突）
            HotkeyBox.Content = Loc.Get("Settings_Screenshot_NeedMod");
            return;
        }
        parts.Add(keyName);

        var combo = string.Join("+", parts);
        _settings.SetScreenshotHotkey(combo); // 持久化 → HotkeyService 自动重绑
        HotkeyBox.Content = combo;
        _recordingHotkey = false;
    }

    /// <summary>WPF Key → 快捷键字符串里的主键名（A-Z / 0-9 / F1-F24 / 少量常用键）。</summary>
    private static string? KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k >= Key.F1 && k <= Key.F24) return k.ToString();
        return k switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            _ => null
        };
    }

    /// <summary>语言下拉改变：持久化 + 立即切换界面语言（本窗口与灵动岛、托盘同步刷新）。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var value = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _settings.SetLanguage(value);
        Loc.Instance.Apply(value);
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 内置的 OpenFolderDialog（无须 WinForms 依赖）
        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("Settings_AddDir"),
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
            MessageBox.Show(this, Loc.Get("Settings_FillRequired"), "Open Island",
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
