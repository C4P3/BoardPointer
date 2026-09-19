namespace BoardPointer.Viewer;

internal static class Program
{
    /// <summary>
    /// 引数なしで普通に起動。実機が無い場所で動作を見たいときは --synthetic、記録を流し直したい
    /// ときは --replay &lt;CSV&gt; を付けると、起動直後からソースが繋がった状態になる。
    /// --seated は座り・足先モードで起動する。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? replayPath = null;
        int index = Array.IndexOf(args, "--replay");
        if (index >= 0 && index + 1 < args.Length)
        {
            replayPath = args[index + 1];
        }

        Application.Run(new MainForm(args.Contains("--synthetic"), replayPath, args.Contains("--seated")));
    }
}
