Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    Write-Output ($_.DeviceName + ' Primary=' + $_.Primary + ' ' + $_.Bounds.X + ',' + $_.Bounds.Y + ' ' + $_.Bounds.Width + 'x' + $_.Bounds.Height)
}
Write-Output '--- virtual screen ---'
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool GetSystemMetrics(int n);' -Name U -Namespace W
Write-Output ('SM_XVIRTUALSCREEN=' + [W.U]::GetSystemMetrics(76) + ' SM_YVIRTUALSCREEN=' + [W.U]::GetSystemMetrics(77) + ' SM_CXVIRTUALSCREEN=' + [W.U]::GetSystemMetrics(78) + ' SM_CYVIRTUALSCREEN=' + [W.U]::GetSystemMetrics(79))
Write-Output '--- calculator processes ---'
Get-Process -Name CalculatorApp, ApplicationFrameHost -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ($_.ProcessName + ' ' + $_.Id + ' title=[' + $_.MainWindowTitle + ']')
}
