$src = @'
using System;
using System.Runtime.InteropServices;
public class EnumDisp {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
        public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmDisplayFrequency;
        public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet=CharSet.Ansi)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
'@
Add-Type -TypeDefinition $src

Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -Name U2 -Namespace W
[W.U2]::SetProcessDPIAware() | Out-Null

foreach ($dev in @('\\.\DISPLAY1', '\\.\DISPLAY7')) {
    $dm = New-Object EnumDisp+DEVMODE
    $dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)
    if ([EnumDisp]::EnumDisplaySettings($dev, -1, [ref]$dm)) {  # ENUM_CURRENT_SETTINGS
        Write-Output ($dev + '  pos=(' + $dm.dmPositionX + ',' + $dm.dmPositionY + ')  res=' + $dm.dmPelsWidth + 'x' + $dm.dmPelsHeight + '  freq=' + $dm.dmDisplayFrequency)
    } else {
        Write-Output ($dev + '  EnumDisplaySettings failed')
    }
}
