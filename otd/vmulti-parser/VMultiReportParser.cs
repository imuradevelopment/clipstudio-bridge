// VMultiReportParser — vmulti仮想タブレットのHIDレポートをOTDのタブレット報告へ変換する。
//
// 対象レポート（vmulti.sys のHIDレポート記述子から実測、tools/dumpdesc.cs）:
//   col03 ReportID 0x05 (標準Digitizer, 10バイト):
//     [0]=0x05 [1]=buttons(tip=0x01, in-range=0x02, barrel=0x04)
//     [2..3]=X 0-32767 [4..5]=Y 0-32767 [6..7]=Pressure 0-8191
//     [8]=TiltX -127..127 [9]=TiltY -127..127
//   col04 ReportID 0x06 (拡張Digitizer, 同形式・筆圧0-16383)
//   ※ 書き込み口の col05 (65/65, ReportID 0x40) はブリッジが使うためOTDとは競合しない
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using OpenTabletDriver.Plugin.Tablet;

namespace ClipStudioBridge.VMultiOtd
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public class VMultiReportParser : IReportParser<IDeviceReport>
    {
        public IDeviceReport Parse(byte[] report)
        {
            if (report.Length >= 10 && (report[0] == 0x05 || report[0] == 0x06))
                return new VMultiTabletReport(report);
            return new DeviceReport(report);
        }
    }

    public struct VMultiTabletReport : ITabletReport, ITiltReport
    {
        public VMultiTabletReport(byte[] report)
        {
            Raw = report;
            Position = new Vector2(
                report[2] | (report[3] << 8),
                report[4] | (report[5] << 8));
            Pressure = (uint)(report[6] | (report[7] << 8));
            if (report[0] == 0x06 && Pressure > 8191)
                Pressure >>= 1; // 拡張レポートは筆圧0-16383 → 定義の0-8191基準に正規化
            Tilt = new Vector2((sbyte)report[8], (sbyte)report[9]);
            PenButtons = new bool[]
            {
                (report[1] & 0x01) != 0, // TipSwitch（ペン先接触）
                (report[1] & 0x04) != 0, // BarrelSwitch（サイドボタン）
            };
        }

        public byte[] Raw { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Tilt { get; set; }
        public uint Pressure { get; set; }
        public bool[] PenButtons { get; set; }
    }
}
