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
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder, userDataFolder: userDataFolder);
        await _web.EnsureCoreWebView2Async(env);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 13, 13);

        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Glass", "glass.html");
        var tcs = new TaskCompletionSource();
        _web.CoreWebView2.NavigationCompleted += (_, _) => tcs.TrySetResult();
        _web.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        await tcs.Task;
        _ready = true;
    }

    /// <summary>渲染一帧：桌面截图（BGRA，物理像素）→ 离屏页面折射 → 截回 BitmapSource。失败返回 null。</summary>
    public async Task<BitmapSource?> RenderAsync(byte[] bgBgra, int w, int h, double cornerRadiusPx)
    {
        await EnsureInitializedAsync();
        await _gate.WaitAsync();
        try
        {
            if (w != _lastW || h != _lastH)
            {
                _host!.Width = w;
                _host.Height = h;
                _lastW = w; _lastH = h;
                await Task.Delay(30); // 等 resize/relayout 落定，避免截到旧尺寸的半帧
            }

            var dataUri = EncodePngDataUri(bgBgra, w, h);
            var radiusArg = cornerRadiusPx.ToString(CultureInfo.InvariantCulture);
            await _web!.CoreWebView2.ExecuteScriptAsync($"window.setGlass && window.setGlass('{dataUri}', {radiusArg})");

            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { _gate.Release(); }
    }

    private static string EncodePngDataUri(byte[] bgra, int w, int h)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
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
