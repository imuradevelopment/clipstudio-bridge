using System;
using HidSharp;
using System.Threading;
static class P2 {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct POINT { public int X, Y; }
    static void Main() {
        HidStream s = null;
        foreach (var d in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (d.GetMaxInputReportLength() == 65 && d.GetMaxOutputReportLength() == 65 && d.TryOpen(out s)) break;
        }
        if (s == null) { Console.WriteLine("ERR"); return; }
        float[] ns = { 0.99f, 1.0f };
        foreach (float nx in ns) foreach (float ny in new float[]{0.1f, 0.9f}) {
            byte[] b = new byte[10];
            b[0]=0x40; b[1]=0x09; b[2]=0x09; b[3]=0x00;
            ushort x=(ushort)(nx*32767f), y=(ushort)(ny*32767f);
            b[4]=(byte)(x&0xFF); b[5]=(byte)(x>>8); b[6]=(byte)(y&0xFF); b[7]=(byte)(y>>8);
            b[8]=0x00; b[9]=0x10;
            s.Write(b); Thread.Sleep(150);
            POINT p=new POINT(); GetCursorPos(out p);
            Console.WriteLine(nx.ToString("0.00")+","+ny.ToString("0.00")+" -> "+p.X+","+p.Y);
        }
        byte[] up=new byte[10]; up[0]=0x40; up[1]=0x09; up[2]=0x09;
        s.Write(up);
    }
}
