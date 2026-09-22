// peninject.cs — vmultiへ直接ペン報告を書く最小ツール（OTD不要・OS直読経路専用）
//
// 用途: Tablet PC Settings のセットアップ識別フローなど、OSのHIDスタックが
//       vmulti(col03 標準Digitizer)を読んでいる状態に報告を届ける。
//       書き込むのは ReportID 0x05 のみ（OTDが開いていなくてもOS経路に届く）。
//
// 使い方:
//   peninject move x y [rid]   … in-range 移動のみ（クリックなし。着地観測用）
//   peninject tap x y [rid]    … タップ（tip down → up、筆圧4096）
//   peninject down x y [rid]   … tip down のみ
//   peninject up [rid]         … lift 報告
//   座標はタブレット座標 0-32767。rid は 5 か 6（省略時 5）。
using System;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

static class PenInject {
    const byte TIP = 0x01, INRANGE = 0x02;
    static byte _rid = 0x05;

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static int Main(string[] args) {
        try { SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); } catch { }
        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "" || (mode != "up" && args.Length < 3)) {
            Console.WriteLine("usage: peninject move|tap|down x y [5|6] | up [5|6]");
            return 1;
        }
        ushort x = 0, y = 0;
        int argi = 1;
        if (mode != "up") {
            x = ushort.Parse(args[1]); y = ushort.Parse(args[2]); argi = 3;
        } else argi = 1;
        if (args.Length > argi) {
            byte r = byte.Parse(args[argi]);
            _rid = (byte)(r == 6 ? 0x06 : 0x05);
        }
        Console.WriteLine("ReportID=0x0" + _rid.ToString("X"));

        using (var s = OpenVmultiControl()) {
            switch (mode) {
                case "move":
                    Send(s, x, y, 0, INRANGE);
                    Thread.Sleep(300);
                    Send(s, x, y, 0, 0);
                    break;
                case "down":
                    Send(s, x, y, 0, INRANGE); Thread.Sleep(60);
                    Send(s, x, y, 4096, (byte)(TIP | INRANGE));
                    break;
                case "up":
                    Send(s, x, y, 0, INRANGE); Thread.Sleep(60);
                    Send(s, x, y, 0, 0);
                    break;
                default: // tap
                    Send(s, x, y, 0, INRANGE); Thread.Sleep(80);
                    Send(s, x, y, 4096, (byte)(TIP | INRANGE)); Thread.Sleep(80);
                    Send(s, x, y, 0, INRANGE); Thread.Sleep(60);
                    Send(s, x, y, 0, 0);
                    break;
            }
            Thread.Sleep(200);
            POINT cp; GetCursorPos(out cp);
            Console.WriteLine("報告後カーソル: (" + cp.X + "," + cp.Y + ")");
            return 0;
        }
    }

    static HidStream OpenVmultiControl() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti control collection (65/65) not found");
    }

    static void Send(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[12];
        b[0] = 0x40;      // VMultiID
        b[1] = 0x0B;      // ReportLength
        b[2] = _rid;      // 標準(0x05)/拡張(0x06) Digitizerレポート
        b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        b[10] = 0; b[11] = 0; // Tilt
        s.Write(b);
        Thread.Sleep(20);
    }
}
