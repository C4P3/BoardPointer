using BoardPointer.Core.Hid;

namespace BoardPointer.Core.Sampling;

/// <summary>
/// サンプルの出どころ。実機 / 記録したCSVの再生 / 合成データ の3つを同じ形にしておくと、
/// Viewer も Replay も「どこから来たか」を気にせず同じパイプラインに流せる。
/// 実機が手元に無い状態でもUIとパイプラインを丸ごと動かせるのが地味に効く。
/// </summary>
public interface ISampleSource : IDisposable
{
    /// <summary>サンプル1つ。実機ソースではHID読みスレッドから飛んでくる点に注意。</summary>
    event Action<RawSample>? SampleReceived;

    /// <summary>切断、あるいは再生終了。引数は理由。</summary>
    event Action<string>? Ended;

    /// <summary>このソースに紐づく工場較正。実機なら読み出した値、CSVならヘッダに記録された値。</summary>
    FactoryCalibration? Calibration { get; }

    /// <summary>人間に見せる名前 (デバイス名、ファイル名など)。</summary>
    string Description { get; }

    void Start();
    void Stop();
}
