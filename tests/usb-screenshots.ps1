param([int]$Width = 1280, [int]$Height = 800, [ValidateSet("TR", "EN")][string]$Language = "TR")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project "src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe"
$outputDir = Join-Path $project "docs/screenshots"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("aktarim-screen-" + [guid]::NewGuid().ToString("N"))
$source = Join-Path $tempRoot "source"
$target = Join-Path $tempRoot "target"
New-Item -ItemType Directory -Path $source, $target | Out-Null
foreach ($name in @("IMG_3339.JPG", "IMG_3338.JPG", "IMG_3329.HEIC", "IMG_3328.MOV", "IMG_3327.JPG", "IMG_3326.HEIC", "IMG_3325.MOV", "IMG_3324.JPG", "IMG_3323.HEIC", "IMG_3322.JPG")) {
    [IO.File]::WriteAllBytes((Join-Path $source $name), (1..3000 | ForEach-Object { [byte]($_ % 256) }))
}
$process = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList @("--fake-usb", "`"$source`"", "`"$target`"") -PassThru
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 100; $i++) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($found) { return $found }
            }
            Start-Sleep -Milliseconds 100
        }
        throw "UI element not found: $id"
    }
    function Invoke-Button([string]$id) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    $null = Find-Element "Nav1"
    [WindowCapture]::MoveWindow($process.MainWindowHandle, 10, 10, $Width, $Height, $true) | Out-Null
    [WindowCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Invoke-Button ("Lang" + $Language)
    Invoke-Button "Nav1"
    Invoke-Button "FindDevices"
    Invoke-Button "ScanMedia"
    foreach ($id in @("File-0", "File-3")) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 500
    $rect = New-Object WindowCapture+RECT
    [WindowCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $path = Join-Path $outputDir ("usb-" + $Width + "x" + $Height + "-" + $Language.ToLower() + ".png")
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Output "Saved $path"
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
