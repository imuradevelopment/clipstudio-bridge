// penbind.cs — 仮想液タブの起動シーケンスの一部としての「ペン↔画面 紐付け処理」
//
// 仮想液タブの構成要素は3つ。このファイルは③を担う:
//   ① 仮想モニタ(画面)          … VDDドライバが提供
//   ② 仮想ペン(入力装置)        … vmultiドライバが提供
//   ③ ペン↔モニタの紐付け(台帳番号周り) … ← これ。起動のたびに必ず走る
//
// ③が無いと何が起きるか(実測済みの事故):
//   ペンはOSに「どの画面のペンか」を番号一致で説明しない限り、既定値の扱いとなり、
//   メイン画面と同番号のまま「メインのペン」として動く → ペン入力がメインに漏れる。
//   ③が起動処理に組み込まれていれば、初期値がどうであっても起動のたびに
//   検証・設定されるので、番号の重複は無害になる。
//
// 起動シーケンスにおける③の位置:
//   仮想液タブ起動 = ①モニタ起動 → ②ペン起動 → ③EnsureCintiqBinding()
//                   → 着地検証(VerifyPenLandsOnVirtual) → 初めて「起動完了」
//   ③か着地検証が失敗した場合、起動処理は「起動失敗」として中断し、
//   ペン報告を1つも書かない(メイン漏れ事故の構造的防止)。
//
// 使い方:
//   penbind check   … 現在の番号を取得して一致検証のみ報告(変更なし。毎回の初手)
//   penbind ensure  … check + 不一致なら設定を試みる + 設定後に着地検証
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static class PenBind {
    // ── PnP照会(cfgmgr32 経由。実測に使ったのと同じ情報源) ──
    // デバイスの台帳番号(ContainerId)と、親子関係を読む。

    static int Main(string[] args) {
        string mode = args.Length > 0 ? args[0] : "check";

        // ① 仮想モニタの特定と番号取得
        var monitor = FindVirtualMonitor();
        if (monitor == null) {
            Console.WriteLine("起動失敗: 仮想モニタが見つからない(①が未起動?)");
            return 1;
        }
        // ② 仮想ペンの特定と番号取得
        var pen = FindVirtualPen();
        if (pen == null) {
            Console.WriteLine("起動失敗: 仮想ペンが見つからない(②が未起動?)");
            return 1;
        }

        Console.WriteLine("仮想モニタ: " + monitor);
        Console.WriteLine("仮想ペン  : " + pen);

        if (mode == "check") {
            bool same = Id(monitor) == Id(pen);
            Console.WriteLine(same
                ? "紐付け: 一致(仮想モニタのペンとして正しい状態)"
                : "紐付け: 不一致(このままでは既定値の扱い=メイン漏れの恐れ)");
            return same ? 0 : 2;
        }

        // ensure: 一致していなければ設定する
        if (Id(monitor) != Id(pen)) {
            Console.WriteLine("紐付け: 不一致 → ペンを仮想モニタへ紐付ける");
            if (!BindPenToVirtualMonitor(monitor, pen)) {
                Console.WriteLine("起動失敗: 紐付けの設定に失敗。ペン報告は書かない。");
                return 3;
            }
            // 設定後に再取得して確認(設定した値がOSに受け入れられたか)
            var pen2 = FindVirtualPen();
            if (pen2 == null || Id(pen2) != Id(monitor)) {
                Console.WriteLine("起動失敗: 設定後に番号が一致していない(OSに戻された)");
                return 4;
            }
            Console.WriteLine("紐付け: 設定完了、番号一致を確認");
        }

        // 着地検証: ペンを仮想モニタ中央へ移動(クリックなし)し、着地が仮想モニタ内か
        int r = VerifyPenLandsOnVirtual();
        if (r != 0) {
            Console.WriteLine("起動失敗: 着地検証不合格(仮想モニタの外に落ちた)。ペン報告はこの後書かない。");
            return 5;
        }
        Console.WriteLine("仮想液タブ 起動完了(①②③すべて成立)");
        return 0;
    }

    // ── ①仮想モニタ特定: VDDのモニタ(devnode DISPLAY\MTT1337) ──
    static DevInfo FindVirtualMonitor() {
        return FindDevice(
            instancePrefix: "DISPLAY\\MTT1337",   // VDD仮想モニタの型番(実測値)
            expectClass: "Monitor");
    }

    // ── ②仮想ペン特定: vmultiルートデバイス(Service=vmulti) ──
    static DevInfo FindVirtualPen() {
        return FindDevice(
            instancePrefix: "ROOT\\HIDCLASS",     // vmultiのルート登録(実測値)
            expectService: "vmulti");
    }

    // ── ③設定: ペンの番号を仮想モニタの番号へ合わせる ──
    // 【実装確定待ちの1点】Windowsが関連付けを保存する正規の場所を特定中。
    // 正規手順(画面識別フロー)が書く先を前後差分で突き止め、判明次第
    // この関数の中身だけ差し替える。関数の形・呼び出し位置はこれで固定。
    static bool BindPenToVirtualMonitor(DevInfo monitor, DevInfo pen) {
        // (確定後) 書き込み先レジストリ値に「仮想モニタの番号」と「ペンの識別子」を
        // 対にして記録し、OSに関連付けとして読み込ませる。
        // 失敗(書けない/拒否された)は false を返す=起動中断。
        Console.WriteLine("  [設定手順の保存先確定待ち] 次の実験(正規識別フローの前後差分)で埋める");
        return false; // 確定までの間、確実に「未設定」として扱う(安全側)
    }

    // ── 着地検証: probe(クリックなし移動) ──
    static int VerifyPenLandsOnVirtual() {
        // peninjectと同じ報告(タブレット座標中央)を書き、GetCursorPosで着地確認。
        // 仮想モニタの物理矩形は画面列挙から都度取得する。
        var psi = new ProcessStartInfo(ProcessPath("peninject.exe"), "move 16383 16383") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        using (var p = Process.Start(psi)) {
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine("  " + outp.Replace("\r\n", " ").Trim());
        }
        return PenInjectProbeResultIsInsideVirtual() ? 0 : 1;
        // ※着地の判定は peninject 側が「報告後カーソル」を出力するので、
        //   仮想モニタ矩形(物理 1920,0 800x600)との比較で判定する。
    }
    static bool PenInjectProbeResultIsInsideVirtual() {
        POINT cp; GetCursorPos(out cp);
        var vd = VirtualMonitorPhysicalRect();
        Console.WriteLine(string.Format("  着地判定: ({0},{1}) vs 仮想モニタ{2}", cp.X, cp.Y, vd));
        return vd.Contains(cp.X, cp.Y);
    }

    // ═══════════ 以下、下請け(PnP照会・画面矩形) ═══════════

    class DevInfo { public string Instance, ContainerId, Service, Class; public override string ToString() {
        return Instance + " 番号=" + ContainerId; } }
    static string Id(DevInfo d) { return (d.ContainerId ?? "").ToUpperInvariant(); }

    static DevInfo FindDevice(string instancePrefix, string expectClass = null, string expectService = null) {
        string outp = RunPowerscript(@"
$pnp = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like '" + instancePrefix + @"*' }
foreach ($d in $pnp) {
  $cid = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_ContainerId').Data
  $svc = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_Service').Data
  $ok1 = $true; $ok2 = $true
  if ('" + (expectClass ?? "") + @"' -ne '') { $ok1 = ($d.Class -eq '" + (expectClass ?? "") + @"') }
  if ('" + (expectService ?? "") + @"' -ne '') { $ok2 = ($svc -eq '" + (expectService ?? "") + @"') }
  if ($ok1 -and $ok2) { Write-Output ($d.InstanceId + '|' + $cid + '|' + $svc + '|' + $d.Class); break }
}");
        if (string.IsNullOrWhiteSpace(outp)) return null;
        var parts = outp.Trim().Split('|');
        return new DevInfo { Instance = parts[0], ContainerId = parts[1], Service = parts[2], Class = parts[3] };
    }

    static string RunPowerscript(string script) {
        var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"" + script.Replace("\"", "'") + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        using (var p = Process.Start(psi)) { string s = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return s; }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static System.Drawing.Rectangle VirtualMonitorPhysicalRect() {
        try { SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); } catch { }
        var sc = System.Windows.Forms.Screen.AllScreens;
        foreach (var s in sc) {
            // 仮想モニタ = メイン以外で、右隣に配置される800x600(実測値)
            if (!s.Primary && s.Bounds.Width == 800 && s.Bounds.Height == 600) return s.Bounds;
        }
        // 見つからなければ「存在しない矩形」=検証は必ず不合格側へ
        return new System.Drawing.Rectangle(-9999, -9999, 0, 0);
    }

    static string ProcessPath(string exe) {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exe);
    }
}
