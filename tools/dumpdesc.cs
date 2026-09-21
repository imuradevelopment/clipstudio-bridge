using System;
using HidSharp;
using HidSharp.Reports;
static class DumpDesc {
    static void Main() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() != 10) continue;
            Console.WriteLine("=== In=10B " + dev.DevicePath + " ===");
            var rd = dev.GetReportDescriptor();
            foreach (var rep in rd.InputReports) {
                Console.WriteLine("InputReport ID=0x" + rep.ReportID.ToString("X2") + " Length=" + rep.Length);
                foreach (DataItem di in rep.DataItems) {
                    Console.WriteLine("  DataItem: Usages=" + di.Usages
                        + " IsVariable=" + di.IsVariable + " IsBoolean=" + di.IsBoolean + " IsAbsolute=" + di.IsAbsolute
                        + " TotalBits=" + di.TotalBits + " ElementCount=" + di.ElementCount
                        + " LogicalMin=" + di.LogicalMinimum + " LogicalMax=" + di.LogicalMaximum
                        + " Usages=" + di.Usages);
                }
            }
            foreach (var rep in rd.OutputReports) {
                Console.WriteLine("OutputReport ID=0x" + rep.ReportID.ToString("X2") + " Length=" + rep.Length);
            }
        }
    }
}
