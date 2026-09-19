using System.Text.RegularExpressions;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BoardPointer.Core.Bluetooth;

public enum PairingResult
{
    Success,
    Cancelled,
    Error,
}

public record PairingOutcome(PairingResult Result, string? DeviceAddress, string? Message);

/// <summary>
/// SYNC ボタンで見えている状態のボードをペアリングする。WiiFitToVRC (MIT) の
/// Bluetooth/BalanceBoardPairing.cs と docs/BALANCE_BOARD.md の手順をそのまま踏襲している。
///
/// 非自明なのは「ボードは PIN を使わない」こと。Windows の標準的な PIN/パスキー認証API
/// (BluetoothAuthenticateDevice 系) を素直に呼ぶと、失敗するかハングする。正解は、SYNC 中に
/// discover できたデバイスに対して HID サービスを有効化するだけ。それだけで Windows が HID
/// デバイスとして入れてくれる。
///
/// そして、一度ペアリングしても次回は SYNC が要る。この手口は本物の Bluetooth 認証を一切
/// 行わないため、Windows に残るプロファイルが Authenticated=false になり、OS 側のバックグラウンド
/// 自動再接続が効かない。あちらのプロジェクトが BluetoothMonitor で計測して確認済みなので、
/// 「電源を入れれば繋がる」方向に時間を使わないこと。
/// </summary>
public static class BalanceBoardPairing
{
    /// <summary>
    /// ボードの型番表記のゆれ (ハード改版・色・地域で変わる) をまとめて拾う正規表現。
    /// 例: "RVL-021-JPN", "RVL-A-BC-JPN-1", "(CW)RVL-A-BC-JPN", "RVL-A-BC(JPN)"
    /// </summary>
    private static readonly Regex BalanceBoardModelRegex = new(
        @"(\([A-Z]{1,4}\))?RVL-([0-9]{3}|[A-Z]-BC)((-[A-Z]{3,4})|(\([A-Z]{3,4}\)))?(-[0-9]+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<BluetoothDeviceInfo> MatchDevices(IEnumerable<BluetoothDeviceInfo> devices, string nameContains)
    {
        var list = devices as ICollection<BluetoothDeviceInfo> ?? devices.ToList();
        var byName = list.Where(d => d.DeviceName.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count > 0 ? byName : list.Where(d => BalanceBoardModelRegex.IsMatch(d.DeviceName));
    }

    /// <summary>
    /// 古い (壊れている可能性のある) プロファイルを消してから、SYNC 中のボードを探して HID を有効化する。
    /// 1回の discover は「そのスキャン中に SYNC が生きていた」場合しか拾えないので、見つかるまで
    /// 無制限に回す。呼び出し側がバックグラウンドスレッドで走らせて、キャンセルはユーザーに任せる。
    /// </summary>
    public static PairingOutcome PairAndInstall(string nameContains = "Nintendo", CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new BluetoothClient();

            foreach (var stale in MatchDevices(client.DiscoverDevices(255, false, true, false), nameContains))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BluetoothSecurity.RemoveDevice(stale.DeviceAddress);
                stale.SetServiceState(BluetoothService.HumanInterfaceDevice, false);
            }

            BluetoothDeviceInfo? target = null;
            while (target is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target = MatchDevices(client.DiscoverDevices(255, false, false, true), nameContains).FirstOrDefault();
            }

            target.SetServiceState(BluetoothService.HumanInterfaceDevice, true);
            return new PairingOutcome(PairingResult.Success, target.DeviceAddress.ToString(), null);
        }
        catch (OperationCanceledException)
        {
            return new PairingOutcome(PairingResult.Cancelled, null, null);
        }
        catch (Exception ex)
        {
            return new PairingOutcome(PairingResult.Error, null, ex.Message);
        }
    }
}
