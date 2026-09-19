using System.Collections.Concurrent;
using System.Text;
using BoardPointer.Core.Sampling;

namespace BoardPointer.Core.Recording;

/// <summary>
/// 生サンプルをCSVに落とす。書くのは生値だけで、kg も重心もフィルタ後の値も書かない --- 派生値を
/// 記録に混ぜると、パイプラインを直した瞬間に過去の記録が「古いパイプラインの出力」になって
/// 比較できなくなる。派生値が見たいときは Replay で毎回作り直す。
///
/// 実際の書き込みは専用スレッド。HID読みスレッドをディスクI/Oで待たせるとサンプルの間隔が乱れ、
/// それがそのままフィルタの dt に効いてしまう。
/// </summary>
public sealed class RawCsvWriter : IDisposable
{
    private readonly BlockingCollection<RawSample> _queue = new(new ConcurrentQueue<RawSample>());
    private readonly StreamWriter _writer;
    private readonly Thread _thread;

    public string Path { get; }
    public int SamplesWritten { get; private set; }

    public RawCsvWriter(string path, RecordingHeader header)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (string line in header.ToLines())
        {
            _writer.WriteLine(line);
        }

        _thread = new Thread(DrainLoop) { IsBackground = true, Name = "RawCsvWriter" };
        _thread.Start();
    }

    /// <summary>サンプルスレッドから呼ぶ。キューに積むだけで、ブロックしない。</summary>
    public void Write(RawSample sample)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(sample);
        }
    }

    private void DrainLoop()
    {
        foreach (var s in _queue.GetConsumingEnumerable())
        {
            _writer.WriteLine($"{s.TimestampMs},{s.TopRight},{s.BottomRight},{s.TopLeft},{s.BottomLeft}");
            SamplesWritten++;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _writer.Flush();
        _writer.Dispose();
        _queue.Dispose();
    }
}
