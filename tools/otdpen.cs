// otdpen.cs — OTD経由のペン入力ツール（メインモニタ誤クリック防止装置つき）
//
// 経路: このツール → vmulti(col05制御コレクション) → vmulti.sys → Digitizerレポート(col03/04)
//       → OTD(パーサー: ClipStudioBridge.VMultiOtd) → OTD Absolute Mode → 仮想モニタ
// 書式は VoiDPlugins.Library.VMulti の公式構造体に従う（12バイト）。
//
// 安全装置（2026-09-23 メイン誤クリック事故の再発防止）:
//   A. 起動時にOTD設定(settings.json)を読み、Display area が仮想モニタ内に
//      収まっていることを検証（収まらなければ即拒否）
//   B. クリック(down)の直前に必ず in-range 移動で着地を実測し、期待座標（自前写像計算）
//      から TOLERANCE px 以上外れたら以降のクリックを全中止（OTD側の非決定的な
//      全体写像・設定外れを検知する）
//   C. 中止時は即座に lift 報告を送りペン状態を残さない
//
// 使い方:
//   otdpen seq x1 y1 x2 y2 ...   … 指定タブレット座標(0-32767)を順にタップ（筆圧4096）
//   otdpen probe                  … 中央へ in-range 移動のみ（クリックなし）
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

static class OtdPen {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const byte TIP = 0x01, INRANGE = 0x02;
    const int TOLERANCE = 30; // 着地の許容誤差（物理px）

    // OTD設定から読む領域（mm/px）
    static double dispW, dispH, dispX, dispY;   // Display area（物理px、X/Yは中心）
    static double tabW, tabH, tabX, tabY;       // Tablet area（mm、X/Yは中心）
    static double maxSX = 32767, maxSY = 32767, digW = 200, digH = 150; // 定義JSONの値

    // 仮想モニタ（物理px）
    static Rectangle _vd;

    static int Main(string[] args) {
        try { SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); } catch { }
        string mode = args.Length > 0 ? args[0] : "";

        _vd = FindVirtualBounds();
        if (_vd.IsEmpty) { Console.WriteLine("ERR: 仮想モニタが見つからない"); return 1; }
        Console.WriteLine("仮想モニタ: (" + _vd.X + "," + _vd.Y + ") " + _vd.Width + "x" + _vd.Height);

        if (!LoadOtdSettings()) return 1;

        using (var s = OpenVmultiControl()) {
            if (mode == "probe") {
                Send(s, 16383, 16383, 0, INRANGE);
                Thread.Sleep(400);
                Send(s, 16383, 16383, 0, 0);
                Thread.Sleep(300);
                POINT cp; GetCursorPos(out cp);
                Console.WriteLine("着地: (" + cp.X + "," + cp.Y + ") → " + (_vd.Contains(cp.X, cp.Y) ? "仮想モニタ内 OK" : "仮想モニタ外 FAIL"));
                return _vd.Contains(cp.X, cp.Y) ? 0 : 1;
            }

            if (mode == "seq") {
                if (args.Length < 3 || (args.Length - 1) % 2 != 0) {
                    Console.WriteLine("usage: otdpen seq x1 y1 [x2 y2 ...]");
                    return 1;
                }
                int n = 0;
                for (int i = 1; i + 1 < args.Length; i += 2) {
                    ushort x = ushort.Parse(args[i]), y = ushort.Parse(args[i + 1]);
                    int r = SafeTap(s, x, y, 4096);
                    if (r != 0) return r; // 中止時は以降のクリックを行わない
                    n++;
                    CaptureVd("tap" + n); // 各タップ後に仮想モニタを1枚（表示の変化で押されたかが分かる）
                    Thread.Sleep(200);
                }
                return 0;
            }

            Console.WriteLine("usage: otdpen probe | seq x1 y1 [...]");
            return 1;
        }
    }

    // ── 安全装置つきタップ ──
    static int SafeTap(HidStream s, ushort tx, ushort ty, ushort pressure) {
        // 期待表示座標（OTDと同じ写像を自前で計算）
        double ex = ExpectedX(tx), ey = ExpectedY(ty);
        Console.WriteLine("tap (" + tx + "," + ty + ") 期待着地: (" + (int)ex + "," + (int)ey + ")");

        if (!_vd.Contains((int)ex, (int)ey)) {
            Console.WriteLine("ABORT: 期待着地が仮想モニタ外。座標またはOTD設定が不正");
            return 1;
        }

        // クリック前に in-range 移動で着地を実測（ここでメイン側への逸れを検知する）
        Send(s, tx, ty, 0, INRANGE);
        Thread.Sleep(120);
        POINT cp; GetCursorPos(out cp);
        bool ok = Math.Abs(cp.X - ex) <= TOLERANCE && Math.Abs(cp.Y - ey) <= TOLERANCE;
        Console.WriteLine("  実測着地: (" + cp.X + "," + cp.Y + ") " + (ok ? "OK" : "→ 期待位置から外れた"));
        if (!ok) {
            Console.WriteLine("ABORT: OTDの写像が期待と異なる（非決定的全体写像の疑い）。以降のクリックを中止し、ペンをlift");
            Send(s, tx, ty, 0, 0);
            return 2;
        }

        // クリック本体
        Send(s, tx, ty, pressure, (byte)(INRANGE | TIP));
        Thread.Sleep(60);
        Send(s, tx, ty, 0, INRANGE);
        Thread.Sleep(60);
        Send(s, tx, ty, 0, 0);
        Thread.Sleep(80);
        return 0;
    }

    // ── OTD設定の読み込みと検証 ──
    static bool LoadOtdSettings() {
        try {
            string json = File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenTabletDriver", "settings.json"));
            var abs = ExtractObject(json, "AbsoluteModeSettings");
            var disp = ExtractObject(abs, "Display");
            var tab = ExtractObject(abs, "Tablet");
            dispW = GetNum(disp, "Width"); dispH = GetNum(disp, "Height");
            dispX = GetNum(disp, "X"); dispY = GetNum(disp, "Y");
            tabW = GetNum(tab, "Width"); tabH = GetNum(tab, "Height");
            tabX = GetNum(tab, "X"); tabY = GetNum(tab, "Y");
        } catch (Exception ex) {
            Console.WriteLine("ERR: OTD settings.json 読み込み失敗: " + ex.Message);
            return false;
        }
        Console.WriteLine("OTD Display area: " + dispW + "x" + dispH + "@<" + dispX + "," + dispY + ">  Tablet area: " + tabW + "x" + tabH + "@<" + tabX + "," + tabY + ">");

        // 安全装置A: Display area が仮想モニタに収まること
        double l = dispX - dispW / 2, t = dispY - dispH / 2, r = dispX + dispW / 2, b = dispY + dispH / 2;
        bool inside = l >= _vd.X && t >= _vd.Y && r <= _vd.X + _vd.Width && b <= _vd.Y + _vd.Height;
        if (!inside) {
            Console.WriteLine("ERR: OTD Display area が仮想モニタ外にはみ出す (" + l + "," + t + ")-(" + r + "," + b + ")。設定を直してから実行すること");
            return false;
        }
        return true;
    }

    // ── OTDと同じ写像の自前計算（回転0・clipping前提） ──
    static double ExpectedX(ushort tx) {
        double mmX = tx / maxSX * digW;                       // レポート→mm
        double rel = (mmX - (tabX - tabW / 2)) / tabW;        // タブレット領域内相対
        rel = Clamp01(rel);
        return (dispX - dispW / 2) + rel * dispW;             // ディスプレイ絶対（物理px）
    }
    static double ExpectedY(ushort ty) {
        double mmY = ty / maxSY * digH;
        double rel = (mmY - (tabY - tabH / 2)) / tabH;
        rel = Clamp01(rel);
        return (dispY - dispH / 2) + rel * dispH;
    }
    // タップ後の証拠キャプチャ（仮想モニタ領域のみ）
    static string _obsDir;
    static void CaptureVd(string name) {
        if (_obsDir == null)
            _obsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures",
                "obs-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_obsDir);
        string path = Path.Combine(_obsDir, name + ".png");
        using (var bmp = new Bitmap(_vd.Width, _vd.Height)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(_vd.X, _vd.Y, 0, 0, new Size(_vd.Width, _vd.Height));
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("  capture: " + path);
    }

    static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

    static Rectangle FindVirtualBounds() {
        foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            if (!sc.Primary) return new Rectangle(sc.Bounds.X, sc.Bounds.Y, sc.Bounds.Width, sc.Bounds.Height);
        return Rectangle.Empty;
    }

    // ── vmulti(col05) 書き込み ──
    static HidStream OpenVmultiControl() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti control collection (65/65) not found");
    }

    // DigitizerInputReport 12バイト。col03(0x05)/col04(0x06)の両方へ書く
    //（OTDが起動のたびにどちらかを開くため。読まれない方への書き込みは無害）
    static void Send(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        WriteReport(s, 0x05, x, y, pressure, buttons);
        WriteReport(s, 0x06, x, y, pressure, buttons);
    }

    static void WriteReport(HidStream s, byte reportId, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[12];
        b[0] = 0x40;      // VMultiID
        b[1] = 0x0B;      // ReportLength = sizeof(12) - 1
        b[2] = reportId;
        b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        b[10] = 0; b[11] = 0; // Tilt
        s.Write(b);
    }

    // ── settings.json 用の最小JSON読み取り（依存DLLなしで読むため） ──
    static string ExtractObject(string json, string key) {
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) throw new Exception("key not found: " + key);
        int c = json.IndexOf('{', i);
        int depth = 0, j = c;
        for (; j < json.Length; j++) {
            if (json[j] == '{') depth++;
            else if (json[j] == '}') { depth--; if (depth == 0) break; }
        }
        return json.Substring(c, j - c + 1);
    }
    static double GetNum(string obj, string key) {
        int i = obj.IndexOf("\"" + key + "\"");
        if (i < 0) throw new Exception("key not found: " + key);
        int c = obj.IndexOf(':', i) + 1;
        while (c < obj.Length && (obj[c] == ' ' || obj[c] == '\t' || obj[c] == '\r' || obj[c] == '\n')) c++;
        int e = c;
        while (e < obj.Length && (char.IsDigit(obj[e]) || obj[e] == '-' || obj[e] == '.' || obj[e] == 'e' || obj[e] == 'E' || obj[e] == '+')) e++;
        return double.Parse(obj.Substring(c, e - c), System.Globalization.CultureInfo.InvariantCulture);
    }
}
