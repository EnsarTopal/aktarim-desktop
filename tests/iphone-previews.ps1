$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project 'src/Aktarim.Desktop/bin/Release/net10.0-windows/Aktarim.exe'
$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
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
    Invoke-Button 'LangEN'; Invoke-Button 'Nav1'; Invoke-Button 'FindDevices'
    if (!(Find-Element 'ScanMedia' 50)) { throw 'No WPD device was available for scanning; unlock and trust the iPhone.' }
    $devices = Find-Element 'Devices'
    $devices.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $choices = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $index = -1
    for ($i = 0; $i -lt $choices.Count; $i++) { if ($choices[$i].Current.Name -match 'iPhone') { $index = $i; break } }
    if ($index -lt 0) { throw 'The WPD device list does not contain an iPhone.' }
    $devices.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    for ($i = 0; $i -lt $index; $i++) { [System.Windows.Forms.SendKeys]::SendWait('{DOWN}') }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Invoke-Button 'ScanMedia'
    if (!(Find-Element 'SearchFiles' 1200)) {
        $status = Find-Element 'UsbStatus' 1
        throw ('Media could not be listed. ' + $(if ($status) { $status.Current.Name } else { '' }))
    }
    foreach ($extension in @('.jpg', '.heic', '.mov')) {
        $search = Find-Element 'SearchFiles'; $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($extension)
        Start-Sleep -Seconds 4
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
        $tiles = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $visible = 0; $loaded = 0
        foreach ($tile in $tiles) {
            if ($tile.Current.IsOffscreen) { continue }
            $visible++
            $imageCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Preview loaded')
            if ($tile.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $imageCondition)) { $loaded++ }
        }
        Write-Output ("{0}: visible={1}, previews={2}" -f $extension.ToUpperInvariant(), $visible, $loaded)
    }
}
finally { if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force } }
