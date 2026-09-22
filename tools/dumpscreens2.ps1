Add-Type -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
[DllImport("user32.dll")] public static extern int GetDpiForSystem();
' -Name U -Namespace W

Write-Output '--- before DPI aware (virtualized) ---'
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    Write-Output ($_.DeviceName + ' Primary=' + $_.Primary + ' ' + $_.Bounds.X + ',' + $_.Bounds.Y + ' ' + $_.Bounds.Width + 'x' + $_.Bounds.Height)
}

[W.U]::SetProcessDPIAware() | Out-Null

Write-Output '--- after DPI aware (physical) ---'
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    Write-Output ($_.DeviceName + ' Primary=' + $_.Primary + ' ' + $_.Bounds.X + ',' + $_.Bounds.Y + ' ' + $_.Bounds.Width + 'x' + $_.Bounds.Height)
}
Write-Output '--- virtual screen (physical, int) ---'
Write-Output ('X=' + [W.U]::GetSystemMetrics(76) + ' Y=' + [W.U]::GetSystemMetrics(77) + ' W=' + [W.U]::GetSystemMetrics(78) + ' H=' + [W.U]::GetSystemMetrics(79))
try { Write-Output ('GetDpiForSystem=' + [W.U]::GetDpiForSystem()) } catch { Write-Output 'GetDpiForSystem n/a' }
