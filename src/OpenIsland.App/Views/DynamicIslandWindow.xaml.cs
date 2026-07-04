using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenIsland.App.Services;
using OpenIsland.App.ViewModels;
using AvalonTextEditor = ICSharpCode.AvalonEdit.TextEditor;

namespace OpenIsland.App.Views;

public partial class DynamicIslandWindow : Window
{
    private readonly DynamicIslandViewModel _viewModel;
    private readonly WorkspaceSettings _settings;

    // ── 自定义拖动 ──
    // 不用 WPF 的 DragMove：它进 OS 模态移动循环、阻塞 UI 线程，拖动期间玻璃根本刷不动（就是"几fps"的根因）。
    // 改成自己在 MouseMove 里改 Left/Top，UI 线程不阻塞，每次移动即时重渲玻璃 → 丝滑。
    private bool _dragArmed;          // 头部已按下、待判定
    private bool _dragMoved;          // 已越过阈值、确实在拖（区分"点一下"与"拖"）
    private Point _dragMouseStart;    // 按下时鼠标屏幕物理坐标
    private Point _dragWinStart;      // 按下时窗口 Left/Top（DIP）
    private double _dpi = 1.0;        // 物理像素 / DIP

    // ── 液态玻璃：离屏 WebView2 渲染 ──
    // 静止时 200ms 刷一帧（兜住背后内容变化）；拖动时在 MouseMove 里每次移动即时重渲 → 丝滑。
    // WebView2 往返 ~30-90ms（见 WebGlassRenderer），不能再同步渲染，全部走 fire-and-forget async +
    // _glassRendering 重入锁：渲染没跟上时新的 MouseMove 直接跳过，不排队、不阻塞拖动手感。
    private readonly WebGlassRenderer _glass;
    private DispatcherTimer? _glassTimer;
    private bool _isGlassTimerHooked;
    private bool _glassRendering;
    [DllImport("user32.dll")] private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // 排除出屏幕抓取：抓玻璃背景时抓不到灵动岛自己
    // 远程桌面/RDP 等环境下 WDA_EXCLUDEFROMCAPTURE 会静默失败（SetWindowDisplayAffinity 返回值
    // 以前从没人检查过）——一旦失效，ScreenGrab 会截到灵动岛自己上一帧的玻璃背景，一路自我反馈
    // 越叠越糊、颜色跑偏（实测会稳定跑到饱和橙红色）。这里在 StartGlass 时用 GetWindowDisplayAffinity
    // 读回实际生效的值来验证，没生效就改用 ScreenGrab.CaptureBytesHideWindow（截屏瞬间隐藏自己）兜底。
    private bool _captureExclusionWorks;
    // 曾经试过整段持续设置排除标志（旧写法）或者干脆不设、永远走"抓帧瞬间隐藏窗口"兜底——
    // 前者让录屏/截图软件整段时间都看不到岛，后者 ShowWindow(HIDE) 是真隐藏，人眼也看得见闪。
    // 现在的写法（见 ProbeCaptureExclusion + CaptureWithTransientExclusion）只在每次抓帧的
    // 瞬间才设排除标志、抓完立刻清掉：不影响真实合成到物理屏幕的画面，人眼无感；录屏/截图
    // 软件绝大部分时间都能正常看到岛，只有毫秒级的抓取瞬间跟其它捕获者一样短暂看不到自己。

    // 普通模式岛宽 vs 权限提示时的拓宽宽度。等"丝滑"动画 0.5s ease-out 在两者间过渡。
    // 加宽是为了让 "2. Yes, don't ask again for vibeisland.app this session" 这种长 label
    // 整条容得下，不被截断。
    // 岛体加宽 1.2 倍（长 1/5）：320→384 / 640→768 / 480→576，彼此比例关系保持不变。
    private const double NormalWidth = 384;
    private const double PermissionWidth = 768;

    // Notch 形态参数：仿 MacBook 刘海的横条；snap 阈值 = 拖到距屏顶 28px 内放手就吸附。
    private const double NotchWidth = 576;
    private const double NotchSnapThreshold = 28;
    private const double NotchUnsnapThreshold = 48;

    public DynamicIslandWindow(DynamicIslandViewModel viewModel, WorkspaceSettings settings, WebGlassRenderer glass)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _glass = glass;
        DataContext = viewModel;

        Width = NormalWidth;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // VM 请求播放一次小章鱼动画（媒体控制 → headphones 等）→ 转给精灵控件
        _viewModel.PlaySprite += name => Dispatcher.BeginInvoke(() => StatusSprite.PlayOnce(name));
        Loaded += (_, _) =>
        {
            PositionAtTopCenter();
            _dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_viewModel.LiquidGlassEnabled) StartGlass();
            UpdateTitleMarquee();
        };

        HookPlanMarkdownCodeBlockFix();
    }

    // ════════════════════════════ 会话卡片内联改名 ════════════════════════════

    /// <summary>改名输入框刚显示出来时：自动获取键盘焦点 + 全选，用户直接输入就替换。</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.IsVisible)
        {
            // 布局刚切显示，Focus 立刻调偶尔拿不到焦点 —— 延后到渲染后再要焦点。
            Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>改名输入框按键：Enter 保存、Esc 取消。</summary>
    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        var item = tb.DataContext as ViewModels.IslandSessionItem;
        if (e.Key == Key.Enter)
        {
            item?.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item?.CancelRename();
            e.Handled = true;
        }
    }

    /// <summary>改名输入框失去焦点：等同 Enter 保存（点别处也算改完）。CommitRename 内部对
    /// 非编辑态是 no-op，所以 Esc 已退出后再触发失焦不会重复处理。</summary>
    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            (tb.DataContext as ViewModels.IslandSessionItem)?.CommitRename();
    }

    // ════════════════════════════ 头部标题跑马灯 ════════════════════════════

    /// <summary>
    /// HeaderTitleText 变化时（默认 "Open Island" ↔ SMTC 曲名）重新判断是否需要跑马灯：
    /// 用 FormattedText 直接量文字实际宽度（不依赖 TextBlock.ActualWidth 走完一次布局才有效，
    /// 避免刚设置完文字那一刻 ActualWidth 还是 0 的时序坑）。超出容器宽度 → 起一个来回 AutoReverse
    /// 循环的 DoubleAnimation 把 TranslateTransform.X 从 0 移到 -(超出量)；没超出 → 清动画归零。
    /// </summary>
    private void UpdateTitleMarquee()
    {
        var text = _viewModel.HeaderTitleText ?? "";
        if (string.IsNullOrEmpty(text))
        {
            TitleTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TitleTranslate.X = 0;
            return;
        }

        var typeface = new Typeface(TitleText.FontFamily, TitleText.FontStyle, TitleText.FontWeight, TitleText.FontStretch);
        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            TitleText.FontSize,
            System.Windows.Media.Brushes.Black,
            dpiScale);

        double textWidth = formatted.WidthIncludingTrailingWhitespace;
        double containerWidth = TitleClip.Width;

        if (textWidth > containerWidth)
        {
            double distance = textWidth - containerWidth + 14; // 多留一点尾部间距
            var anim = new DoubleAnimation
            {
                From = 0,
                To = -distance,
                Duration = TimeSpan.FromSeconds(Math.Max(2.5, distance / 28.0)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            TitleTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            TitleTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TitleTranslate.X = 0;
        }
    }

    // ════════════════════════════ 液态玻璃 ════════════════════════════

    /// <summary>开启玻璃：探测一次"抓取排除"能不能用（测完立刻清掉，不持续设置）+ 启动 200ms
    /// 兜底刷新 + 立即出一帧。</summary>
    private void StartGlass()
    {
        ProbeCaptureExclusion();
        ApplyGlassResources(true);
        _glassTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        if (!_isGlassTimerHooked) { _glassTimer.Tick += (_, _) => _ = RenderGlassNowAsync(); _isGlassTimerHooked = true; }
        _glassTimer.Start();
        _ = RenderGlassNowAsync();
    }

    /// <summary>关闭玻璃：停循环 + 内部面板回纯黑 + 清掉玻璃帧（背景经转换器回退纯黑）。
    /// 不用再"解除排除"——排除标志现在只在每次抓帧的瞬间临时设、抓完立刻清，本来就没有
    /// 持续设置着，没什么可解除的。</summary>
    private void StopGlass()
    {
        _glassTimer?.Stop();
        ApplyGlassResources(false);
        _viewModel.GlassFrame = null;
    }

    /// <summary>切换展开后内部面板/卡片的填充：玻璃开 → 半透明（透出毛玻璃），关 → 原黑色。
    /// 改 DynamicResource 一处生效全部（含 DataTemplate 里的会话卡片）。</summary>
    private void ApplyGlassResources(bool glass)
    {
        SolidColorBrush B(byte a, byte r, byte g, byte b) =>
            new(System.Windows.Media.Color.FromArgb(a, r, g, b));
        if (glass)
        {
            Resources["CardFill"] = B(0x1A, 0xFF, 0xFF, 0xFF);  // 半透明白 ~0.10
            Resources["PanelFill"] = B(0x14, 0xFF, 0xFF, 0xFF); // ~0.08
            Resources["CodeFill"] = B(0x38, 0x00, 0x00, 0x00);  // 半透明黑 ~0.22（代码块保持可读）
            Resources["ChipFill"] = B(0x16, 0xFF, 0xFF, 0xFF);  // 按钮 chip 毛玻璃 ~0.085
            Resources["ChipHover"] = B(0x30, 0xFF, 0xFF, 0xFF); // 悬停更亮
        }
        else
        {
            Resources["CardFill"] = B(0xFF, 0x11, 0x11, 0x11);
            Resources["PanelFill"] = B(0xFF, 0x1C, 0x1C, 0x1E);
            Resources["CodeFill"] = B(0xFF, 0x0F, 0x0F, 0x11);
            Resources["ChipFill"] = B(0xFF, 0x25, 0x23, 0x20);
            Resources["ChipHover"] = B(0xFF, 0x2F, 0x2C, 0x28);
        }
    }

    /// <summary>探测 WDA_EXCLUDEFROMCAPTURE 在这台机器上是否真的生效（远程桌面等环境该 API
    /// 会静默失败）。只在这里测一次、测完立刻清掉标志——不持续设置。真正渲染时改成"抓帧那一
    /// 瞬间才设、抓完立刻清"（见 CaptureWithTransientExclusion），因为 SetWindowDisplayAffinity
    /// 只影响"捕获 API 看到什么"，完全不影响真实合成到物理屏幕的画面——跟 ShowWindow(HIDE)
    /// 那种会让窗口真消失、人眼看得见闪烁的方式不一样。持续设置的旧写法会让录屏/截图软件在
    /// 整段液态玻璃开启期间都看不到岛；改成"抓的瞬间才设"之后，录屏绝大部分时间都能正常看到
    /// 岛，只有毫秒级的抓取瞬间跟其它捕获者一样短暂看不到自己，而且这个瞬间对人眼和录屏画面
    /// 都是无感的（不是"消失"，只是"被排除在这一次捕获结果之外"）。</summary>
    private void ProbeCaptureExclusion()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) { _captureExclusionWorks = false; return; }
            SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
            _captureExclusionWorks = GetWindowDisplayAffinity(h, out var readBack) && readBack == WDA_EXCLUDEFROMCAPTURE;
            SetWindowDisplayAffinity(h, WDA_NONE); // 探测完立刻清掉，不留着
        }
        catch { _captureExclusionWorks = false; }
    }

    /// <summary>只在这次 BitBlt 的一瞬间把自己设为"排除出捕获"，抓完立刻清掉。</summary>
    private byte[]? CaptureWithTransientExclusion(int x, int y, int w, int h)
    {
        var h2 = new WindowInteropHelper(this).Handle;
        if (h2 == IntPtr.Zero) return ScreenGrab.CaptureBytes(x, y, w, h);
        SetWindowDisplayAffinity(h2, WDA_EXCLUDEFROMCAPTURE);
        try { return ScreenGrab.CaptureBytes(x, y, w, h); }
        finally { SetWindowDisplayAffinity(h2, WDA_NONE); }
    }

    /// <summary>渲染一帧：抓灵动岛正下方真实桌面（自身瞬时排除）→ 离屏 WebView2 折射 → 写回 GlassFrame。
    /// 异步、~30-90ms 往返；_glassRendering 防重入，渲染没跟上时新调用直接跳过。</summary>
    private async Task RenderGlassNowAsync()
    {
        if (_glassRendering || !_viewModel.LiquidGlassEnabled || !IsVisible) return;
        if (MainBorder.ActualWidth < 8 || MainBorder.ActualHeight < 8) return;
        _glassRendering = true;
        try
        {
            // MainBorder 在屏幕上的物理像素矩形（= 胶囊背后那块桌面）
            var tl = MainBorder.PointToScreen(new Point(0, 0));
            var br = MainBorder.PointToScreen(new Point(MainBorder.ActualWidth, MainBorder.ActualHeight));
            int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
            int w = (int)Math.Round(br.X - tl.X), h = (int)Math.Round(br.Y - tl.Y);
            if (w < 8 || h < 8) return;
            // 排除生效的路径要在这个方法所在的 UI 线程上切换排除标志（跟隐藏兜底路径一样，
            // 涉及 hwnd 的操作不丢线程池），BitBlt 本身够快，这点同步开销可忽略。
            var bg = _captureExclusionWorks
                ? CaptureWithTransientExclusion(x, y, w, h)
                : ScreenGrab.CaptureBytesHideWindow(new WindowInteropHelper(this).Handle, x, y, w, h);
            if (bg == null) return;

            // 四角分别乘 DPI 转成物理像素——不能只看 TopLeft 再"没有就退回 18"，Notch 模式下
            // 顶两角本来就该是 0（顶边贴屏幕，不需要圆角），之前这条兜底会把合法的 0 误判成
            // "没初始化"强行退回 18，导致玻璃内容自己在顶角多裁一圈圆角、露出黑色背景。
            var cr = MainBorder.CornerRadius;
            var rDev = new CornerRadius(cr.TopLeft * _dpi, cr.TopRight * _dpi, cr.BottomRight * _dpi, cr.BottomLeft * _dpi);
            var frame = await _glass.RenderAsync(bg, w, h, rDev,
                _settings.GlassBlurPx, _settings.GlassSaturationPercent, _settings.GlassRefractionPercent);
            if (frame != null) _viewModel.GlassFrame = frame;
        }
        catch { /* 单帧失败无所谓，下一拍再来 */ }
        finally { _glassRendering = false; }
    }

    /// <summary>圆形关闭按钮：小章鱼挥手拜拜，约 3 秒后隐藏灵动岛（托盘菜单可再显示）。</summary>
    private void CloseIsland_Click(object sender, RoutedEventArgs e)
    {
        StatusSprite.PlayOnce("byebye"); // 30 帧 ≈ 3s 的挥手告别
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3100) };
        t.Tick += (_, _) => { t.Stop(); Hide(); };
        t.Start();
    }

    private void PositionAtTopCenter()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Left + (screen.Width - ActualWidth) / 2;
        Top = screen.Top + 10;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = true;
        _dragMoved = false;
        _dragMouseStart = PointToScreen(e.GetPosition(this)); // 屏幕物理坐标
        _dragWinStart = new Point(Left, Top);
        (sender as UIElement)?.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        // PointToScreen(相对窗口点) 始终给出鼠标的绝对屏幕坐标（即便窗口已移动）→ 总位移稳定。
        var cur = PointToScreen(e.GetPosition(this));
        double dx = cur.X - _dragMouseStart.X, dy = cur.Y - _dragMouseStart.Y;
        if (!_dragMoved && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)) _dragMoved = true;
        if (_dragMoved)
        {
            Left = _dragWinStart.X + dx / _dpi; // 物理 delta → DIP
            Top = _dragWinStart.Y + dy / _dpi;
            if (_viewModel.LiquidGlassEnabled) _ = RenderGlassNowAsync(); // 每次移动即时重渲 → 背景丝滑跟随
        }
    }

    /// <summary>
    /// 拖拽结束后检查是否要吸附到 Notch / 离开 Notch。
    /// - 当前不在 Notch 模式 + 距屏顶 < 28px 放手 → 吸附为 Notch
    /// - 当前在 Notch 模式 + 距屏顶 > 48px 放手 → 翻回默认岛
    /// - 当前在 Notch 模式 + 仍贴顶 → 复位到 Top=0 不让它斜挂
    /// </summary>
    private void CheckNotchSnap()
    {
        var screenTop = SystemParameters.WorkArea.Top;
        var distFromTop = Top - screenTop;

        if (!_viewModel.IsNotchMode && distFromTop < NotchSnapThreshold)
        {
            EnterNotchMode();
        }
        else if (_viewModel.IsNotchMode && distFromTop > NotchUnsnapThreshold)
        {
            ExitNotchMode();
        }
        else if (_viewModel.IsNotchMode)
        {
            Top = screenTop; // 紧贴顶
        }
    }

    private void EnterNotchMode()
    {
        _viewModel.IsNotchMode = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(450);

        // Notch 形态 = 默认岛粘到屏顶居中，不改 Width（让原 384/权限 768 都能保留所有
        // 内部功能：展开箭头、会话列表、状态灯、权限三键）。只动 Top → 0、Left → 居中。
        var screenCenter = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2;
        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;

        AnimateWindowProp(LeftProperty, screenCenter - currentWidth / 2, dur, ease);
        AnimateWindowProp(TopProperty, SystemParameters.WorkArea.Top, dur, ease);
    }

    private void ExitNotchMode()
    {
        _viewModel.IsNotchMode = false;
        // 不改 Width / Top / Left —— 用户拖出顶部后位置由 DragMove 已经定位，
        // 这里只翻 IsNotchMode 让外形（Margin/CornerRadius）通过 DataTrigger 自然过渡。
    }

    private void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        _dragArmed = false;
        (sender as UIElement)?.ReleaseMouseCapture();

        if (_dragMoved)
        {
            CheckNotchSnap();
            if (_viewModel.LiquidGlassEnabled) _ = RenderGlassNowAsync(); // 落点后按新位置重渲
            return;
        }

        // 短点（非拖拽）= 用户意图"点 Open Island"：一键清空下面所有栏目，谁活动谁再回来，
        // 然后照旧 toggle 展开/收起（不破坏看列表的能力）。Notch 与默认形态行为一致。
        // 例外：点在最左的小章鱼上 → 放一次"龟派气功"彩蛋，不展开/收起。
        var pOnSprite = e.GetPosition(StatusSprite);
        bool onSprite = pOnSprite.X >= 0 && pOnSprite.Y >= 0
                        && pOnSprite.X < StatusSprite.ActualWidth
                        && pOnSprite.Y < StatusSprite.ActualHeight;
        if (onSprite)
            StatusSprite.PlayOnce("kamehameha");
        else
            _viewModel.OnHeaderTapped();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DynamicIslandViewModel.IsExpanded))
            Dispatcher.BeginInvoke(() => AnimateExpand(_viewModel.IsExpanded));
        else if (e.PropertyName == nameof(DynamicIslandViewModel.IsPermissionMode))
            Dispatcher.BeginInvoke(() => AnimateWidth(_viewModel.IsPermissionMode));
        else if (e.PropertyName == nameof(DynamicIslandViewModel.LiquidGlassEnabled))
            Dispatcher.BeginInvoke(() => { if (_viewModel.LiquidGlassEnabled) StartGlass(); else StopGlass(); });
        else if (e.PropertyName == nameof(DynamicIslandViewModel.HeaderTitleText))
            Dispatcher.BeginInvoke(UpdateTitleMarquee, DispatcherPriority.Background);
    }

    /// <summary>
    /// 动画一个 Window 属性 + 动画结束后清掉动画 / 写本地值。
    /// 必须如此：默认 FillBehavior.HoldEnd 会把属性永久锁在 To 值，DragMove 无法实际移动
    /// （Win32 移了 OS 窗口但 WPF 属性读出来还是动画值）→ 后续 CheckNotchSnap 读 Top 永远
    /// 是 0，永远 unsnap 不出来；Width/Left 同理影响其他交互。
    /// </summary>
    private void AnimateWindowProp(DependencyProperty prop, double target, TimeSpan dur, IEasingFunction ease)
    {
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = dur,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(prop, null); // 清动画
            SetValue(prop, target);     // 写到本地值，后续读写正常
        };
        BeginAnimation(prop, anim);
    }

    /// <summary>
    /// 权限模式 ↔ 普通模式：岛宽从 384 ↔ 768 拉伸/收缩，并同步移动 Left 让水平中心保持
    /// 不动，避免视觉上"向右滑出"。0.5s CubicEase EaseOut，配合 Visibility 切换内部
    /// 视图——视图切换是瞬时的，但宽度动画让整体过渡显得平滑。
    /// </summary>
    private void AnimateWidth(bool toPermission)
    {
        double targetWidth = toPermission ? PermissionWidth : NormalWidth;
        double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        double widthDelta = targetWidth - currentWidth;
        double targetLeft = Left - widthDelta / 2;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(500);

        AnimateWindowProp(WidthProperty, targetWidth, dur, ease);
        AnimateWindowProp(LeftProperty, targetLeft, dur, ease);

        // 进入权限模式时强制展开内容区，否则拓宽空岛没意义。
        if (toPermission && !_viewModel.IsExpanded)
        {
            _viewModel.IsExpanded = true; // 这会经 PropertyChanged 调一次 AnimateExpand
        }

        // 同帧再触发一次 AnimateExpand，让高度动画跟宽度动画并肩起步、500ms 同步收尾 ——
        // 之前这里走 Dispatcher.BeginInvoke(Background)，比 AnimateWidth 慢半拍，结果
        // 退出权限时高度先收完再轮宽度，看起来卡顿。
        if (_viewModel.IsExpanded)
        {
            AnimateExpand(true);
        }
    }

    private void AnimateExpand(bool expand)
    {
        double targetHeight;
        if (expand)
        {
            if (_viewModel.IsPermissionMode)
            {
                // 权限面板：直接给兜底大值，不 measure（避免 layout pass 没追上时拿到旧内容尺寸）
                targetHeight = 1200;
            }
            else
            {
                // 普通模式：用 *目标* 宽度 NormalWidth 而非 ActualWidth measure ——
                // 退出权限模式时 Width 还在 768→384 动画中段，ActualWidth 拿到的是错的，
                // 测出来的高度也错（窄列内容会变高）。按 384 measure 才是收回后的真实尺寸。
                ExpandedContent.Measure(new Size(NormalWidth, double.PositiveInfinity));
                targetHeight = ExpandedContent.DesiredSize.Height;
                if (targetHeight < 1) targetHeight = 600;
            }
        }
        else
        {
            targetHeight = 0;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 高度动画时长跟 AnimateWidth 的 500ms 对齐 ——
        // 之前 220ms 高度先收完，剩下"窄高已收 + 宽度还在收"那一截让消失看起来卡顿。
        // 同步后高度跟宽度并肩走，视觉上像一团方块同步收缩。
        ExpandedContent.BeginAnimation(MaxHeightProperty, new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = ease
        });

        // Rotate chevron: 90° = right (collapsed), 270° = down (expanded)
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation
            {
                To = expand ? 270 : 90,
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = ease
            });
    }

    // ════════════════════════════ ExitPlanMode 计划审阅：Markdown 代码块深色适配 ════════════════════════════
    //
    // 背景（详见 DynamicIslandWindow.xaml 里 PlanMd* 系列 Style 上方的大段注释）：
    // MdXaml 把 Markdown 里的 ```代码块``` 转成内嵌的 AvalonEdit TextEditor 控件渲染，默认白底黑字。
    // 这个控件是运行时动态创建、挂在 FlowDocument 的 BlockUIContainer 里的 —— 实测证明 WPF 对这种
    // 宿主场景的隐式样式（ResourceDictionary 里声明的、不带 x:Key 的 implicit Style）解析不可靠：
    // 无论把 Style 放在 Window.Resources / Application.Resources 还是 MarkdownScrollViewer.Resources，
    // TextEditor 实例都不会自动套用（哪怕手动 TryFindResource 能查到，自动解析这条路径就是不生效），
    // 只有 DependencyProperty.OverrideMetadata 也测过同样不行（AvalonEdit 自带主题 Style 的 Setter
    // 优先级比默认值高）。唯一稳定生效的办法：每次 Markdown 渲染出新 FlowDocument 时，直接对里面
    // 找到的 TextEditor 实例设置本地属性值（本地值 WPF 属性优先级最高，稳赢任何 Style）。
    //
    // 这里用 DependencyPropertyDescriptor 监听 PlanMarkdownViewer（继承自 FlowDocumentScrollViewer）
    // 的 Document 属性变化——PlanMarkdown 绑定每次更新（切换到不同计划 / 重新渲染）都会替换整个
    // Document 对象，从而触发一次新的着色遍历。纯外观 best-effort，任何异常都不应影响卡片其余渲染。
    private static readonly SolidColorBrush PlanCodeBlockBackground = new(Color.FromRgb(0x0A, 0x0A, 0x0B));
    private static readonly SolidColorBrush PlanCodeBlockForeground = new(Color.FromRgb(0xE5, 0xE5, 0xEA));
    private static readonly FontFamily PlanCodeBlockFontFamily = new("Consolas, Cascadia Code, Menlo");

    private void HookPlanMarkdownCodeBlockFix()
    {
        var viewer = PlanMarkdownViewer;
        var descriptor = DependencyPropertyDescriptor.FromProperty(
            FlowDocumentScrollViewer.DocumentProperty, typeof(FlowDocumentScrollViewer));
        descriptor?.AddValueChanged(viewer, (_, _) => FixPlanCodeBlockColors(viewer));
        // 首次挂载时 Document 可能已经存在（PlanMarkdown 早于 Loaded 就绑定上了），补一次。
        FixPlanCodeBlockColors(viewer);
    }

    private static void FixPlanCodeBlockColors(MdXaml.MarkdownScrollViewer viewer)
    {
        try
        {
            var doc = viewer.Document;
            if (doc == null) return;
            foreach (var block in doc.Blocks) FixPlanCodeBlock(block);
        }
        catch
        {
            // 纯外观 best-effort：着色失败也不能影响计划卡片本身的渲染/交互。
        }
    }

    /// <summary>递归遍历 FlowDocument 的 Block 树（含 Section/List 内嵌套的代码块），
    /// 找到 BlockUIContainer 包着的 AvalonEdit TextEditor 就直接改本地属性值。</summary>
    private static void FixPlanCodeBlock(Block block)
    {
        switch (block)
        {
            case BlockUIContainer { Child: AvalonTextEditor editor }:
                editor.Background = PlanCodeBlockBackground;
                editor.Foreground = PlanCodeBlockForeground;
                editor.BorderThickness = new Thickness(0);
                editor.FontFamily = PlanCodeBlockFontFamily;
                editor.Padding = new Thickness(8);
                break;
            case Section section:
                foreach (var b in section.Blocks) FixPlanCodeBlock(b);
                break;
            case System.Windows.Documents.List list:
                foreach (var item in list.ListItems)
                    foreach (var b in item.Blocks) FixPlanCodeBlock(b);
                break;
            case Table table:
                foreach (var rowGroup in table.RowGroups)
                    foreach (var row in rowGroup.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var b in cell.Blocks) FixPlanCodeBlock(b);
                break;
        }
    }
}
