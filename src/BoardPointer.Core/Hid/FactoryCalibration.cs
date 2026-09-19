namespace BoardPointer.Core.Hid;

/// <summary>
/// ボード自身が持っている工場出荷時の較正テーブル。拡張機能レジスタ 0xA40024 から24バイトで読める
/// (Wiibrew のバランスボードの項に記載のレイアウト)。センサー1つにつき
/// 「0kgのときの生値 / 17kgのときの生値 / 34kgのときの生値」の3点が入っている:
///
///   0xA40024 + 0  : 0kg  の TR, BR, TL, BL (各2バイト big-endian)
///   0xA40024 + 8  : 17kg の TR, BR, TL, BL
///   0xA40024 + 16 : 34kg の TR, BR, TL, BL
///
/// WiiFitToVRC はこれを読まず、ゼロ点オフセットを引いた「各隅の割合」だけで足踏みを分類している。
/// 離散イベントの分類ならそれで足りるが、重心 (COP) を座標として使うなら話が別で、センサーごとの
/// ゲイン差がそのまま重心位置の歪みになる。実際あちらは弱いセンサーを経験的な補正係数で殴っている
/// (SensorCorrection.cs)。ここではボード自身が知っている正解を使って、素直に kg に直す。
///
/// 読めなかった場合 (ペアリング直後の取りこぼし、個体差、拡張レジスタの初期化失敗) は null のまま
/// 動く。そのときパイプラインは「生値のまま」モードに落ちるだけで、重心も割合ベースで一応出る。
/// </summary>
public sealed class FactoryCalibration
{
    /// <summary>拡張機能レジスタ上の較正テーブルの先頭。</summary>
    public const int RegisterAddress = 0xA40024;

    /// <summary>読み出すバイト数。0kg/17kg/34kg × 4センサー × 2バイト。</summary>
    public const int RegisterLength = 24;

    private readonly ushort[] _kg0 = new ushort[4];
    private readonly ushort[] _kg17 = new ushort[4];
    private readonly ushort[] _kg34 = new ushort[4];

    private FactoryCalibration(ReadOnlySpan<byte> raw)
    {
        for (int i = 0; i < 4; i++)
        {
            _kg0[i] = BigEndian(raw, 0 + i * 2);
            _kg17[i] = BigEndian(raw, 8 + i * 2);
            _kg34[i] = BigEndian(raw, 16 + i * 2);
        }
    }

    private static ushort BigEndian(ReadOnlySpan<byte> b, int offset) => (ushort)((b[offset] << 8) | b[offset + 1]);

    /// <summary>
    /// 24バイトの生データを解釈する。3点が単調増加していない個体/読み取りは信用できないので null を返す
    /// --- 中途半端に壊れたテーブルで kg 換算するより、生値モードに落ちたほうがまだ扱いやすい。
    /// </summary>
    public static FactoryCalibration? TryParse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < RegisterLength)
        {
            return null;
        }

        var calibration = new FactoryCalibration(raw);
        for (int i = 0; i < 4; i++)
        {
            if (calibration._kg0[i] >= calibration._kg17[i] || calibration._kg17[i] >= calibration._kg34[i])
            {
                return null;
            }
        }
        return calibration;
    }

    /// <summary>CSVヘッダに書き出すためのテキスト表現 (例 "1234,1210,1250,1198")。</summary>
    public string Format(int point) => string.Join(",", Points(point));

    private ushort[] Points(int point) => point switch
    {
        0 => _kg0,
        17 => _kg17,
        34 => _kg34,
        _ => throw new ArgumentOutOfRangeException(nameof(point)),
    };

    /// <summary>保存用の1行表現。CSVヘッダの calib= と同じ形。</summary>
    public string ToStorageString() => $"{Format(0)}|{Format(17)}|{Format(34)}";

    /// <summary><see cref="ToStorageString"/> の逆。</summary>
    public static FactoryCalibration? TryParseStorage(string text)
    {
        string[] points = text.Trim().Split('|');
        return points.Length == 3 ? TryParseHeader(points[0], points[1], points[2]) : null;
    }

    /// <summary>CSVヘッダから復元する。リプレイ時に記録当時と同じ kg 換算を再現するために要る。</summary>
    public static FactoryCalibration? TryParseHeader(string kg0, string kg17, string kg34)
    {
        var buffer = new byte[RegisterLength];
        if (!Fill(buffer, 0, kg0) || !Fill(buffer, 8, kg17) || !Fill(buffer, 16, kg34))
        {
            return null;
        }
        return TryParse(buffer);
    }

    private static bool Fill(byte[] buffer, int offset, string csv)
    {
        string[] parts = csv.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }
        for (int i = 0; i < 4; i++)
        {
            if (!ushort.TryParse(parts[i].Trim(), out ushort value))
            {
                return false;
            }
            buffer[offset + i * 2] = (byte)(value >> 8);
            buffer[offset + i * 2 + 1] = (byte)(value & 0xFF);
        }
        return true;
    }

    /// <summary>
    /// 生値を kg に直す。0-17kg と 17-34kg の2区間の線形補間で、34kg を超える分は上側の区間の傾きを
    /// そのまま伸ばす (体重80kgの人が片足に寄せれば1センサーで40kg超は普通に出るので、外挿は必須)。
    /// </summary>
    /// <param name="sensor">0=TR, 1=BR, 2=TL, 3=BL。<see cref="RawOrder"/> と同じ並び。</param>
    public double ToKilograms(int sensor, ushort raw)
    {
        double p0 = _kg0[sensor];
        double p17 = _kg17[sensor];
        double p34 = _kg34[sensor];

        return raw < p17
            ? 17.0 * (raw - p0) / (p17 - p0)
            : 17.0 + 17.0 * (raw - p17) / (p34 - p17);
    }

    /// <summary>
    /// kg から生値へ。合成データ生成 (<see cref="Sampling.SyntheticSource"/>) で「本物と同じ形の生CSV」を
    /// 作るために要る逆変換で、実機の経路では使わない。
    /// </summary>
    public ushort ToRaw(int sensor, double kilograms)
    {
        double p0 = _kg0[sensor];
        double p17 = _kg17[sensor];
        double p34 = _kg34[sensor];

        double raw = kilograms < 17.0
            ? p0 + (p17 - p0) * kilograms / 17.0
            : p17 + (p34 - p17) * (kilograms - 17.0) / 17.0;

        return (ushort)Math.Clamp(Math.Round(raw), 0, ushort.MaxValue);
    }

    /// <summary>
    /// 生値1カウントが何 kg に相当するか（4センサーの平均）。実測のボードで約 0.0097 kg
    /// （= 約 103.5 カウント/kg）。重心の分解能を出すのに使う。
    /// </summary>
    public double AverageKilogramsPerCount
    {
        get
        {
            double sum = 0;
            for (int i = 0; i < 4; i++)
            {
                // 0kg から 34kg までの傾きの逆数。区間で多少違うが、分解能の桁を知るには十分。
                sum += 34.0 / (_kg34[i] - _kg0[i]);
            }
            return sum / 4.0;
        }
    }

    /// <summary>センサーの並び。HIDレポート上の順序と、配列添字の対応。</summary>
    public static readonly string[] RawOrder = ["TopRight", "BottomRight", "TopLeft", "BottomLeft"];

    /// <summary>
    /// 合成データ用のダミー。原点は適当だが、感度は実測のボードに合わせてある
    /// (約103.5カウント/kg = 17kg あたり 1760 カウント)。合成データの荷重が実機と同じ桁で出ないと、
    /// kg で決めた閾値の確認にならないため。
    /// </summary>
    public static FactoryCalibration CreateNominal()
    {
        var buffer = new byte[RegisterLength];
        ushort[] zero = [1000, 1050, 980, 1020];
        const ushort per17Kg = 1760;
        for (int i = 0; i < 4; i++)
        {
            Write(buffer, 0 + i * 2, zero[i]);
            Write(buffer, 8 + i * 2, (ushort)(zero[i] + per17Kg));
            Write(buffer, 16 + i * 2, (ushort)(zero[i] + per17Kg * 2));
        }
        return TryParse(buffer)!;
    }

    private static void Write(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }
}
