// reroute.cs — vmultiペンのルーティング先（デジタイザ↔ディスプレイの関連付け）を
//              メインモニタからVDD仮想モニタへ切り替える。
//
// 原理（Microsoft公式仕様に基づく）:
//   [原因] vmulti(ROOT\HIDCLASS\0000)はルート列挙でRemovable=0のため、ContainerIDが
//          システムコンテナ{00000000-0000-0000-FFFF-FFFFFFFFFFFF}＝内蔵モニタと同一になる。
//          Digitizer Display Mappingは「ContainerID一致＝統合デジタイザ」として扱うため、
//          ペンがメインモニタに紐付く。→ 着地点がメインに固定される構造的原因。
//   [対処] 「Container IDs Generated from a Removable Device Capability Override」(learn.microsoft.com)
//          の正規機構で vmulti を Removable 扱いにオーバーライドする:
//            HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides
//              pentablet#hid            ← HardwareID(pentablet\hid)の\を#に置換
//                LocationPaths
//                  *                     ← 全devnodeに適用
//                    Removable=1 (DWORD)
//          → PnPマネージャが vmulti に独自ContainerIDを生成 → メインとの一致が解消され、
//          「外付けデジタイザ＋外付けディスプレイ1台(VDD)」の自動マップ規則の対象になる。
//          （Enum\...\ContainerID の直書きは devnode 有効化のたびOSに上書きされるため使わない）
//
// 使い方:
//   reroute.exe probe          … 何も変えず着地検証のみ（クリックなし移動報告。メイン無害・管理者不要）
//   reroute.exe                … DeviceOverrides書き込み+vmulti再有効化+着地検証（要管理者、UAC 1回）
//   reroute.exe revert         … DeviceOverridesを削除し元へ戻す（要管理者）
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;
using HidSharp;

static class Reroute {
    const string VMULTI_INSTANCE = @"ROOT\HIDCLASS\0000";
    const string VMULTI_KEY = @"SYSTEM\CurrentControlSet\Enum\" + VMULTI_INSTANCE;
    static readonly string SystemContainer = "{00000000-0000-0000-FFFF-FFFFFFFFFFFF}";
    static readonly string OverrideRoot = @"SYSTEM\CurrentControlSet\Control\DeviceOverrides";

    // ── Win32 ──
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out IntPtr dn, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Disable_DevNode(IntPtr dn, uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Enable_DevNode(IntPtr dn, uint flags);

    static int Main(string[] args) {
        // 昇格プロセスの出力をファイルにも残す（bash側から読めないため）
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures", "reroute-log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
        Console.SetOut(new TeeWriter(logPath));

        bool revert = args.Length > 0 && args[0] == "revert";
        bool probe = args.Length > 0 && args[0] == "probe";
        try {
            if (!IsAdmin() && !probe) return RelaunchElevated(revert);
            if (probe) return VerifyLanding();
            return revert ? DoRevert() : DoReroute();
        } catch (Exception ex) {
            Console.WriteLine("FAIL: " + ex.Message);
            return 2;
        }
    }

    class TeeWriter : TextWriter {
        TextWriter _file;
        public TeeWriter(string path) { _file = new StreamWriter(path, true, System.Text.Encoding.UTF8); }
        public override System.Text.Encoding Encoding { get { return System.Text.Encoding.UTF8; } }
        public override void WriteLine(string s) { base.WriteLine(s); _file.WriteLine(s); _file.Flush(); }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }

    static bool IsAdmin() {
        System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static int RelaunchElevated(bool revert) {
        Console.WriteLine("管理者権限が必要なためUACで昇格します（「はい」を押してください）...");
        Process p = new Process();
        p.StartInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;
        p.StartInfo.Arguments = revert ? "revert" : "";
        p.StartInfo.UseShellExecute = true;
        p.StartInfo.Verb = "runas";
        try { p.Start(); } catch (Exception ex) {
            Console.WriteLine("昇格が拒否されました: " + ex.Message);
            return 3;
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    // vmultiの識別確認（EnumキーにFriendlyNameは無く、DeviceDesc/Service が実在値）
    static void VerifyVmultiIdentity() {
        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(VMULTI_KEY)) {
            if (k == null) throw new Exception(VMULTI_KEY + " が開けない");
            object descObj = k.GetValue("DeviceDesc") ?? k.GetValue("FriendlyName");
            object svcObj = k.GetValue("Service");
            string desc = descObj == null ? "" : descObj.ToString();
            string svc = svcObj == null ? "" : svcObj.ToString();
            Console.WriteLine("対象デバイス: " + VMULTI_INSTANCE + "  Desc=\"" + desc + "\"  Service=" + svc);
            if (!desc.Contains("Pentablet") && svc != "vmulti")
                throw new Exception("Pentablet HID (vmulti) ではない。処理を中止");
        }
    }

    static string ReadContainer() {
        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(VMULTI_KEY)) {
            object cid = k == null ? null : k.GetValue("ContainerID");
            return cid == null ? null : cid.ToString();
        }
    }

    // HardwareIDを読み、DeviceOverrides用に \ を # へ置換したキー名を返す
    static string GetOverrideKeyName() {
        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(VMULTI_KEY)) {
            object hw = k == null ? null : k.GetValue("HardwareID");
            string[] ids = hw as string[];
            if (ids == null || ids.Length == 0) throw new Exception("HardwareID が読めない");
            Console.WriteLine("HardwareID: " + string.Join(", ", ids));
            return ids[0].Replace('\\', '#');
        }
    }

    static void RestartVmulti() {
        IntPtr dn;
        uint cr = CM_Locate_DevNodeW(out dn, VMULTI_INSTANCE, 0);
        if (cr != 0) throw new Exception("CM_Locate_DevNodeW err=" + cr);
        cr = CM_Disable_DevNode(dn, 0);
        Console.WriteLine("無効化: cr=" + cr);
        if (cr != 0) throw new Exception("無効化に失敗 cr=" + cr);
        Thread.Sleep(2000);
        cr = CM_Enable_DevNode(dn, 0);
        Console.WriteLine("有効化: cr=" + cr);
        if (cr != 0) throw new Exception("有効化に失敗 cr=" + cr);
        Thread.Sleep(2500);
    }

    // ── メイン: DeviceOverrides書き込み → 再有効化 → 着地検証 ──
    static int DoReroute() {
        Console.WriteLine("=== vmultiをRemovableオーバーライドで独自コンテナ化し、外付け扱いへ ===");
        VerifyVmultiIdentity();
        Console.WriteLine("現在のContainerID: " + ReadContainer());

        string keyName = GetOverrideKeyName(); // pentablet#hid
        string path = OverrideRoot + "\\" + keyName + "\\LocationPaths\\*";
        using (RegistryKey k = Registry.LocalMachine.CreateSubKey(path)) {
            k.SetValue("Removable", 1, RegistryValueKind.DWord);
        }
        Console.WriteLine("オーバーライド書き込み: HKLM\\" + path + "  Removable=1");

        RestartVmulti();

        string after = ReadContainer();
        Console.WriteLine("再有効化後のContainerID: " + after);
        if (after == SystemContainer)
            Console.WriteLine("警告: まだシステムコンテナ。オーバーライドが効いていない可能性");

        return VerifyLanding();
    }

    static int DoRevert() {
        Console.WriteLine("=== オーバーライドを削除して元へ戻す ===");
        VerifyVmultiIdentity();
        string keyName = GetOverrideKeyName();
        string devKey = OverrideRoot + "\\" + keyName;
        if (Registry.LocalMachine.OpenSubKey(devKey) != null) {
            Registry.LocalMachine.DeleteSubKeyTree(devKey);
            Console.WriteLine("削除: HKLM\\" + devKey);
        } else {
            Console.WriteLine("オーバーライドなし（既に元の状態）");
        }
        RestartVmulti();
        Console.WriteLine("戻し後のContainerID: " + ReadContainer());
        return 0;
    }

    // 着地検証: クリックなしの移動報告のみ（失敗してメイン側に落ちてもカーソル移動だけで無害）
    static int VerifyLanding() {
        Rectangle vd = FindVirtualBounds();
        if (vd.IsEmpty) { Console.WriteLine("検証スキップ: 仮想モニタが見つからない"); return 1; }
        Console.WriteLine("仮想モニタ範囲: (" + vd.X + "," + vd.Y + ") " + vd.Width + "x" + vd.Height);

        using (HidStream s = OpenVmulti()) {
            // 中央 (0.5, 0.5) = 16383, 16383 へ移動報告（buttons=0 なのでクリックしない）
            Send(s, 16383, 16383, 4096, 0x00);
            Thread.Sleep(300);
            Send(s, 16383, 16383, 0, 0x00);
            Thread.Sleep(200);
        }

        POINT cp;
        GetCursorPos(out cp);
        bool inside = cp.X >= vd.X && cp.X < vd.X + vd.Width && cp.Y >= vd.Y && cp.Y < vd.Y + vd.Height;
        Console.WriteLine("着地座標: (" + cp.X + "," + cp.Y + ")  → " + (inside ? "仮想モニタ内 PASS" : "仮想モニタ外 FAIL"));
        return inside ? 0 : 1;
    }

    static Rectangle FindVirtualBounds() {
        foreach (Screen sc in Screen.AllScreens)
            if (!sc.Primary) return sc.Bounds;
        return Rectangle.Empty;
    }

    static HidStream OpenVmulti() {
        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device (65/65) not found");
    }

    // VMulti AbsoluteInputReport 10バイト: [0]=0x40 [1]=0x09 [2]=0x09 [3]=buttons [4..5]=X [6..7]=Y [8..9]=Pressure
    static void Send(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }
}
