// pipelinetest.cs — 仮想液タブの通し検証（1本のプログラムで接続→盤面読み→ペン入力→切断を貫通する）
//
// 構造（設計書 pipeline-test-design.md §1 + 2026-09-23実測の修正）:
//   仮想モニタ = VirtualDisplayDriver(ROOT\DISPLAY\0000, MTT1337)
//   仮想ペン   = vmulti(ROOT\HIDCLASS\0000, col03 標準デジタイザ ReportID 0x05)
//   関連付け   = 実測の結果、OSは外部ペンに対しDigimon関連付けを参照しない（内蔵ペンの
//                ペアリングはバス統合(HIDI2C)由来。外部ペンは常に既定のspanマッピング
//                （デバイス全域→デスクトップ全域の線形写像）になる）。よって本テストは
//                OS報告の矩形(GetPointerDeviceRects)からspanマッピングを読み、VDD領域に
//                対応するデバイス座標のみを使う。Digimonへの書き込みはユニット構成として
//                冪等に維持する（ペンには効かないが害もない）
//   書き       = vmulti制御コレクション(col05 65/65)経由の標準デジタイザ報告（デバイス絶対座標のみ。
//                画面座標を計算して撃つコードは存在しない。SetCursorPos/SendInputは呼ばない）
//   読み       = GDI+ CopyFromScreen（全デスクトップ。メイン+仮想モニタが常に同一画像内に写る）
//   データパスは1本: テストプログラム → vmulti.sys → OSペンスタック（唯一の読み手）→ span写像
//   （OTDは経路に置かない。SendInput仮想マウスによる二重配信がメイン誤クリックの原因だったため）
//
// 無影響性の構造（§2をspan実装で置き換えた形）:
//   1. 読み手が1つ（OSペンスタックのみ）
//   2. 書き込み側に画面座標が存在しない（デバイス絶対座標 0..32767 のみ。全てOS報告矩形からの変換）
//   3. 入力ゼロ事前ゲート: 最初のペン報告の前に (a)col03がOSに読まれているか (b)OS報告矩形が
//      デスクトップ全域（既定span）か を確認し、VDD中央に対応する座標でホバー1発の着地実証をする。
//      着地がVDD外ならクリック1発送らず中断
//   4. 全タップ直前にホバー検証: 着地がVDD内かつ期待点±60pxのときだけ同じ座標でクリックする。
//      マッピングが変わっても、クリックがメインに届く前にホバーが外れて中断する
//
// キャプチャ（全て全画面。全てrollの何枚目かをログとindexに残す）:
//   01_接続後 02_初期盤面 03_電卓配置後 04〜11_操作前後(7,×,6,= の各前後8枚)
//   12_最終表示 13_電卓終了後 14_切断後 ＋ 接続完了〜切断完了の0.5秒間隔roll
//   プログラムは画像の成否判定をしない。判定は実行後にキャプチャを見て行う
//
// 観測の実装上の注意（2026-09-23 実測）:
//   - GetPointerDevices() はこのOSでは count を正しく返してもバッファへ1要素分しか書かない
//     （strideがドキュメントと不一致）。観測には RAWINPUT(GetRawInputDeviceList) のハンドルを
//     GetPointerDevice / GetPointerDeviceRects に渡す正規のブリッジを使う
//   - col03 のステータスビットは Tip=0x01 Barrel=0x02 Eraser=0x04 Invert=0x08 InRange=0x10
//     （レポート記述子の実測順）。旧実装の INRANGE=0x02 は Barrel Switch（右ボタン相当）だった
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using HidSharp;
using Microsoft.Win32;

static class PipelineTest {
    // ── Win32 ──
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static readonly IntPtr PMV2 = new IntPtr(-4);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h2, uint f);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList(IntPtr list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern bool GetPointerDevice(IntPtr device, IntPtr info);
    [DllImport("user32.dll")] static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerRect, out RECT displayRect);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;

    // ── 定数 ──
    const ushort VMULTI_VID = 0x00FF, VMULTI_PID = 0xBACC;
    const string VdAdapterId = @"ROOT\DISPLAY\0000";   // Virtual Display Driver
    const string VmultiRootId = @"ROOT\HIDCLASS\0000"; // Pentablet HID (vmulti)
    const string VdMonitorHw = "MTT1337";
    const string DigimonKey = @"SOFTWARE\Microsoft\Wisp\Pen\Digimon";
    const string MonitorIfaceGuid = "{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    const byte StTip = 0x01, StBarrel = 0x02, StInRange = 0x10; // col03 記述子実測順
    const ushort PenPressure = 4096;

    // ── 状態 ──
    static Rectangle _vd, _all;
    static string _runDir, _rollDir;
    static StreamWriter _log;
    static readonly object _ioLock = new object();
    static HidStream _pen;          // vmulti col05 制御コレクション
    static volatile bool _rolling;
    static int _rollCount;
    static Thread _rollThread;
    static readonly List<string> _index = new List<string>();

    static void Log(string s) {
        lock (_ioLock) {
            Console.WriteLine(s);
            try { _log.WriteLine(s); _log.Flush(); } catch { }
        }
    }

    // ══════════════════ Main ══════════════════

    static int Main(string[] args) {
        try { SetProcessDpiAwarenessContext(PMV2); } catch { }

        // 昇格: 管理者操作（pnputil / Digimon書き込み）は接続フェーズの先頭に1回だけ集約
        if (!IsAdmin()) {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string rd = Path.Combine(Path.GetDirectoryName(exe), "captures", "run-" + DateTime.Now.ToString("HHmmss"));
            var psi = new ProcessStartInfo(exe, "--rundir \"" + rd + "\"") { Verb = "runas", UseShellExecute = true };
            Console.WriteLine("=== UAC: 管理者権限が要るため昇格します。ダイアログを承認してください ===");
            try { var p = Process.Start(psi); p.WaitForExit(); Console.WriteLine("昇格プロセス exit=" + p.ExitCode + " ログ: " + rd + "\\test.log"); return p.ExitCode; }
            catch (Exception ex) { Console.WriteLine("昇格失敗/拒否: " + ex.Message); return 9; }
        }

        // 実行ディレクトリ（親から --rundir で指定される想定）
        _runDir = GetArg(args, "--rundir");
        if (_runDir == null) _runDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures", "run-" + DateTime.Now.ToString("HHmmss"));
        _rollDir = Path.Combine(_runDir, "roll");
        Directory.CreateDirectory(_runDir);
        Directory.CreateDirectory(_rollDir);
        _log = new StreamWriter(Path.Combine(_runDir, "test.log"), false, Encoding.UTF8) { AutoFlush = true };

        Log("=== pipelinetest 開始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
        Log("run dir: " + _runDir);

        int exit;
        try {
            exit = Run();
        } catch (Exception ex) {
            Log("致命的例外: " + ex);
            try { exit = Disconnect(1); } catch (Exception ex2) { Log("切断も失敗: " + ex2); exit = 1; }
        }
        try {
            File.WriteAllText(Path.Combine(_runDir, "index.md"), BuildIndex());
            Log("index: " + Path.Combine(_runDir, "index.md"));
        } catch (Exception ex) { Log("index書き込み失敗: " + ex.Message); }
        Log("=== pipelinetest 終了 exit=" + exit + " ===");
        _log.Dispose();
        return exit;
    }

    static int Run() {
        // ── 事前状態スナップショット（変更前。入力ゼロ） ──
        Log("\n=== [0] 事前状態スナップショット ===");
        Snapshot("事前");

        // ── [1] 接続 ──
        Log("\n=== [1] 接続 ===");
        if (!ConnectVirtualMonitor()) return Disconnect(1);
        if (!ConnectVmulti()) return Disconnect(1);
        bool assocWritten;
        if (!BindAssociation(out assocWritten)) return Disconnect(1);
        if (assocWritten) {
            if (!RunPnputil("restart-device", VmultiRootId, "関連付け反映: ペンHID再起動")) return Disconnect(1);
            Thread.Sleep(2500); // 再列挙とペンスタック再読込の猶予
        } else {
            Log("関連付けは既に正しいので書き込まない（冪等）");
        }
        // 制御チャネル（col05）はデバイス再起動が全て終わった後に開く（再起動でハンドルが無効化されるため）
        if (!OpenPenControl()) return Disconnect(1);

        // ── ★入力ゼロ事前ゲート（ここまで入力ゼロ。ここで確定するまでペン報告は1報告も送らない） ──
        Log("\n=== [2] 入力ゼロ事前ゲート ===");
        string gateFail;
        if (!InputZeroGate(out gateFail)) {
            Log("GATE NG: " + gateFail + " → クリック1発も送らず中断し、切断へ");
            return Disconnect(1);
        }
        Log("GATE OK");

        Capture("01_接続後");
        StartRolling();

        // ── [3] 盤面読み（1回目: 電卓なしの初期盤面） ──
        Log("\n=== [3] 盤面読み（初期） ===");
        Capture("02_初期盤面");

        // ── [4] 対象アプリ（電卓）を仮想モニタへ配置 ──
        Log("\n=== [4] 電卓起動・配置 ===");
        if (!LaunchCalcOnVd()) return Disconnect(1);
        Capture("03_電卓配置後");

        // ── [5] ボタン位置の実測（UIA。推定値は使わない） ──
        Log("\n=== [5] ボタン位置の実測 ===");
        string[][] plan; // {名前, X, Y(デバイス座標)}
        if (!MeasureButtons(out plan)) {
            Log("ボタン実測に失敗 → ペン入力は1発も送らず切断へ");
            return Disconnect(1);
        }

        // ── [5.5] ホバー着地検証（電卓を閉じたクリーン状態で実施する） ──
        // UWP（電卓）がフォーカスを持つとペンホバーがシステムカーソルに反映されなく
        // なることを実測（hoverdiag T6）。一方、ペンクリック（tip接触）は正規マッピング
        // で正確に着地する（同T7）。よって着地検証はクリーン状態で全座標に対して済ませ、
        // クリック時はマッピング矩形の再確認（入力ゼロ）で安全性を担保する。
        Log("\n=== [5.5] ホバー着地検証（クリーン状態） ===");
        KillCalc();
        if (!SpanMapping(out gateFail)) { Log("検証前マッピング確認NG: " + gateFail); return Disconnect(1); }
        foreach (var row in plan) {
            ushort vx = ushort.Parse(row[1]), vy = ushort.Parse(row[2]);
            if (!HoverVerify(vx, vy, row[0])) {
                Log("ホバー着地検証が外れた → クリックは1発も送らず切断へ");
                return Disconnect(1);
            }
        }
        Log("全" + plan.Length + "座標の着地検証 OK");

        // 電卓を再度起動・配置し、ボタン位置が同一であることを再実測で確認
        if (!LaunchCalcOnVd()) return Disconnect(1);
        string[][] plan2;
        if (!MeasureButtons(out plan2)) return Disconnect(1);
        for (int i = 0; i < plan.Length; i++) {
            if (plan[i][0] != plan2[i][0] ||
                Math.Abs(int.Parse(plan[i][1]) - int.Parse(plan2[i][1])) > 40 ||
                Math.Abs(int.Parse(plan[i][2]) - int.Parse(plan2[i][2])) > 40) {
                Log("ERR: 再起動後のボタン位置が不一致（" + plan[i][0] + "）→ クリックせず切断へ");
                return Disconnect(1);
            }
        }
        Log("再実測 OK: 検証済み座標と一致");

        // ── [6] ペン操作 7 × 6 = ──
        Log("\n=== [6] ペン操作 7 × 6 = ===");
        string no = "04";
        bool tapsOk = true;
        for (int i = 0; i < plan2.Length && tapsOk; i++) {
            string n = plan2[i][0];
            ushort x = ushort.Parse(plan2[i][1]), y = ushort.Parse(plan2[i][2]);
            Capture(no + "_前_" + n); no = NextNo(no);
            tapsOk = Tap(x, y);
            Capture(no + "_後_" + n); no = NextNo(no);
        }

        // ── [7] 最終表示 ──
        Thread.Sleep(600);
        Capture("12_最終表示");

        return Disconnect(tapsOk ? 0 : 1);
    }

    static string NextNo(string no) {
        int v = int.Parse(no) + 1;
        return v.ToString("00");
    }

    // ══════════════════ 接続フェーズ ══════════════════

    // 仮想モニタ: 無ければアダプタ有効化（拡張トポロジへ）。有なら触らない。
    // 注意: 無効化されたデバイスはStatusが"Error"と表示される（"Disabled"ではない）
    static bool ConnectVirtualMonitor() {
        _vd = FindVdScreen();
        Log("仮想モニタ確認: アダプタ " + VdAdapterId + " 状態=" + (PnpStatus(VdAdapterId) ?? "?"));
        if (_vd.IsEmpty) {
            Log("  モニタがデスクトップに無い → アダプタ有効化（=1台刺したのと同種のトポロジ変化。接続時に1回だけ）");
            if (!RunPnputil("enable-device", VdAdapterId, "仮想モニタ有効化")) return false;
            for (int i = 0; i < 30 && _vd.IsEmpty; i++) { Thread.Sleep(500); _vd = FindVdScreen(); }
            if (_vd.IsEmpty) {
                Log("  ERR: 有効化してもモニタが出ない。表示ドライバの再起動（VDDControl Restart）は禁止済みのため触れず中断");
                return false;
            }
        } else {
            Log("  モニタは既にデスクトップに在る → 触らない");
        }
        if (_vd.IsEmpty) { Log("  ERR: 仮想モニタが見つからない"); return false; }
        _all = UnionAllScreens();
        Log("  仮想モニタ: (" + _vd.X + "," + _vd.Y + ")-(" + _vd.Right + "," + _vd.Bottom + ")  全画面: " + _all.Width + "x" + _all.Height);
        return true;
    }

    // vmulti: 無ければ有効化。有なら触らない。
    static bool ConnectVmulti() {
        Log("vmulti確認: " + VmultiRootId + " 状態=" + (PnpStatus(VmultiRootId) ?? "?"));
        if (FindVmultiPenPath() == null) {
            Log("  col03が無い → ルート有効化");
            if (!RunPnputil("enable-device", VmultiRootId, "vmulti有効化")) return false;
        }
        // col03 の出現を待つ（全列挙は使わずVID/PIDフィルタのみ。全列挙は特定環境で落ちる）
        for (int i = 0; i < 20; i++) {
            if (FindVmultiPenPath() != null) break;
            Thread.Sleep(500);
        }
        if (FindVmultiPenPath() == null) { Log("  ERR: vmulti col03(ペン)が見つからない"); return false; }
        Log("  col03ペン を確認");
        return true;
    }

    // Digimon: 既に正しければ書かない。死んだvmulti値（旧列挙インスタンス）は掃除する。
    static bool BindAssociation(out bool written) {
        written = false;
        Log("Digimon関連付け確認: " + DigimonKey);
        string monPath = FindVdMonitorPath();
        if (monPath == null) { Log("  ERR: VDDモニタのインターフェースパスが見つからない"); return false; }
        Log("  モニタパス: " + monPath);

        var pens = EnumVmultiPens();
        if (pens.Count == 0) { Log("  ERR: vmultiのペンコレクション(col03/col04)がRAWINPUTに無い"); return false; }
        foreach (var p in pens) Log("  ペン: " + p.Path);

        using (var key = Registry.LocalMachine.CreateSubKey(DigimonKey)) {
            if (key == null) { Log("  ERR: Digimonキーを作れない"); return false; }
            // 死んだvmulti値の掃除（今回のユニット境界内のみ）
            foreach (var v in key.GetValueNames()) {
                if (!v.StartsWith("20-\\?\\HID", StringComparison.OrdinalIgnoreCase)) continue;
                bool ours = false, current = false;
                foreach (var p in pens) { if (v.EndsWith(p.Path.Substring(4), StringComparison.OrdinalIgnoreCase)) current = true; }
                if (v.IndexOf("col03", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("col04", StringComparison.OrdinalIgnoreCase) >= 0) ours = true;
                if (ours && !current) {
                    key.DeleteValue(v, false);
                    Log("  旧インスタンスの値を削除: " + v);
                    written = true;
                }
            }
            foreach (var p in pens) {
                string name = "20-" + p.Path;
                object cur = key.GetValue(name);
                if (cur is string && string.Equals((string)cur, monPath, StringComparison.OrdinalIgnoreCase)) {
                    Log("  既に正しい: " + ShortName(name));
                    continue;
                }
                key.DeleteValue(name, false);
                key.SetValue(name, monPath, RegistryValueKind.String);
                Log("  書き込み: " + ShortName(name));
                Log("       → " + monPath);
                written = true;
            }
        }
        return true;
    }

    static string ShortName(string n) {
        return n.Length > 70 ? n.Substring(0, 70) + "…" : n;
    }

    // ══════════════════ 入力ゼロ事前ゲート ══════════════════
    //
    // 実証済みのOS構造（2026-09-23 実測）: 外部ペンデジタイザに対し、OSはDigimon関連付けを
    // 参照しない（書き込み+HID再起動[親/子両devnode]でも関連付け矩形は不変。内蔵ペンは
    // バス統合(HIDI2C)で自動ペアリングする＝統合判定はバス由来で、root列挙のvmultiは
    // 常に外部ペンとして既定のspanマッピング(デバイス全域→デスクトップ全域の線形写像)になる）。
    //
    // よって本テストは:
    //  1. OS報告の矩形（GetPointerDeviceRects）からspanマッピングを確定し（OSがマッピングの唯一の権威）,
    //  2. VDD領域に対応するデバイス座標のみを使い,
    //  3. クリックは必ず「直前のホバー着地がVDD内」を経験した同一座標でのみ行う。
    // マッピングが何らかの理由で変わった場合、クリックがメインに届く前にホバー検証が
    // 外れて中断する。画面座標を計算して撃つコードは存在しない（デバイス座標のみ）。

    static RECT _mapPr, _mapDr; // 直近のOS報告矩形（SpanMappingで更新）

    static bool InputZeroGate(out string fail) {
        fail = null;
        if (!SpanMapping(out fail)) return false;

        // ホバー実証: VDD中央に対応するデバイス座標でホバー1発 → 着地がVDD内か
        ushort cx, cy;
        DesktopToDevice(_vd.X + _vd.Width / 2, _vd.Y + _vd.Height / 2, out cx, out cy);
        POINT p0; GetCursorPos(out p0);
        WritePen(cx, cy, 0, StInRange);
        Thread.Sleep(600);
        POINT p1; GetCursorPos(out p1);
        WritePen(cx, cy, 0, 0);
        Thread.Sleep(300);
        Log("  ホバー実証: (" + p0.X + "," + p0.Y + ") → (" + p1.X + "," + p1.Y + ") 目標=VDD中央 (" + (_vd.X + _vd.Width / 2) + "," + (_vd.Y + _vd.Height / 2) + ")");
        if (!_vd.Contains(p1.X, p1.Y)) { fail = "ホバー着地が仮想モニタ外（マッピング不一致。クリックは1発送らず中断）"; return false; }
        Log("  着地は仮想モニタ内 OK");
        return true;
    }

    // col03登録とOS報告矩形の取得。矩形がデスクトップ全域（既定span）であることを確認し、
    // VDD領域のデバイス座標範囲を検証する。タップの直前にも再実行して変化を検知する。
    static bool SpanMapping(out string fail) {
        fail = null;
        var pens = EnumVmultiPens();
        IntPtr col03 = IntPtr.Zero;
        foreach (var p in pens) if (p.Path.ToLower().Contains("col03")) col03 = p.Handle;
        if (col03 == IntPtr.Zero) { fail = "col03がRAWINPUTに無い（ペンスタックに読まれていない）"; return false; }
        IntPtr buf = Marshal.AllocHGlobal(256);
        if (!GetPointerDevice(col03, buf)) { fail = "col03がポインタ登録されていない"; return false; }
        RECT pr, dr;
        if (!GetPointerDeviceRects(col03, out pr, out dr)) { fail = "GetPointerDeviceRects失敗"; return false; }
        Rectangle all = UnionAllScreens();
        if (dr.L != all.X || dr.T != all.Y || dr.R != all.Right || dr.B != all.Bottom) {
            fail = "OSのマッピング矩形(" + dr.L + "," + dr.T + ")-(" + dr.R + "," + dr.B + ")がデスクトップ全域と不一致（想定外の関連付け）";
            return false;
        }
        _mapPr = pr; _mapDr = dr; // 変換に使う前に必ず設定
        ushort x0, y0, x1, y1;
        DesktopToDevice(_vd.X + 2, _vd.Y + 2, out x0, out y0);
        DesktopToDevice(_vd.Right - 2, _vd.Bottom - 2, out x1, out y1);
        if (x1 <= x0 || y1 <= y0) { fail = "VDD領域のデバイス座標範囲が異常"; return false; }
        Log("  col03 矩形: display(" + dr.L + "," + dr.T + ")-(" + dr.R + "," + dr.B + ") / pointer(" + pr.L + "," + pr.T + ")-(" + pr.R + "," + pr.B + ")");
        Log("  VDD領域のデバイス座標範囲: x[" + x0 + ".." + x1 + "] y[" + y0 + ".." + y1 + "]");
        return true;
    }

    // デスクトップ物理px → デバイス座標。OS報告の矩形（spanマッピング）からの線形変換のみ。
    static void DesktopToDevice(int px, int py, out ushort dx, out ushort dy) {
        double u = (double)(px - _mapDr.L) / (_mapDr.R - _mapDr.L);
        double v = (double)(py - _mapDr.T) / (_mapDr.B - _mapDr.T);
        int x = (int)Math.Round(u * 32767), y = (int)Math.Round(v * 32767);
        if (x < 1) x = 1; if (x > 32766) x = 32766;
        if (y < 1) y = 1; if (y > 32766) y = 32766;
        dx = (ushort)x; dy = (ushort)y;
    }

    // ホバー着地検証（クリーン状態専用。UWPフォーカス下ではホバーがカーソルに反映されない）
    static bool HoverVerify(ushort x, ushort y, string name) {
        int expX = _mapDr.L + (int)Math.Round((double)x * (_mapDr.R - _mapDr.L) / 32767);
        int expY = _mapDr.T + (int)Math.Round((double)y * (_mapDr.B - _mapDr.T) / 32767);
        WritePen(x, y, 0, StInRange);
        Thread.Sleep(800);
        POINT p; GetCursorPos(out p);
        WritePen(x, y, 0, 0);
        Thread.Sleep(300);
        bool inVd = _vd.Contains(p.X, p.Y);
        bool near = Math.Abs(p.X - expX) <= 60 && Math.Abs(p.Y - expY) <= 60;
        Log("  検証[" + name + "] (" + x + "," + y + ") 着地(" + p.X + "," + p.Y + ") 期待(" + expX + "," + expY + ") VDD内=" + inVd + " ±60=" + near);
        return inVd && near;
    }

    static void KillCalc() {
        bool killed = false;
        foreach (var p in Process.GetProcessesByName("CalculatorApp")) { try { p.Kill(); killed = true; } catch { } }
        if (killed) Thread.Sleep(1200);
        Log("  電卓を閉じた" + (killed ? "" : "（起動していなかった）"));
    }

    // クリック（電卓フォーカス下でも正規マッピングで着地する＝hoverdiag T7実証）。
    // 直前の SpanMapping 再確認（入力ゼロ）でマッピング変化を検知し、変化時は撃たない。
    static bool Tap(ushort x, ushort y) {
        string gfail;
        if (!SpanMapping(out gfail)) { Log("タップ中止（マッピング変化）: " + gfail); return false; }
        Log("tap (" + x + "," + y + ") 筆圧=" + PenPressure + " ※ホバー検証は[5.5]で実施済み、マッピング再確認OK");
        WritePen(x, y, 0, StInRange);
        Thread.Sleep(150);
        WritePen(x, y, PenPressure, (byte)(StInRange | StTip));
        Thread.Sleep(60);
        WritePen(x, y, 0, StInRange);
        Thread.Sleep(60);
        WritePen(x, y, 0, 0);
        Thread.Sleep(150);
        return true;
    }

    // ══════════════════ 電卓 ══════════════════

    static bool LaunchCalcOnVd() {
        Process.Start("explorer.exe", "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Thread.Sleep(3000);
        IntPtr hwnd = FindCalcWindow();
        if (hwnd == IntPtr.Zero) { Log("ERR: 電卓ウィンドウが見つからない"); return false; }
        SetWindowPos(hwnd, IntPtr.Zero, _vd.X + 10, _vd.Y + 10, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE|NOZORDER|NOACTIVATE
        Log("電卓を (" + (_vd.X + 10) + "," + (_vd.Y + 10) + ") へ移動");
        Thread.Sleep(1500);
        return true;
    }

    static IntPtr FindCalcWindow() {
        foreach (var p in Process.GetProcessesByName("ApplicationFrameHost")) {
            try {
                if (p.MainWindowTitle == "電卓") return p.MainWindowHandle;
            } catch { }
        }
        foreach (var p in Process.GetProcessesByName("CalculatorApp")) {
            try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; } catch { }
        }
        return IntPtr.Zero;
    }

    // ボタン位置はUIAで実測（AutomationId優先、名前フォールバック）。推定値・定数は使わない。
    static bool MeasureButtons(out string[][] plan) {
        plan = null;
        int calcPid = -1;
        foreach (var p in Process.GetProcessesByName("CalculatorApp")) { calcPid = p.Id; break; }
        if (calcPid < 0) { Log("ERR: CalculatorApp プロセスが無い"); return false; }

        AutomationElement win = null;
        for (int t = 0; t < 20 && win == null; t++) {
            // トップレベルはApplicationFrameHost。タイトル一致で探し、ダメなら
            // 子(CoreWindow)のプロセスIDで特定する
            foreach (var title in new string[] { "電卓", "Calculator" }) {
                win = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                                     new PropertyCondition(AutomationElement.NameProperty, title)));
                if (win != null) break;
            }
            if (win == null) {
                foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)) {
                    AutomationElement child = null;
                    try { child = e.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, calcPid)); }
                    catch { }
                    if (child != null) { win = e; break; }
                }
            }
            if (win == null) Thread.Sleep(500);
        }
        if (win == null) { Log("ERR: 電卓のUIAウィンドウが見つからない"); return false; }
        Log("  ウィンドウ: '" + win.GetCurrentPropertyValue(AutomationElement.NameProperty) + "'");

        string[][] want = {
            new string[] { "7",  "num7Button",     "7" },
            new string[] { "×",  "multiplyButton", "乗算|掛ける|×|Multiply" },
            new string[] { "6",  "num6Button",     "6" },
            new string[] { "=",  "equalButton",    "等号|イコール|=|Equals" },
        };

        plan = new string[want.Length][];
        var seen = new List<string>();
        for (int t = 0; t < 20; t++) {
            var buttons = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            bool allFound = true;
            for (int i = 0; i < want.Length; i++) {
                if (plan[i] != null) continue;
                AutomationElement hit = null;
                string how = null;
                foreach (AutomationElement b in buttons) {
                    string aid = "", nm = "";
                    try { aid = (b.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string) ?? ""; } catch { }
                    try { nm = (b.GetCurrentPropertyValue(AutomationElement.NameProperty) as string) ?? ""; } catch { }
                    if (seen.Count < 200 && aid != "") {
                        string key = aid + "='" + nm + "'";
                        if (!seen.Contains(key)) seen.Add(key);
                    }
                    if (aid == want[i][1]) { hit = b; how = "AutomationId=" + aid; break; }
                    foreach (var cand in want[i][2].Split('|')) if (nm == cand) { hit = b; how = "Name=" + nm; break; }
                    if (hit != null) break;
                }
                if (hit == null) { allFound = false; continue; }
                System.Windows.Rect r = hit.Current.BoundingRectangle;
                int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
                if (!_vd.Contains(cx, cy)) {
                    Log("  WARN: ボタン" + want[i][0] + "の中心 (" + cx + "," + cy + ") が仮想モニタ外。実測を採用しない");
                    allFound = false; continue;
                }
                ushort dx, dy;
                DesktopToDevice(cx, cy, out dx, out dy);
                plan[i] = new string[] { want[i][0], dx.ToString(), dy.ToString() };
                Log("  " + want[i][0] + ": " + how + " 中心(" + cx + "," + cy + ")物理 → デバイス(" + dx + "," + dy + ")");
            }
            if (allFound) return true;
            Thread.Sleep(500);
        }
        Log("ERR: ボタンが特定できない。実測できたボタン一覧:");
        foreach (var s in seen) Log("    " + s);
        return false;
    }

    // ══════════════════ ペン書き込み（デバイス絶対座標のみ） ══════════════════

    static void WritePen(ushort x, ushort y, ushort pressure, byte status) {
        if (_pen == null) throw new InvalidOperationException("ペン制御チャネル未接続");
        byte[] b = new byte[65];
        b[0] = 0x40;   // VMultiID
        b[1] = 0x0B;   // ReportLength
        b[2] = 0x05;   // col03 標準デジタイザ
        b[3] = status; // Tip=0x01 Barrel=0x02 Eraser=0x04 Invert=0x08 InRange=0x10
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        _pen.Write(b);
    }

    // ══════════════════ 切断フェーズ ══════════════════

    static int Disconnect(int code) {
        try {
            Log("\n=== [切断] ===");
            // 電卓終了
            bool killed = false;
            foreach (var p in Process.GetProcessesByName("CalculatorApp")) { try { p.Kill(); killed = true; } catch { } }
            Thread.Sleep(1200);
            Log("電卓終了" + (killed ? "" : "（起動していない）"));
            if (killed) Capture("13_電卓終了後");

            // ペンlift保証（最後の報告は必ず out-of-range）
            try { if (_pen != null) WritePen(16384, 16384, 0, 0); } catch (Exception ex) { Log("lift保証失敗: " + ex.Message); }
            try { if (_pen != null) _pen.Dispose(); } catch { }
            _pen = null;

            // 常時キャプチャはデバイス無効化の「前」に止める。消滅中の画面を
            // CopyFromScreen すると GDI がブロックして入力スタックごと固まる（run-195035 実測）。
            StopRolling();

            // 仮想モニタ無効化（= 抜いた相当。devcon remove は使わない）
            if (!_vd.IsEmpty) {
                RunPnputil("disable-device", VdAdapterId, "仮想モニタ無効化");
                for (int i = 0; i < 20 && !FindVdScreen().IsEmpty; i++) Thread.Sleep(500);
                _vd = FindVdScreen();
            }
            // vmulti無効化
            RunPnputil("disable-device", VmultiRootId, "vmulti無効化");
            Thread.Sleep(1000);
            _all = UnionAllScreens();

            // 入力ゼロ事後検証
            Log("\n=== [切断後の入力ゼロ検証] ===");
            var nowPens = EnumVmultiPens();
            Log("RAWINPUT上のvmulti: " + (nowPens.Count == 0 ? "消えた OK" : "まだ在る(" + nowPens.Count + ") NG"));
            Log("仮想モニタのデスクトップからの消失: " + (FindVdScreen().IsEmpty ? "OK" : "NG"));
            Log("切断後の画面構成: " + DescribeScreens());

            Capture("14_切断後");
            Snapshot("事後");
        } catch (Exception ex) {
            Log("切断フェーズ例外: " + ex);
            if (code == 0) code = 1;
        }
        return code;
    }

    // ══════════════════ RAWINPUT ↔ ポインタ観測（入力ゼロ） ══════════════════

    class PenEntry { public IntPtr Handle; public string Path; public bool Pointer; public RECT Display; public bool HasRect; }

    static List<PenEntry> EnumVmultiPens() {
        var r = new List<PenEntry>();
        foreach (var d in RawInputHids()) {
            if (d.Vid != VMULTI_VID || d.Pid != VMULTI_PID) continue;
            if (d.UsagePage != 0x0D) continue;
            if (d.Path.ToLower().Contains("col03") || d.Path.ToLower().Contains("col04")) {
                var e = new PenEntry { Handle = d.Handle, Path = d.Path };
                IntPtr buf = Marshal.AllocHGlobal(256);
                e.Pointer = GetPointerDevice(d.Handle, buf);
                if (e.Pointer) e.HasRect = GetPointerDeviceRects(d.Handle, out e.Display, out e.Display);
                r.Add(e);
            }
        }
        return r;
    }

    class RawHid { public IntPtr Handle; public uint Vid, Pid; public ushort UsagePage, Usage; public string Path; }

    static List<RawHid> RawInputHids() {
        var list = new List<RawHid>();
        uint n = 0, sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(IntPtr.Zero, ref n, sz) == uint.MaxValue || n == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)(sz * n));
        if (GetRawInputDeviceList(buf, ref n, sz) == uint.MaxValue) return list;
        for (uint i = 0; i < n; i++) {
            IntPtr p = new IntPtr(buf.ToInt64() + (int)(i * sz));
            IntPtr h = Marshal.ReadIntPtr(p);
            uint dt = (uint)Marshal.ReadInt32(p, 8);
            if (dt != 2) continue; // RIM_TYPEHID
            // RID_DEVICE_INFO: cbSize=32, vendorId@8, productId@12, version@16, usagePage@20(2), usage@22(2) 実測レイアウト
            int isz = 32;
            IntPtr di = Marshal.AllocHGlobal(isz);
            for (int z = 0; z < isz; z++) Marshal.WriteByte(di, z, 0);
            Marshal.WriteInt32(di, 0, isz); Marshal.WriteInt32(di, 4, 2);
            uint dsz = (uint)isz;
            if (GetRawInputDeviceInfoW(h, RIDI_DEVICEINFO, di, ref dsz) == uint.MaxValue) continue;
            var rh = new RawHid {
                Handle = h,
                Vid = (uint)Marshal.ReadInt32(di, 8),
                Pid = (uint)Marshal.ReadInt32(di, 12),
                UsagePage = (ushort)Marshal.ReadInt16(di, 20),
                Usage = (ushort)Marshal.ReadInt16(di, 22),
            };
            uint nsz = 0;
            GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, IntPtr.Zero, ref nsz);
            if (nsz > 0 && nsz < 4096) {
                IntPtr np = Marshal.AllocHGlobal((int)nsz * 2 + 2);
                if (GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, np, ref nsz) != uint.MaxValue)
                    rh.Path = Marshal.PtrToStringUni(np) ?? "";
            }
            list.Add(rh);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    // ══════════════════ vmulti HidSharp ══════════════════

    // col03(ペン, 入力10バイト)の存在確認（全列挙は使わない。特定環境でHidSharpの全列挙が落ちるため）
    static string FindVmultiPenPath() {
        foreach (var dev in DeviceList.Local.GetHidDevices(VMULTI_VID, VMULTI_PID)) {
            string path = dev.DevicePath.ToLower();
            if (path.Contains("col03") && dev.GetMaxInputReportLength() == 10) return dev.DevicePath;
        }
        return null;
    }

    // col05(制御 65/65)を開く。デバイス再起動後の呼び出しでは再列挙を待つ
    static bool OpenPenControl() {
        for (int t = 0; t < 20; t++) {
            foreach (var dev in DeviceList.Local.GetHidDevices(VMULTI_VID, VMULTI_PID)) {
                if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                    HidStream s;
                    if (dev.TryOpen(out s)) {
                        _pen = s;
                        Log("制御チャネル(col05)接続: " + dev.DevicePath);
                        return true;
                    }
                }
            }
            Thread.Sleep(500);
        }
        Log("ERR: 制御チャネル(col05)を開けない");
        return false;
    }

    // ══════════════════ 画面・モニタ ══════════════════

    static Rectangle FindVdScreen() {
        var all = System.Windows.Forms.Screen.AllScreens;
        if (all.Length < 2) return Rectangle.Empty;
        foreach (var s in all) if (!s.Primary) return s.Bounds;
        return Rectangle.Empty;
    }

    static Rectangle UnionAllScreens() {
        var r = Rectangle.Empty;
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            r = r.IsEmpty ? s.Bounds : Rectangle.Union(r, s.Bounds);
        return r;
    }

    static string DescribeScreens() {
        var sb = new StringBuilder();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            sb.Append(s.DeviceName).Append("(primary=").Append(s.Primary).Append(" ").Append(s.Bounds.Width).Append("x").Append(s.Bounds.Height).Append("@").Append(s.Bounds.X).Append(",").Append(s.Bounds.Y).Append(") ");
        return sb.ToString();
    }

    // VDDモニタのインターフェースパス（DeviceClasses実測。##?# → \\?\ 正規化）
    static string FindVdMonitorPath() {
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceClasses\" + MonitorIfaceGuid)) {
            if (k == null) return null;
            foreach (var sub in k.GetSubKeyNames())
                if (sub.IndexOf(VdMonitorHw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sub.StartsWith("##?#") ? "\\\\?\\" + sub.Substring(4) : sub;
        }
        return null;
    }

    static string PnpStatus(string instanceId) {
        try {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"(Get-PnpDevice -InstanceId '" + instanceId + "').Status\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi)) {
                string s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return s;
            }
        } catch { return null; }
    }

    static bool RunPnputil(string op, string instanceId, string what) {
        Log("pnputil /" + op + " " + instanceId + " — " + what);
        try {
            var psi = new ProcessStartInfo("pnputil.exe", "/" + op + " \"" + instanceId + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi)) {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                Log("  exit=" + p.ExitCode + (o.Trim() != "" ? " out: " + o.Trim().Replace("\r\n", " / ") : "") + (e.Trim() != "" ? " err: " + e.Trim() : ""));
                return p.ExitCode == 0;
            }
        } catch (Exception ex) {
            Log("  pnputil失敗: " + ex.Message);
            return false;
        }
    }

    // ══════════════════ キャプチャ（全画面。成否判定はしない） ══════════════════

    static void Capture(string name) {
        try {
            var fresh = UnionAllScreens();   // 画面構成は毎回取り直す（切断直後の構成変化に追従）
            if (!fresh.IsEmpty) _all = fresh;
        } catch { }
        if (_all.IsEmpty) { Log("  capture skip(画面構成なし): " + name); return; }
        try {
            string path = Path.Combine(_runDir, name + ".png");
            lock (_ioLock)
            using (var bmp = new Bitmap(_all.Width, _all.Height)) {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(_all.X, _all.Y, 0, 0, new Size(_all.Width, _all.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            string rollRef = _rolling ? " roll/" + _rollCount.ToString("0000") + ".jpg" : "";
            Log("  capture: " + name + ".png" + rollRef);
            _index.Add(name + ".png" + rollRef);
        } catch (Exception ex) {
            Log("  capture失敗(" + name + "): " + ex.Message);
        }
    }

    static void StartRolling() {
        _rolling = true; _rollCount = 0;
        _rollThread = new Thread(() => {
            while (_rolling) {
                try {
                    string path = Path.Combine(_rollDir, _rollCount.ToString("0000") + ".jpg");
                    lock (_ioLock)
                    using (var bmp = new Bitmap(_all.Width, _all.Height)) {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(_all.X, _all.Y, 0, 0, new Size(_all.Width, _all.Height));
                        var jp = new EncoderParameters(1);
                        jp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                        bmp.Save(path, GetJpegEncoder(), jp);
                    }
                    _rollCount++;
                } catch { _rollCount++; }
                Thread.Sleep(500);
            }
        });
        _rollThread.IsBackground = true;
        _rollThread.Start();
        Log("常時キャプチャ開始（0.5秒間隔 → roll/）");
    }

    static void StopRolling() {
        if (!_rolling) return;
        _rolling = false;
        if (_rollThread != null) _rollThread.Join(5000);
        Log("常時キャプチャ終了（" + _rollCount + "枚）");
    }

    static ImageCodecInfo GetJpegEncoder() {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
    }

    // ══════════════════ スナップショット・index ══════════════════

    static void Snapshot(string label) {
        Log("[" + label + "] 画面: " + DescribeScreens());
        var pens = EnumVmultiPens();
        Log("[" + label + "] vmultiポインタ登録: " + pens.Count + "件");
        foreach (var p in pens)
            Log("  " + p.Path + " 登録=" + p.Pointer + (p.HasRect ? " display(" + p.Display.L + "," + p.Display.T + ")-(" + p.Display.R + "," + p.Display.B + ")" : ""));
        try {
            using (var k = Registry.LocalMachine.OpenSubKey(DigimonKey)) {
                if (k == null) Log("[" + label + "] Digimon: キーなし");
                else foreach (var v in k.GetValueNames())
                    if (v.IndexOf("col0", StringComparison.OrdinalIgnoreCase) >= 0)
                        Log("[" + label + "] Digimon: " + ShortName(v));
            }
        } catch { }
        Log("[" + label + "] VDDアダプタ=" + (PnpStatus(VdAdapterId) ?? "?") + " vmultiルート=" + (PnpStatus(VmultiRootId) ?? "?"));
    }

    static string BuildIndex() {
        var sb = new StringBuilder();
        sb.AppendLine("# 観測インデックス");
        sb.AppendLine();
        sb.AppendLine("- 実行: " + _runDir);
        sb.AppendLine("- 名前付きキャプチャ（時点: roll/NNNN.jpg）と期待される見え方");
        sb.AppendLine();
        string[] expect = {
            "01_接続後|仮想液タブ接続完了。電卓はまだ無い。メインに変化なし",
            "02_初期盤面|仮想モニタは空のデスクトップ（電卓なし）",
            "03_電卓配置後|電卓が仮想モニタ上に在り、表示は0",
            "04_前_7|表示0（7を押す直前）", "05_後_7|表示7",
            "06_前_×|表示7", "07_後_×|表示7×（演算子表示）",
            "08_前_6|表示7×", "09_後_6|表示7×6",
            "10_前_=|表示7×6", "11_後_=|表示42",
            "12_最終表示|42",
            "13_電卓終了後|仮想モニタから電卓が消える",
            "14_切断後|仮想モニタがデスクトップから消える（メインのみ）",
        };
        foreach (var e in expect) {
            int i = e.IndexOf('|');
            string n = e.Substring(0, i) + ".png";
            string exp = e.Substring(i + 1);
            string roll = "";
            foreach (var line in _index) if (line.StartsWith(n)) { int j = line.IndexOf(" roll/"); if (j >= 0) roll = line.Substring(j + 1); }
            sb.AppendLine("- " + n + "  " + (roll != "" ? "（" + roll + "）" : "") + " — " + exp);
        }
        sb.AppendLine();
        sb.AppendLine("- roll/: 接続完了〜切断完了の全画面連続キャプチャ。判定はキャプチャの内容のみに基づく");
        sb.AppendLine("- 判定者への注意: メイン領域（左側の大画面）に意図しない変化（メニュー・入力痕・URL変化）が無いことも同一系列で確認すること");
        return sb.ToString();
    }

    // ══════════════════ ユーティリティ ══════════════════

    static bool IsAdmin() {
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static string GetArg(string[] args, string name) {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }
}
