using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OpenIsland.App.Services;

/// <summary>
/// 液态玻璃：离屏 WebView2 渲染 HTML/SVG 折射效果（取代原生 CPU 版 GlassRenderer）。
///
/// 架构：一个永不出现在屏幕/任务栏/截屏里的 WPF 宿主窗口（挪到屏幕外 (-32000,-32000)，而不是
/// Hide()——WebView2 需要窗口真的 Show() 过才会持续渲染，彻底隐藏的窗口会停止合成新帧），
/// 里面塞一个 WebView2 控件，导航到 Assets/Glass/glass.html。每次 RenderAsync：
///   1) 若胶囊尺寸变了（折叠/展开/权限拓宽/Notch），先 resize 宿主窗口跟上；
///   2) 把桌面截图编码成 PNG data URI，连同目标圆角一起塞给页面的 window.setGlass()；
///   3) CapturePreviewAsync 把整页当前帧读回来解码成 BitmapSource，直接够 GlassFrame 用。
/// 全程用同一枚 SemaphoreSlim 串行化，避免 resize 与截帧互相打架。
/// </summary>
public sealed class WebGlassRenderer : IDisposable
{
    private Window? _host;
    private WebView2? _web;
    private bool _ready;
    private int _lastW = -1, _lastH = -1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _initTask;

    private async Task EnsureInitializedAsync()
    {
        if (_ready) return;
        _initTask ??= InitializeAsync();
        await _initTask;
    }

    private async Task InitializeAsync()
    {
        _host = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
            Left = -32000,
            Top = -32000,
            Width = 640,
            Height = 1200,
        };
        _web = new WebView2();
        _host.Content = _web;
        _host.Show(); // 必须真正 Show：离屏但仍是活窗口，才会持续出新帧供 CapturePreviewAsync 读取

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userDataFolder = Path.Combine(appData, "OpenIsland", "wv2-glass-userdata");
        // Fixed Version Runtime：跟 exe 一起发布的 WebView2\ 目录（WebView2.Runtime.X64 包打进去的），
        // 不依赖用户机器系统安装的 Evergreen Runtime。目录不存在时（比如本地未 restore 全部包）
        // 传 null 退回系统 Evergreen，开发调试仍可用。
        var fixedVersionFolder = Path.Combine(AppContext.BaseDirectory, "WebView2");
        var browserExecutableFolder = Directory.Exists(fixedVersionFolder) ? fixedVersionFolder : null;
        // 宿主窗口常年挂在 (-32000,-32000)（所有显示器范围之外）。Chromium 的原生窗口遮挡检测
        // （NativeWinOcclusion）会把这种"挪到屏幕外"的窗口当成不可见/被遮挡，主动把它的渲染
        // 降频节流（rAF 压到 ~1Hz、合成器降优先级）——这正是玻璃背景"只有几 fps、慢半拍、
        // 黏滞"的根因，跟 Electron/CEF 应用挪去屏幕外时的经典坑一样。用启动参数整体关掉这套
        // 节流机制，让这个离屏窗口一直按真实帧率渲染。
        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments =
                "--disable-features=CalculateNativeWinOcclusion " +
                "--disable-backgrounding-occluded-windows " +
                "--disable-renderer-backgrounding " +
                "--disable-background-timer-throttling",
        };
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder, userDataFolder, envOptions);
        await _web.EnsureCoreWebView2Async(env);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 13, 13);

        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Glass", "glass.html");
        var tcs = new TaskCompletionSource();
        _web.CoreWebView2.NavigationCompleted += (_, _) => tcs.TrySetResult();
        _web.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        await tcs.Task;
        _ready = true;
    }

    // 折叠胶囊已经够快；展开态（会话列表/统计面板全展开）面积能到好几倍，SVG 滤镜链
    // （3 条位移贴图 + 高斯模糊 + backdrop-filter blur）是按宿主窗口的实际像素面积计算的，
    // 面积越大滤镜算得越久——这才是"胶囊流畅、展开依旧卡"的根因：之前只缩小了喂进去的
    // 图片，没缩小宿主窗口/画布本身，滤镜该跑多少像素还是跑多少。这里把画布本身也按比例
    // 缩小渲染，再靠 WPF 的 ImageBrush(Stretch=Fill) 免费拉伸回真实尺寸——反正本来就是磨砂
    // 效果，不损观感。
    private const double RenderScale = 0.35;
    private const int ScaleMinW = 160, ScaleMinH = 50; // 折叠胶囊本来就小又快，跳过再缩

    /// <summary>渲染一帧：桌面截图（BGRA，物理像素）→ 离屏页面折射 → 截回 BitmapSource。失败返回 null。
    /// cornerRadiusPx 是 MainBorder 当前实际的四角圆角（物理像素，调用方已乘 DPI）——Notch 模式
    /// 下是非对称的（顶边贴屏幕、顶两角是 0，底两角有圆角），必须四个角分别传，不能只传一个统一值
    /// 再靠"退回默认值"猜——之前就是"TopLeft&lt;=0 时退回 18"这条兜底，把 Notch 模式合法的 0
    /// 误判成"没初始化"，导致玻璃内容自己在顶角又多裁了一圈圆角、露出黑色背景（"多了一圈黑框"）。
    /// blurPx/saturationPercent/refractionPercent 是控制中心「液态玻璃」区的三个可调参数
    /// （模糊强度 px / 饱和度 % / 折射色散强度 %），每帧原样透传给 glass.html 的 setGlass()。</summary>
    public async Task<BitmapSource?> RenderAsync(
        byte[] bgBgra, int w, int h, CornerRadius cornerRadiusPx,
        double blurPx, double saturationPercent, double refractionPercent)
    {
        await EnsureInitializedAsync();
        await _gate.WaitAsync();
        try
        {
            bool shrink = w > ScaleMinW && h > ScaleMinH;
            int rw = shrink ? Math.Max(1, (int)Math.Round(w * RenderScale)) : w;
            int rh = shrink ? Math.Max(1, (int)Math.Round(h * RenderScale)) : h;

            if (rw != _lastW || rh != _lastH)
            {
                _host!.Width = rw;
                _host.Height = rh;
                _lastW = rw; _lastH = rh;
                await Task.Delay(30); // 等 resize/relayout 落定，避免截到旧尺寸的半帧
            }

            // PNG 编码 + 最终解码都是纯 CPU 活。以前直接同步跑在这个方法所在的 UI 线程上——
            // 每次 MouseMove 触发重渲染，UI 线程就要为编码/解码卡住几十毫秒，正是拖动时
            // "透过玻璃看桌面帧率低、卡顿"的直接成因。挪进 Task.Run 扔给线程池；不带
            // ConfigureAwait(false)，await 完默认切回原 SynchronizationContext（UI 线程），
            // 后面调 WebView2 COM 对象仍在其宿主线程上，安全。
            var dataUri = await Task.Run(() => EncodePngDataUri(bgBgra, w, h, rw, rh));
            double radiusScale = shrink ? RenderScale : 1.0;
            string R(double v) => (v * radiusScale).ToString(CultureInfo.InvariantCulture);
            // 四个角分别传，顺序对齐 CSS border-radius 简写（TL TR BR BL），跟 WPF CornerRadius
            // 结构体字段顺序天然一致，不用做映射。
            var radiusArg = $"[{R(cornerRadiusPx.TopLeft)},{R(cornerRadiusPx.TopRight)},{R(cornerRadiusPx.BottomRight)},{R(cornerRadiusPx.BottomLeft)}]";
            var blurArg = blurPx.ToString(CultureInfo.InvariantCulture);
            var satArg = saturationPercent.ToString(CultureInfo.InvariantCulture);
            var refrArg = refractionPercent.ToString(CultureInfo.InvariantCulture);
            await _web!.CoreWebView2.ExecuteScriptAsync(
                $"window.setGlass && window.setGlass('{dataUri}', {radiusArg}, {blurArg}, {satArg}, {refrArg})");

            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            var pngBytes = ms.ToArray(); // 不依赖 Position，ToArray 总是取 0..Length

            return await Task.Run(() =>
            {
                using var msDecode = new MemoryStream(pngBytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = msDecode;
                bmp.EndInit();
                bmp.Freeze();
                return (BitmapSource)bmp;
            });
        }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>把 (w,h) 的桌面截图编码成 PNG data URI；targetW/targetH 不同于源尺寸时先降采样
    /// （画布本身已经按 RenderScale 缩小，这里让源图跟画布尺寸对齐，浏览器不用再自己缩放）。</summary>
    private static string EncodePngDataUri(byte[] bgra, int w, int h, int targetW, int targetH)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        BitmapSource forEncode = src;
        if (targetW != w || targetH != h)
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform((double)targetW / w, (double)targetH / h));
            scaled.Freeze();
            forEncode = scaled;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(forEncode));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    public void Dispose()
    {
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); } catch { }
        _gate.Dispose();
    }
}
