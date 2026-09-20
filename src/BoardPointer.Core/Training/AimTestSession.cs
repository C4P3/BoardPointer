using BoardPointer.Core.Mapping;

namespace BoardPointer.Core.Training;

/// <summary>
/// 1試行の結果。時間はすべてミリ秒。
///
/// 3つに割ってあるのが肝。<see cref="FirstTouchMs"/> が「大まかに素早く合わせる」フェーズ、
/// <see cref="SettleMs"/> が「そこから細かく詰める」フェーズ、維持の1秒は定数なので引いてある。
/// 合計だけ見ていると、速いが行き過ぎる設定と、遅いが一発で止まる設定が同じ点数になって、
/// どちらのつまみを触ればいいのか分からない。
/// </summary>
/// <param name="FirstTouchMs">試行開始から、初めて的に入るまで。</param>
/// <param name="SettleMs">
/// 初めて的に入ってから、最後まで続く維持が始まるまで。一発で止まれば0。行き過ぎて入り直した
/// ぶんだけ増える。
/// </param>
/// <param name="CompletionMs">試行開始から維持完了まで。＝ FirstTouch + Settle + 維持時間。</param>
/// <param name="ReEntries">的に入り直した回数。0が理想。オーバーシュートの直接の証拠。</param>
/// <param name="MaxOvershootPx">初到達のあと、的の**外**へ出た最大の距離。中に収まっていれば0。</param>
/// <param name="PathEfficiency">直線距離 / 実際に通った距離。1.0が理想。</param>
/// <param name="DwellDriftPx">成立した維持のあいだの、的の中心からの距離のRMS。震えの大きさ。</param>
/// <param name="ClosestApproachPx">
/// 試行中に的の中心へ最も近づいた距離。的に一度も入れなかったときの切り分けに要る ---
/// 遠くで止まっているなら届いていない (デッドゾーン・原点・速度)、縁まで来ているのに入れない
/// なら止め際か分解能で、直すつまみがまるで違う。
/// </param>
/// <param name="PeakRadius">試行中に出た正規化半径の最大。曲線のどこまで使えたか。</param>
/// <param name="PeakSpeedPxPerSec">試行中に出た速度の最大。</param>
/// <param name="MinPressureFactor">荷重の倍率が最小どこまで落ちたか。荷重モードを使っていなければ1。</param>
public readonly record struct AimTrialResult(
    int Index,
    bool IsWarmup,
    bool TimedOut,
    double DistancePx,
    double TargetDiameterPx,
    double DirectionDegrees,
    double IndexOfDifficulty,
    double FirstTouchMs,
    double SettleMs,
    double CompletionMs,
    int ReEntries,
    double MaxOvershootPx,
    double PathEfficiency,
    double DwellDriftPx,
    double ClosestApproachPx,
    double PeakRadius,
    double PeakSpeedPxPerSec,
    double MinPressureFactor)
{
    public string DirectionLabel => AimTestPlan.DirectionLabel(DirectionDegrees);

    /// <summary>的に一度も入れなかったか。時間切れの中でも性質がまったく違う。</summary>
    public bool NeverTouched => FirstTouchMs < 0;

    public const string CsvHeader =
        "index,warmup,timed_out,distance_px,target_dia_px,direction_deg,direction,id_bits," +
        "first_touch_ms,settle_ms,completion_ms,re_entries,max_overshoot_px,path_efficiency," +
        "dwell_drift_px,closest_px,peak_radius,peak_speed_px_s,min_pressure_factor";

    public string ToCsv() =>
        $"{Index},{(IsWarmup ? 1 : 0)},{(TimedOut ? 1 : 0)},{DistancePx:F1},{TargetDiameterPx:F0}," +
        $"{DirectionDegrees:F1},{DirectionLabel},{IndexOfDifficulty:F3}," +
        $"{FirstTouchMs:F0},{SettleMs:F0},{CompletionMs:F0},{ReEntries},{MaxOvershootPx:F1}," +
        $"{PathEfficiency:F3},{DwellDriftPx:F2},{ClosestApproachPx:F1},{PeakRadius:F3}," +
        $"{PeakSpeedPxPerSec:F0},{MinPressureFactor:F3}";
}

/// <summary>
/// エイムテストの進行と計測。
///
/// カーソルは実際の Windows のカーソルではなく、この中の仮想カーソルを動かす。理由は3つ:
/// テスト中に本物のカーソルが飛ばないこと、画面端の扱いを自分で決められること、的との距離が
/// 素直に手に入ること。<see cref="SubPixelAccumulator"/> を通すのは <see cref="MouseOutput"/> と
/// 同じで、そこを省くと「100px/秒未満が切り捨てられて動かない」という**実運用で一番効く性質**が
/// テストから消えてしまう。測っているのは設定の良し悪しであって、理想化された積分ではない。
///
/// 時間はサンプルのタイムスタンプで測る。壁時計を使わないのは、パイプラインの他の段と同じ理由
/// (記録を流し直しても同じ結果になる) に加えて、UIタイマーの分解能 (約15.6ms) が整定時間の
/// オーダーに対して無視できないため。
/// </summary>
public sealed class AimTestSession
{
    private readonly IReadOnlyList<AimTrialPlan> _plan;
    private readonly List<AimTrialResult> _results = [];
    private readonly SubPixelAccumulator _accumulator = new();

    private readonly double _widthPx;
    private readonly double _heightPx;

    private long _lastTimestampMs = -1;

    // --- 現在の試行の状態 ---
    private long _trialStartMs = -1;
    private double _firstTouchMs = -1;
    private double _dwellStartMs = -1;
    private int _reEntries;
    private bool _wasInside;
    private double _maxDistanceAfterTouch;
    private double _pathPx;
    private double _pathAtDwellStartPx;
    private double _driftSquaredSum;
    private int _driftCount;
    private double _closestPx = double.MaxValue;

    /// <summary>
    /// 試行の開始時点で、実際にカーソルが的からどれだけ離れていたか。
    ///
    /// 課題側の名目距離を使わないのは、前の試行が時間切れで終わるとカーソルが的に乗っていない
    /// ため。そのとき次の試行は名目より近い (または遠い) ところから始まっていて、名目で難易度を
    /// 計算するとスループットがそのぶん嘘になる。経路効率の分子も同じ理由でこちらを使う。
    /// </summary>
    private double _startDistancePx;
    private double _peakRadius;
    private double _peakSpeed;
    private double _minPressureFactor = 1.0;
    private double _lastCursorXPx;
    private double _lastCursorYPx;

    /// <summary>維持しなければならない時間 [ms]。</summary>
    public double DwellMs { get; }

    /// <summary>
    /// この時間で的に入って維持できなければ、失敗として次へ進む [ms]。
    ///
    /// 短めにしてあるのは、設定が外れているときの体験のため。うまくいっている試行は2〜3秒で
    /// 終わるので、8秒かかった時点でその試行はもう失敗として扱ってよい。長い打ち切りにすると、
    /// 外れた設定では24試行ぜんぶが打ち切りまで粘って、テストが数分の苦行になる。
    /// </summary>
    public double TimeoutMs { get; }

    public double CursorXPx { get; private set; }
    public double CursorYPx { get; private set; }

    public int TrialIndex { get; private set; }
    public int TrialCount => _plan.Count;
    public bool IsComplete => TrialIndex >= _plan.Count;
    public bool Aborted { get; private set; }

    /// <summary>
    /// 計測が始まっているか。
    ///
    /// 始まる前でもカーソルは動かす。開始の合図を待つあいだに「ボードが効いていること」と
    /// 「足を中立に戻したときにカーソルが止まること」を本人が確かめられるようにするため。
    /// 原点がずれたまま始めると全試行が遠回りになり、それは設定の問題として記録されてしまう。
    /// </summary>
    public bool Started { get; private set; }

    public AimTrialPlan Current => _plan[Math.Min(TrialIndex, _plan.Count - 1)];
    public IReadOnlyList<AimTrialResult> Results => _results;

    /// <summary>今、的の中にいるか。描画用。</summary>
    public bool InsideTarget { get; private set; }

    /// <summary>維持の進み具合 (0〜1)。描画用。</summary>
    public double DwellProgress { get; private set; }

    /// <summary>乗っていない / 荷重不足で操作できない状態か。描画用。</summary>
    public bool InputInactive { get; private set; }

    public AimTestSession(
        IReadOnlyList<AimTrialPlan> plan,
        double widthPx,
        double heightPx,
        double dwellMs = 1000,
        double timeoutMs = 8000)
    {
        _plan = plan;
        _widthPx = widthPx;
        _heightPx = heightPx;
        DwellMs = dwellMs;
        TimeoutMs = timeoutMs;
        CursorXPx = _lastCursorXPx = widthPx / 2;
        CursorYPx = _lastCursorYPx = heightPx / 2;
    }

    /// <summary>計測を始める。ここまでに動いたぶんは1試行目の経路に入れない。</summary>
    public void Start()
    {
        Started = true;
        ResetTrialState();
        _lastCursorXPx = CursorXPx;
        _lastCursorYPx = CursorYPx;
    }

    public void Abort()
    {
        Aborted = true;
        TrialIndex = _plan.Count;
    }

    /// <summary>
    /// サンプル1つぶん進める。サンプルスレッドから呼ぶ。
    ///
    /// 速度の積分と判定を同じ場所でやるのは、両方が同じ dt に乗っている必要があるため。
    /// 描画側のタイマーで判定すると、維持の1秒が「UIが何回描けたか」で測られてしまう。
    /// </summary>
    public void Feed(in PointerCommand command, long timestampMs)
    {
        if (IsComplete)
        {
            return;
        }

        double dt = _lastTimestampMs < 0 ? 0.01 : (timestampMs - _lastTimestampMs) / 1000.0;
        _lastTimestampMs = timestampMs;
        if (dt <= 0 || dt > 0.5)
        {
            // 記録の途切れや一時停止で飛んだぶんは、積分にも計測にも入れない。
            dt = 0;
        }

        InputInactive = !command.Active;

        // --- カーソルを動かす ---
        if (dt > 0 && command.Active)
        {
            var (dx, dy) = _accumulator.Accumulate(command.VelocityXPxPerSec, command.VelocityYPxPerSec, dt);
            if (dx != 0 || dy != 0)
            {
                CursorXPx = Math.Clamp(CursorXPx + dx, 0, _widthPx - 1);
                CursorYPx = Math.Clamp(CursorYPx + dy, 0, _heightPx - 1);
            }
        }
        else if (!command.Active)
        {
            _accumulator.Reset();
        }

        if (!Started)
        {
            // 開始前はカーソルを動かすだけ。原点の確認に使ってもらう。
            _lastCursorXPx = CursorXPx;
            _lastCursorYPx = CursorYPx;
            return;
        }

        if (_trialStartMs < 0)
        {
            _trialStartMs = timestampMs;
            _startDistancePx = _plan[TrialIndex].Target.DistanceFrom(CursorXPx, CursorYPx);
        }

        double moved = Math.Sqrt(
            Math.Pow(CursorXPx - _lastCursorXPx, 2) + Math.Pow(CursorYPx - _lastCursorYPx, 2));
        _pathPx += moved;
        _lastCursorXPx = CursorXPx;
        _lastCursorYPx = CursorYPx;

        double speed = Math.Sqrt(
            command.VelocityXPxPerSec * command.VelocityXPxPerSec +
            command.VelocityYPxPerSec * command.VelocityYPxPerSec);
        _peakSpeed = Math.Max(_peakSpeed, speed);
        _peakRadius = Math.Max(_peakRadius, command.NormalizedRadius);
        if (command.Active)
        {
            _minPressureFactor = Math.Min(_minPressureFactor, command.PressureFactor);
        }

        // --- 判定 ---
        var plan = _plan[TrialIndex];
        double elapsed = timestampMs - _trialStartMs;
        double distance = plan.Target.DistanceFrom(CursorXPx, CursorYPx);
        _closestPx = Math.Min(_closestPx, distance);
        bool inside = distance <= plan.Target.RadiusPx;
        InsideTarget = inside;

        if (inside)
        {
            if (_firstTouchMs < 0)
            {
                _firstTouchMs = elapsed;
                _maxDistanceAfterTouch = distance;
            }
            else if (!_wasInside)
            {
                // 一度出てから入り直した。これがオーバーシュートの回数そのもの。
                _reEntries++;
            }

            if (_dwellStartMs < 0)
            {
                _dwellStartMs = elapsed;
                _pathAtDwellStartPx = _pathPx;
                _driftSquaredSum = 0;
                _driftCount = 0;
            }
            _driftSquaredSum += distance * distance;
            _driftCount++;

            double held = elapsed - _dwellStartMs;
            DwellProgress = Math.Clamp(held / DwellMs, 0, 1);
            if (held >= DwellMs)
            {
                CompleteTrial(plan, elapsed, timedOut: false);
                return;
            }
        }
        else
        {
            // 的の外にいる。維持はやり直し。
            _dwellStartMs = -1;
            DwellProgress = 0;
            if (_firstTouchMs >= 0)
            {
                _maxDistanceAfterTouch = Math.Max(_maxDistanceAfterTouch, distance);
            }
        }
        _wasInside = inside;

        if (elapsed >= TimeoutMs)
        {
            CompleteTrial(plan, elapsed, timedOut: true);
        }
    }

    private void CompleteTrial(in AimTrialPlan plan, double elapsed, bool timedOut)
    {
        // 経路効率は「維持に入るまで」で測る。維持中の微小な揺れまで経路に入れると、長く
        // 止まっているほど効率が悪いことになって、意味が逆転する。
        double pathForResult = timedOut || _pathAtDwellStartPx <= 0 ? _pathPx : _pathAtDwellStartPx;
        double efficiency = pathForResult > 1 ? Math.Clamp(_startDistancePx / pathForResult, 0, 1) : 1;
        double drift = _driftCount > 0 ? Math.Sqrt(_driftSquaredSum / _driftCount) : 0;
        double overshoot = _firstTouchMs >= 0 ? Math.Max(0, _maxDistanceAfterTouch - plan.Target.RadiusPx) : 0;

        _results.Add(new AimTrialResult(
            Index: plan.Index,
            IsWarmup: plan.IsWarmup,
            TimedOut: timedOut,
            DistancePx: _startDistancePx,
            TargetDiameterPx: plan.Target.DiameterPx,
            DirectionDegrees: plan.DirectionDegrees,
            IndexOfDifficulty: Math.Log2(_startDistancePx / Math.Max(1.0, plan.Target.DiameterPx) + 1.0),
            FirstTouchMs: _firstTouchMs,
            SettleMs: _firstTouchMs >= 0 && _dwellStartMs >= 0 ? _dwellStartMs - _firstTouchMs : -1,
            CompletionMs: timedOut ? -1 : elapsed,
            ReEntries: _reEntries,
            MaxOvershootPx: overshoot,
            PathEfficiency: efficiency,
            DwellDriftPx: drift,
            ClosestApproachPx: _closestPx == double.MaxValue ? -1 : _closestPx,
            PeakRadius: _peakRadius,
            PeakSpeedPxPerSec: _peakSpeed,
            MinPressureFactor: _minPressureFactor));

        TrialIndex++;
        ResetTrialState();
    }

    private void ResetTrialState()
    {
        _trialStartMs = -1;
        _firstTouchMs = -1;
        _dwellStartMs = -1;
        _reEntries = 0;
        _wasInside = false;
        _maxDistanceAfterTouch = 0;
        _pathPx = 0;
        _pathAtDwellStartPx = 0;
        _driftSquaredSum = 0;
        _driftCount = 0;
        _closestPx = double.MaxValue;
        _startDistancePx = 0;
        _peakRadius = 0;
        _peakSpeed = 0;
        _minPressureFactor = 1.0;
        DwellProgress = 0;
        InsideTarget = false;
    }
}
