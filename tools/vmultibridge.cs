// vmultibridge.cs — vmulti装置にペン報告を書き込む（OTD経由でクリスタに届ける）
// 使い方: vmultibridge.exe <tap|stroke> <vdX> <vdY> [pressure]
//   tap    … 仮想モニタ上の指定座標を1クリック
//   stroke … 仮想モニタ上の指定座標間を線で描く
// 座標系: 仮想モニタのローカル座標（左上=0,0）
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class VmultiBridge {
    // ── Win32 ──
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref ATTR a);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(IntPtr h, byte[] buf, uint size);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr d, IntPtr i, ref Guid g, uint m, ref SP_DEV_IF_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr d, ref SP_DEV_IF_DATA data, IntPtr detail, uint size, out uint req, IntPtr s);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr d);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    static Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    public struct ATTR { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEV_IF_DATA { public int cbSize; public Guid ifClass; public uint flags; public IntPtr reserved; }

    // ── VMulti AbsoluteInputReport (10B) ──
    // [0]=0x40 [1]=0x09 [2]=0x09(reportID) [3]=buttons [4..5]=X [6..7]=Y [8..9]=Pressure
    static HidStream OpenVmulti() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device (65/65) not found — VMulti driver installed?");
    }

    static void SendPenReport(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }

    static void Main(string[] args) {
        if (args.Length < 4) {
            Console.WriteLine("usage: vmultibridge <tap|stroke|clear> <vx> <vy> [vx2 vy2] [pressure]");
            Console.WriteLine("  vx,vy = 仮想モニタ内座標（0-800, 0-600）");
            Console.WriteLine("  tap: 指定座標を1クリック");
            Console.WriteLine("  stroke: (vx,vy)から(vx2,vy2)まで線を描く");
            Console.WriteLine("  clear: 全てのクリック報告をリセット（何も送らない）");
            return;
        }

        string mode = args[0];
        int vx = int.Parse(args[1]), vy = int.Parse(args[2]);
        ushort pressure = args.Length > 3 ? ushort.Parse(args[3]) : 4096;

        using (var s = OpenVmulti()) {
            switch (mode) {
                case "tap":
                    PenTap(s, vx, vy, pressure);
                    break;
                case "stroke":
                    int vx2 = int.Parse(args[3]), vy2 = int.Parse(args[4]);
                    ushort p2 = args.Length > 5 ? ushort.Parse(args[5]) : pressure;
                    PenStroke(s, vx, vy, vx2, vy2, pressure, p2);
                    break;
                default:
                    Console.WriteLine("unknown mode: " + mode);
                    return;
            }
        }
        Console.WriteLine("done: " + mode + " (" + vx + "," + vy + ")");
    }

    static void PenTap(HidStream s, int vx, int vy, ushort pressure) {
        ushort px = (ushort)((float)vx / VD_W * 32767f);
        ushort py = (ushort)((float)vy / VD_H * 32767f);
        // DOWN
        SendPenReport(s, px, py, pressure, 0x01); // Left button down
        Thread.Sleep(60);
        // UP
        SendPenReport(s, px, py, 0, 0x00); // Pressure 0, lift
        Thread.Sleep(80);
        Console.WriteLine("  tap: (" + vx + "," + vy + ") pressure=" + pressure);
    }

    static void PenStroke(HidStream s, int x1, int y1, int x2, int y2, ushort p1, ushort p2) {
        ushort px1 = ToPen(x1, VD_W), py1 = ToPen(y1, VD_H);
        ushort px2 = ToPen(x2, VD_W), py2 = ToPen(y2, VD_H);
        // DOWN
        SendPenReport(s, px1, py1, p1, 0x01);
        Thread.Sleep(60);
        // MOVE ×10（線を描く）
        for (int i = 1; i <= 10; i++) {
            float t = (float)i / 10;
            ushort mx = Lerp(px1, px2, t), my = Lerp(py1, py2, t);
            ushort mp = Lerp(p1, p2, t);
            SendPenReport(s, mx, my, mp, 0x01);
            Thread.Sleep(30);
        }
        // UP
        SendPenReport(s, px2, py2, 0, 0x00);
        Thread.Sleep(80);
    }

    static ushort Lerp(ushort a, ushort b, float t) {
        return (ushort)(a + (b - a) * t);
    }

    static ushort ToPen(int val, int max) {
        return (ushort)((float)val / max * 32767f);
    }

    const int VD_W = 800;
    const int VD_H = 600;
}
