namespace BoardPointer.Core.Pipeline;

/// <summary>
/// 人が乗っているかの判定。閾値と時間の両方でヒステリシスをかける。
///
/// 閾値を上下で分けるのは必須で、1つの値で両方向を見ると、合計荷重がその境界付近で震えたときに
/// オン側とオフ側のタイマーが毎サンプル互いにリセットし合って、どちらも規定時間に到達しなくなる。
/// WiiFitToVRC が着席モードで実測 139 回/セッションのちらつきを踏んで同じ結論に至っている
/// (docs/GESTURE_DETECTION.md)。
///
/// 時間のヒステリシスは用途が別で、オン側は「ボードに足が当たっただけで操作が始まる」のを防ぎ、
/// オフ側は「動作中の一瞬の荷重抜けで操作が切れる」のを防ぐ。
/// </summary>
public sealed class PresenceGate
{
    private bool _present;
    private long _aboveSinceMs = -1;
    private long _belowSinceMs = -1;

    public bool IsPresent => _present;

    public void Reset()
    {
        _present = false;
        _aboveSinceMs = -1;
        _belowSinceMs = -1;
    }

    public bool Update(double totalKg, long timestampMs, PipelineOptions options)
    {
        if (totalKg >= options.PresenceOnKg)
        {
            _belowSinceMs = -1;
            if (_aboveSinceMs < 0)
            {
                _aboveSinceMs = timestampMs;
            }
            if (!_present && timestampMs - _aboveSinceMs >= options.PresenceOnMs)
            {
                _present = true;
            }
        }
        else if (totalKg <= options.PresenceOffKg)
        {
            _aboveSinceMs = -1;
            if (_belowSinceMs < 0)
            {
                _belowSinceMs = timestampMs;
            }
            if (_present && timestampMs - _belowSinceMs >= options.PresenceOffMs)
            {
                _present = false;
            }
        }
        else
        {
            // 2つの閾値の間 (不感帯)。どちらのタイマーも進めず、今の状態を保つ。
            _aboveSinceMs = -1;
            _belowSinceMs = -1;
        }

        return _present;
    }
}
