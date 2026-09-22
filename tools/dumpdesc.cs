// dumpdesc.cs — vmulti全HIDコレクションのレポート記述子をダンプする（OTDパーサー設計の入力用）
using System;
using HidSharp;
using HidSharp.Reports;

static class DumpDesc {
    static void Main() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            Console.WriteLine("=== " + dev.DevicePath);
            Console.WriteLine("    MaxIn=" + dev.GetMaxInputReportLength() + " MaxOut=" + dev.GetMaxOutputReportLength()
                + " Release=" + dev.ReleaseNumber + " Serial=" + (dev.GetSerialNumber() ?? "(none)")
                + " Product='" + dev.GetProductName() + "'");
            try {
                var rd = dev.GetReportDescriptor();
                foreach (var rep in rd.InputReports) {
                    Console.WriteLine("  InputReport ID=0x" + rep.ReportID.ToString("X2") + " Len=" + rep.Length);
                    foreach (DataItem di in rep.DataItems) {
                        Console.WriteLine("    Var=" + di.IsVariable + " Abs=" + di.IsAbsolute
                            + " Bits=" + di.TotalBits + " Count=" + di.ElementCount
                            + " LogMin=" + di.LogicalMinimum + " LogMax=" + di.LogicalMaximum
                            + " Usages=" + di.Usages);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine("  (descriptor read failed: " + ex.Message + ")");
            }
        }
    }
}
