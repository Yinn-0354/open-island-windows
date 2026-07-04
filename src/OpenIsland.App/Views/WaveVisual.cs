using System.Windows;
using System.Windows.Media;

namespace OpenIsland.App.Views;

/// <summary>
/// 纯 WPF 波浪可视化控件 —— 画 3 层半透明水波形状叠加，颜色来自专辑封面主色
/// （AlbumPaletteExtractor.ExtractTop3），振幅来自音频响度包络（AudioLevelReactor.Level/Bass）。
///
/// 刻意不用 Shader/Win2D/SkiaSharp：这个项目做液态玻璃背景时已经踩过 WPF ShaderEffect 和
/// Win2D 在这里都走不通的坑，最终验证可行的路子是纯 C#/WPF 原生实现（见 WebGlassRenderer
/// 的说明）。这里延续同一原则，只用 DrawingContext + StreamGeometry 画贝塞尔波浪轮廓，
/// 计算量控制在几个 Sin 的组合，是纯视觉装饰不是流体仿真。
/// </summary>
public class WaveVisual : FrameworkElement
{
    #region 依赖属性

    public static readonly DependencyProperty Color1Property =
        DependencyProperty.Register(nameof(Color1), typeof(Color), typeof(WaveVisual),
            new FrameworkPropertyMetadata(Color.FromRgb(0x6a, 0x5a, 0xcd), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Color2Property =
        DependencyProperty.Register(nameof(Color2), typeof(Color), typeof(WaveVisual),
            new FrameworkPropertyMetadata(Color.FromRgb(0x40, 0x8a, 0xd6), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Color3Property =
        DependencyProperty.Register(nameof(Color3), typeof(Color), typeof(WaveVisual),
            new FrameworkPropertyMetadata(Color.FromRgb(0x2f, 0x4f, 0x8f), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1，当前振幅强度（一般绑 AudioLevelReactor.Level/Bass 的平滑值）。
    /// 波峰最高高度 = Amplitude * (ActualHeight * 0.5) —— 硬性封顶在控件自身高度的一半，
    /// 不会做成可以超过一半。</summary>
    public static readonly DependencyProperty AmplitudeProperty =
        DependencyProperty.Register(nameof(Amplitude), typeof(double), typeof(WaveVisual),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>最前面一层（视觉上"在最上面"）的颜色，一般传 ExtractTop3 结果里占比最高的那个。</summary>
    public Color Color1
    {
        get => (Color)GetValue(Color1Property);
        set => SetValue(Color1Property, value);
    }

    /// <summary>中间层颜色。</summary>
    public Color Color2
    {
        get => (Color)GetValue(Color2Property);
        set => SetValue(Color2Property, value);
    }

    /// <summary>最后面一层颜色。</summary>
    public Color Color3
    {
        get => (Color)GetValue(Color3Property);
        set => SetValue(Color3Property, value);
    }

    public double Amplitude
    {
        get => (double)GetValue(AmplitudeProperty);
        set => SetValue(AmplitudeProperty, value);
    }

    #endregion

    /// <summary>持续向一个方向缓慢平移的相位，每帧 CompositionTarget.Rendering 都往前走一点点。</summary>
    private double _phase;
    private bool _renderingHooked;

    /// <summary>每帧相位推进量 —— 很小的一个值，视觉上是"缓慢平移"，不是流体仿真。</summary>
    private const double PhaseStep = 0.012;

    /// <summary>沿宽度采样波浪轮廓的点数上限 —— 贝塞尔曲线经过这些采样点，数量控制在
    /// 一两百个以内，避免每帧算太多 Sin/Cos。</summary>
    private const int MaxSamplePoints = 160;

    // 三层的 sum-of-sines 参数：每层 3 个不同频率/相位/幅值的正弦波叠加，营造自然感。
    // 每层的幅值权重合计为 1.0 —— 保证即便三个频率恰好同相位叠加，也不会超出该层分配到的
    // 振幅上限（层间的 0.5/0.7/1.0 比例见 OnRender 里的 ampRatio）。
    private static readonly (double wavelengthPx, double weight, double phaseSpeed, double phaseOffset)[] LayerBack =
    {
        (300, 0.45, 0.6, 1.3),
        (130, 0.35, 1.0, 3.0),
        (70,  0.20, 1.7, 0.6),
    };

    private static readonly (double wavelengthPx, double weight, double phaseSpeed, double phaseOffset)[] LayerMid =
    {
        (260, 0.50, 0.8, 0.4),
        (110, 0.33, 1.3, 2.0),
        (60,  0.17, 2.0, 4.0),
    };

    private static readonly (double wavelengthPx, double weight, double phaseSpeed, double phaseOffset)[] LayerFront =
    {
        (220, 0.55, 1.0, 0.0),
        (95,  0.30, 1.6, 1.1),
        (50,  0.15, 2.3, 2.7),
    };

    public WaveVisual()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 按可见性挂/退渲染循环 —— 不能只按 Loaded/Unloaded：宿主 DynamicIslandWindow 是
        // DI 单例、整个应用生命周期常驻可视树，永远不会 Unloaded；而本控件的 Visibility 绑
        // ShowNowPlayingWave，波形关掉/没在放歌时是 Collapsed。WPF 的 Loaded/Unloaded 只跟
        // "是否挂在可视树上"有关，跟 Visibility 无关——Collapsed 不触发 Unloaded。所以若只按
        // Loaded 挂钩，CompositionTarget.Rendering 会从应用启动起每帧空转（_phase++ +
        // InvalidateVisual，~60fps）到关机，波形明明没显示也在烧 CPU/耗电。改成可见才挂、
        // 不可见就退。
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateRenderingHook();

    private void OnUnloaded(object sender, RoutedEventArgs e) => UpdateRenderingHook();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRenderingHook();

    /// <summary>仅当控件既在可视树上（IsLoaded）又真正可见（IsVisible，Collapsed/Hidden 为 false）
    /// 时才订阅全局渲染循环；否则退订。这个事件挂在全局渲染循环上，不退订控件也不会被 GC，
    /// 所以隐藏与卸载都要退。</summary>
    private void UpdateRenderingHook()
    {
        bool shouldHook = IsLoaded && IsVisible;
        if (shouldHook == _renderingHooked) return;
        if (shouldHook) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = shouldHook;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _phase += PhaseStep;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double amplitude = Math.Clamp(Amplitude, 0.0, 1.0);
        // 硬性约束：波峰最高高度不超过控件自身高度的一半
        double maxAmpPx = amplitude * h * 0.5;

        // 从后往前画：后面的层先画，前面的层盖在上面。层间振幅比例 0.5 / 0.7 / 1.0，
        // baseline 依次降低（更靠近底部）让前面的层覆盖更多画面、后面的层只在波峰处探出来。
        DrawLayer(dc, w, h, baselineY: h * 0.38, ampPx: maxAmpPx * 0.5,
            color: Color3, opacity: 0.42, phase: _phase, layer: LayerBack);
        DrawLayer(dc, w, h, baselineY: h * 0.52, ampPx: maxAmpPx * 0.7,
            color: Color2, opacity: 0.55, phase: _phase, layer: LayerMid);
        DrawLayer(dc, w, h, baselineY: h * 0.66, ampPx: maxAmpPx * 1.0,
            color: Color1, opacity: 0.72, phase: _phase, layer: LayerFront);
    }

    /// <summary>画一层波浪：采样出穿过整个宽度的波浪轮廓点，用贝塞尔曲线连成平滑曲线，
    /// 再往下补到底边围成一个面，Fill 半透明色。</summary>
    private void DrawLayer(
        DrawingContext dc, double w, double h, double baselineY, double ampPx,
        Color color, double opacity, double phase,
        (double wavelengthPx, double weight, double phaseSpeed, double phaseOffset)[] layer)
    {
        int pointCount = Math.Max(2, Math.Min(MaxSamplePoints, (int)(w / 6)));
        var points = new Point[pointCount + 1];
        double dx = w / pointCount;

        for (int i = 0; i <= pointCount; i++)
        {
            double x = i * dx;
            double y = baselineY - ampPx * WaveOffset(x, phase, layer);
            points[i] = new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: true, isClosed: true);

            // 经过采样点的平滑曲线：以 points[i] 为控制点、points[i]/points[i+1] 中点为
            // 段终点的经典二次贝塞尔平滑写法，比逐点直线连接自然。
            for (int i = 1; i < points.Length - 1; i++)
            {
                var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                ctx.QuadraticBezierTo(points[i], mid, isStroked: true, isSmoothJoin: false);
            }
            ctx.LineTo(points[^1], isStroked: true, isSmoothJoin: false);

            // 补到底边围成一个面
            ctx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(0, h), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();

        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>sum-of-sines：几个不同频率/相位的正弦波叠加，返回值范围约在 [-1, 1]
    /// （各分量权重合计为 1.0，保证不越界），乘以 ampPx 就是相对 baseline 的垂直偏移。</summary>
    private static double WaveOffset(
        double x, double phase,
        (double wavelengthPx, double weight, double phaseSpeed, double phaseOffset)[] layer)
    {
        double sum = 0.0;
        for (int i = 0; i < layer.Length; i++)
        {
            var (wavelengthPx, weight, phaseSpeed, phaseOffset) = layer[i];
            double angle = 2.0 * Math.PI * x / wavelengthPx + phase * phaseSpeed + phaseOffset;
            sum += weight * Math.Sin(angle);
        }
        return sum;
    }
}
