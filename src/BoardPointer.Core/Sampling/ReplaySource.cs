using System.Diagnostics;
using BoardPointer.Core.Hid;
using BoardPointer.Core.Recording;

namespace BoardPointer.Core.Sampling;

/// <summary>
/// メモリ上のサンプル列を、記録時のタイムスタンプ通りの速さで流し直す。実機の代わりになる。
///
/// 1回の起床で「その時点までに来ているはずのサンプル」をまとめて吐く作りにしてある。Windows の
/// 既定のタイマー分解能 (約15.6ms) では 100Hz を1サンプルずつ正確に刻めないため、素直に
/// 「1サンプルごとに sleep」すると再生が実時間より遅くなる。まとめて吐けば粒は粗くなっても
/// 壁時計上の速度は合う。なお <see cref="Pipeline.BoardPipeline"/> の出力は記録側のタイムスタンプ
/// だけで決まるので、この粒の粗さは解析結果には一切影響しない (見え方だけの話)。
/// </summary>
public sealed class ReplaySource : ISampleSource
{
    private readonly IReadOnlyList<RawSample> _samples;
    private Thread? _thread;
    private volatile bool _stopping;

    public event Action<RawSample>? SampleReceived;
    public event Action<string>? Ended;

    public FactoryCalibration? Calibration { get; }
    public string Description { get; }

    /// <summary>1.0 で等速、2.0 で倍速、0 以下なら待たずに一気に流す。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>最後まで行ったら先頭に戻る。可視化しながらパラメータを触るときに便利。</summary>
    public bool Loop { get; set; }

    public int SampleCount => _samples.Count;

    public ReplaySource(IReadOnlyList<RawSample> samples, FactoryCalibration? calibration, string description)
    {
        _samples = samples;
        Calibration = calibration;
        Description = description;
    }

    public static ReplaySource FromCsv(string path)
    {
        var (header, samples) = RawCsvReader.Read(path);
        return new ReplaySource(samples, header.Calibration, Path.GetFileName(path));
    }

    public static ReplaySource Synthetic(double seconds = 30.0, int seed = 1)
    {
        var calibration = FactoryCalibration.CreateNominal();
        var samples = SyntheticGenerator.Generate(seconds, calibration, seed);
        return new ReplaySource(samples, calibration, $"合成データ ({seconds:F0}秒)");
    }

    public void Start()
    {
        _stopping = false;
        _thread = new Thread(ReplayLoop) { IsBackground = true, Name = "ReplaySource" };
        _thread.Start();
    }

    public void Stop() => _stopping = true;

    private void ReplayLoop()
    {
        do
        {
            var clock = Stopwatch.StartNew();
            int index = 0;
            long startMs = _samples.Count > 0 ? _samples[0].TimestampMs : 0;

            while (index < _samples.Count && !_stopping)
            {
                if (Speed <= 0)
                {
                    SampleReceived?.Invoke(_samples[index++]);
                    continue;
                }

                double dueMs = (_samples[index].TimestampMs - startMs) / Speed;
                if (clock.Elapsed.TotalMilliseconds >= dueMs)
                {
                    SampleReceived?.Invoke(_samples[index++]);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
        while (Loop && !_stopping);

        if (!_stopping)
        {
            Ended?.Invoke("再生終了");
        }
    }

    public void Dispose() => Stop();
}
