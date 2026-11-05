param([string]$Configuration = "Release", [ValidateSet("TR", "EN")][string]$Language = "EN", [string]$Exe = "")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$project = Split-Path -Parent $PSScriptRoot
if (!$Exe) { $Exe = Join-Path $project "src/Aktarim.Desktop/bin/$Configuration/net10.0-windows/Aktarim.exe" }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("aktarim-ui-" + [guid]::NewGuid().ToString("N"))
$source = Join-Path $tempRoot "source"
$target = Join-Path $tempRoot "target"
$profile = Join-Path $tempRoot "profile"
New-Item -ItemType Directory -Path $source, $target | Out-Null
[IO.File]::WriteAllBytes((Join-Path $source "a-good.mov"), (1..20000 | ForEach-Object { [byte]($_ % 256) }))
[IO.File]::WriteAllBytes((Join-Path $source "disconnect-b.mov"), (1..10000 | ForEach-Object { [byte]($_ % 256) }))
$process = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList @("--fake-usb", "`"$source`"", "`"$target`"", "`"$profile`"") -PassThru
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 100; $i++) {
            $window = if ($process.MainWindowHandle -ne [IntPtr]::Zero) { [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) } else { $null }
            if ($window) {
                $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($found) { return $found }
            }
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        throw "UI element not found: $id"
    }
    function Invoke-Button([string]$id) {
        $element = Find-Element $id
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Invoke-Button ("Lang" + $Language)
    Invoke-Button "Nav1"
    Invoke-Button "FindDevices"
    Invoke-Button "ScanMedia"
    foreach ($id in @("File-0", "File-1")) {
        $element = Find-Element $id
        $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    $search = Find-Element "SearchFiles"
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("a-good")
    $summary = (Find-Element "SelectionSummary").Current.Name
    if ($summary -notmatch '^2 ' -or $summary -notmatch '1 ') { throw "Filtered selection summary incorrect: $summary" }
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("")
    foreach ($id in @("File-0", "File-1")) {
        $toggle = (Find-Element $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw "Selection lost after search: $id" }
    }
    Invoke-Button "CopySelected"
    $good = Join-Path $target "a-good.mov"
    for ($i = 0; $i -lt 100; $i++) {
        $status = Find-Element "UsbStatus"
        if ($status.Current.Name -match "^(Connection lost|Ba).+disconnect-b") { break }
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path $good)) { throw "The first selected file was not published." }
    if ((Get-FileHash $good).Hash -ne (Get-FileHash (Join-Path $source "a-good.mov")).Hash) { throw "The verified file differs." }
    if (Test-Path (Join-Path $target "disconnect-b.mov")) { throw "Interrupted file was published." }
    if (Get-ChildItem $target -Filter "*.partial") { throw "Interrupted temporary file remained." }
    $status = (Find-Element "UsbStatus").Current.Name
    if ($status -notmatch "^(Connection lost|Ba).+disconnect-b\.mov") { throw "Failure was not displayed: $status" }
    if ($status -notmatch '\d[\d.,]* / \d[\d.,]* (bytes read|bayt okundu)') { throw "Short-read details were not displayed: $status" }
    $historyPath = Join-Path $profile 'UsbHistory/history.json'
    $entries = @(Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json | Where-Object { $_.Destination -eq $target })
    if ($entries.Count -ne 1 -or [IO.Path]::GetFileName($entries[0].Path) -ne 'a-good.mov') { throw 'Interrupted copy was added to history or verified copy missing.' }
    [IO.File]::WriteAllText((Join-Path $source "reconnected"), "")
    Invoke-Button "BackupNew"
    $resumed = Join-Path $target "disconnect-b.mov"
    for ($i = 0; $i -lt 100 -and !(Test-Path $resumed); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path $resumed)) { throw "Retry did not publish the file." }
    for ($i = 0; $i -lt 100; $i++) { $status = (Find-Element 'UsbStatus').Current.Name; if ($status -match '1 (copied|kopyaland).+1 (skipped|atland)') { break }; Start-Sleep -Milliseconds 100 }
    if ($status -notmatch '1 (copied|kopyaland).+1 (skipped|atland)') { throw "Completed file was not skipped on backup retry: $status" }
    if ((Get-FileHash $resumed).Hash -ne (Get-FileHash (Join-Path $source "disconnect-b.mov")).Hash) { throw "Retried file differs." }
    if ((Get-ChildItem $target -Filter "a-good*").Count -ne 1) { throw "Completed file was copied again." }
    Write-Output "PASS WPF selection, verified copy, interruption, completed-file preservation and retry"
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
