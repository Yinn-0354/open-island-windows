using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenIsland.App.Services;

/// <summary>
/// 从专辑封面 JPEG 缩略图（SMTC 给的那种 ~150px 小图）里提取最主要的 3 个颜色，供
/// Views.WaveVisual 的三层波浪当配色源。
///
/// 实现思路：解码成 Bgra32 像素 → 等间隔降采样封顶到几百个点（性能，不逐像素算）→
/// 轻量 k-means(K=3，RGB 欧氏距离，固定 8 轮不等收敛) → 按簇内点数从多到少排序 → 转
/// Color[3] 返回。任何一步失败都不抛异常，返回一个兜底的中性灰三元组。
/// </summary>
public static class AlbumPaletteExtractor
{
    /// <summary>解码/采样失败时的兜底配色 —— 三个相同的中性灰，避免调用方要额外判空。</summary>
    private static Color[] Fallback => new[]
    {
        Color.FromRgb(0x80, 0x80, 0x80),
        Color.FromRgb(0x80, 0x80, 0x80),
        Color.FromRgb(0x80, 0x80, 0x80),
    };

    /// <summary>采样点数上限 —— 降采样封顶到几百到一千个点，k-means 在这个规模上几毫秒内跑完。</summary>
    private const int MaxSamples = 800;

    /// <summary>k-means 簇数：固定按 3 个主色提取（波浪三层各用一个）。</summary>
    private const int K = 3;

    /// <summary>固定迭代轮数，不等收敛 —— 对这种小样本/低精度需求足够，也保证耗时可控。</summary>
    private const int Iterations = 8;

    /// <summary>提取封面 JPEG 字节里最主要的 3 个颜色，按占比从高到低排序。失败/异常一律返回
    /// 兜底中性灰三元组，绝不抛异常、绝不返回 null 元素。</summary>
    public static Color[] ExtractTop3(byte[]? jpegBytes)
    {
        try
        {
            if (jpegBytes == null || jpegBytes.Length == 0) return Fallback;

            var pixels = DecodeToBgra32(jpegBytes, out int width, out int height);
            if (pixels == null || width <= 0 || height <= 0) return Fallback;

            var samples = Sample(pixels, width, height);
            if (samples.Count == 0) return Fallback;

            return KMeansTop3(samples);
        }
        catch
        {
            return Fallback;
        }
    }

    /// <summary>用 JpegBitmapDecoder 解码，转成 FormatConvertedBitmap(Bgra32) 拿像素 byte[]。</summary>
    private static byte[]? DecodeToBgra32(byte[] jpegBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        using var ms = new MemoryStream(jpegBytes);
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        width = converted.PixelWidth;
        height = converted.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>等间隔降采样（按 sqrt(总像素/上限) 取网格步长），封顶到 MaxSamples 个点，
    /// 不逐像素跑 k-means。</summary>
    private static List<(byte r, byte g, byte b)> Sample(byte[] pixels, int width, int height)
    {
        var list = new List<(byte, byte, byte)>();
        long total = (long)width * height;
        int step = (int)Math.Max(1, Math.Sqrt(total / (double)MaxSamples));
        int stride = width * 4;

        for (int y = 0; y < height; y += step)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x += step)
            {
                int idx = rowOffset + x * 4;
                if (idx + 3 >= pixels.Length) continue;
                // Bgra32：B, G, R, A 顺序
                byte b = pixels[idx];
                byte g = pixels[idx + 1];
                byte r = pixels[idx + 2];
                list.Add((r, g, b));
                if (list.Count >= MaxSamples) return list;
            }
        }
        return list;
    }

    /// <summary>轻量 k-means(K=3)：初始质心取样本按亮度排序后的第 0、中位、末尾三个点（避免退化成
    /// 3 个几乎一样的簇），固定 8 轮迭代退出。最终按簇内点数从多到少排序，转 Color[3] 返回；
    /// 样本太少/纯色导致有效簇不足 3 个时，用最后一个有效颜色补齐。</summary>
    private static Color[] KMeansTop3(List<(byte r, byte g, byte b)> samples)
    {
        int n = samples.Count;

        var byLuminance = samples
            .Select(s => (s.r, s.g, s.b, lum: 0.299 * s.r + 0.587 * s.g + 0.114 * s.b))
            .OrderBy(t => t.lum)
            .ToList();

        var centroids = new (double r, double g, double b)[K];
        centroids[0] = (byLuminance[0].r, byLuminance[0].g, byLuminance[0].b);
        centroids[1] = (byLuminance[n / 2].r, byLuminance[n / 2].g, byLuminance[n / 2].b);
        centroids[2] = (byLuminance[n - 1].r, byLuminance[n - 1].g, byLuminance[n - 1].b);

        var assign = new int[n];
        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < n; i++)
            {
                var s = samples[i];
                double best = double.MaxValue;
                int bestK = 0;
                for (int k = 0; k < K; k++)
                {
                    double dr = s.r - centroids[k].r;
                    double dg = s.g - centroids[k].g;
                    double db = s.b - centroids[k].b;
                    double d = dr * dr + dg * dg + db * db;
                    if (d < best) { best = d; bestK = k; }
                }
                assign[i] = bestK;
            }

            var sumR = new double[K];
            var sumG = new double[K];
            var sumB = new double[K];
            var count = new int[K];
            for (int i = 0; i < n; i++)
            {
                int k = assign[i];
                var s = samples[i];
                sumR[k] += s.r; sumG[k] += s.g; sumB[k] += s.b; count[k]++;
            }
            for (int k = 0; k < K; k++)
            {
                // count[k] == 0：该簇本轮没分到点，保留上一轮质心不变，避免除零/NaN
                if (count[k] > 0)
                    centroids[k] = (sumR[k] / count[k], sumG[k] / count[k], sumB[k] / count[k]);
            }
        }

        var finalCount = new int[K];
        for (int i = 0; i < n; i++) finalCount[assign[i]]++;

        var order = Enumerable.Range(0, K)
            .Where(k => finalCount[k] > 0)
            .OrderByDescending(k => finalCount[k])
            .ToList();

        if (order.Count == 0) return Fallback; // 理论上 n>0 时不会发生，兜底保险

        var result = new Color[3];
        for (int i = 0; i < 3; i++)
        {
            int k = i < order.Count ? order[i] : order[^1]; // 不足 3 个有效簇：用最后一个补齐
            var c = centroids[k];
            result[i] = Color.FromRgb(ClampByte(c.r), ClampByte(c.g), ClampByte(c.b));
        }
        return result;
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(v, 0, 255);
}
