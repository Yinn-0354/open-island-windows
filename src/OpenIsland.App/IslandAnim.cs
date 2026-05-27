using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OpenIsland.App;

/// <summary>
/// Reusable XAML-facing motion helpers exposed as attached properties so any
/// <see cref="FrameworkElement"/> can opt into the app's motion language from markup
/// (<c>local:Anim.PressScale="True"</c> etc.).
///
/// 动效设计规则（与设计系统一致）：
///   · enter = ease-out，exit = ease-in，绝不用 linear；
///   · 微交互 150–300ms（按压 80ms）；弹性回弹用 BackEase；
///   · 只动 Opacity / RenderTransform（绝不碰 Width/Height/Margin，省 GPU、避免触发布局）；
///   · 所有缓动函数 / 画刷尽量 Freeze()，跨线程共享、少分配。
///
/// 每元素的运行时状态（ScaleTransform、Storyboard、"首次设值"标记）都存在私有 attached DP 上，
/// 这样不需要额外的 ConditionalWeakTable，状态随元素一起被 GC。
/// </summary>
public static class Anim
{
    // ── Shared, frozen easing functions（共享缓动，全程只读、已 Freeze）────────────

    /// <summary>Enter / settle easing（ease-out）。</summary>
    private static readonly IEasingFunction EaseOutCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Exit easing（ease-in）。</summary>
    private static readonly IEasingFunction EaseInCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

    /// <summary>Press release bounce（轻微回弹，Amplitude 0.35）。</summary>
    private static readonly IEasingFunction BackOut = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    // 时长常量（集中管理，符合 80 / 150–300ms 规则）
    private static readonly Duration PressDownDur = new(TimeSpan.FromMilliseconds(80));
    private static readonly Duration PressUpDur = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration EntranceDur = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration RevealOpenDur = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration RevealCloseDur = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration FillDur = new(TimeSpan.FromMilliseconds(220));

    private const double PressScaleFactor = 0.94; // 按下时缩到 94%

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 1. PressScale — tactile button press（按压缩放反馈）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attached property: <c>bool</c>. When <c>true</c> on a <see cref="FrameworkElement"/>,
    /// hooks pointer events so the element scales to 94% on press (80ms) and springs back
    /// to 1.0 on release / mouse-leave (260ms, BackEase). When <c>false</c>, unhooks.
    /// </summary>
    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached(
            "PressScale", typeof(bool), typeof(Anim),
            new PropertyMetadata(false, OnPressScaleChanged));

    public static bool GetPressScale(DependencyObject obj) => (bool)obj.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject obj, bool value) => obj.SetValue(PressScaleProperty, value);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        if ((bool)e.NewValue)
        {
            // 缩放围绕中心；确保有一个可写 ScaleTransform 作为 RenderTransform
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            EnsureScaleTransform(fe);

            // 先解绑再绑定：保证恰好一份订阅（防 Style/local 值竞争或重复置 true 导致重复挂钩）
            fe.PreviewMouseLeftButtonDown -= OnPressDown; fe.PreviewMouseLeftButtonDown += OnPressDown;
            fe.PreviewMouseLeftButtonUp -= OnPressUp;     fe.PreviewMouseLeftButtonUp += OnPressUp;
            fe.MouseLeave -= OnPressLeave;                fe.MouseLeave += OnPressLeave;
        }
        else
        {
            fe.PreviewMouseLeftButtonDown -= OnPressDown;
            fe.PreviewMouseLeftButtonUp -= OnPressUp;
            fe.MouseLeave -= OnPressLeave;
        }
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
        => AnimatePressScale((FrameworkElement)sender, PressScaleFactor, PressDownDur, EaseOutCubic);

    private static void OnPressUp(object sender, MouseButtonEventArgs e)
        => AnimatePressScale((FrameworkElement)sender, 1.0, PressUpDur, BackOut);

    private static void OnPressLeave(object sender, MouseEventArgs e)
        => AnimatePressScale((FrameworkElement)sender, 1.0, PressUpDur, BackOut);

    private static void AnimatePressScale(FrameworkElement fe, double to, Duration dur, IEasingFunction ease)
    {
        var st = GetScaleTransform(fe);
        if (st == null) return;
        var anim = new DoubleAnimation { To = to, Duration = dur, EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. Entrance — one-shot card entrance（卡片入场，仅一次）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attached property: <c>bool</c>. When <c>true</c>, plays a one-shot entrance once the
    /// element is loaded: Opacity 0→1 and a TranslateTransform Y 8→0 over 220ms (ease-out).
    /// Assumes the element has no other RenderTransform.
    /// </summary>
    public static readonly DependencyProperty EntranceProperty =
        DependencyProperty.RegisterAttached(
            "Entrance", typeof(bool), typeof(Anim),
            new PropertyMetadata(false, OnEntranceChanged));

    public static bool GetEntrance(DependencyObject obj) => (bool)obj.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject obj, bool value) => obj.SetValue(EntranceProperty, value);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;

        // 已加载则立即播；否则挂到 Loaded 上播一次（用完即摘，保证只跑一遍）
        if (fe.IsLoaded)
        {
            PlayEntrance(fe);
        }
        else
        {
            void Handler(object s, RoutedEventArgs args)
            {
                fe.Loaded -= Handler;
                PlayEntrance(fe);
            }
            fe.Loaded += Handler;
        }
    }

    private static void PlayEntrance(FrameworkElement fe)
    {
        // 入场专用 TranslateTransform（基线 Y=0；假设元素没有其它 RenderTransform）
        var translate = new TranslateTransform(0, 0);
        fe.RenderTransform = translate;

        // FillBehavior.Stop：动画结束后回到基线（Opacity=1 / Y=0），不留挂起时钟（每张卡省一个 clock）
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = EntranceDur, EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation { From = 8, To = 0, Duration = EntranceDur, EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };

        fe.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. RevealOpen — collapsible reveal（可折叠展开/收起，替代 Visibility 绑定）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attached property: <c>bool</c> (bindable). Drives a collapsible reveal in place of a
    /// Visibility binding. <c>true</c>: become Visible, then Opacity 0→1 + ScaleY 0→1
    /// (origin top-left) over 180ms ease-out. <c>false</c>: Opacity→0 + ScaleY→0 over 160ms
    /// ease-in, then Collapsed on completion. The initial value is honored at attach time.
    /// </summary>
    public static readonly DependencyProperty RevealOpenProperty =
        DependencyProperty.RegisterAttached(
            "RevealOpen", typeof(bool), typeof(Anim),
            new PropertyMetadata(false, OnRevealOpenChanged));

    public static bool GetRevealOpen(DependencyObject obj) => (bool)obj.GetValue(RevealOpenProperty);
    public static void SetRevealOpen(DependencyObject obj, bool value) => obj.SetValue(RevealOpenProperty, value);

    private static void OnRevealOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        bool open = (bool)e.NewValue;

        // 收起方向需要顶部锚定的 ScaleTransform（沿 Y 折叠）
        var st = EnsureRevealScale(fe);

        // 在元素加载前、首次落到 false（含从默认值 false 起步）：直接套收起终态，不做动画，
        // 这样初始就是 Collapsed（honor initial value），也避免初始化时一闪。
        if (!open && !fe.IsLoaded)
        {
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            fe.Opacity = 0;
            st.ScaleY = 0;
            fe.Visibility = Visibility.Collapsed;
            return;
        }

        if (open)
        {
            fe.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = RevealOpenDur, EasingFunction = EaseOutCubic };
            var grow = new DoubleAnimation { From = 0, To = 1, Duration = RevealOpenDur, EasingFunction = EaseOutCubic };
            fe.BeginAnimation(UIElement.OpacityProperty, fade);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else
        {
            var fade = new DoubleAnimation { To = 0, Duration = RevealCloseDur, EasingFunction = EaseInCubic };
            var shrink = new DoubleAnimation { To = 0, Duration = RevealCloseDur, EasingFunction = EaseInCubic };

            // 收完才真正 Collapsed（在 Completed 里置；同时停掉动画 hold，让属性回到本地值）
            void OnDone(object? s, EventArgs args)
            {
                fade.Completed -= OnDone;
                if (!GetRevealOpen(fe)) fe.Visibility = Visibility.Collapsed; // 期间又被打开则别收起
            }
            fade.Completed += OnDone;

            fe.BeginAnimation(UIElement.OpacityProperty, fade);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. Pulse — attention cue（注意力脉冲，闪 3 次后停）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attached property: <c>bool</c> (bindable). When <c>true</c>, pulses Opacity
    /// 1→0.45→1 over 700ms, repeated 3 times, ending at Opacity 1. When <c>false</c>,
    /// stops the storyboard and restores Opacity to 1.
    /// </summary>
    public static readonly DependencyProperty PulseProperty =
        DependencyProperty.RegisterAttached(
            "Pulse", typeof(bool), typeof(Anim),
            new PropertyMetadata(false, OnPulseChanged));

    public static bool GetPulse(DependencyObject obj) => (bool)obj.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject obj, bool value) => obj.SetValue(PulseProperty, value);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        // 停掉上一段（无论开关，先清场）
        GetPulseStoryboard(fe)?.Stop(fe);

        if (!(bool)e.NewValue)
        {
            SetPulseStoryboard(fe, null);
            fe.BeginAnimation(UIElement.OpacityProperty, null); // 释放任何残留动画 hold
            fe.Opacity = 1;
            return;
        }

        // 1 → 0.45 → 1，单程 700ms，重复 3 次（不是 Forever）
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop, // 播完释放 hold，让 Completed 里的 Opacity=1 生效
        };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromPercent(0.5), SharedEaseInOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), SharedEaseInOut));

        Storyboard.SetTarget(pulse, fe);
        Storyboard.SetTargetProperty(pulse, new PropertyPath(UIElement.OpacityProperty));

        var sb = new Storyboard();
        sb.Children.Add(pulse);

        // 结束后保持 Opacity = 1（FillBehavior.Stop + 本地值即 1）
        sb.Completed += (_, _) => fe.Opacity = 1;

        SetPulseStoryboard(fe, sb);
        sb.Begin(fe, isControllable: true);
    }

    // 脉冲用一个 ease-in-out（柔和往返）；与静态只读的 in / out 那两个区分开
    private static readonly IEasingFunction SharedEaseInOut =
        Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    // ════════════════════════════════════════════════════════════════════════
    // 5. FillColor — smooth Shape fill transition（Shape 填充色平滑过渡）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attached property: <see cref="Color"/> (bindable). Smoothly animates a
    /// <see cref="Shape"/>'s fill <see cref="SolidColorBrush"/> to the new color over 220ms
    /// (ease-out). A non-frozen brush is created and assigned if the shape has none. The very
    /// first set (old value is the default <see cref="Color"/>) is applied instantly.
    /// </summary>
    public static readonly DependencyProperty FillColorProperty =
        DependencyProperty.RegisterAttached(
            "FillColor", typeof(Color), typeof(Anim),
            new PropertyMetadata(default(Color), OnFillColorChanged));

    public static Color GetFillColor(DependencyObject obj) => (Color)obj.GetValue(FillColorProperty);
    public static void SetFillColor(DependencyObject obj, Color value) => obj.SetValue(FillColorProperty, value);

    private static void OnFillColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Shape shape) return;

        var newColor = (Color)e.NewValue;
        var brush = EnsureFillBrush(shape);

        // 首次设值（旧值是默认 Color，且尚未标记过）→ 直接落色，不动画
        if (!GetFillInitialized(shape))
        {
            SetFillInitialized(shape, true);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = newColor;
            return;
        }

        var anim = new ColorAnimation { To = newColor, Duration = FillDur, EasingFunction = EaseOutCubic };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>Ensure the shape's Fill is a writable (non-frozen) <see cref="SolidColorBrush"/>.</summary>
    private static SolidColorBrush EnsureFillBrush(Shape shape)
    {
        // 已有可写 SolidColorBrush 就复用；否则新建（不 Freeze，因为要动画它的 Color）
        if (shape.Fill is SolidColorBrush scb && !scb.IsFrozen)
            return scb;

        var startColor = shape.Fill is SolidColorBrush frozen ? frozen.Color : GetFillColor(shape);
        var brush = new SolidColorBrush(startColor);
        shape.Fill = brush;
        return brush;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Private per-element state（私有 attached DP 存运行时状态）
    // ════════════════════════════════════════════════════════════════════════

    // PressScale 用的 ScaleTransform（挂在元素上，配套 RenderTransform）
    private static readonly DependencyProperty ScaleTransformProperty =
        DependencyProperty.RegisterAttached(
            "ScaleTransform", typeof(ScaleTransform), typeof(Anim),
            new PropertyMetadata(null));

    private static ScaleTransform? GetScaleTransform(DependencyObject obj)
        => (ScaleTransform?)obj.GetValue(ScaleTransformProperty);
    private static void SetScaleTransform(DependencyObject obj, ScaleTransform? value)
        => obj.SetValue(ScaleTransformProperty, value);

    private static ScaleTransform EnsureScaleTransform(FrameworkElement fe)
    {
        // 复用已存的；若元素当前 RenderTransform 恰是 ScaleTransform 也接管它
        var existing = GetScaleTransform(fe);
        if (existing != null) return existing;

        if (fe.RenderTransform is ScaleTransform current && !current.IsFrozen)
        {
            SetScaleTransform(fe, current);
            return current;
        }

        var st = new ScaleTransform(1, 1);
        fe.RenderTransform = st;
        SetScaleTransform(fe, st);
        return st;
    }

    // RevealOpen 用的顶部锚定 ScaleTransform
    private static readonly DependencyProperty RevealScaleProperty =
        DependencyProperty.RegisterAttached(
            "RevealScale", typeof(ScaleTransform), typeof(Anim),
            new PropertyMetadata(null));

    private static ScaleTransform? GetRevealScale(DependencyObject obj)
        => (ScaleTransform?)obj.GetValue(RevealScaleProperty);
    private static void SetRevealScale(DependencyObject obj, ScaleTransform? value)
        => obj.SetValue(RevealScaleProperty, value);

    private static ScaleTransform EnsureRevealScale(FrameworkElement fe)
    {
        var existing = GetRevealScale(fe);
        if (existing != null) return existing;

        var st = new ScaleTransform(1, 1);
        fe.RenderTransformOrigin = new Point(0, 0); // 顶部左上角锚定，沿 Y 折叠
        fe.RenderTransform = st;
        SetRevealScale(fe, st);
        return st;
    }

    // Pulse 当前 Storyboard（用于 false 时 Stop）
    private static readonly DependencyProperty PulseStoryboardProperty =
        DependencyProperty.RegisterAttached(
            "PulseStoryboard", typeof(Storyboard), typeof(Anim),
            new PropertyMetadata(null));

    private static Storyboard? GetPulseStoryboard(DependencyObject obj)
        => (Storyboard?)obj.GetValue(PulseStoryboardProperty);
    private static void SetPulseStoryboard(DependencyObject obj, Storyboard? value)
        => obj.SetValue(PulseStoryboardProperty, value);

    // FillColor "首次设值" 标记（首帧直接落色不动画）
    private static readonly DependencyProperty FillInitializedProperty =
        DependencyProperty.RegisterAttached(
            "FillInitialized", typeof(bool), typeof(Anim),
            new PropertyMetadata(false));

    private static bool GetFillInitialized(DependencyObject obj) => (bool)obj.GetValue(FillInitializedProperty);
    private static void SetFillInitialized(DependencyObject obj, bool value) => obj.SetValue(FillInitializedProperty, value);
}
