using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace OpenIsland.App.Services;

/// <summary>
/// 系统混音响度反应器 —— WasapiLoopbackCapture 抓当前系统正在播放的音频输出（网易云 /
/// 浏览器 / 任意播放器，只要在出声都能抓到），跑 FFT 取一个 0..1 的"响度包络"，给
/// Views.WaveVisual 的波浪动效当振幅驱动源，让波浪跟着音乐节奏起伏而不是匀速装饰动画。
///
/// 算法照抄一个独立验证项目里跑通的思路（环形缓冲 + Hann 窗 + FFT + 自动增益 + 静音门），
/// 不重新设计：
///   · 1024 点环形缓冲，每次 DataAvailable 回调写入新样本、跑一次 FFT。
///   · 统计 20-150Hz（低频/鼓点）平均幅值 = Bass 的原始输入；全频平均幅值 = Level 的原始输入。
///   · AGC：分别维护低频/全频历史最大值，按 0.999 衰减松弛下探；当前值除以历史最大值
///     再开平方压缩，得到有动态范围的 0..1，否则小音量时波浪几乎不动。
///   · 静音门：全频平均幅值小于阈值时直接给 0，避免没声音时设备底噪被 AGC 放大出假信号。
///   · 最终结果做一次轻量指数平滑，避免帧间跳变。
/// </summary>
public sealed class AudioLevelReactor : IDisposable
{
    private const int FftSize = 1024;
    private const float AgcDecay = 0.999f;   // 历史最大值的松弛衰减系数（每次分析都乘一次）
    private const float Gate = 3e-6f;        // 静音门阈值：全频平均幅值低于此值直接判定无声
    private const float Smoothing = 0.35f;   // 指数平滑系数（0..1，越大跟得越紧、越小越平滑）

    private WasapiLoopbackCapture? _cap;
    private readonly object _lock = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private readonly float[] _window = new float[FftSize];

    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bits = 32;
    private bool _isFloat = true;

    // AGC 历史最大值（分别给低频段/全频段），初值给个很小的正数避免第一次除零
    private float _bassMax = 1e-4f;
    private float _levelMax = 1e-5f;

    /// <summary>0..1，全频响度包络（已做 AGC + 平滑）。</summary>
    public float Level;

    /// <summary>0..1，低频段(20-150Hz，鼓点/低音)能量包络（已做 AGC + 平滑）。</summary>
    public float Bass;

    /// <summary>最近一次收到音频数据的时间（UTC）。调用方可以拿它判断"设备是否还有声音在
    /// 流动"——超过几百毫秒没更新，说明当前没有音频在播放。</summary>
    public DateTime Last;

    /// <summary>开始环回采集系统混音输出。创建 WasapiLoopbackCapture 失败（没有输出设备等
    /// 极端情况）时整个服务静默不生效（Level/Bass 永远停在 0），绝不让 App 崩。</summary>
    public void Start()
    {
        try
        {
            _cap = new WasapiLoopbackCapture(); // 默认渲染设备（跟系统音量条一致）
            var wf = _cap.WaveFormat;
            _sampleRate = wf.SampleRate;
            _channels = Math.Max(1, wf.Channels);
            _bits = wf.BitsPerSample;
            _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;

            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);

            _cap.DataAvailable += OnDataAvailable;
            _cap.StartRecording();
        }
        catch
        {
            try { _cap?.Dispose(); } catch { /* 忽略：本来就在处理失败路径 */ }
            _cap = null;
        }
    }

    /// <summary>把新样本（各声道取平均，折成单声道）写进环形缓冲，然后立即跑一次 FFT 分析。</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = Math.Max(1, _bits / 8);
        int stride = bytesPerSample * _channels;
        int frames = e.BytesRecorded / Math.Max(1, stride);
        var buf = e.Buffer;

        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int ch = 0; ch < _channels; ch++)
                {
                    int idx = f * stride + ch * bytesPerSample;
                    if (idx + bytesPerSample > buf.Length) continue;
                    float s = _isFloat
                        ? BitConverter.ToSingle(buf, idx)
                        : _bits == 16 ? BitConverter.ToInt16(buf, idx) / 32768f : 0f;
                    sum += s;
                }
                _ring[_ringPos] = sum / _channels;
                _ringPos = (_ringPos + 1) % FftSize;
            }
        }

        Last = DateTime.UtcNow;
        Analyze();
    }

    /// <summary>环形缓冲整体做 Hann 窗 + FFT，统计低频段/全频段平均幅值，AGC 归一化 + 静音门，
    /// 再做一次轻量指数平滑写进 Bass/Level。</summary>
    private void Analyze()
    {
        var comp = new Complex[FftSize];
        lock (_lock)
        {
            for (int i = 0; i < FftSize; i++)
            {
                int idx = (_ringPos + i) % FftSize;
                comp[i].X = _ring[idx] * _window[i];
                comp[i].Y = 0f;
            }
        }

        int m = (int)Math.Log2(FftSize);
        FastFourierTransform.FFT(true, m, comp);

        float bass = 0f; int bassN = 0;
        float total = 0f; int totalN = 0;
        for (int bin = 1; bin < FftSize / 2; bin++)
        {
            float mag = MathF.Sqrt(comp[bin].X * comp[bin].X + comp[bin].Y * comp[bin].Y);
            float freq = bin * (float)_sampleRate / FftSize;
            total += mag; totalN++;
            if (freq >= 20f && freq <= 150f) { bass += mag; bassN++; }
        }

        float rawBass = bassN > 0 ? bass / bassN : 0f;
        float rawLevel = totalN > 0 ? total / totalN : 0f;

        // AGC：历史最大值按 AgcDecay 缓慢往下松弛，跟当前值取 max —— 既能适配任意音量，
        // 又不会因为一次瞬时峰值就把往后所有帧压得很小。
        _bassMax = MathF.Max(rawBass, _bassMax * AgcDecay);
        _levelMax = MathF.Max(rawLevel, _levelMax * AgcDecay);

        // 静音门：全频平均幅值太小时直接给 0，避免没声音时设备底噪被 AGC 抖动出假信号
        float gate = rawLevel < Gate ? 0f : 1f;
        float targetBass = gate * MathF.Min(1f, MathF.Sqrt(rawBass / MathF.Max(_bassMax, 1e-6f)));
        float targetLevel = gate * MathF.Min(1f, MathF.Sqrt(rawLevel / MathF.Max(_levelMax, 1e-7f)));

        // 轻量指数平滑，避免帧间跳变
        Bass += (targetBass - Bass) * Smoothing;
        Level += (targetLevel - Level) * Smoothing;
    }

    public void Dispose()
    {
        try
        {
            if (_cap != null)
            {
                _cap.DataAvailable -= OnDataAvailable;
                _cap.StopRecording();
            }
        }
        catch { /* 释放阶段忽略异常 */ }
        try { _cap?.Dispose(); } catch { /* 同上 */ }
        _cap = null;
    }
}
