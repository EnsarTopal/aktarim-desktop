param([string]$Playwright = 'C:/Users/kubra/AppData/Local/Temp/aktarim-browser-test/node_modules/playwright', [ValidateSet('HTTPS','HTTP')][string]$Mode = 'HTTPS')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type 'using System; using System.Runtime.InteropServices; public static class TestNative { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-lan-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'; $target = Join-Path $tempRoot 'target'; $cert = Join-Path $tempRoot 'test.pfx'
New-Item -ItemType Directory -Path $source, $target | Out-Null
$process = $null; $browserTest = $null
try {
    & dotnet script (Join-Path $PSScriptRoot 'make-test-cert.csx') -- $cert
    if ($LASTEXITCODE -ne 0) { throw 'Test certificate creation failed' }
    $env:AKTARIM_LAN_DIAGNOSTICS='1'; $env:AKTARIM_LAN_TEST='1'
    $process = Start-Process -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"") -RedirectStandardOutput (Join-Path $tempRoot 'server.log') -RedirectStandardError (Join-Path $tempRoot 'server-err.log') -PassThru
    function Find-Element([string]$id, [int]$attempts = 100) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i=0; $i -lt $attempts; $i++) {
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
    function Set-Value([string]$id,[string]$value) { (Find-Element $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
    Invoke-Button 'LangEN'; Invoke-Button 'Nav2'
    if ($Mode -eq 'HTTPS') {
        Set-Value 'LanPfx' $cert
        $password = Find-Element 'LanPassword'; $password.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait('test-only')
    } else {
        $modeControl = Find-Element 'LanMode'; $modeControl.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait('{END}')
    }
    Invoke-Button 'StartLan'
    $url = (Find-Element 'LanTestUrl' 200).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if (-not $url.StartsWith($Mode.ToLower() + '://127.0.0.1:')) { throw "Wrong receiver mode: $Mode" }
    $qr = Find-Element 'LanQr'; $rect = $qr.Current.BoundingRectangle
    $image = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $image.Size)
    $qrFile = Join-Path $tempRoot 'qr.png'; $image.Save($qrFile, [System.Drawing.Imaging.ImageFormat]::Png); $graphics.Dispose(); $image.Dispose()
    $env:AKTARIM_QR_FILE=$qrFile
    $env:AKTARIM_TEST_ROOT=$tempRoot; $env:AKTARIM_PAIR_URL=$url; $env:AKTARIM_PLAYWRIGHT=$Playwright
    $stdout=Join-Path $tempRoot 'out.log'; $stderr=Join-Path $tempRoot 'err.log'
    $browserTest = Start-Process -FilePath 'node' -ArgumentList "`"$(Join-Path $PSScriptRoot 'lan-browser.cjs')`"" -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $null = $browserTest.Handle
    $approved=$false
    for ($i=0; $i -lt 300; $i++) {
        $browserTest.Refresh(); if ($browserTest.HasExited) { break }
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($window in $windows) {
            if ($window.Current.ProcessId -ne $process.Id) { continue }
            $dialog=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -eq '#32770' -and $_.Current.Name -eq 'Pairing request' } | Select-Object -First 1
            $yes = if ($dialog) { $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.AutomationId -eq '6' } | Select-Object -First 1 }
            if ($yes) { [TestNative]::SendMessage([IntPtr]$yes.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; $approved=$true; break }
        }
        if ($approved) { break }; Start-Sleep -Milliseconds 100
    }
    if (!$approved) { if (Test-Path $stderr) { Get-Content $stderr }; Get-Content (Join-Path $tempRoot 'server.log'); Get-Content (Join-Path $tempRoot 'server-err.log'); throw 'Windows pairing approval dialog not found' }
    if (!$browserTest.WaitForExit(180000)) { throw 'Browser test timed out' }
    $browserTest.Refresh()
    Get-Content $stdout; Get-Content $stderr
    if ($browserTest.ExitCode -ne 0) { throw 'Browser test failed' }
    $endpoint = [Uri]$url
    (Find-Element 'Nav0') | Out-Null
    $stop = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle).FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'Stop receiver' } | Select-Object -First 1
    $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i=0; $i -lt 50; $i++) { if (-not (Get-NetTCPConnection -LocalPort $endpoint.Port -State Listen -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 100 }
    if (Get-NetTCPConnection -LocalPort $endpoint.Port -State Listen -ErrorAction SilentlyContinue) { throw 'Receiver still listening after stop' }
    Write-Output "PASS $Mode listener stopped"
}
finally {
    if ($browserTest -and !$browserTest.HasExited) { Stop-Process -Id $browserTest.Id -Force }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
