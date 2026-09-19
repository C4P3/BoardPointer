# サードパーティの表示

BoardPointer は以下を利用・参照しています。それぞれのライセンス条文を全文掲載します。

---

## 1. WiiFitToVRC — MIT License（**コードを流用しています**）

https://github.com/Nyamochi/WiiFitToVRC

以下のファイルは WiiFitToVRC の実装を移植または強く参照しています。MIT ライセンスに従い、
下記の著作権表示とライセンス条文を掲載します。

| BoardPointer のファイル | 由来 |
|---|---|
| `src/BoardPointer.Core/Hid/NativeMethods.cs` | ほぼそのまま移植 |
| `src/BoardPointer.Core/Hid/BalanceBoardDevice.cs` | HID の起動シーケンス、キープアライブ、デバイス列挙を移植（較正読み出しと LED 制御は新規） |
| `src/BoardPointer.Core/Bluetooth/BalanceBoardPairing.cs` | SYNC ペアリングの手順と型番の正規表現を移植 |
| `src/BoardPointer.Core/Pipeline/PresenceGate.cs` | 2閾値＋時間のヒステリシスという設計を参照（実装は新規） |

```
MIT License

Copyright (c) 2026 Hirotaka Suzuki

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 2. 32feet.NET (`InTheHand.Net.Personal.dll`) — Microsoft Public License (Ms-PL)（**バイナリを再配布しています**）

http://32feet.codeplex.com

`lib/InTheHand.Net.Personal.dll` としてリポジトリに含め、配布ビルドの単一 exe にも埋め込んで
います。**この再配布が、下記 Ms-PL 条文を掲載する理由です。**

> ⚠️ **公開前に確認してください。** この DLL は WiiBalanceWalker の配布物から取得したもので、
> バイナリ自体にライセンス表示が埋め込まれているかは未確認です。32feet.NET は CodePlex 時代に
> Ms-PL で公開されていたという前提でこの表示を置いています。
>
> **推奨: NuGet の [`InTheHand.Net.Bluetooth`](https://www.nuget.org/packages/InTheHand.Net.Bluetooth)
> （32feet.NET 4.x、MIT ライセンス）に差し替えること。** 再配布の疑義が消え、依存も新しくなります。
> API は `BluetoothClient.DiscoverDevices` / `SetServiceState` など近いので、移行は小さいはずです。

```
Microsoft Public License (Ms-PL)

This license governs use of the accompanying software. If you use the software, you accept this
license. If you do not accept the license, do not use the software.

1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same
meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the software.

A "contributor" is any person that distributes its contribution under this license.

"Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
copyright license to reproduce its contribution, prepare derivative works of its contribution, and
distribute its contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or
otherwise dispose of its contribution in the software or derivative works of the contribution in
the software.

3. Conditions and Limitations

(A) No Trademark License- This license does not grant you rights to use any contributors' name,
logo, or trademarks.

(B) If you bring a patent claim against any contributor over patents that you claim are infringed
by the software, your patent license from such contributor to the software ends automatically.

(C) If you distribute any portion of the software, you must retain all copyright, patent,
trademark, and attribution notices that are present in the software.

(D) If you distribute any portion of the software in source code form, you may do so only under
this license by including a complete copy of this license with your distribution. If you
distribute any portion of the software in compiled or object code form, you may only do so under a
license that complies with this license.

(E) The software is licensed "as-is." You bear the risk of using it. The contributors give no
express warranties, guarantees or conditions. You may have additional consumer rights under your
local laws which this license cannot change. To the extent permitted under your local laws, the
contributors exclude the implied warranties of merchantability, fitness for a particular purpose
and non-infringement.
```

---

## 3. WiiBalanceWalker — Microsoft Public License (Ms-PL)（**コードは流用していません**）

https://github.com/lshachar/WiiBalanceWalker
（v0.4 以前は GreyCube.com の Richard Perry 氏によるリリース、Ms-PL）

参考にしたのは**知識1点だけ**です。「ボードの青いLEDの点滅は、プレイヤーLEDのレポートを
1本送れば止まる」という事実を、`FormBluetooth.cs` の `SetLEDs(true, false, false, false)` から
学びました。

実装（`MouseOutput` ではなく `BalanceBoardDevice.SetLeds`、レポート `0x11`）は HID の仕様から
自分で書いており、**WiiBalanceWalker のソースコードは1行も含んでいません。** あちらが使っている
WiimoteLib と vJoy にも依存していません。

したがって Ms-PL の条文掲載義務は生じませんが、事実として助けられたので記録します。
（上記 2. に掲載した Ms-PL 条文が、同じライセンスとして参照できます。）

---

## 4. .NET / Windows Forms — MIT License

https://github.com/dotnet/runtime · https://github.com/dotnet/winforms

配布ビルドは自己完結形式なので、.NET ランタイムを同梱しています。Copyright (c) .NET Foundation
and Contributors、MIT ライセンス。条文は上記 1. の MIT と同一です。
