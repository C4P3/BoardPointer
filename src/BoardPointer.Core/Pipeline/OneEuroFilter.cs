namespace BoardPointer.Core.Pipeline;

/// <summary>
/// 1ユーロフィルタ (Casiez, Roussel, Vogel 2012)。ポインティング入力のために作られたフィルタで、
/// 単純な指数移動平均より明確に相性がいい。
///
/// 指数移動平均だと「静止時のジッタを消す強さ」と「動き出しの遅れ」が1つの係数で心中する。
/// 揺れを消すほど狙いが重くなり、軽くすると止まらない。1ユーロフィルタは推定した速度でカットオフを
/// 動かすので、ゆっくり = 強く平滑化 (揺れが消える)、速く = 弱く平滑化 (遅れない) を両立できる。
/// バランスボードは静止しているつもりでも重心が常に揺れている (姿勢制御が閉ループなので、そもそも
/// 止まらない) 入力源なので、この性質がそのまま効く。
///
/// つまみは2つだけ: MinCutoff を下げると静止時が静かになり、Beta を上げると速い動きの遅れが減る。
/// </summary>
public sealed class OneEuroFilter
{
    private readonly LowPass _value = new();
    private readonly LowPass _derivative = new();
    private double _lastValue;
    private bool _hasPrevious;

    public double MinCutoffHz { get; set; } = 1.0;
    public double Beta { get; set; }
    public double DerivativeCutoffHz { get; set; } = 1.0;

    public void Reset()
    {
        _value.Reset();
        _derivative.Reset();
        _hasPrevious = false;
        _lastValue = 0;
    }

    /// <param name="value">今回の入力値。</param>
    /// <param name="dtSeconds">前回サンプルからの実経過時間。壁時計ではなくサンプルのタイムスタンプ
    /// から出すこと。そうしておかないとリプレイとライブで結果が変わってしまう。</param>
    public double Filter(double value, double dtSeconds)
    {
        if (dtSeconds <= 0 || double.IsNaN(dtSeconds))
        {
            dtSeconds = 0.01; // 100Hz 相当。最初のサンプルと、タイムスタンプが巻き戻った場合の保険。
        }
        double rate = 1.0 / dtSeconds;

        double derivative = _hasPrevious ? (value - _lastValue) * rate : 0;
        _lastValue = value;
        _hasPrevious = true;

        double smoothedDerivative = _derivative.Filter(derivative, Alpha(DerivativeCutoffHz, dtSeconds));
        double cutoff = MinCutoffHz + Beta * Math.Abs(smoothedDerivative);
        return _value.Filter(value, Alpha(cutoff, dtSeconds));
    }

    private static double Alpha(double cutoffHz, double dtSeconds)
    {
        double tau = 1.0 / (2.0 * Math.PI * Math.Max(cutoffHz, 1e-6));
        return 1.0 / (1.0 + tau / dtSeconds);
    }

    private sealed class LowPass
    {
        private double _previous;
        private bool _initialized;

        public void Reset() => _initialized = false;

        public double Filter(double value, double alpha)
        {
            if (!_initialized)
            {
                _initialized = true;
                _previous = value;
                return value;
            }
            _previous = alpha * value + (1 - alpha) * _previous;
            return _previous;
        }
    }
}
