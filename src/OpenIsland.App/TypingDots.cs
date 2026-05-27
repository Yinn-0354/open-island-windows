using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OpenIsland.App;

/// <summary>
/// “AI 正在思考”指示器：三个小圆点以错位节奏循环脉动（类似聊天输入指示器）。
/// 全部在代码中自建视觉，不依赖任何 XAML 资源文件。
/// 性能：循环动画仅在控件可见时运行，控件不可见（Collapsed/Hidden）或卸载时停止，
/// 此时占用零 CPU。
/// </summary>
public class TypingDots : Control
{
    // 圆点直径（约 5px）
    private const double DotDiameter = 5.0;

    // 圆点之间的水平间距（约 3px）
    private const double DotGap = 3.0;

    // 单个圆点一次完整脉动（0.3 → 1.0 → 0.3）的时长（约 1000ms）
    private const double PulseDurationMs = 1000.0;

    // 相邻圆点之间的起始时间错位（约 160ms），使波浪从左向右移动
    private const double StaggerMs = 160.0;

    // 圆点数量
    private const int DotCount = 3;

    /// <summary>
    /// 圆点画刷依赖属性。默认 #9AA0A6（已冻结）。圆点 Fill 绑定到该属性。
    /// </summary>
    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(
            nameof(DotBrush),
            typeof(Brush),
            typeof(TypingDots),
            new PropertyMetadata(CreateDefaultBrush()));

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    // 运行中的循环 Storyboard，保存引用以便启动/停止。
    private Storyboard? _storyboard;

    static TypingDots()
    {
        // 让控件使用我们在实例构造中通过代码设置的 Template，而不是默认外观。
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TypingDots),
            new FrameworkPropertyMetadata(typeof(TypingDots)));
    }

    public TypingDots()
    {
        // 在代码中构建模板：一个水平 StackPanel，内含 3 个 Ellipse。
        Template = BuildTemplate();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private static Brush CreateDefaultBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 在代码中构建可视化模板：水平 StackPanel + 3 个 Ellipse。
    /// 每个 Ellipse 的 Fill 通过 TemplateBinding 绑定到 DotBrush，
    /// 并被赋予唯一名称（Dot0/Dot1/Dot2），以便在 OnApplyTemplate 中查找并附加动画。
    /// </summary>
    private static ControlTemplate BuildTemplate()
    {
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panelFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        panelFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);

        for (int i = 0; i < DotCount; i++)
        {
            var dotFactory = new FrameworkElementFactory(typeof(Ellipse));
            dotFactory.Name = GetDotName(i);
            dotFactory.SetValue(WidthProperty, DotDiameter);
            dotFactory.SetValue(HeightProperty, DotDiameter);
            // 初始 Opacity 设为脉动的低点，避免动画未运行时显得过亮。
            dotFactory.SetValue(OpacityProperty, 0.3);
            dotFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            // Fill 绑定到 DotBrush 依赖属性。
            dotFactory.SetValue(Shape.FillProperty, new TemplateBindingExtension(DotBrushProperty));

            // 除第一个外，给后续圆点添加左侧间距。
            if (i > 0)
            {
                dotFactory.SetValue(MarginProperty, new Thickness(DotGap, 0, 0, 0));
            }

            panelFactory.AppendChild(dotFactory);
        }

        var template = new ControlTemplate(typeof(TypingDots))
        {
            VisualTree = panelFactory
        };
        template.Seal();
        return template;
    }

    private static string GetDotName(int index) => "Dot" + index;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 仅在确实可见时启动，避免控件被加载但处于折叠状态时空跑动画。
        if (IsVisible)
        {
            StartAnimation();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool visible && visible)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    /// <summary>
    /// 构建并启动循环 Storyboard。若已在运行则不重复创建。
    /// 必须在模板应用之后（圆点已存在于可视化树中）才能成功定位目标元素。
    /// </summary>
    private void StartAnimation()
    {
        // 确保模板已展开，命名元素已注册到模板的命名作用域中。
        ApplyTemplate();

        if (_storyboard != null)
        {
            // 已存在：从头恢复播放即可。
            _storyboard.Begin(this, isControllable: true);
            return;
        }

        var storyboard = new Storyboard();

        // 缓动函数：进出都平滑，使脉动更自然。冻结以提升性能。
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();

        for (int i = 0; i < DotCount; i++)
        {
            var dot = GetTemplateChild(GetDotName(i)) as Ellipse;
            if (dot == null)
            {
                // 模板尚未就绪——放弃本次创建，等待下次可见/加载再试。
                return;
            }

            var animation = new DoubleAnimationUsingKeyFrames
            {
                // 整个循环时长 = 脉动时长，循环之间无停顿。
                Duration = new Duration(System.TimeSpan.FromMilliseconds(PulseDurationMs)),
                // 错位起始时间，形成从左到右的波浪。
                BeginTime = System.TimeSpan.FromMilliseconds(StaggerMs * i),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // 0.3 → 1.0 → 0.3
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.3, KeyTime.FromPercent(0.0), ease));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                1.0, KeyTime.FromPercent(0.5), ease));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.3, KeyTime.FromPercent(1.0), ease));

            // 不能 Freeze：下面要给它设 Storyboard.Target/TargetProperty（冻结后只读会抛异常）。
            // 缓动 SineEase 已冻结，性能足够；逐点动画短小、可见时才创建。
            Storyboard.SetTarget(animation, dot);
            Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));

            storyboard.Children.Add(animation);
        }

        _storyboard = storyboard;
        // isControllable: true 允许之后对该 Storyboard 调用 Stop/Pause 等控制操作。
        _storyboard.Begin(this, isControllable: true);
    }

    /// <summary>
    /// 停止循环 Storyboard，使控件占用零 CPU。
    /// </summary>
    private void StopAnimation()
    {
        if (_storyboard == null)
        {
            return;
        }

        _storyboard.Stop(this);
    }
}
