using System.Globalization;
using System.Text;
using BoardPointer.Core.Bluetooth;
using BoardPointer.Core.Hid;
using BoardPointer.Core.Mapping;
using BoardPointer.Core.Pipeline;
using BoardPointer.Core.Recording;
using BoardPointer.Core.Sampling;

namespace BoardPointer.Replay;

/// <summary>
/// 記録した生CSVを、今のパイプライン設定で流し直して統計を出すCLI。
///
/// これがこのスケルトンの中心にある道具。ボードに乗ってパラメータを触ると「さっきより良くなった
/// 気がする」以上のことが言えないが、同じ記録に対して設定違いを2回流せば差が数字で出る。
/// フィルタのつまみを決めるときは、必ず実機の記録に対してこれを回すこと。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            if (args[0] == "--mouse-selftest")
            {
                return RunMouseSelfTest();
            }
            if (args[0] == "--import-calib")
            {
                return RunImportCalibration(args);
            }
            if (args[0] == "--bluetooth-selftest")
            {
                return RunBluetoothSelfTest();
            }
            return args[0] == "--synth" ? RunSynth(args) : RunReplay(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            使い方:
              BoardPointer.Replay <生CSV> [オプション]      記録を流し直して統計を出す
              BoardPointer.Replay --synth <出力CSV> [...]   実機無しで試すための合成データを作る
              BoardPointer.Replay --mouse-selftest         カーソルが指示どおり動くかを1回試す (両モード)
              BoardPointer.Replay --import-calib <生CSV>   記録に入っている工場較正を保存して使い回す
              BoardPointer.Replay --bluetooth-selftest     Bluetooth ライブラリが読めるかを確認する

            オプション:
              --out <CSV>          各段の値 (kg, 重心, フィルタ後, 在席) を書き出す
              --min-cutoff <Hz>    1ユーロフィルタの最低カットオフ (既定 1.0)
              --beta <値>          1ユーロフィルタの速度応答 (既定 0.00005)
              --no-filter          フィルタを通さない (生の揺れを見る)
              --tare-seconds <秒>  先頭の何秒を無人とみなして荷重のゼロ点を取るか (既定 2.0)
              --no-tare            荷重のゼロ点補正をしない
              --seated             椅子に座って足先で操作する前提の既定値を使う
              --center-seconds <秒> 先頭の何秒の重心を中立姿勢とみなして原点にするか (既定 0=しない)
              --mouse              マッピングを通してカーソルの動きを検証する
              --deadzone <値>      デッドゾーン [正規化半径] (既定 0.18)
              --exponent <値>      応答曲線の指数 (既定 2.0)
              --max-speed <px/s>   フルスケールでの速度 (既定 900)
              --reach <前,後,左,右> 可動域 [mm] (既定 60,45,70,70)
              --pressure <モード>  荷重の使い方: off / clutch / throttle (既定 off)
              --pressure-ref <kg>  安静時の基準荷重。省略すると --center-seconds の測定値を使う
              --pressure-engage <比> 動き始める荷重比 (既定 0.97)
              --pressure-full <比>   全開になる荷重比 (既定 0.85。作動側より小さければ軽くする向き)
              --load-smoothing <ms>  合計荷重を均す時定数 (既定 100、0 でなし)
              --load-exponent <値>   荷重の応答曲線の指数 (既定 1.0)
              --load-at-engage <倍率> 作動側での速度の倍率 (既定 0)
              --load-at-full <倍率>   振り切り側での速度の倍率 (既定 1)
              --seconds <秒>       --synth のときの長さ (既定 30)
              --seed <整数>        --synth の乱数種 (既定 1)

            例:
              BoardPointer.Replay --synth debug/synth.csv
              BoardPointer.Replay debug/synth.csv --beta 0.0005 --out debug/derived.csv
              BoardPointer.Replay debug/session_*.csv --seated --no-tare --center-seconds 2
            """);
    }

    private static int RunMouseSelfTest()
    {
        Console.WriteLine("カーソルを少し動かして、元の位置に戻します。");

        // 絶対座標: 変換 (仮想デスクトップの原点とサイズ、65535 への正規化) の検算。
        // ここがずれるのはこちらのバグなので、合否を出すのはこの側だけ。
        var (rdx, rdy, adx, ady) = MouseOutput.SelfTest(PointerMode.Absolute);
        Console.WriteLine("  -- 送り先: デスクトップ (絶対座標) --");
        Console.WriteLine($"    指示 : dx {rdx} / dy {rdy}");
        Console.WriteLine($"    実測 : dx {adx} / dy {ady}");

        bool ok = Math.Abs(adx - rdx) <= 1 && Math.Abs(ady - rdy) <= 1;
        Console.WriteLine(ok
            ? "    OK。絶対座標への変換は合っています。"
            : "    [!] ずれています。仮想デスクトップの原点/サイズの扱いを疑ってください。");

        // 相対: こちらは検算ではなく実測。指示と実測の差は Windows 側の倍率と加速なので、
        // ずれていても「間違い」ではない。ゲームでの効き方を読むための数字として出す。
        var (rrdx, rrdy, radx, rady) = MouseOutput.SelfTest(PointerMode.Relative);
        var pointer = PointerSettings.Read();
        Console.WriteLine();
        Console.WriteLine("  -- 送り先: ゲーム (相対デルタ) --");
        Console.WriteLine($"    指示 : dx {rrdx} / dy {rrdy}");
        Console.WriteLine($"    実測 : dx {radx} / dy {rady}");
        Console.WriteLine($"    Windows 側 : 速度スライダー {pointer.SpeedSlider}/20、"
                        + $"ポインターの精度を高める {(pointer.EnhancePointerPrecision ? "入" : "切")}");
        Console.WriteLine("    指示と実測の差は Windows の倍率と加速です。Raw Input を読むゲーム");
        Console.WriteLine("    (相対モードで狙っている相手) には、この加速はかかりません。");

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 過去の記録に入っている工場較正を取り出して保存する。
    ///
    /// 工場較正はボードに焼かれた定数なので、一度でも読めた記録があればそれが正解。読み出しは
    /// 接続のたびに成否が揺れるが、保存しておけば以降は失敗しても精度を落とさずに済む。
    /// </summary>
    private static int RunImportCalibration(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("--import-calib には、較正が入っている生CSVのパスが要ります。");
            return 1;
        }

        var (header, _) = RawCsvReader.Read(args[1]);
        if (header.Calibration is null)
        {
            Console.Error.WriteLine($"{args[1]} には工場較正が入っていません。");
            return 1;
        }

        if (!CalibrationStore.TrySave(header.Calibration))
        {
            Console.Error.WriteLine($"保存できませんでした: {CalibrationStore.DefaultPath}");
            return 1;
        }

        Console.WriteLine($"工場較正を保存しました: {CalibrationStore.DefaultPath}");
        Console.WriteLine($"  {header.Calibration.ToStorageString()}");
        Console.WriteLine($"  感度 {1.0 / header.Calibration.AverageKilogramsPerCount:F1} カウント/kg");
        Console.WriteLine("Viewer は、接続時に読み出しが失敗したらこれを使います。");
        return 0;
    }

    /// <summary>
    /// 32feet.NET が読み込めるかだけを確かめる。
    ///
    /// これは配布形態の検査。あの DLL は .NET Framework 時代のアセンブリで、単一ファイル publish
    /// との相性が最も怪しい部分にあたる。読み込みに失敗しても、SYNC ペアリングを押すまで誰も
    /// 気づかない --- ボードを繋ぐ側は P/Invoke だけで動いてしまうため。先に潰しておく。
    /// </summary>
    private static int RunBluetoothSelfTest()
    {
        try
        {
            var outcome = BalanceBoardPairing.PairAndInstall(
                cancellationToken: new CancellationToken(canceled: true));

            // 即キャンセルなので Cancelled が返るのが正常。ここまで来れば DLL は読めている。
            Console.WriteLine(outcome.Result == PairingResult.Cancelled
                ? "OK。Bluetooth ライブラリ (32feet.NET) を読み込めています。"
                : $"読み込めましたが、想定外の結果でした: {outcome.Result} {outcome.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[!] Bluetooth ライブラリを読み込めません: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("    単一ファイル publish で InTheHand.Net.Personal.dll が落ちている可能性があります。");
            return 1;
        }
    }

    private static int RunSynth(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("--synth には出力先のCSVパスが要ります。");
            return 1;
        }

        string path = args[1];
        double seconds = GetDouble(args, "--seconds", 30.0);
        int seed = (int)GetDouble(args, "--seed", 1);

        var calibration = FactoryCalibration.CreateNominal();
        var samples = SyntheticGenerator.Generate(seconds, calibration, seed);
        var header = new RecordingHeader(
            DateTimeOffset.Now,
            "SyntheticGenerator",
            $"合成データ seconds={seconds} seed={seed} (実測ではない)",
            calibration);

        using (var writer = new RawCsvWriter(path, header))
        {
            foreach (var sample in samples)
            {
                writer.Write(sample);
            }
        }

        Console.WriteLine($"{path} に {samples.Count} サンプル ({seconds:F1}秒) を書き出しました。");
        return 0;
    }

    private static int RunReplay(string[] args)
    {
        string inputPath = args[0];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"ファイルがありません: {inputPath}");
            return 1;
        }

        var (header, samples) = RawCsvReader.Read(inputPath);
        if (samples.Count == 0)
        {
            Console.Error.WriteLine("サンプルが1つも読めませんでした。");
            return 1;
        }

        var options = args.Contains("--seated") ? PipelineOptions.SeatedFoot() : PipelineOptions.Standing();
        options.FilterEnabled = !args.Contains("--no-filter");
        options.TareEnabled = !args.Contains("--no-tare");
        options.MinCutoffHz = GetDouble(args, "--min-cutoff", options.MinCutoffHz);
        options.Beta = GetDouble(args, "--beta", options.Beta);
        double tareSeconds = GetDouble(args, "--tare-seconds", 2.0);
        double centerSeconds = GetDouble(args, "--center-seconds", 0.0);

        var pipeline = new BoardPipeline(options, header.Calibration);
        var frames = new List<BoardFrame>(samples.Count);

        if (options.TareEnabled)
        {
            pipeline.Tare.BeginSampling();
        }
        if (centerSeconds > 0)
        {
            pipeline.Centering.BeginSampling();
        }
        long startMs = samples[0].TimestampMs;
        foreach (var sample in samples)
        {
            if (options.TareEnabled
                && pipeline.Tare.IsSampling
                && sample.TimestampMs - startMs >= tareSeconds * 1000)
            {
                pipeline.Tare.FinishSampling();
            }
            if (pipeline.Centering.IsSampling && sample.TimestampMs - startMs >= centerSeconds * 1000)
            {
                pipeline.Centering.FinishSampling();
            }
            frames.Add(pipeline.Process(sample));
        }
        if (pipeline.Tare.IsSampling)
        {
            pipeline.Tare.FinishSampling(); // tare-seconds より短い記録
        }
        if (pipeline.Centering.IsSampling)
        {
            pipeline.Centering.FinishSampling();
        }

        PrintReport(inputPath, header, options, tareSeconds, pipeline, frames);

        if (args.Contains("--mouse"))
        {
            PrintMouseReport(args, frames, pipeline);
        }

        string? outPath = GetString(args, "--out");
        if (outPath is not null)
        {
            WriteDerived(outPath, frames);
            Console.WriteLine();
            Console.WriteLine($"各段の値を {outPath} に書き出しました。");
        }

        return 0;
    }

    private static void PrintReport(
        string path, RecordingHeader header, PipelineOptions options,
        double tareSeconds, BoardPipeline pipeline, List<BoardFrame> frames)
    {
        var intervals = new List<double>();
        for (int i = 1; i < frames.Count; i++)
        {
            intervals.Add(frames[i].TimestampMs - frames[i - 1].TimestampMs);
        }
        double medianInterval = Median(intervals);
        double durationSec = (frames[^1].TimestampMs - frames[0].TimestampMs) / 1000.0;
        int dropouts = intervals.Count(v => v > medianInterval * 3);

        Console.WriteLine($"== {Path.GetFileName(path)} ==");
        Console.WriteLine($"  記録日時       : {(header.RecordedAt == default ? "不明" : header.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        Console.WriteLine($"  ソース         : {header.Source}");
        if (!string.IsNullOrWhiteSpace(header.Note))
        {
            Console.WriteLine($"  メモ           : {header.Note}");
        }
        Console.WriteLine($"  較正           : {(header.Calibration is null ? "工場較正なし → 公称ゲイン (推定kg)" : "工場較正あり (kg)")}");
        if (header.Calibration is null)
        {
            Console.WriteLine("      公称 103.5 カウント/kg で換算。原点は荷重ゼロ点が与えるので、ゼロ点を");
            Console.WriteLine("      取っていない記録では荷重が確定せず、在席判定も重心も止まります。");
        }
        Console.WriteLine();

        Console.WriteLine("  -- 取り込み --");
        Console.WriteLine($"  サンプル数     : {frames.Count}  ({durationSec:F1} 秒)");
        Console.WriteLine($"  実効レート     : {frames.Count / Math.Max(durationSec, 1e-6):F1} Hz  (間隔の中央値 {medianInterval:F1} ms)");
        Console.WriteLine($"  間隔のばらつき : p95 {Percentile(intervals, 0.95):F1} ms / 最大 {(intervals.Count > 0 ? intervals.Max() : 0):F1} ms");
        Console.WriteLine($"  欠測らしき箇所 : {dropouts} 回 (中央値の3倍を超える間隔)");
        Console.WriteLine();

        Console.WriteLine("  -- 設定 --");
        Console.WriteLine($"  在席閾値       : オン {options.PresenceOnKg} / オフ {options.PresenceOffKg} kg");
        Console.WriteLine($"  重心の最低荷重 : {options.MinLoadForCopKg} kg");
        Console.WriteLine($"  荷重ゼロ点     : {(options.TareEnabled ? $"あり (先頭 {tareSeconds:F1} 秒)" : "なし")}");
        if (options.TareEnabled && pipeline.Tare.IsCalibrated)
        {
            var o = pipeline.Tare.Offsets;
            Console.WriteLine($"                   TR {o[0]:F2} / BR {o[1]:F2} / TL {o[2]:F2} / BL {o[3]:F2}");
        }
        Console.WriteLine($"  重心の原点     : {(pipeline.Centering.IsCalibrated ? $"X {pipeline.Centering.OriginXMm:F1} / Y {pipeline.Centering.OriginYMm:F1} mm" : "なし")}");
        Console.WriteLine($"  フィルタ       : {(options.FilterEnabled ? $"1ユーロ (min-cutoff {options.MinCutoffHz} Hz, beta {options.Beta})" : "なし")}");
        Console.WriteLine();

        // ここで拾いたい間違いは1つ。荷重のゼロ点を「足を載せたまま」測ってしまうと、足の重さごと
        // 差し引かれて合計荷重がほぼ0になり、重心は合計で割る比なので端まで振り切れる。
        // 症状は「なんか少しずれている」ではなく「重心が使いものにならない」なので、黙って進めない。
        if (options.TareEnabled && pipeline.Tare.IsCalibrated && !pipeline.CanCheckEmptyBoard)
        {
            Console.WriteLine("  [!] 工場較正が無いため、ゼロ点の測定中にボードが空だったかを判定できません。");
            Console.WriteLine($"      (測定中の合計は {pipeline.Tare.SampledTotalMedian:F0} カウント。生カウントには原点が無いので、");
            Console.WriteLine("      この数字からは空かどうかが分かりません。)");
            Console.WriteLine();
        }
        else if (options.TareEnabled && pipeline.Tare.IsCalibrated && pipeline.Tare.SampledTotalMedian > pipeline.EmptyBoardThresholdKg)
        {
            Console.WriteLine($"  [!] 荷重ゼロ点の測定中、合計 {pipeline.Tare.SampledTotalMedian:F1} kg が載っていました。");
            Console.WriteLine("      ボードが空ではなかったということで、この記録のゼロ点は信用できません。");
            Console.WriteLine("      --no-tare で測り直すか、足を載せたままの中立姿勢を消したいのなら");
            Console.WriteLine("      --center-seconds (重心の原点合わせ) を使ってください。");
            Console.WriteLine();
        }

        var present = frames.Where(f => f.Present).ToList();
        int transitions = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].Present != frames[i - 1].Present)
            {
                transitions++;
            }
        }

        Console.WriteLine("  -- 在席 --");
        Console.WriteLine($"  乗っていた割合 : {(frames.Count > 0 ? present.Count * 100.0 / frames.Count : 0):F1} %");
        Console.WriteLine($"  状態の切り替わり: {transitions} 回");
        Console.WriteLine();

        if (present.Count < 2)
        {
            Console.WriteLine("  乗っている区間が短すぎて、重心の統計は出せません。");
            return;
        }

        string unit = frames[0].UsesFactoryCalibration ? "kg" : "推定kg";
        int invalid = present.Count(f => !f.CopValid);
        double halfWidth = options.BoardWidthMm / 2.0;
        int railed = present.Count(f => Math.Abs(f.CopXMm + pipeline.Centering.OriginXMm) >= halfWidth - 0.01);

        Console.WriteLine("  -- 乗っている間 --");
        if (invalid > 0 || railed > 0)
        {
            Console.WriteLine($"  [!] 重心が信用できないサンプル : {invalid} 個 (荷重不足) / 端に張り付き {railed} 個");
        }
        Console.WriteLine($"  合計荷重       : 平均 {present.Average(f => f.TotalKg):F1} / 最小 {present.Min(f => f.TotalKg):F1} / 最大 {present.Max(f => f.TotalKg):F1} {unit}");
        Console.WriteLine($"  重心 X の範囲  : {present.Min(f => f.CopXMm):F1} 〜 {present.Max(f => f.CopXMm):F1} mm");
        Console.WriteLine($"  重心 Y の範囲  : {present.Min(f => f.CopYMm):F1} 〜 {present.Max(f => f.CopYMm):F1} mm");

        // 生値は整数なので重心も飛び飛びになる。その刻み幅は合計荷重に反比例するので、
        // 「値が離散的に見える」ときに疑うべきは分解能ではなく合計荷重のほう。
        var resolutions = present.Select(f => f.CopResolutionMm).Where(double.IsFinite).ToList();
        if (resolutions.Count > 0)
        {
            double medianRes = Median(resolutions);
            double worstRes = resolutions.Max();
            Console.WriteLine($"  重心の分解能   : 中央 {medianRes:F2} / 最悪 {worstRes:F2} mm/count  (1カウントで重心が動く距離)");
            if (medianRes > 1.0)
            {
                Console.WriteLine($"  [!] 重心が {medianRes:F1}mm 刻みでしか動けていません。合計荷重が小さすぎます。");
                Console.WriteLine("      生値が整数であること自体ではなく、その整数を小さな合計で割っていることが原因です。");
            }
        }
        Console.WriteLine();

        // 重心の移動速度。静止しているつもりの区間でも0にはならず、その残りがそのまま
        // 「カーソルが勝手に震える量」になる。フィルタの効きはここで見るのが一番わかりやすい。
        var rawSpeeds = new List<double>();
        var filteredSpeeds = new List<double>();
        var deviations = new List<double>();
        for (int i = 1; i < frames.Count; i++)
        {
            if (!frames[i].Present || !frames[i - 1].Present)
            {
                continue;
            }
            double dt = (frames[i].TimestampMs - frames[i - 1].TimestampMs) / 1000.0;
            if (dt <= 0)
            {
                continue;
            }
            rawSpeeds.Add(Distance(frames[i].CopXMm, frames[i].CopYMm, frames[i - 1].CopXMm, frames[i - 1].CopYMm) / dt);
            filteredSpeeds.Add(Distance(frames[i].CopXFilteredMm, frames[i].CopYFilteredMm, frames[i - 1].CopXFilteredMm, frames[i - 1].CopYFilteredMm) / dt);
            deviations.Add(Distance(frames[i].CopXMm, frames[i].CopYMm, frames[i].CopXFilteredMm, frames[i].CopYFilteredMm));
        }

        Console.WriteLine("  -- フィルタの効き --");
        Console.WriteLine($"  重心速度の中央値 : 生 {Median(rawSpeeds):F1} → フィルタ後 {Median(filteredSpeeds):F1} mm/s");
        Console.WriteLine($"                     (小さいほど静止時に震えない)");
        Console.WriteLine($"  生との距離       : 平均 {(deviations.Count > 0 ? deviations.Average() : 0):F2} / p95 {Percentile(deviations, 0.95):F2} mm");
        Console.WriteLine($"                     (大きいほど遅れている。震えの少なさとの取引)");
    }

    /// <summary>
    /// マッピングを記録に対して流して、オフラインで確かめられることだけを出す。
    ///
    /// レート制御は閉ループなので、記録の中の足の動きは「そのとき見えていたカーソル」に反応した
    /// 結果であって、別の設定で流し直しても同じようには動かない。つまり操作感そのものはここでは
    /// 詰められない。それでも以下は記録から確かめられる:
    ///
    ///   - 静かにしているつもりの時間に、カーソルが動いてしまっていないか (デッドゾーンの広さ)
    ///   - 最大速度に届いているか (可動域と曲線が噛み合っているか)
    ///   - 速度が飛んでいないか (曲線が連続か)
    ///   - 端数の持ち越しが効いているか (低速域が死んでいないか)
    /// </summary>
    private static void PrintMouseReport(string[] args, List<BoardFrame> frames, BoardPipeline pipeline)
    {
        var mapper = new PointerMapper();
        mapper.Pressure = GetString(args, "--pressure") switch
        {
            "clutch" => PressureMode.Clutch,
            "throttle" => PressureMode.Throttle,
            _ => PressureMode.Off,
        };
        // 基準は重心の原点を測ったとき (力を抜いた姿勢) の荷重。--center-seconds で測れている。
        mapper.ReferenceLoadKg = GetDouble(args, "--pressure-ref", pipeline.Centering.RestingLoadKg);
        mapper.PressureEngageRatio = GetDouble(args, "--pressure-engage", mapper.PressureEngageRatio);
        mapper.PressureFullRatio = GetDouble(args, "--pressure-full", mapper.PressureFullRatio);
        mapper.LoadSmoothingMs = GetDouble(args, "--load-smoothing", mapper.LoadSmoothingMs);
        mapper.LoadExponent = GetDouble(args, "--load-exponent", mapper.LoadExponent);
        mapper.LoadFactorAtEngage = GetDouble(args, "--load-at-engage", mapper.LoadFactorAtEngage);
        mapper.LoadFactorAtFull = GetDouble(args, "--load-at-full", mapper.LoadFactorAtFull);
        mapper.Curve.Deadzone = GetDouble(args, "--deadzone", mapper.Curve.Deadzone);
        mapper.Curve.Exponent = GetDouble(args, "--exponent", mapper.Curve.Exponent);
        mapper.Curve.MaxSpeedPxPerSec = GetDouble(args, "--max-speed", mapper.Curve.MaxSpeedPxPerSec);

        string? reach = GetString(args, "--reach");
        if (reach is not null)
        {
            string[] parts = reach.Split(',');
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double front)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double leftMm)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double rightMm))
            {
                mapper.Reach.FrontMm = front;
                mapper.Reach.BackMm = back;
                mapper.Reach.LeftMm = leftMm;
                mapper.Reach.RightMm = rightMm;
            }
        }

        var accumulator = new SubPixelAccumulator();
        var speeds = new List<double>();
        var ratios = new List<double>();
        var factors = new List<double>();
        double travelPx = 0;
        double emittedPx = 0;
        int active = 0, inDeadzone = 0, moving = 0, pressureBlocked = 0;
        long previousTimestamp = -1;

        foreach (var frame in frames)
        {
            var command = mapper.Update(frame);
            double dt = previousTimestamp < 0 ? 0.01 : (frame.TimestampMs - previousTimestamp) / 1000.0;
            previousTimestamp = frame.TimestampMs;

            if (!command.Active)
            {
                accumulator.Reset();
                continue;
            }

            active++;
            double speed = Math.Sqrt(
                command.VelocityXPxPerSec * command.VelocityXPxPerSec +
                command.VelocityYPxPerSec * command.VelocityYPxPerSec);
            speeds.Add(speed);
            ratios.Add(command.PressureRatio);
            factors.Add(command.PressureFactor);

            if (command.InDeadzone)
            {
                inDeadzone++;
            }
            else if (command.Engaged)
            {
                moving++;
            }
            else
            {
                pressureBlocked++;
            }

            travelPx += speed * dt;
            var (dx, dy) = accumulator.Accumulate(command.VelocityXPxPerSec, command.VelocityYPxPerSec, dt);
            // 経路長と比べるので、送出側もユークリッド距離で数える。|dx|+|dy| で数えると斜めのぶん
            // 必ず経路長より大きくなり、取りこぼしの検査にならない。
            emittedPx += Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        Console.WriteLine();
        Console.WriteLine("  == マウスマッピング ==");
        Console.WriteLine($"  可動域         : 前 {mapper.Reach.FrontMm:F0} / 後 {mapper.Reach.BackMm:F0} / 左 {mapper.Reach.LeftMm:F0} / 右 {mapper.Reach.RightMm:F0} mm");
        Console.WriteLine($"  曲線           : デッドゾーン {mapper.Curve.Deadzone:F2} / 指数 {mapper.Curve.Exponent:F2} / 最大 {mapper.Curve.MaxSpeedPxPerSec:F0} px/s");
        Console.WriteLine($"  荷重モード     : {mapper.Pressure}"
                        + (mapper.Pressure == PressureMode.Off
                            ? string.Empty
                            : mapper.PressureIsAvailable
                                ? $" (基準 {mapper.ReferenceLoadKg:F1} kg / 平滑 {mapper.LoadSmoothingMs:F0}ms / 作動 {mapper.PressureEngageRatio:F2} → 全開 {mapper.PressureFullRatio:F2} 倍"
                                  + $" = {(mapper.PressureEngagesWhenLighter ? "軽くする" : "踏み込む")}向き)"
                                : " [!] 基準荷重が無いため無効 (--center-seconds か --pressure-ref が要る)"));
        Console.WriteLine();

        if (active == 0)
        {
            Console.WriteLine("  出力できる区間がありませんでした (乗っていない / 荷重不足)。");
            return;
        }

        // デッドゾーンは正規化半径なので、実寸は方向ごとに変わる。無反応な範囲が体感どのくらいかは
        // mm で見ないと判断できない。
        double dead = mapper.Curve.Deadzone;
        Console.WriteLine($"  無反応な範囲   : 前 {mapper.Reach.FrontMm * dead:F0} / 後 {mapper.Reach.BackMm * dead:F0} "
                        + $"/ 左 {mapper.Reach.LeftMm * dead:F0} / 右 {mapper.Reach.RightMm * dead:F0} mm");
        Console.WriteLine($"  デッドゾーン内 : {inDeadzone * 100.0 / active:F1} %  (カーソルが完全に止まっていた割合)");
        Console.WriteLine($"  動いていた     : {moving * 100.0 / active:F1} %");
        if (mapper.Pressure != PressureMode.Off && mapper.PressureIsAvailable)
        {
            Console.WriteLine($"  荷重待ち       : {pressureBlocked * 100.0 / active:F1} %  (デッドゾーン外だが荷重が閾値に届いていない)");
            // 閾値が到達可能かは、その記録で実際に出ていた比を見ないと判断できない。
            // 倍率が毎サンプル跳ねると、曲線グラフの縦方向が暴れ、カーソルの速度もばたつく。
            var factorSteps = new List<double>();
            for (int i = 1; i < factors.Count; i++)
            {
                factorSteps.Add(Math.Abs(factors[i] - factors[i - 1]));
            }
            Console.WriteLine($"  倍率のばたつき : 中央 {Median(factorSteps):F4} / p95 {Percentile(factorSteps, 0.95):F4} (1サンプルあたりの倍率の変化)");
            Console.WriteLine($"  荷重比の分布   : 最小 {ratios.Min():F2} / p5 {Percentile(ratios, 0.05):F2} / 中央 {Median(ratios):F2} / p95 {Percentile(ratios, 0.95):F2} / 最大 {ratios.Max():F2} 倍");
            bool reached = mapper.PressureEngagesWhenLighter
                ? ratios.Min() <= mapper.PressureEngageRatio
                : ratios.Max() >= mapper.PressureEngageRatio;
            if (!reached)
            {
                Console.WriteLine(mapper.PressureEngagesWhenLighter
                    ? $"  [!] 作動閾値 {mapper.PressureEngageRatio:F2} 倍に一度も届いていません。閾値を {ratios.Min():F2} 倍より上げてください。"
                    : $"  [!] 作動閾値 {mapper.PressureEngageRatio:F2} 倍に一度も届いていません。閾値を {ratios.Max():F2} 倍より下げてください。");
            }
        }
        Console.WriteLine($"  カーソル移動   : 経路 {travelPx:F0} px / 実際に送出 {emittedPx:F0} px");
        Console.WriteLine($"  速度           : 中央 {Median(speeds):F0} / p95 {Percentile(speeds, 0.95):F0} / 最大 {speeds.Max():F0} px/s");
        Console.WriteLine($"  最大速度への到達: {speeds.Max() / mapper.Curve.MaxSpeedPxPerSec * 100:F0} %");
        Console.WriteLine();

        // 端数の持ち越しが無いと、1サンプルあたり1pxに満たない速度が全部切り捨てられて低速域が死ぬ。
        // 経路長と送出量がほぼ一致していれば、その取りこぼしは起きていない。整数への丸めで数%は
        // ずれる (斜めの1pxは経路より長い) ので、一致の判定は緩めでよい。
        double lossPercent = travelPx > 1 ? Math.Abs(travelPx - emittedPx) / travelPx * 100 : 0;
        Console.WriteLine(lossPercent < 15
            ? $"  端数の持ち越し : 効いています (経路と送出の差 {lossPercent:F1} %)"
            : $"  [!] 経路 {travelPx:F0} px に対し送出 {emittedPx:F0} px ({lossPercent:F0} % のずれ)。低速域を取りこぼしている可能性があります。");

        if (inDeadzone == 0)
        {
            Console.WriteLine("  [!] 一度もデッドゾーンに入っていません。静止してもカーソルが止まらない状態です。");
        }
        if (speeds.Max() < mapper.Curve.MaxSpeedPxPerSec * 0.5)
        {
            Console.WriteLine("  [!] 最大速度の半分にも届いていません。可動域が広すぎるか、指数がきつすぎます。");
        }
    }

    private static void WriteDerived(string path, List<BoardFrame> frames)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine(BoardFrame.CsvHeader);
        foreach (var frame in frames)
        {
            writer.WriteLine(frame.ToCsv());
        }
    }

    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int index = Math.Clamp((int)Math.Round((sorted.Length - 1) * p), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string? GetString(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double GetDouble(string[] args, string name, double fallback)
    {
        string? raw = GetString(args, name);
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }
}
