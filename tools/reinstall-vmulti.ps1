# reinstall-vmulti.ps1 - remove and reinstall vmulti, then check if DeviceOverrides
# changed the ContainerID (evaluated at device enumeration). Run as Administrator.
$log = 'C:\Users\imura\.zcode\workspace\default\sandbox\clipstudio-bridge\tools\captures\reinstall-log.txt'
function Log($s) { $s | Tee-Object -FilePath $log -Append }

Log "=== vmulti remove + reinstall (DeviceOverrides evaluation test) ==="
Log ("time: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

$devconDir = 'C:\Users\imura\.zcode\workspace\default\sandbox\_downloads\vmulti'
Push-Location $devconDir

Log "--- devcon remove ---"
$output = & .\devcon.exe remove "pentablet\hid" 2>&1
Log ($output -join "`n")

Start-Sleep -Seconds 2

Log "--- devcon install ---"
$output = & .\devcon.exe /r install vmulti.inf "pentablet\hid" 2>&1
Log ($output -join "`n")

Start-Sleep -Seconds 4
Pop-Location

Log "--- state after reinstall ---"
$key = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS\0000'
if (Test-Path $key) {
    $props = Get-ItemProperty $key
    Log ("ContainerID: " + $props.ContainerID)
    Log ("DeviceDesc : " + $props.DeviceDesc)
} else {
    Log "ROOT\HIDCLASS\0000 not found (instance number may have changed)"
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS' -ErrorAction SilentlyContinue | ForEach-Object {
        Log ("  " + $_.PSChildName + " => " + (Get-ItemProperty $_.PSPath).DeviceDesc)
    }
}
Log ("vmulti status: " + (Get-PnpDevice -InstanceId 'ROOT\HIDCLASS\0000' -ErrorAction SilentlyContinue).Status)
Log "=== done ==="
