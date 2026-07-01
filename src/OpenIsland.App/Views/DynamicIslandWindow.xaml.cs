using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenIsland.App.Services;
using OpenIsland.App.ViewModels;

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

    // ── 液态玻璃 CPU 渲染 ──
    // 静止时 200ms 刷一帧（兜住背后内容变化）；拖动时在 MouseMove 里每次移动即时重渲 → 丝滑。
    // CPU 折射单帧 ~1ms，同步渲染不阻塞观感（见 GlassRenderer）。
    private readonly GlassRenderer _glass = new();
    private DispatcherTimer? _glassTimer;
    private bool _isGlassTimerHooked;
    private bool _glassRendering;
    [DllImport("user32.dll")] private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // 排除出屏幕抓取：抓玻璃背景时抓不到灵动岛自己

    // 普通模式岛宽 vs 权限提示时的拓宽宽度。等"丝滑"动画 0.5s ease-out 在两者间过渡。
    // 加宽是为了让 "2. Yes, don't ask again for vibeisland.app this session" 这种长 label
    // 整条容得下，不被截断。
    private const double NormalWidth = 320;
    private const double PermissionWidth = 640;

    // Notch 形态参数：仿 MacBook 刘海的横条；snap 阈值 = 拖到距屏顶 28px 内放手就吸附。
    private const double NotchWidth = 480;
    private const double NotchSnapThreshold = 28;
    private const double NotchUnsnapThreshold = 48;

    public DynamicIslandWindow(DynamicIslandViewModel viewModel, WorkspaceSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
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
        };
    }

    // ════════════════════════════ 液态玻璃 ════════════════════════════

    /// <summary>开启玻璃：本窗口设为"抓取排除"（抓背景抓不到自己）+ 启动 200ms 兜底刷新 + 立即出一帧。</summary>
    private void StartGlass()
    {
        SetCaptureExclusion(true);
        ApplyGlassResources(true);
        _glassTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        if (!_isGlassTimerHooked) { _glassTimer.Tick += (_, _) => RenderGlassNow(); _isGlassTimerHooked = true; }
        _glassTimer.Start();
        RenderGlassNow();
    }

    /// <summary>关闭玻璃：停循环 + 解除抓取排除 + 内部面板回纯黑 + 清掉玻璃帧（背景经转换器回退纯黑）。</summary>
    private void StopGlass()
    {
        _glassTimer?.Stop();
        SetCaptureExclusion(false);
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

    private void SetCaptureExclusion(bool exclude)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }
        catch { }
    }

    /// <summary>渲染一帧：抓灵动岛正下方真实桌面（自身已排除）→ CPU 折射 → 写回 GlassFrame。同步、~1ms。</summary>
    private void RenderGlassNow()
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
            var bg = ScreenGrab.CaptureBytes(x, y, w, h);
            if (bg == null) return;

            double rDev = (MainBorder.CornerRadius.TopLeft > 0 ? MainBorder.CornerRadius.TopLeft : 18) * _dpi;
            _viewModel.GlassFrame = _glass.Render(bg, w, h, rDev);
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
            if (_viewModel.LiquidGlassEnabled) RenderGlassNow(); // 每次移动即时重渲 → 背景丝滑跟随
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

        // Notch 形态 = 默认岛粘到屏顶居中，不改 Width（让原 320/权限 640 都能保留所有
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
            if (_viewModel.LiquidGlassEnabled) RenderGlassNow(); // 落点后按新位置重渲
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
    /// 权限模式 ↔ 普通模式：岛宽从 320 ↔ 640 拉伸/收缩，并同步移动 Left 让水平中心保持
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
                // 退出权限模式时 Width 还在 640→320 动画中段，ActualWidth 拿到的是错的，
                // 测出来的高度也错（窄列内容会变高）。按 320 measure 才是收回后的真实尺寸。
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
}
