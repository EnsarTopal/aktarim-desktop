$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$root = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-preview-cancel-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'; $target = Join-Path $root 'target'; $profile = Join-Path $root 'profile'
New-Item -ItemType Directory -Path $source, $target -Force | Out-Null
$file = Join-Path $source 'photo.jpg'
[IO.File]::WriteAllText($file, 'photo contents')
[IO.File]::WriteAllBytes(($file + '.thumb'), [byte[]]::new(1048576))
[IO.File]::WriteAllText(($file + '.slowthumb'), '')
$process = $null
try {
    $process = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    function Find-Element([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 150; $i++) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                if ($window) { $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition); if ($element) { return $element } }
            }
            Start-Sleep -Milliseconds 100
        }
        throw "Missing UI element: $id"
    }
    function Click([string]$id) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Click 'LangEN'; Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'
    for ($i = 0; $i -lt 100 -and !(Test-Path ($file + '.thumb.reading')); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path ($file + '.thumb.reading'))) { throw 'Preview did not start.' }
    Click 'BackupNew'
    for ($i = 0; $i -lt 200 -and !(Test-Path (Join-Path $target 'photo.jpg')); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path (Join-Path $target 'photo.jpg'))) { throw 'Backup did not finish.' }
    if (Test-Path ($file + '.thumb.incomplete')) { throw 'Preview stream was closed before it was read.' }
    if ((Get-FileHash $file).Hash -ne (Get-FileHash (Join-Path $target 'photo.jpg')).Hash) { throw 'Backup hash mismatch.' }
    Write-Output 'PASS WPF: cancelling an active preview finishes its bounded stream before verified backup'
    for ($i = 0; $i -lt 100 -and (Find-Element 'UsbStatus').Current.Name -notmatch '^Backup complete'; $i++) { Start-Sleep -Milliseconds 100 }
    $retry = Join-Path $source 'retry.mov'
    [IO.File]::WriteAllText($retry, 'video requiring repeated retries')
    [IO.File]::WriteAllText(($retry + '.short-retries'), '3')
    for ($i = 0; $i -lt 100 -and !(Find-Element 'ScanMedia').Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 100 }
    Click 'ScanMedia'
    for ($i = 0; $i -lt 100 -and !(Find-Element 'BackupNew').Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 100 }
    Click 'BackupNew'
    for ($i = 0; $i -lt 200 -and !(Test-Path (Join-Path $target 'retry.mov')); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path (Join-Path $target 'retry.mov'))) { throw 'Backup did not recover from repeated short reads.' }
    if ((Get-FileHash $retry).Hash -ne (Get-FileHash (Join-Path $target 'retry.mov')).Hash) { throw 'Retried backup hash mismatch.' }
    Write-Output 'PASS WPF: three short reads resume and publish only the complete verified file'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $root -Recurse -Force
}
