using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OpenIsland.App.Views;

/// <summary>
/// 区域截图覆盖层（微信式逻辑）：全屏冻结当前画面 → 拖拽框选一个矩形 → 松手即裁剪并复制到剪贴板。
/// Esc / 右键 取消。覆盖整个虚拟桌面（多显示器）。
///
/// 实现要点：
///   · 打开时先用 System.Drawing 抓一张整屏物理像素位图（_shot），覆盖层本身透明不入镜。
///   · 选区外铺半透明黑(EvenOdd 路径挖洞)，选区内透出真实画面，加蓝色边框 + 尺寸标签。
///   · 松手时把选区 DIP 坐标按 (位图像素 / 窗口 DIP) 比例换算到位图像素裁剪 —— 单显示器精确，
///     多显示器同 DPI 也准确。复制到剪贴板同时写 Bitmap(DIB) 与 PNG 两种格式，粘贴兼容性好。
/// </summary>
public class ScreenshotOverlayWindow : Window
{
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                      SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private System.Drawing.Bitmap? _shot;        // 冻结的整屏物理像素位图
    private int _vx, _vy, _vcx, _vcy;            // 虚拟屏物理像素边界
    private Point _start;
    private bool _dragging;
    private bool _done;

    private readonly Canvas _root = new();
    private readonly System.Windows.Shapes.Path _dim = new();
    private readonly Rectangle _sel = new();
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();

    public ScreenshotOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;

        // 冻结整屏（覆盖层尚未显示，不会入镜）
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _vcx = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _vcy = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (_vcx <= 0 || _vcy <= 0) { _vcx = (int)SystemParameters.PrimaryScreenWidth; _vcy = (int)SystemParameters.PrimaryScreenHeight; }
        try
        {
            _shot = new System.Drawing.Bitmap(_vcx, _vcy, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(_shot);
            g.CopyFromScreen(_vx, _vy, 0, 0, new System.Drawing.Size(_vcx, _vcy));
        }
        catch { _shot = null; }

        // 覆盖窗口铺满虚拟桌面（DIP）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 选区外的半透明遮罩（EvenOdd 路径：全屏矩形 减去 选区矩形）
        _dim.Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        _dim.IsHitTestVisible = false;
        // 选区边框（透出真实画面），蓝色细边
        _sel.Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xA3, 0xFF));
        _sel.StrokeThickness = 1.5;
        _sel.Fill = Brushes.Transparent;
        _sel.IsHitTestVisible = false;
        _sel.Visibility = Visibility.Collapsed;
        // 尺寸标签
        _labelText.Foreground = Brushes.White;
        _labelText.FontSize = 12;
        _labelText.FontFamily = new FontFamily("Consolas, Segoe UI");
        _label.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
        _label.CornerRadius = new CornerRadius(3);
        _label.Padding = new Thickness(6, 2, 6, 2);
        _label.Child = _labelText;
        _label.IsHitTestVisible = false;
        _label.Visibility = Visibility.Collapsed;

        _root.Background = Brushes.Transparent; // 透明但可命中鼠标
        _root.Children.Add(_dim);
        _root.Children.Add(_sel);
        _root.Children.Add(_label);
        Content = _root;
        RebuildDim(new Rect(0, 0, 0, 0)); // 构造即铺好半透明遮罩

        Loaded += (_, _) => { RebuildDim(new Rect(0, 0, 0, 0)); Activate(); };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
    }

    private void RebuildDim(Rect hole)
    {
        // 用构造时设定的 Width/Height（虚拟屏 DIP，始终有效）铺满，不依赖可能尚未就绪的 ActualWidth。
        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
        grp.Children.Add(full);
        if (hole.Width > 0 && hole.Height > 0) grp.Children.Add(new RectangleGeometry(hole));
        _dim.Data = grp;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_root);
        _dragging = true;
        _sel.Visibility = Visibility.Visible;
        _label.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_root);
        var r = MakeRect(_start, p);
        UpdateSelection(r);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(_root);
        var r = MakeRect(_start, p);
        if (r.Width < 4 || r.Height < 4) { Cancel(); return; }
        Confirm(r);
    }

    private static Rect MakeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void UpdateSelection(Rect r)
    {
        RebuildDim(r);
        Canvas.SetLeft(_sel, r.X);
        Canvas.SetTop(_sel, r.Y);
        _sel.Width = r.Width;
        _sel.Height = r.Height;
        // 尺寸标签（按位图像素显示真实截图尺寸）
        double sx = (_shot != null && Width > 0) ? _shot.Width / Width : 1.0;
        double sy = (_shot != null && Height > 0) ? _shot.Height / Height : 1.0;
        _labelText.Text = $"{(int)Math.Round(r.Width * sx)} × {(int)Math.Round(r.Height * sy)}";
        double ly = r.Y - 22; if (ly < 2) ly = r.Y + 4;
        Canvas.SetLeft(_label, r.X);
        Canvas.SetTop(_label, ly);
    }

    private void Confirm(Rect r)
    {
        if (_done) return;
        _done = true;
        try
        {
            if (_shot == null) { Close(); return; }
            double sx = Width > 0 ? _shot.Width / Width : 1.0;
            double sy = Height > 0 ? _shot.Height / Height : 1.0;
            int px = (int)Math.Round(r.X * sx);
            int py = (int)Math.Round(r.Y * sy);
            int pw = (int)Math.Round(r.Width * sx);
            int ph = (int)Math.Round(r.Height * sy);
            px = Math.Max(0, Math.Min(px, _shot.Width - 1));
            py = Math.Max(0, Math.Min(py, _shot.Height - 1));
            pw = Math.Max(1, Math.Min(pw, _shot.Width - px));
            ph = Math.Max(1, Math.Min(ph, _shot.Height - py));

            using var crop = _shot.Clone(
                new System.Drawing.Rectangle(px, py, pw, ph), _shot.PixelFormat);
            var bs = ToBitmapSource(crop);
            CopyToClipboard(bs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Screenshot confirm failed: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private void Cancel() { if (_done) return; _done = true; Close(); }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    /// <summary>同时写 Bitmap(DIB) 与 PNG 两种剪贴板格式，兼容大多数粘贴目标（微信/Office/画图…）。</summary>
    private static void CopyToClipboard(BitmapSource src)
    {
        var data = new DataObject();
        data.SetImage(src);
        try
        {
            using var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(ms);
            data.SetData("PNG", ms);
        }
        catch { /* PNG 附加失败不影响主 DIB */ }

        // 剪贴板可能被其它进程短暂占用，重试几次。
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch { System.Threading.Thread.Sleep(40); }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _shot?.Dispose();
        _shot = null;
        base.OnClosed(e);
    }
}
