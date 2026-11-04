$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-preview-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'; $target = Join-Path $tempRoot 'target'
New-Item -ItemType Directory -Path $source, $target | Out-Null
$process = $null
try {
    $bitmap = New-Object System.Drawing.Bitmap(32, 32)
    $bitmap.SetPixel(0, 0, [System.Drawing.Color]::Red)
    $bitmap.Save((Join-Path $source 'a-photo.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bitmap.Save((Join-Path $source 'b-video.mov.thumb'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bitmap.Dispose()
    [IO.File]::WriteAllBytes((Join-Path $source 'b-video.mov'), [byte[]](1,2,3))
    [IO.File]::WriteAllBytes((Join-Path $source 'c-no-thumb.mov'), [byte[]](1,2,3))
    $large = [IO.File]::Create((Join-Path $source 'd-large.jpg')); $large.SetLength(13MB); $large.Dispose()
    foreach ($name in @('a-photo.jpg', 'b-video.mov', 'c-no-thumb.mov', 'd-large.jpg')) { [IO.File]::WriteAllText((Join-Path $source ($name + '.track')), '') }
    $process = Start-Process -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"") -PassThru
    function Find-Element([string]$id, [int]$attempts = 100) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt $attempts; $i++) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($found) { return $found }
            }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    function Invoke-Button([string]$id) { $element = Find-Element $id; if (!$element) { throw "Missing $id" }; $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Invoke-Button 'LangEN'; Invoke-Button 'Nav1'; Invoke-Button 'FindDevices'; Invoke-Button 'ScanMedia'
    foreach ($id in @('Preview-0', 'Preview-1')) {
        $found = $null
        for ($i = 0; $i -lt 100; $i++) { $found = Find-Element $id 1; if ($found -and $found.Current.Name -eq 'Preview loaded') { break }; Start-Sleep -Milliseconds 100 }
        if (!$found -or $found.Current.Name -ne 'Preview loaded') { throw "Preview not loaded: $id" }
    }
    foreach ($id in @('Preview-2', 'Preview-3')) { $found = Find-Element $id 1; if ($found -and $found.Current.Name -eq 'Preview loaded') { throw "Unexpected preview: $id" } }
    Start-Sleep -Milliseconds 500
    if (!(Test-Path (Join-Path $source 'a-photo.jpg.opened'))) { throw 'Photo fallback was not read' }
    foreach ($name in @('b-video.mov', 'c-no-thumb.mov', 'd-large.jpg')) { if (Test-Path (Join-Path $source ($name + '.opened'))) { throw "Unexpected full media read: $name" } }
    Write-Output 'PASS WPF: photo fallback, device video thumbnail, missing video and oversized photo'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
