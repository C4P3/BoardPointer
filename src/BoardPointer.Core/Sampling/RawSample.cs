namespace BoardPointer.Core.Sampling;

/// <summary>
/// ボードが吐く生のまま、1サンプル分。4隅のひずみゲージの未較正の値と、記録開始からの経過ミリ秒。
///
/// ここに較正済みの値や重心を入れないのは意図的。記録するのは常にこの生データだけで、kg換算も
/// フィルタも重心計算もすべて後段の <see cref="Pipeline.BoardPipeline"/> でやる。そうしておけば、
/// パイプラインをいくら書き換えても過去の記録が無効にならない --- 同じCSVを新しいパイプラインに
/// 通し直せる。
/// </summary>
/// <param name="TimestampMs">記録/接続開始を0とした経過ミリ秒。</param>
/// <param name="TopRight">前右センサー。"Top" はボードの前側 (立ったときのつま先側)。</param>
/// <param name="BottomRight">後右センサー。</param>
/// <param name="TopLeft">前左センサー。</param>
/// <param name="BottomLeft">後左センサー。</param>
public readonly record struct RawSample(
    long TimestampMs,
    ushort TopRight,
    ushort BottomRight,
    ushort TopLeft,
    ushort BottomLeft);
