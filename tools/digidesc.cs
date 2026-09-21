// digidesc.cs — vmultiの10Bデジタイザコレクションの入力レポート定義をHidSharpで解析する
using System;
using HidSharp;
using HidSharp.Reports;

static class DigiDesc {
    static void Main() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() != 10) continue;
            Console.WriteLine("=== " + dev.DevicePath + " (In=10B) ===");
            HidStream s;
            if (!dev.TryOpen(out s)) { Console.WriteLine("  open failed"); continue; }
            var rd = dev.GetReportDescriptor();
            foreach (var fr in rd.InputReports) {
                Console.WriteLine("  InputReport ID=0x" + fr.ReportID.ToString("X2"));
                foreach (var item in fr.ReportItems) {
                    Console.WriteLine("    " + item.ItemType + " UsagePage=0x" + item.UsagePage.ToString("X4")
                        + " Usages=[" + string.Join(",", item.Usages.Select(u => "0x" + u.ToString("X4")))
                        + "] Bits=" + item.Elements.Count + " StartBit=" + item.StartIndex
                        + (item.IsButtonValue ? " [value]" : ""));
                }
            }
            s.Close();
        }
    }
}
