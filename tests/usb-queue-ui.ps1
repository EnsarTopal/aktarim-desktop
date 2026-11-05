param([string]$Exe = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (!$Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-queue-ui-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'; $target = Join-Path $root 'target'; $profile = Join-Path $root 'profile'
New-Item -ItemType Directory -Path $source, $target, (Join-Path $source 'z') -Force | Out-Null
foreach ($name in @('a.mov','b.mov','c.mov','d.mov','z/d.mov')) { [IO.File]::WriteAllText((Join-Path $source $name), "test video $name") }
[IO.File]::WriteAllText((Join-Path $source 'b.mov.unreadable'), '')
[IO.File]::WriteAllText((Join-Path $source 'c.mov.transient'), '')
$process = $null
try {
    $process = Start-Process -WindowStyle Hidden -FilePath $Exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 150; $i++) { $process.Refresh(); if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle); if ($window) { $e = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition); if ($e) { return $e } } }; Start-Sleep -Milliseconds 100 }
        throw "Missing UI element: $id"
    }
    function Click([string]$id) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function Wait-Status([string]$pattern) {
        for ($i=0; $i -lt 250; $i++) { $status=(Find-Element 'UsbStatus').Current.Name; if ($status -match $pattern) { return }; Start-Sleep -Milliseconds 100 }
        throw "Unexpected status: $status"
    }
    Click 'LangEN'; Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'; Click 'BackupNew'
    Wait-Status '^Some files could not be copied.+4 copied, 0 skipped, 1 failed'
    foreach ($name in @('a.mov','c.mov','d.mov','d (1).mov')) { if (!(Test-Path (Join-Path $target $name))) { throw "Missing completed video $name" } }
    if (Test-Path (Join-Path $target 'b.mov')) { throw 'Unreadable file was published.' }
    if ((Get-FileHash (Join-Path $target 'd.mov')).Hash -eq (Get-FileHash (Join-Path $target 'd (1).mov')).Hash) { throw 'Same-named videos did not preserve both contents.' }
    Remove-Item -LiteralPath (Join-Path $source 'b.mov.unreadable')
    [IO.File]::WriteAllText((Join-Path $source 'b.mov.diskfull'), '')
    [IO.File]::WriteAllText((Join-Path $source 'e.mov'), 'waiting video')
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Status '^Destination disk is full'
    if (Test-Path (Join-Path $target 'e.mov')) { throw 'Queue did not pause on simulated disk full.' }
    if ((Get-ChildItem $target -File).Count -ne 4) { throw 'Completed videos were lost.' }
    Write-Output 'PASS WPF video queue: file-specific failure, transient retry, same names, simulated disk-full pause'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $root -Recurse -Force
}
