param([ValidateSet('TR','EN')][string]$Language = 'EN')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$root = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-pause-ui-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'; $target = Join-Path $root 'target'; $profile = Join-Path $root 'profile'
New-Item -ItemType Directory -Path $source, $target, $profile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'a.jpg'), 'completed first')
$large = Join-Path $source 'b.mov'
$stream = [IO.File]::Create($large); try { $stream.SetLength(4194304) } finally { $stream.Dispose() }
[IO.File]::WriteAllText(($large + '.slow'), '')
$process = $null
try {
    $process = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    function Find-Element([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 150; $i++) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                if ($window) { $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition); if ($found) { return $found } }
            }
            Start-Sleep -Milliseconds 100
        }
        throw "UI element not found: $id"
    }
    function Click([string]$id) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function Wait-Status([string]$pattern) {
        for ($i = 0; $i -lt 200; $i++) {
            $status = (Find-Element 'UsbStatus').Current.Name
            if ($status -match $pattern) { return }
            Start-Sleep -Milliseconds 100
        }
        throw "Status did not match $pattern : $status"
    }
    Click ('Lang' + $Language); Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'; Click 'BackupNew'
    Wait-Status 'b\.mov.*1 MB'
    Click 'PauseBackup'
    Wait-Status $(if ($Language -eq 'TR') { '^Yedekleme durduruldu' } else { '^Backup paused' })
    if (!(Test-Path (Join-Path $target 'a.jpg')) -or (Test-Path (Join-Path $target 'b.mov')) -or @(Get-ChildItem $target -Force -Filter '*.partial').Count -ne 0) { throw 'Pause did not preserve completed file or clean the partial file.' }
    $partial = @(Get-ChildItem (Join-Path $profile 'UsbPartial') -Filter '*.partial')
    if ($partial.Count -ne 1 -or $partial[0].Length -lt 1048576 -or $partial[0].Length -ge 4194304) { throw 'Paused bytes were not retained for resume.' }
    $history = Get-Content -LiteralPath (Join-Path $profile 'UsbHistory/history.json') -Raw | ConvertFrom-Json
    if (@($history).Count -ne 1) { throw 'Incomplete file was recorded in history.' }
    Click 'ResumeBackup'
    Wait-Status $(if ($Language -eq 'TR') { '^Yedekleme bitti: 2' } else { '^Backup complete: 2 copied' })
    if (@(Get-ChildItem $target -File).Count -ne 2 -or (Get-FileHash $large).Hash -ne (Get-FileHash (Join-Path $target 'b.mov')).Hash) { throw 'Resume produced duplicates or incorrect output.' }
    if (@(Get-ChildItem (Join-Path $profile 'UsbPartial') -Filter '*.partial').Count -ne 0) { throw 'Published partial was not removed.' }
    $crashFile = Join-Path $source 'c.mov'
    $stream = [IO.File]::Create($crashFile); try { $stream.SetLength(4194304) } finally { $stream.Dispose() }
    [IO.File]::WriteAllText(($crashFile + '.slow'), '')
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Status 'c\.mov.*1 MB'
    Stop-Process -Id $process.Id -Force; $process.WaitForExit()
    $partial = @(Get-ChildItem (Join-Path $profile 'UsbPartial') -Filter '*.partial')
    if ($partial.Count -ne 1 -or $partial[0].Length -lt 1048576 -or (Test-Path (Join-Path $target 'c.mov'))) { throw 'Interrupted partial not retained safely.' }
    $process = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    Click ('Lang' + $Language); Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'; Click 'BackupNew'
    Wait-Status $(if ($Language -eq 'TR') { '^Yedekleme bitti: 1' } else { '^Backup complete: 1 copied, 2 skipped' })
    if (@(Get-ChildItem $target -File).Count -ne 3 -or (Get-FileHash $crashFile).Hash -ne (Get-FileHash (Join-Path $target 'c.mov')).Hash) { throw 'Restart after interruption failed or duplicated completed files.' }
    if (@(Get-ChildItem (Join-Path $profile 'UsbPartial') -Filter '*.partial').Count -ne 0) { throw 'Restarted partial was not removed.' }
    $disconnected = Join-Path $source 'disconnect-d.mov'
    [IO.File]::WriteAllBytes($disconnected, (1..20000 | ForEach-Object { [byte]($_ % 256) }))
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Status 'disconnect-d\.mov'
    Wait-Status $(if ($Language -eq 'TR') { '^Ba' } else { '^Connection lost' })
    $diagnostics = Find-Element 'BackupDiagnostics'
    $diagnostics.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $detailCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $details = $diagnostics.FindAll([System.Windows.Automation.TreeScope]::Descendants, $detailCondition) | ForEach-Object { $_.Current.Name }
    if (($details -join ' ') -notmatch $(if ($Language -eq 'TR') { '10\D?000 / 20\D?000 bayt okundu' } else { '10\D?000 / 20\D?000 bytes read' })) { throw "Missing source byte counts: $details" }
    $partial = @(Get-ChildItem (Join-Path $profile 'UsbPartial') -Filter '*.partial')
    if ($partial.Count -ne 1 -or $partial[0].Length -ne 10000 -or (Test-Path (Join-Path $target 'disconnect-d.mov'))) { throw 'Disconnected backup did not preserve its partial safely.' }
    [IO.File]::WriteAllText((Join-Path $source 'reconnected'), '')
    Click 'FindDevices'; Click 'ScanMedia'; Click 'BackupNew'
    Wait-Status $(if ($Language -eq 'TR') { '^Yedekleme bitti: 1' } else { '^Backup complete: 1 copied, 3 skipped' })
    if (@(Get-ChildItem $target -File).Count -ne 4 -or (Get-FileHash $disconnected).Hash -ne (Get-FileHash (Join-Path $target 'disconnect-d.mov')).Hash) { throw 'Disconnected backup did not resume correctly.' }
    Write-Output "PASS $Language WPF backup pause, retained partial, verified resume and completed-file preservation"
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $root -Recurse -Force
}
