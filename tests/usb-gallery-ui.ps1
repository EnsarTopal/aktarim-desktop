param([string]$Exe = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GalleryKeys {
 [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
if (!$Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-gallery-ui-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'; $target = Join-Path $tempRoot 'target'; $profile = Join-Path $tempRoot 'profile'
New-Item -ItemType Directory -Path $source, $target -Force | Out-Null
for ($i = 0; $i -lt 120; $i++) {
    $ext = if ($i -lt 105) { '.jpg' } else { '.mov' }
    $file = Join-Path $source (('{0:D3}' -f $i) + $ext)
    [IO.File]::WriteAllBytes($file, [byte[]]::new($i + 1))
    [IO.File]::SetCreationTime($file, [DateTime]::new(2020, 1, 1).AddDays($i))
}
$process = $null
try {
    $process = Start-Process -FilePath $Exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    function Window { $process.Refresh(); if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) } }
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 150; $i++) { $window = Window; if ($window) { $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition); if ($found) { return $found } }; Start-Sleep -Milliseconds 100 }
        throw "Missing UI element: $id"
    }
    function Click([string]$id) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function Space([string]$id, [bool]$shift = $false, [bool]$ctrl = $false) {
        $element = Find-Element $id
        [GalleryKeys]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        $element.SetFocus(); Start-Sleep -Milliseconds 100
        if ($shift) { [GalleryKeys]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero) }
        if ($ctrl) { [GalleryKeys]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }
        [GalleryKeys]::keybd_event(0x20, 0, 0, [UIntPtr]::Zero); [GalleryKeys]::keybd_event(0x20, 0, 2, [UIntPtr]::Zero)
        if ($ctrl) { [GalleryKeys]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
        if ($shift) { [GalleryKeys]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 150
    }
    function Summary([int]$count) { $actual = (Find-Element 'SelectionSummary').Current.Name; if ($actual -notmatch "^$count ") { throw "Expected $count selections: $actual" } }
    function Sort-By([string]$name) {
        $sort = Find-Element 'SortFiles'; $sort.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $sort.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    function FileOrder {
        $all = (Window).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        return @($all | Where-Object { $_.Current.AutomationId -match '^File-\d+$' } | ForEach-Object { $_.Current.AutomationId })
    }
    Click 'LangEN'; Click 'Nav1'; Click 'FindDevices'; Click 'ScanMedia'; Click 'Filter1'; Sort-By 'Size: largest'
    $order = @(FileOrder); if ($order.Count -ne 100 -or $order[0] -ne 'File-104' -or $order[99] -ne 'File-5') { throw 'Photo filter, size sort or page capacity incorrect.' }
    Space 'File-104'; Summary 1; Click 'NextPage'
    $order = @(FileOrder); if ($order.Count -ne 5 -or $order[0] -ne 'File-4') { throw 'Second page order incorrect.' }
    Space 'File-4' $true; Summary 101
    Click 'ClearSelection'; Space 'File-4'; Click 'PreviousPage'; Space 'File-104' $true; Summary 101
    Space 'File-104' $false $true; Summary 100
    Click 'ClearSelection'; Click 'Filter2'; Sort-By 'Date: oldest'
    $order = @(FileOrder); if ($order.Count -ne 15 -or $order[0] -ne 'File-105' -or $order[14] -ne 'File-119') { throw 'Video filter and date sort incorrect.' }
    Space 'File-105' $true; Summary 1
    Write-Output 'PASS WPF filtered sorting, 100-item pages, cross-page shift, reverse range and Ctrl keyboard selection'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
