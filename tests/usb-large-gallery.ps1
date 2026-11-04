$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aktarim-large-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'
$target = Join-Path $tempRoot 'target'
New-Item -ItemType Directory -Path $source, $target | Out-Null
$process = $null
try {
    for ($i = 0; $i -lt 10000; $i++) { [IO.File]::WriteAllBytes((Join-Path $source ('IMG_{0:D5}.jpg' -f $i)), [byte[]]@()) }
    $process = Start-Process -FilePath $exe -ArgumentList @('--fake-usb', "`"$source`"", "`"$target`"") -PassThru
    function Find-Element([string]$id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($i = 0; $i -lt 300; $i++) {
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
    Invoke-Button 'Nav1'
    Invoke-Button 'FindDevices'
    Invoke-Button 'ScanMedia'
    $null = Find-Element 'File-0'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
    $count = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition).Count
    if ($count -ne 30) { throw "Expected 30 rendered tiles, found $count" }
    Invoke-Button 'NextPage'
    $null = Find-Element 'File-30'
    $search = Find-Element 'SearchFiles'
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('IMG_09999')
    $null = Find-Element 'File-9999'
    Write-Output 'PASS 10,000 files: 30 rendered tiles, next page and search'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
