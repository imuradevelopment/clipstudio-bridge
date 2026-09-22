// setdisp.cs — メインディスプレイの切り替え（仮想モニタ⇔物理モニタ）
// setdisp list            … アクティブディスプレイの一覧
// setdisp swap <vw>       … 幅vwのディスプレイを(0,0)メインにし、他を右にずらす
using System;
using System.Runtime.InteropServices;

static class SetDisp {
    const int ENUM_CURRENT_SETTINGS = -1;
    const uint DM_POSITION = 0x20;
    const uint ATTACHED_TO_DESKTOP = 0x1;
    const uint CDS_UPDATEREGISTRY = 0x1;
    const uint CDS_NORESET = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
        public int dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplayDevices(string dev, uint num, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettings(string name, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr wnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettingsEx(string dev, IntPtr dm, IntPtr wnd, uint flags, IntPtr param);

    static DEVMODE Current(string name) {
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        dm.dmDriverExtra = 0;
        EnumDisplaySettings(name, ENUM_CURRENT_SETTINGS, ref dm);
        return dm;
    }

    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "list") { List(); return 0; }
        if (args.Length == 2 && args[0] == "swap") { return Swap(int.Parse(args[1])); }
        Console.WriteLine("usage: setdisp list | setdisp swap <virtualWidth>");
        return 2;
    }

    static void List() {
        for (uint i = 0; ; i++) {
            var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;
            DEVMODE dm = Current(dd.DeviceName);
            Console.WriteLine(dd.DeviceName + " " + dm.dmPelsWidth + "x" + dm.dmPelsHeight
                + " pos(" + dm.dmPositionX + "," + dm.dmPositionY + ")"
                + " primary=" + (((dd.StateFlags & 4) != 0) ? "yes" : "no"));
        }
    }

    static int Swap(int virtualWidth) {
        // アクティブな全ディスプレイの現在設定を収集
        var names = new System.Collections.Generic.List<string>();
        var modes = new System.Collections.Generic.List<DEVMODE>();
        for (uint i = 0; ; i++) {
            var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;
            DEVMODE dm = Current(dd.DeviceName);
            names.Add(dd.DeviceName); modes.Add(dm);
        }
        if (modes.Count < 2) { Console.WriteLine("ERR: need 2+ active displays"); return 1; }

        // 仮想モニタ(幅virtualWidth)を(0,0)へ、他をその右へ
        int cursorX = 0;
        bool virtualFound = false;
        for (int i = 0; i < modes.Count; i++) {
            if (modes[i].dmPelsWidth == virtualWidth) {
                DEVMODE dm = modes[i];
                dm.dmFields |= DM_POSITION;
                dm.dmPositionX = 0; dm.dmPositionY = 0;
                ChangeDisplaySettingsEx(names[i], ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                cursorX = virtualWidth; virtualFound = true;
            }
        }
        if (!virtualFound) { Console.WriteLine("ERR: virtual display (width " + virtualWidth + ") not found"); return 1; }
        for (int i = 0; i < modes.Count; i++) {
            if (modes[i].dmPelsWidth == virtualWidth) continue;
            DEVMODE dm = modes[i];
            dm.dmFields |= DM_POSITION;
            dm.dmPositionX = cursorX; dm.dmPositionY = 0;
            ChangeDisplaySettingsEx(names[i], ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            cursorX += modes[i].dmPelsWidth;
        }
        // 適用
        int r = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Console.WriteLine("apply result: " + r);
        return (r == 0) ? 0 : 1;
    }
}
