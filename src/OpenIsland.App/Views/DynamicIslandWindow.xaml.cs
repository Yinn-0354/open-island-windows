using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using OpenIsland.App.ViewModels;

namespace OpenIsland.App.Views;

public partial class DynamicIslandWindow : Window
{
    private readonly DynamicIslandViewModel _viewModel;
    private bool _isDragging;
    private Point _dragStartPoint;

    // 普通模式岛宽 vs 权限提示时的拓宽宽度。等"丝滑"动画 0.5s ease-out 在两者间过渡。
    // 加宽是为了让 "2. Yes, don't ask again for vibeisland.app this session" 这种长 label
    // 整条容得下，不被截断。
    private const double NormalWidth = 320;
    private const double PermissionWidth = 640;

    // Notch 形态参数：仿 MacBook 刘海的横条；snap 阈值 = 拖到距屏顶 28px 内放手就吸附。
    private const double NotchWidth = 480;
    private const double NotchSnapThreshold = 28;
    private const double NotchUnsnapThreshold = 48;

    public DynamicIslandWindow(DynamicIslandViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Width = NormalWidth;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // VM 请求播放一次小章鱼动画（媒体控制 → headphones 等）→ 转给精灵控件
        _viewModel.PlaySprite += name => Dispatcher.BeginInvoke(() => StatusSprite.PlayOnce(name));
        Loaded += (_, _) => PositionAtTopCenter();
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
        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        (sender as UIElement)?.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var currentPos = e.GetPosition(this);
            var delta = currentPos - _dragStartPoint;
            if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
            {
                _isDragging = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                DragMove(); // 阻塞直到鼠标松开
                CheckNotchSnap();
            }
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
        if (_isDragging)
        {
            // 短点（非拖拽）= 用户意图"点 Open Island"：一键清空下面所有栏目，谁活动谁再回来，
            // 然后照旧 toggle 展开/收起（不破坏看列表的能力）。Notch 与默认形态行为一致。
            // 清空逻辑见 DynamicIslandViewModel.ClearAllSessions（复用单卡叉号的 dismiss
            // 状态机，未答的权限 prompt 不会被清掉）。
            // A genuine tap (not a drag) on the "Open Island" header clears the session
            // list (active ones reappear on their own) and then toggles expand/collapse as
            // before — see DynamicIslandViewModel.OnHeaderTapped / ClearAllSessions.
            // 例外：点在最左的小章鱼上 → 放一次"龟派气功"彩蛋，不展开/收起。
            // （Grid 在 MouseDown 抓了鼠标，MouseUp 总落到 Grid，小章鱼自己的事件收不到，
            //   所以在这里按命中位置分流。）
            var pOnSprite = e.GetPosition(StatusSprite);
            bool onSprite = pOnSprite.X >= 0 && pOnSprite.Y >= 0
                            && pOnSprite.X < StatusSprite.ActualWidth
                            && pOnSprite.Y < StatusSprite.ActualHeight;
            if (onSprite)
                StatusSprite.PlayOnce("kamehameha");
            else
                _viewModel.OnHeaderTapped();
        }
        _isDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DynamicIslandViewModel.IsExpanded))
            Dispatcher.BeginInvoke(() => AnimateExpand(_viewModel.IsExpanded));
        else if (e.PropertyName == nameof(DynamicIslandViewModel.IsPermissionMode))
            Dispatcher.BeginInvoke(() => AnimateWidth(_viewModel.IsPermissionMode));
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
