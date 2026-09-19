using BoardPointer.Core.Hid;
using BoardPointer.Core.Sampling;

namespace BoardPointer.Core.Pipeline;

/// <summary>
/// 生サンプル → 工場較正 (kg) → ゼロ点補正 → 重心 → 1ユーロフィルタ → 在席判定、を1本に繋いだもの。
///
/// 全段がサンプル列だけに依存し、壁時計にも実際の経過時間にも一切触らない。だから同じCSVを同じ
/// オプションで流せば、ライブ実行と1ビットまで同じ結果が出る --- ボードに乗らずにチューニングを
/// 回せるのはこの性質のおかげなので、あとから段を足すときもここを壊さないこと。
///
/// マッピング (重心 → カーソル速度) はここには無く、Mapping/ 以下が担当する。この段の責任は
/// 「信頼できる重心が毎フレーム出てくる」ところまで。
/// </summary>
public sealed class BoardPipeline
{
    private readonly OneEuroFilter _filterX = new();
    private readonly OneEuroFilter _filterY = new();
    private readonly double[] _corners = new double[4];
    private long _lastTimestampMs = -1;
    private double _lastAbsoluteCopX;
    private double _lastAbsoluteCopY;

    public PipelineOptions Options { get; }
    public TareStage Tare { get; } = new();
    public CopCentering Centering { get; } = new();
    public PresenceGate Presence { get; } = new();

    /// <summary>工場較正。null なら生カウントのまま動く。</summary>
    public FactoryCalibration? Calibration { get; set; }

    /// <summary>ボードから読んだ工場較正を使っているか。false なら公称ゲインによる推定。</summary>
    public bool UsesFactoryCalibration => Calibration is not null;

    /// <summary>
    /// ゼロ点の測定中にボードが空だったかを検算できるか。工場較正が読めているときだけ可能。
    ///
    /// 公称ゲインのモードには原点が無い。誰も乗っていないボードの生値の合計は実測で6万カウント
    /// 前後あり、そこを 0 と見なしてよいかは、まさにこれから測るゼロ点が決めることなので、
    /// 測る前に「空だったか」を判定する術がない。判定できないときは、できないと言うのが正しい。
    /// </summary>
    public bool CanCheckEmptyBoard => UsesFactoryCalibration;

    /// <summary>ゼロ点の測定中に「ボードが空ではなかった」と判断する閾値 [kg]。</summary>
    public double EmptyBoardThresholdKg => 3.0;

    public BoardPipeline(PipelineOptions? options = null, FactoryCalibration? calibration = null)
    {
        Options = options ?? new PipelineOptions();
        Calibration = calibration;
    }

    /// <summary>ソースを切り替えるとき (別のCSVを再生し直すなど) に、段の内部状態を全部落とす。</summary>
    public void Reset()
    {
        _filterX.Reset();
        _filterY.Reset();
        Presence.Reset();
        _lastTimestampMs = -1;
        _lastAbsoluteCopX = 0;
        _lastAbsoluteCopY = 0;
    }

    public BoardFrame Process(RawSample raw)
    {
        // 1. 生値 → 荷重。工場較正があれば kg、無ければ生カウントのまま次の段へ。
        ushort[] rawCorners = [raw.TopRight, raw.BottomRight, raw.TopLeft, raw.BottomLeft];
        bool factory = Calibration is not null;
        for (int i = 0; i < 4; i++)
        {
            _corners[i] = factory ? Calibration!.ToKilograms(i, rawCorners[i]) : rawCorners[i];
        }

        // 2. ゼロ点。サンプリング中は補正前の値を食わせる。
        Tare.Feed(_corners);
        double tr = Options.TareEnabled ? Tare.Apply(0, _corners[0]) : _corners[0];
        double br = Options.TareEnabled ? Tare.Apply(1, _corners[1]) : _corners[1];
        double tl = Options.TareEnabled ? Tare.Apply(2, _corners[2]) : _corners[2];
        double bl = Options.TareEnabled ? Tare.Apply(3, _corners[3]) : _corners[3];

        // 2b. 工場較正が無いときは、ここで公称ゲインを掛けて kg に直す。
        //
        //     ゼロ点を引いた「あと」に割るのが肝。工場較正の値のうち原点は個体ごとにまるで違って
        //     推定できないが、原点は荷重ゼロ点が消してくれる。残る傾きはボードの機構で決まるので、
        //     公称値 (103.5 カウント/kg) で十分実用になる。
        //
        //     これをやらないと、kg で決めた閾値が全部意味を失う。実害は在席判定で、生カウントの
        //     合計 (安静時で数千) は在席の閾値 (2.5kg) を常に超えるため「降りた」が一生成立せず、
        //     足を外した瞬間に重心が暴れてもカーソル出力が止まらない。
        double scale = factory ? 1.0 : 1.0 / Math.Max(1e-6, Options.NominalCountsPerKg);
        if (!factory)
        {
            tr *= scale;
            br *= scale;
            tl *= scale;
            bl *= scale;
        }
        double total = tr + br + tl + bl;

        // 工場較正があれば原点を知っているので、ゼロ点を取っていなくても荷重は意味を持つ。
        // 公称ゲイン側は原点をゼロ点に依存しているので、取っていなければ荷重は信用できない。
        bool loadIsCalibrated = factory || (Options.TareEnabled && Tare.IsCalibrated);

        // 3. 重心。各隅の荷重の按分で、センサー間距離の半分をかける。
        //
        //    合計で割る比なので、合計が小さいほどノイズが拡大される。閾値を下回っている間は
        //    更新せず直前の値を保持する --- ここを素通りさせると、荷重が抜けた瞬間に重心が
        //    ボードの端まで飛ぶ (実測で ±216.5mm ちょうど、つまり振り切り) 。
        bool copValid = loadIsCalibrated && total >= Options.MinLoadForCopKg;
        if (copValid)
        {
            _lastAbsoluteCopX = (Options.BoardWidthMm / 2.0) * ((tr + br) - (tl + bl)) / total;
            _lastAbsoluteCopY = (Options.BoardLengthMm / 2.0) * ((tl + tr) - (bl + br)) / total;
        }
        double absoluteCopX = _lastAbsoluteCopX;
        double absoluteCopY = _lastAbsoluteCopY;

        // 4. 中立姿勢を原点に移す。荷重のゼロ点とは別物で、こちらは操作する姿勢のまま測る。
        Centering.Feed(absoluteCopX, absoluteCopY, total, copValid);
        double copX = absoluteCopX - Centering.OriginXMm;
        double copY = absoluteCopY - Centering.OriginYMm;

        // 5. 重心の分解能。生値は整数なので、重心も飛び飛びの値しか取れない。その刻み幅は
        //    「1カウントぶんの荷重 ÷ 合計荷重 × 半幅」で、合計に反比例する。
        //    整数であること自体は問題ではない (実測のボードで 1 count ≒ 9.7g、15kg なら
        //    0.14mm 刻み = ノイズの 1/8)。効いてくるのは合計が小さいときで、合計 0.2kg まで
        //    落ちると 1 カウントで 10mm 飛ぶ。荷重ゼロ点を足を載せたまま取ってしまったときに
        //    重心が飛び飛びに見えるのは、これが原因。
        double quantum = factory ? Calibration!.AverageKilogramsPerCount : scale;
        double copResolutionMm = total > 1e-9
            ? (Options.BoardWidthMm / 2.0) * quantum / total
            : double.PositiveInfinity;

        // 6. フィルタ。dt はサンプルのタイムスタンプ差から出す (壁時計ではない)。
        double dt = _lastTimestampMs < 0 ? 0.01 : (raw.TimestampMs - _lastTimestampMs) / 1000.0;
        _lastTimestampMs = raw.TimestampMs;

        double filteredX = copX, filteredY = copY;
        if (Options.FilterEnabled)
        {
            _filterX.MinCutoffHz = _filterY.MinCutoffHz = Options.MinCutoffHz;
            _filterX.Beta = _filterY.Beta = Options.Beta;
            _filterX.DerivativeCutoffHz = _filterY.DerivativeCutoffHz = Options.DerivativeCutoffHz;
            filteredX = _filterX.Filter(copX, dt);
            filteredY = _filterY.Filter(copY, dt);
        }

        // 7. 在席判定。
        bool present = loadIsCalibrated && Presence.Update(total, raw.TimestampMs, Options);

        return new BoardFrame(
            TimestampMs: raw.TimestampMs,
            Raw: raw,
            TopRightKg: tr,
            BottomRightKg: br,
            TopLeftKg: tl,
            BottomLeftKg: bl,
            TotalKg: total,
            CopXMm: copX,
            CopYMm: copY,
            CopXFilteredMm: filteredX,
            CopYFilteredMm: filteredY,
            CopResolutionMm: copResolutionMm,
            Present: present,
            CopValid: copValid,
            LoadIsCalibrated: loadIsCalibrated,
            UsesFactoryCalibration: factory);
    }
}
