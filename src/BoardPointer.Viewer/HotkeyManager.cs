using System.Runtime.InteropServices;
using BoardPointer.Core.Settings;

namespace BoardPointer.Viewer;

/// <summary>
/// グローバルショートカットの登録をまとめて面倒みる。
///
/// グローバルである必要があるのは、マウス出力中はこのアプリの窓のボタンを押しに行くのが難しく
/// なるため。特に「出力を止める」は、フォーカスがどこにあっても効かないと詰む。
///
/// 割り当ての変更中は <see cref="Suspend"/> で一時的に全部外す。外さないと、変更しようとして
/// 押したキーが WM_HOTKEY として横取りされ、キー入力としては届かない。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModNoRepeat = 0x4000;
    private const int BaseId = 0xB000;

    private readonly IntPtr _handle;
    private readonly List<HotkeyAction> _registered = [];
    private bool _suspended;

    /// <summary>登録に失敗した動作。他のアプリが同じキーを押さえている場合など。</summary>
    public List<HotkeyAction> Failed { get; } = [];

    public HotkeyManager(IntPtr handle) => _handle = handle;

    public void Register(AppSettings settings)
    {
        Unregister();
        if (_suspended)
        {
            return;
        }

        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            var binding = settings.For(action);
            if (!binding.IsAssigned)
            {
                continue;
            }

            if (RegisterHotKey(_handle, BaseId + (int)action, binding.Modifiers | ModNoRepeat, binding.VirtualKey))
            {
                _registered.Add(action);
            }
            else
            {
                Failed.Add(action);
            }
        }
    }

    public void Unregister()
    {
        foreach (var action in _registered)
        {
            UnregisterHotKey(_handle, BaseId + (int)action);
        }
        _registered.Clear();
        Failed.Clear();
    }

    /// <summary>キー割り当ての変更中だけ外す。戻すときは <see cref="Register"/> を呼び直す。</summary>
    public void Suspend()
    {
        _suspended = true;
        Unregister();
    }

    public void Resume(AppSettings settings)
    {
        _suspended = false;
        Register(settings);
    }

    /// <summary>WndProc から呼ぶ。自分宛てなら動作を返す。</summary>
    public HotkeyAction? Match(ref Message message)
    {
        if (message.Msg != WmHotkey)
        {
            return null;
        }
        int id = message.WParam.ToInt32() - BaseId;
        return Enum.IsDefined(typeof(HotkeyAction), id) ? (HotkeyAction)id : null;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
