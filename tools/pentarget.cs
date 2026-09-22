// pentarget.cs — ディスプレイ構成から仮想モニタ上の座標を計算してペン報告を送る
// やり方: Screen.AllScreensで構成を取得 → 仮想モニタの範囲を特定 → 正規化座標を計算 → VMultiに書き込み
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using HidSharp;

static class PenTarget {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    static void Main(string[] args) {
        // 1. 全ディスプレイの構成を取得
        var screens = System.Windows.Forms.Screen.AllScreens;
        Console.WriteLine("=== 現在のディスプレイ構成 ===");
        foreach (var s in screens) {
            Console.WriteLine("  " + s.DeviceName
                + " Primary=" + s.Primary
                + " Bounds=(" + s.Bounds.X + "," + s.Bounds.Y + ") " + s.Bounds.Width + "x" + s.Bounds.Height);
        }

        // 2. メインディスプレイ（Primary=true）を特定
        var primary = screens.First(s => s.Primary);
        Console.WriteLine("\nメインディスプレイ: " + primary.DeviceName + " " + primary.Bounds.Width + "x" + primary.Bounds.Height);

        // 3. 拡張ディスプレイ（Primary=false）を特定
        var extended = screens.FirstOrDefault(s => !s.Primary);
        if (extended == null) { Console.WriteLine("ERR: 拡張ディスプレイが見つかりません"); return; }
        Console.WriteLine("拡張ディスプレイ（仮想モニタ）: " + extended.DeviceName
            + " 位置=(" + extended.Bounds.X + "," + extended.Bounds.Y + ")"
            + " サイズ=" + extended.Bounds.Width + "x" + extended.Bounds.Height);

        // 4. デスクトップ全体の範囲を計算
        int minX = screens.Min(s => s.Bounds.X);
        int minY = screens.Min(s => s.Bounds.Y);
        int maxX = screens.Max(s => s.Bounds.X + s.Bounds.Width);
        int maxY = screens.Max(s => s.Bounds.Y + s.Bounds.Height);
        int desktopW = maxX - minX;
        int desktopH = maxY - minY;
        Console.WriteLine("デスクトップ全体: (" + minX + "," + minY + ") 〜 (" + maxX + "," + maxY + ") = " + desktopW + "x" + desktopH);

        // 5. 仮想モニタ上の目標座標を正規化 → ペン報告値に変換
        //    仮想モニタの中心をターゲットにする
        int targetVx = extended.Bounds.Width / 2;
        int targetVy = extended.Bounds.Height / 2;
        int absX = extended.Bounds.X + targetVx;
        int absY = extended.Bounds.Y + targetVy;
        Console.WriteLine("\n=== 座標計算 ===");
        Console.WriteLine("仮想モニタ上のターゲット: (" + targetVx + "," + targetVy + ") 仮想モニタローカル座標");
        Console.WriteLine("デスクトップ絶対座標: (" + absX + "," + absY + ")");
        float nx = (float)absX / desktopW;
        float ny = (float)absY / desktopH;
        Console.WriteLine("正規化座標: nx=" + nx.ToString("0.0000") + " ny=" + ny.ToString("0.0000"));
        ushort penX = (ushort)(nx * 32767f);
        ushort penY = (ushort)(ny * 32767f);
        Console.WriteLine("ペン報告値: X=" + penX + " Y=" + penY);

        // 6. ペン報告を送信
        Console.WriteLine("\n=== ペン報告送信 ===");
        using (var s = OpenVmulti()) {
            // DOWN（接触）
            SendDigi(s, penX, penY, 4096, 0x01);
            Thread.Sleep(80);
            // UP（離す）
            SendDigi(s, penX, penY, 0, 0x00);
            Thread.Sleep(150);
        }
        Console.WriteLine("ペン報告送信完了");

        // 7. カーソル位置確認
        POINT cp = new POINT();
        GetCursorPos(out cp);
        Console.WriteLine("カーソル位置: (" + cp.X + "," + cp.Y + ")");
    }

    static HidStream OpenVmulti() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device not found");
    }

    static void SendDigi(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }
}
