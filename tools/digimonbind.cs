// digimonbind.cs — 仮想液タブの認識構造(ペン↔モニタ関連付け)をOSの正規保存先へ書く
//
// 保存先と形式(Microsoft純正 MultiDigiMon.exe と同じ。形式は2つのOSS実装で相互確認済み):
//   キー   : HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon
//   値名   : "20-" + <ペンのHIDインターフェースパス>   (逐語。正規化禁止)
//   値データ : <モニタのインターフェースパス> (REG_SZ)
//   反映   : ペンのHIDデバイスを pnputil /restart-device で再起動→入力スタックが再読込
//
// 観測(GetPointerDevices): OSが各ペン/タッチデバイスを「今どのモニタに紐付けたか」を
//   入力を1つも流さずに読む公式API。マップ先の変化はこれで確認する(メイン事故防止)。
//
// 使い方:
//   digimonbind list     … 現在のOS紐付けを観測(変更なし・管理者不要)
//   digimonbind bind     … Digimon書き+HID再起動+再観測(要管理者 UAC 1回)
//   digimonbind unbind   … 書いた値を削除+HID再起動(要管理者)
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class DigimonBind {
    const string DigimonKey = @"SOFTWARE\Microsoft\Wisp\Pen\Digimon";

    // VDD仮想モニタのインターフェースパス(DeviceClasses実測値)
    const string VdMonitorPath = @"\\?\DISPLAY#MTT1337#1&28a6823a&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    // vmultiのDigitizerコレクション(col03標準/col04拡張)のパス(現在の列挙インスタンス実測値)
    static readonly string[] PenPaths = {
        @"\\?\HID#hid&Col03#1&2d595ca7&3&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"\\?\HID#hid&Col04#1&2d595ca7&3&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    };

    // ── GetPointerDevices: OSの現在のペン↔モニタ紐付けを読む(入力ゼロ) ──
    [DllImport("user32.dll")]
    static extern uint GetPointerDevices(ref uint deviceCount, IntPtr devices);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct POINTER_DEVICE_INFO {
        public uint dwSize;
        public IntPtr device;
        public IntPtr peripheralName;   // PWSTR
        public uint pointerDeviceType;  // 1=内蔵ペン 2=外付けペン 3=タッチ 4=タッチパッド
        public IntPtr monitor;          // HMONITOR
        public ushort vendorId;
        public ushort productId;
        public byte versionIsAvailable; // BOOLEAN(1バイト)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string productString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string manufacturerString;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX lpmi);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static void ListPointerDevices() {
        uint n = 0;
        GetPointerDevices(ref n, IntPtr.Zero);
        if (n == 0) { Console.WriteLine("pointerデバイスなし"); return; }
        int size = Marshal.SizeOf<POINTER_DEVICE_INFO>();
        IntPtr buf = Marshal.AllocHGlobal(size * (int)n);
        for (int i = 0; i < n; i++) Marshal.WriteInt32(buf + i * size, size); // dwSize
        uint r2 = GetPointerDevices(ref n, buf);
        Console.WriteLine(string.Format("GetPointerDevices: ret={0} 数={1} sizeof={2}", r2, n, size));
        if (r2 == 0) { Marshal.FreeHGlobal(buf); Console.WriteLine("GetPointerDevices失敗"); return; }
        for (int i = 0; i < n; i++) {
            IntPtr p = buf + i * size;
            IntPtr devHandle = Marshal.ReadIntPtr(p + 8);
            IntPtr namePtr  = Marshal.ReadIntPtr(p + 16);
            uint type       = (uint)Marshal.ReadInt32(p + 24);
            IntPtr mon      = Marshal.ReadIntPtr(p + 32);
            ushort vid      = (ushort)Marshal.ReadInt16(p + 40);
            ushort pid      = (ushort)Marshal.ReadInt16(p + 42);
            string product  = Marshal.PtrToStringUni(p + 48);
            string name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr) : "";
            if (product != null && product.Length > 40) product = product.Substring(0, 40);
            string monStr = "(未紐付け)";
            if (mon != IntPtr.Zero) {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoW(mon, ref mi)) {
                    var r = mi.rcMonitor;
                    monStr = string.Format("{0} ({1},{2})-({3},{4})", mi.szDevice, r.L, r.T, r.R, r.B);
                }
            }
            Console.WriteLine(string.Format("[{0}] vid=0x{1:X4} pid=0x{2:X4} type={3} 名={4} 製品={5}",
                i, vid, pid, type, name, product));
            Console.WriteLine("    → 紐付けモニタ: " + monStr);
        }
        Marshal.FreeHGlobal(buf);
    }

    // ── 書き込み/削除 + HID再起動 ──
    static void WriteBindings() {
        using (var key = Registry.LocalMachine.CreateSubKey(DigimonKey)) {
            if (key == null) throw new Exception("Digimonキーを作れない");
            foreach (var p in PenPaths) {
                var name = "20-" + p;
                key.DeleteValue(name, false);           // MultiDigiMonと同じ: 削ってから
                key.SetValue(name, VdMonitorPath, RegistryValueKind.String);
                Console.WriteLine("書き込み: " + name);
                Console.WriteLine("     → " + VdMonitorPath);
            }
        }
    }
    static void RemoveBindings() {
        using (var key = Registry.LocalMachine.OpenSubKey(DigimonKey, writable: true)) {
            if (key == null) { Console.WriteLine("Digimonキーなし"); return; }
            foreach (var p in PenPaths) key.DeleteValue("20-" + p, false);
            Console.WriteLine("削除済み");
        }
    }
    static void RestartVmulti() {
        var psi = new ProcessStartInfo("pnputil.exe", "/restart-device \"ROOT\\HIDCLASS\\0000\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        using (var p = Process.Start(psi)) { Console.WriteLine(p.StandardOutput.ReadToEnd()); p.WaitForExit(15000); }
    }

    static bool IsAdmin() {
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    static int RelaunchElevated(string mode) {
        var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, mode) { Verb = "runas", UseShellExecute = true };
        try { Process.Start(psi).WaitForExit(); return 0; }
        catch (Exception ex) { Console.WriteLine("UAC拒否/失敗: " + ex.Message); return 9; }
    }

    static int Main(string[] args) {
        string mode = args.Length > 0 ? args[0] : "list";
        Console.WriteLine("=== OSの現在の紐付け(GetPointerDevices / 入力ゼロ観測) ===");
        ListPointerDevices();
        if (mode == "list") return 0;
        if (!IsAdmin()) return RelaunchElevated(mode);

        if (mode == "bind") {
            Console.WriteLine("=== Digimon書き込み ===");
            WriteBindings();
        } else if (mode == "unbind") {
            Console.WriteLine("=== Digimon削除 ===");
            RemoveBindings();
        }
        Console.WriteLine("=== vmulti再起動(反映) ===");
        RestartVmulti();
        System.Threading.Thread.Sleep(1500);
        Console.WriteLine("=== 再起動後のOS紐付け ===");
        ListPointerDevices();
        return 0;
    }
}
