namespace BoardPointer.Core.Mapping;

/// <summary>
/// 速度 [px/秒] を、整数ピクセルの移動量に変換する。端数は次回に持ち越す。
///
/// これが無いと低速域が丸ごと死ぬ。100Hz で送るなら、1サンプルあたり 1px に満たない速度 ---
/// つまり 100 px/秒 未満 --- は毎回切り捨てられて、永久に 0 のままになる。「ゆっくり動かしたい
/// ときだけカーソルが反応しない」という、原因の分かりにくい症状になるので、端数の持ち越しは必須。
///
/// 切り捨ては 0 方向へ (Truncate)。Floor だと負の速度で余計に 1px 進んでしまい、左右で挙動が
/// 変わる。
/// </summary>
public sealed class SubPixelAccumulator
{
    private double _x;
    private double _y;

    /// <summary>持ち越し中の端数。デバッグ表示用。</summary>
    public double PendingX => _x;
    public double PendingY => _y;

    public void Reset()
    {
        _x = 0;
        _y = 0;
    }

    /// <returns>今回送るべき整数の移動量。両方 0 なら何も送らなくてよい。</returns>
    public (int Dx, int Dy) Accumulate(double velocityXPxPerSec, double velocityYPxPerSec, double dtSeconds)
    {
        if (dtSeconds <= 0 || double.IsNaN(dtSeconds))
        {
            return (0, 0);
        }

        _x += velocityXPxPerSec * dtSeconds;
        _y += velocityYPxPerSec * dtSeconds;

        int dx = (int)Math.Truncate(_x);
        int dy = (int)Math.Truncate(_y);
        _x -= dx;
        _y -= dy;
        return (dx, dy);
    }
}
