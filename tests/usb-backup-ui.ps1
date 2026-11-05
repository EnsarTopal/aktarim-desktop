param([string]$Exe = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (!$Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-backup-ui-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'; $target = Join-Path $tempRoot 'target'
$profile = Join-Path $tempRoot 'profile'; $historyDir = Join-Path $profile 'UsbHistory'
New-Item -ItemType Directory -Path (Join-Path $source 'a'), (Join-Path $source 'b'), $target, $historyDir -Force | Out-Null
$historyFile = Join-Path $historyDir 'history.json'; $damaged = '{ invalid history'
[IO.File]::WriteAllText($historyFile, $damaged)
[IO.File]::WriteAllText((Join-Path $source 'a/same.jpg'), 'first fake photo')
[IO.File]::WriteAllText((Join-Path $source 'b/same.jpg'), 'other fake photo with more bytes')
[IO.File]::WriteAllText((Join-Path $source 'b/same.jpg.volatile-id'), '')
[IO.File]::SetCreationTime((Join-Path $source 'a/same.jpg'), [DateTime]::new(2020, 1, 2))
[IO.File]::SetCreationTime((Join-Path $source 'b/same.jpg'), [DateTime]::new(2022, 1, 2))
$process = $null
try {
    $process = Start-Process -WindowStyle Hidden -FilePath $Exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
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
    function Wait-Backup([string]$expected) {
        for ($i = 0; $i -lt 200; $i++) {
            $status = (Find-Element 'UsbStatus').Current.Name
            if ($status -match $expected) { return }
            if ($status -match '^Transfer failed') { throw $status }
            Start-Sleep -Milliseconds 100
        }
        throw "Backup status did not match $expected : $status"
    }
    Click 'LangEN'; Click 'Nav4'
    $dateSetting = (Find-Element 'UsbDateFolders').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($dateSetting.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $dateSetting.Toggle() }
    Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'
    Click 'BackupNew'; Wait-Backup '^Copy verified, but history could not be saved'
    if ((Get-ChildItem $target -File).Count -ne 1 -or [IO.File]::ReadAllText($historyFile) -ne $damaged) { throw 'Corrupt history was trusted or changed, or verified copy was not kept.' }
    Click 'Nav3'
    $copyWarning = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, ('Copy verified, history could not be saved: ' + (Get-ChildItem $target -File)[0].FullName))
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if (!$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $copyWarning)) { throw 'Verified copy and history failure were not separately reported.' }
    Click 'RecoverHistory'; Click 'CancelHistoryRecovery'
    if ([IO.File]::ReadAllText($historyFile) -ne $damaged) { throw 'Cancelled recovery changed history.' }
    Click 'RecoverHistory'; Click 'ConfirmHistoryRecovery'
    for ($i = 0; $i -lt 100; $i++) { try { if ((Test-Path $historyFile) -and [IO.File]::ReadAllText($historyFile) -eq '[]') { break } } catch [IO.IOException] { }; Start-Sleep -Milliseconds 100 }
    $archive = @(Get-ChildItem $historyDir -Filter 'history.corrupt-*.json')
    if ($archive.Count -ne 1 -or [IO.File]::ReadAllText($archive[0].FullName) -ne $damaged -or [IO.File]::ReadAllText($historyFile) -ne '[]') { throw 'Approved recovery failed to preserve the damaged history.' }
    Get-ChildItem $target -File | Remove-Item
    Click 'Nav1'
    function Sort-By([string]$label) {
        $sort = Find-Element 'SortFiles'
        $sort.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $label)
        $option = $sort.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (!$option) { throw "Sort option missing: $label" }
        $option.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    Sort-By 'Size: largest'
    if ((Find-Element 'File-1').Current.BoundingRectangle.Left -ge (Find-Element 'File-0').Current.BoundingRectangle.Left) { throw 'Size sort incorrect.' }
    Sort-By 'Date: oldest'
    if ((Find-Element 'File-0').Current.BoundingRectangle.Left -ge (Find-Element 'File-1').Current.BoundingRectangle.Left) { throw 'Date sort incorrect.' }
    Click 'BackupNew'; Wait-Backup 'Backup complete: 2 copied, 0 skipped'
    $a = Join-Path $target 'same.jpg'; $b = Join-Path $target 'same (1).jpg'
    if (!(Test-Path $a) -or !(Test-Path $b)) { throw 'Same-named files were not preserved.' }
    $hashes = @((Get-FileHash $a).Hash, (Get-FileHash $b).Hash)
    if ($hashes -notcontains (Get-FileHash (Join-Path $source 'a/same.jpg')).Hash -or $hashes -notcontains (Get-FileHash (Join-Path $source 'b/same.jpg')).Hash) { throw 'Backup hash mismatch.' }
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Backup 'Backup complete: 0 copied, 2 skipped'
    Remove-Item -LiteralPath $a
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Backup 'Backup complete: 1 copied, 1 skipped'
    if (!(Test-Path $a) -or (Get-ChildItem $target -File).Count -ne 2) { throw 'Deleted backup was not recreated safely.' }
    [IO.File]::WriteAllText((Join-Path $source 'dated.jpg'), 'dated file')
    [IO.File]::WriteAllText((Join-Path $source 'unknown.jpg'), 'unknown date file')
    [IO.File]::WriteAllText((Join-Path $source 'unknown.jpg.unknown-date'), '')
    Click 'Nav4'
    $dateSetting = (Find-Element 'UsbDateFolders').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($dateSetting.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $dateSetting.Toggle() }
    Click 'Nav1'; Click 'ScanMedia'; Click 'BackupNew'; Wait-Backup 'Backup complete: 2 copied, 2 skipped'
    $date = (Get-Item (Join-Path $source 'dated.jpg')).CreationTime
    $dated = Join-Path $target ($date.ToString('yyyy/MM/dd', [Globalization.CultureInfo]::InvariantCulture) + '/dated.jpg')
    if (!(Test-Path $dated) -or !(Test-Path (Join-Path $target 'unknown.jpg'))) { throw "Date folders or unknown-date fallback incorrect; expected $dated; files: $((Get-ChildItem $target -File -Recurse).FullName -join ', ')" }
    Click 'Nav4'
    $dateSetting = (Find-Element 'UsbDateFolders').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($dateSetting.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $dateSetting.Toggle() }
    $verifiedSource = Join-Path $source 'verify.jpg'
    [IO.File]::WriteAllText($verifiedSource, 'AAAA'); [IO.File]::WriteAllText(($verifiedSource + '.volatile-id'), '')
    $modified = [IO.File]::GetLastWriteTimeUtc($verifiedSource)
    Click 'Nav1'; Click 'ScanMedia'; Click 'BackupNew'; Wait-Backup 'Backup complete: 1 copied, 4 skipped'
    [IO.File]::WriteAllText($verifiedSource, 'BBBB'); [IO.File]::SetLastWriteTimeUtc($verifiedSource, $modified)
    Click 'ScanMedia'; Click 'BackupNew'; Wait-Backup 'Backup complete: 1 copied, 4 skipped'
    if ([IO.File]::ReadAllText((Join-Path $target 'verify.jpg')) -ne 'AAAA' -or [IO.File]::ReadAllText((Join-Path $target 'verify (1).jpg')) -ne 'BBBB') { throw 'Changed source with identical metadata was skipped or overwritten.' }
    Write-Output 'PASS WPF fake USB backup, rescan skip, same names and deleted backup recreation'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
