using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenIsland.App.Services;

/// <summary>
/// 液态毛玻璃 CPU 渲染。把灵动岛正下方的真实桌面做"平滑模糊（毛玻璃）+ 暗 tint + 收敛高光边"，
/// 当 MainBorder / 卡片背景画出来。
///
/// 模糊用 <b>降采样 2× → 半分辨率上多趟可分离盒式模糊（滑动窗口，≈高斯）→ 双线性放大</b>：
/// · 多趟盒式 = 平滑颜色晕染，<b>无降采样块状颗粒感</b>（之前单纯降采样+放大那种"廉价"颗粒已消除）；
/// · 在 1/4 像素上做模糊 + Parallel 分行/列并行 → 展开态(~43万像素)一帧 ~6ms，
///   折叠态 ~1ms，加抓屏总计 ~10ms 内 → 拖动（含展开）60fps 丝滑。
/// 圆角内外用 SDF 掩码，外部透传由 MainBorder 裁掉。
/// </summary>
public sealed class GlassRenderer
{
    private const int Down = 2;                            // 降采样因子
    private const int BlurRadius = 6;                      // 半分辨率上的盒式模糊半径（≈全分辨率 12）
    private const int BlurPasses = 3;                      // 趟数：3 趟 ≈ 高斯，平滑无块
    private const double TintA = 0.42;                     // 暗 tint 透明度（压暗到稳定可读、不过曝）
    private static readonly double[] Tint = { 12, 8, 6 };  // BGR 暗蓝黑

    private int _mw = -1, _mh = -1;
    private double _mr = -1;
    private bool[] _in = System.Array.Empty<bool>();
    private float[] _hl = System.Array.Empty<float>();     // 方向性高光环权重（变白，几何相关缓存）
    private float[] _dark = System.Array.Empty<float>();   // 底部细暗边权重（变暗）

    private static double Sdf(double px, double py, int W, int H, double R)
    {
        double qx = System.Math.Abs(px - W / 2.0) - (W / 2.0 - R);
        double qy = System.Math.Abs(py - H / 2.0) - (H / 2.0 - R);
        double ax = System.Math.Max(qx, 0), ay = System.Math.Max(qy, 0);
        return System.Math.Sqrt(ax * ax + ay * ay) + System.Math.Min(System.Math.Max(qx, qy), 0) - R;
    }

    private void EnsureMap(int W, int H, double R)
    {
        if (W == _mw && H == _mh && System.Math.Abs(R - _mr) < 0.5) return;
        _mw = W; _mh = H; _mr = R;
        int n = W * H;
        _in = new bool[n]; _hl = new float[n]; _dark = new float[n];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                double d = Sdf(x + 0.5, y + 0.5, W, H, R);
                if (d >= 0) { _in[i] = false; continue; }
                _in[i] = true;
                double inside = -d;
                // 方向性高光环（仿 liquid-glass-react 边缘）：极脆内白线 + 脆边 + 内发光，
                // 整体沿 135° 对角线（左上→右下）做亮度晕染（峰值约 0.6 处），保底 0.26 避免另一侧全黑。
                double line = System.Math.Clamp(1 - inside / 0.9, 0, 1);
                double core = System.Math.Clamp(1 - inside / 2.2, 0, 1);
                double glow = System.Math.Clamp(1 - inside / 10.0, 0, 1); glow = glow * glow * 0.55;
                double t = ((double)x / W + (double)y / H) * 0.5;
                double sheen = System.Math.Exp(-System.Math.Pow((t - 0.6) / 0.32, 2));
                double dir = 0.26 + 0.74 * sheen;
                _hl[i] = (float)System.Math.Min(1, (line * 0.55 + core * 0.70 + glow * 0.45) * dir);
                _dark[i] = (float)((inside < 1.4 && y > H * 0.55) ? 0.18 * System.Math.Clamp(1 - inside / 1.4, 0, 1) : 0);
            }
    }

    // 水平盒式模糊（滑动窗口 + 边缘复制），逐行并行。src→dst。
    private static void BoxH(byte[] src, byte[] dst, int W, int H, int r)
    {
        int win = 2 * r + 1;
        Parallel.For(0, H, y =>
        {
            int row = y * W * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = (r + 1) * src[row + c];
                for (int k = 1; k <= r; k++) sum += src[row + System.Math.Min(k, W - 1) * 4 + c];
                for (int x = 0; x < W; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum / win);
                    int xin = System.Math.Min(x + r + 1, W - 1), xout = System.Math.Max(x - r, 0);
                    sum += src[row + xin * 4 + c] - src[row + xout * 4 + c];
                }
            }
        });
    }
    // 垂直盒式模糊，逐列并行。src→dst。
    private static void BoxV(byte[] src, byte[] dst, int W, int H, int r)
    {
        int win = 2 * r + 1;
        Parallel.For(0, W, x =>
        {
            int col = x * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = (r + 1) * src[col + c];
                for (int k = 1; k <= r; k++) sum += src[System.Math.Min(k, H - 1) * W * 4 + col + c];
                for (int y = 0; y < H; y++)
                {
                    dst[y * W * 4 + col + c] = (byte)(sum / win);
                    int yin = System.Math.Min(y + r + 1, H - 1), yout = System.Math.Max(y - r, 0);
                    sum += src[yin * W * 4 + col + c] - src[yout * W * 4 + col + c];
                }
            }
        });
    }

    private static double Bi(byte[] d, int dw, int x0, int y0, int x1, int y1, double tx, double ty, int ch)
    {
        double a = d[(y0 * dw + x0) * 4 + ch], b = d[(y0 * dw + x1) * 4 + ch], c = d[(y1 * dw + x0) * 4 + ch], e = d[(y1 * dw + x1) * 4 + ch];
        double top = a + (b - a) * tx, bot = c + (e - c) * tx; return top + (bot - top) * ty;
    }

    /// <summary>把桌面 bg（设备像素 BGRA，W×H）渲染成平滑毛玻璃，返回冻结的 BitmapSource。</summary>
    public BitmapSource Render(byte[] bg, int W, int H, double R)
    {
        EnsureMap(W, H, R);

        // 1) 降采样 2×（块平均）
        int D = Down, dw = (W + D - 1) / D, dh = (H + D - 1) / D;
        var small = new byte[dw * dh * 4];
        Parallel.For(0, dh, dy =>
        {
            for (int dx = 0; dx < dw; dx++)
            {
                int sb = 0, sg = 0, sr = 0, cnt = 0, x0 = dx * D, y0 = dy * D;
                for (int yy = y0; yy < y0 + D && yy < H; yy++)
                    for (int xx = x0; xx < x0 + D && xx < W; xx++)
                    { int p = (yy * W + xx) * 4; sb += bg[p]; sg += bg[p + 1]; sr += bg[p + 2]; cnt++; }
                if (cnt == 0) cnt = 1;
                int di = (dy * dw + dx) * 4;
                small[di] = (byte)(sb / cnt); small[di + 1] = (byte)(sg / cnt); small[di + 2] = (byte)(sr / cnt);
            }
        });

        // 2) 半分辨率上多趟盒式模糊 → 平滑颜色晕染
        var tmp = new byte[dw * dh * 4];
        for (int p = 0; p < BlurPasses; p++) { BoxH(small, tmp, dw, dh, BlurRadius); BoxV(tmp, small, dw, dh, BlurRadius); }

        // 3) 双线性放大 + 暗 tint + 高光，并行分行
        var o = new byte[W * H * 4];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4, mi = y * W + x;
                if (!_in[mi])
                {
                    o[i] = bg[i]; o[i + 1] = bg[i + 1]; o[i + 2] = bg[i + 2]; o[i + 3] = 255;
                    continue;
                }
                double fx = (x + 0.5) / (double)D - 0.5, fy = (y + 0.5) / (double)D - 0.5;
                int bx0 = (int)System.Math.Floor(fx), by0 = (int)System.Math.Floor(fy), bx1 = bx0 + 1, by1 = by0 + 1;
                double tx = fx - bx0, ty = fy - by0;
                bx0 = System.Math.Clamp(bx0, 0, dw - 1); bx1 = System.Math.Clamp(bx1, 0, dw - 1);
                by0 = System.Math.Clamp(by0, 0, dh - 1); by1 = System.Math.Clamp(by1, 0, dh - 1);
                double B = Bi(small, dw, bx0, by0, bx1, by1, tx, ty, 0);
                double G = Bi(small, dw, bx0, by0, bx1, by1, tx, ty, 1);
                double Rr = Bi(small, dw, bx0, by0, bx1, by1, tx, ty, 2);
                B = B * (1 - TintA) + Tint[0] * TintA;
                G = G * (1 - TintA) + Tint[1] * TintA;
                Rr = Rr * (1 - TintA) + Tint[2] * TintA;
                double w = _hl[mi];
                if (w > 0) { B += (255 - B) * w; G += (255 - G) * w; Rr += (255 - Rr) * w; }
                double dk = _dark[mi];
                if (dk > 0) { B *= (1 - dk); G *= (1 - dk); Rr *= (1 - dk); }
                o[i] = (byte)(B < 0 ? 0 : B > 255 ? 255 : B);
                o[i + 1] = (byte)(G < 0 ? 0 : G > 255 ? 255 : G);
                o[i + 2] = (byte)(Rr < 0 ? 0 : Rr > 255 ? 255 : Rr);
                o[i + 3] = 255;
            }
        });

        var wb = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, W, H), o, W * 4, 0);
        wb.Freeze();
        return wb;
    }
}

/// <summary>
/// 屏幕区域抓取（GDI BitBlt）→ BGRA byte[]。坐标为物理像素。设了 WDA_EXCLUDEFROMCAPTURE 的窗口
/// （灵动岛自己）被排除，抓到的是其背后的桌面，避免毛玻璃套毛玻璃自我反馈。
/// </summary>
internal static class ScreenGrab
{
    [DllImport("user32.dll")] private static extern System.IntPtr GetDC(System.IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(System.IntPtr hwnd, System.IntPtr dc);
    [DllImport("gdi32.dll")] private static extern System.IntPtr CreateCompatibleDC(System.IntPtr dc);
    [DllImport("gdi32.dll")] private static extern System.IntPtr CreateCompatibleBitmap(System.IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern System.IntPtr SelectObject(System.IntPtr dc, System.IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(System.IntPtr d, int x, int y, int w, int h, System.IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(System.IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(System.IntPtr dc);
    private const int SRCCOPY = 0x00CC0020;

    /// <summary>抓 (x,y,w,h)（物理像素）→ BGRA byte[]，失败返回 null。</summary>
    public static byte[] CaptureBytes(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        System.IntPtr screen = GetDC(System.IntPtr.Zero);
        System.IntPtr mem = CreateCompatibleDC(screen);
        System.IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        System.IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                bmp, System.IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var buf = new byte[h * stride];
            conv.CopyPixels(buf, stride, 0);
            return buf;
        }
        catch { return null; }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(System.IntPtr.Zero, screen);
        }
    }
}
