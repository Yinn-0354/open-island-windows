using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Views;

/// <summary>
/// 像素风状态精灵 —— 绑 SessionPhase，按状态切 sprite sheet 并播帧。
///
/// Sprite sheet 规范（Aseprite 导出）：
///   · 放 src/OpenIsland.App/Assets/，水平条带、正方形帧
///   · 帧边长 = 图片高度，帧数 = 宽 / 高（控件自动算，任意正方尺寸都行）
///   · Aseprite：File → Export Sprite Sheet → Horizontal Strip，不要 padding/trim
///
/// 文件名约定：
///   · running.png / attention.png  —— 单张，Claude 工作/需关注，10fps 连续循环
///   · idle.png  / completed.png    —— 空闲/完成的"基础"动画
///   · idle2.png / idle3.png ...     —— idle 的额外变体（completed 同理 completed2.png…）
///     idle/completed 平时停第 1 帧；每 IdleCycleInterval 从所有变体里 *随机* 挑一个
///     完整播一遍，然后停回（小动作 + 省电）。
///
/// 渲染：NearestNeighbor + 整数倍 Scale + UseLayoutRounding —— 125%/150% DPI 不糊。
/// </summary>
public class PixelStatusSprite : Image
{
    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(nameof(Phase), typeof(SessionPhase), typeof(PixelStatusSprite),
            new PropertyMetadata(SessionPhase.Idle, OnPhaseChanged));

    /// <summary>当前会话聚合 phase（由 DynamicIslandViewModel.AggregatePhase 绑过来）。</summary>
    public SessionPhase Phase
    {
        get => (SessionPhase)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    /// <summary>显示放大倍数：原生帧 × Scale。必须整数，保持像素清晰。</summary>
    public int Scale { get; set; } = 2;

    /// <summary>多 agent（同时多个会话在跑）：为 true 且处于 Running 时，改播"两只章鱼讨论"(multiagent.png)。</summary>
    public static readonly DependencyProperty MultiAgentProperty =
        DependencyProperty.Register(nameof(MultiAgent), typeof(bool), typeof(PixelStatusSprite),
            new PropertyMetadata(false, OnMultiAgentChanged));

    // MultiAgent 变更：用当前 Phase 重新 Apply（不能用 OnPhaseChanged —— 那会把 bool 强转 SessionPhase 崩）
    private static void OnMultiAgentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (PixelStatusSprite)d;
        if (s._oneShot) return; // 同上：一次性动画播放中不打断
        s.Apply(s.Phase);
    }

    public bool MultiAgent
    {
        get => (bool)GetValue(MultiAgentProperty);
        set => SetValue(MultiAgentProperty, value);
    }

    /// <summary>Idle 默认动画：每隔这么久随机切换到另一个默认动画（站立眨眼/wink/睡觉吐泡泡/喝可乐）。</summary>
    public static readonly TimeSpan IdleCycleInterval = TimeSpan.FromMinutes(3);

    private readonly DispatcherTimer _timer;      // 10fps 帧推进
    private readonly DispatcherTimer _idleCycle;  // idle/completed 周期触发
    private readonly Random _rng = new();

    private CroppedBitmap[] _frames = Array.Empty<CroppedBitmap>(); // 当前正在播的动画
    private readonly List<CroppedBitmap[]> _variants = new();        // idle/completed 的所有变体
    private int _frame;
    private bool _loop;   // Running/Attention 连续循环
    private bool _burst;  // completed 完成庆祝"播一遍停末帧"进行中
    private bool _oneShot;// 点击小人 / 媒体控制 / 关闭 触发的一次性播放进行中
    private int _lastIdleIdx = -1; // 上一个 idle 默认动画下标，切换时尽量不连播同一个

    // Running 状态叠加的垂直上下跳动（保留旧占位图的弹跳手感，跟帧动画独立）
    private readonly TranslateTransform _bounce = new();

    public PixelStatusSprite()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Stretch = Stretch.Fill;
        RenderTransform = _bounce;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Advance();
        _idleCycle = new DispatcherTimer { Interval = IdleCycleInterval };
        _idleCycle.Tick += (_, _) => PlayRandomIdleLoop();
        Loaded += (_, _) => Apply(Phase);
        Unloaded += (_, _) => { _timer.Stop(); _idleCycle.Stop(); };
    }

    private static void OnPhaseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (PixelStatusSprite)d;
        if (s._oneShot) return; // 一次性动画(点击/媒体/关闭)播放中，不被状态刷新打断；播完自会回到最新状态
        s.Apply((SessionPhase)e.NewValue);
    }

    private void Apply(SessionPhase phase)
    {
        _timer.Stop();
        _idleCycle.Stop();
        _burst = false;
        _loop = false;
        _variants.Clear();
        StopBounce(); // 离开 Running 先把跳动复位（其它状态不跳）

        _oneShot = false;

        var name = phase switch
        {
            SessionPhase.Running => "running",
            SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer => "attention",
            SessionPhase.Completed => "completed",
            _ => "idle",
        };
        // 多 agent 专属动画已取消：Running 一律播正常动画。multiagent.png 资源暂保留备用。
        // （MultiAgent 依赖属性仍保留绑定，仅不再切换动画，避免改 XAML。）

        bool busy = phase is SessionPhase.Running
            or SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer;

        if (busy)
        {
            // Running / Attention：单张图，多帧则 10fps 连续循环
            var sheet = LoadSheet(name);
            if (sheet == null) { Source = null; return; }
            _frames = Slice(sheet);
            _frame = 0;
            Width = sheet.PixelHeight * Scale;
            Height = sheet.PixelHeight * Scale;
            Source = _frames[0];
            if (_frames.Length > 1) { _loop = true; _timer.Start(); }
            // Running 额外叠加上下跳动（保留旧占位图手感）；Attention 不跳
            if (phase == SessionPhase.Running) StartBounce();
            return;
        }

        // Completed：完成庆祝动画播一遍，停在最后一帧（不循环）。
        if (phase == SessionPhase.Completed)
        {
            var sheet = LoadSheet("completed");
            if (sheet == null) { Source = null; return; }
            _frames = Slice(sheet);
            _frame = 0;
            Width = sheet.PixelHeight * Scale;
            Height = sheet.PixelHeight * Scale;
            Source = _frames[0];
            if (_frames.Length > 1) { _burst = true; _timer.Start(); }
            return;
        }

        // Idle（默认/启动状态）：收集默认动画变体 idle.png / idle2.png / idle3.png / idle4.png ...
        // 随机挑一个 *连续循环* 播放，每 IdleCycleInterval(3 分钟) 再随机切换到另一个。
        for (int i = 0; ; i++)
        {
            var n = i == 0 ? "idle" : "idle" + (i + 1);
            var sheet = LoadSheet(n);
            if (sheet == null)
            {
                if (i == 0) { Source = null; return; }   // 连基础图都没有
                break;                                    // 变体序列到头
            }
            _variants.Add(Slice(sheet));
            if (i == 0)
            {
                Width = sheet.PixelHeight * Scale;
                Height = sheet.PixelHeight * Scale;
            }
            if (i >= 11) break; // 上限保护
        }

        PlayRandomIdleLoop();   // 立即随机挑一个循环播放（启动即默认动画）
        if (_variants.Count > 1) _idleCycle.Start(); // 多于一个才需要 3 分钟切换
    }

    /// <summary>随机挑一个默认动画连续循环播放（每 IdleCycleInterval 由 _idleCycle 触发切换）。</summary>
    private void PlayRandomIdleLoop()
    {
        if (_variants.Count == 0) return;
        int idx = _rng.Next(_variants.Count);
        if (_variants.Count > 1 && idx == _lastIdleIdx) idx = (idx + 1) % _variants.Count; // 尽量不连播同一个
        _lastIdleIdx = idx;
        _frames = _variants[idx];
        _frame = 0;
        _loop = true;            // 连续循环（动画自身含站立停顿/眨眼等）
        Source = _frames[0];
        if (_frames.Length > 1) _timer.Start();
    }

    /// <summary>
    /// 一次性播放某个动画表(name.png)一遍，播完自动回到当前 Phase 的常态动画。
    /// 用于：点击小章鱼(kamehameha)、媒体控制(headphones)、关闭按钮(byebye)。
    /// </summary>
    public void PlayOnce(string name)
    {
        var sheet = LoadSheet(name);
        if (sheet == null) return;
        _timer.Stop();
        _idleCycle.Stop();
        _burst = false;
        _loop = false;
        StopBounce();
        _oneShot = true;
        _frames = Slice(sheet);
        _frame = 0;
        Width = sheet.PixelHeight * Scale;
        Height = sheet.PixelHeight * Scale;
        Source = _frames[0];
        if (_frames.Length > 1) _timer.Start(); // 多帧才需要推进；单帧直接停在这一帧
    }

    private void Advance()
    {
        if (_frames.Length == 0) return;

        if (_oneShot)
        {
            _frame++;
            if (_frame >= _frames.Length)
            {
                _timer.Stop();
                _oneShot = false;
                Apply(Phase); // 一次性动画播完，回到当前状态的常态动画
                return;
            }
            Source = _frames[_frame];
            return;
        }

        if (_loop)
        {
            _frame = (_frame + 1) % _frames.Length;
            Source = _frames[_frame];
            return;
        }

        if (_burst)
        {
            _frame++;
            if (_frame >= _frames.Length)
            {
                // 一遍播完：停在 *最后一帧*（动画末尾即休息姿势），保持到下一个 30s 周期
                _timer.Stop();
                _burst = false;
                _frame = _frames.Length - 1;
            }
            Source = _frames[_frame];
        }
    }

    /// <summary>Running 时叠加平滑的上下往复跳动（与帧动画独立的 RenderTransform）。
    /// 幅度按显示尺寸取，约 1/8 高度，正弦缓动来回，永久循环。</summary>
    private void StartBounce()
    {
        double amp = Math.Max(2.0, Height / 8.0);
        var anim = new DoubleAnimation
        {
            From = 0,
            To = -amp,
            Duration = TimeSpan.FromMilliseconds(380),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _bounce.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void StopBounce()
    {
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _bounce.Y = 0;
    }

    private static CroppedBitmap[] Slice(BitmapSource sheet)
    {
        int fh = sheet.PixelHeight;
        int fw = fh; // 正方形帧
        int count = Math.Max(1, sheet.PixelWidth / fw);
        var frames = new CroppedBitmap[count];
        for (int i = 0; i < count; i++)
            frames[i] = new CroppedBitmap(sheet, new Int32Rect(i * fw, 0, fw, fh));
        return frames;
    }

    private static BitmapSource? LoadSheet(string name)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // 程序集限定 pack URI —— 不依赖入口程序集，生产 / 测试都能解析到
            // OpenIsland.dll 内嵌的 Assets 资源。资源不存在会抛 → catch 返回 null。
            bmp.UriSource = new Uri($"pack://application:,,,/OpenIsland;component/Assets/{name}.png", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.None;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
