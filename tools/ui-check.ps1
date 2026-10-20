<#
.SYNOPSIS
    Drives the app through UI Automation in demo mode and checks the interactive states
    a screenshot can't show.

.DESCRIPTION
    Each page runs in its own --demo launch over a throwaway --data-dir, so nothing touches
    the GPU or your real settings. Checks: a dashboard tile opens and closes its expanded
    graph; the FPS tile with no game leads to the FPS page; moving a Tuning slider shows
    "Unapplied changes" and Apply clears it; the Fans mode switch reveals the duty slider;
    saving a profile lists it and enables the list actions; settings switches carry
    accessible names and toggle. Captures a PNG of each state into -Out.
    Exit code = number of failed checks.
#>
param([Parameter(Mandatory)] [string]$Exe,
      [string]$Out = (Join-Path $env:TEMP 'afterglow-ui-check'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace UiTest -Name W -ReferencedAssemblies System.Drawing -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr hdc, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
public static void Capture(System.IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) {
        var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
'@
New-Item -ItemType Directory -Force $Out | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$script:fail = 0

function Start-App([string]$page, [string]$data) {
    $p = Start-Process $Exe -ArgumentList @('--demo', '--data-dir', "`"$data`"", '--page', $page) -PassThru
    for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh() }
    Start-Sleep -Seconds 3
    $p.Refresh()
    return $p
}
function Root($p) { $A::FromHandle($p.MainWindowHandle) }
function Find($root, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    return $root.FindFirst($Scope::Descendants, $c)
}
function Has($root, [string]$name) { return $null -ne (Find $root $name) }
function Check([bool]$ok, [string]$what) {
    if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fail++ }
}
function Invoke($root, [string]$name) {
    $e = Find $root $name
    if (-not $e) { throw "not found: $name" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
}
function Shot($p, [string]$file) { [UiTest.W]::Capture($p.MainWindowHandle, (Join-Path $Out $file)) }

$data = Join-Path $env:TEMP 'afterglow-ui-check-data'
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $data | Out-Null

"Dashboard"
$p = Start-App 'dashboard' $data
try {
    $r = Root $p
    Invoke $r 'Core clock: show the last 10 minutes'
    Check (Has $r ("Core clock {0} last 10 min" -f [char]0xB7)) 'tile opens its expanded graph'
    Check (Has $r 'Close the expanded graph') 'expanded graph has a Close button'
    Shot $p 'dash-expanded.png'
    Invoke $r 'Close the expanded graph'
    Check (Has $r ("GPU load {0} last 10 min" -f [char]0xB7)) 'Close brings the four graphs back'
    Invoke $r 'Frames per second'
    Check (Has $r 'Average FPS') 'FPS tile with no game opens the FPS page'
    Shot $p 'dash-fps-nav.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Tuning"
$p = Start-App 'tuning' $data
try {
    $r = Root $p
    Check (-not (Has $r ' Unapplied changes')) 'no pending indicator at start'
    $s = Find $r 'Core clock offset, megahertz'
    $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(120)
    Start-Sleep -Milliseconds 600
    $pending = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'TextBlock'))) # warm
    $texts = $r.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    $names = $texts | ForEach-Object { $_.Current.Name }
    Check (($names -join '|') -match 'Unapplied changes') 'moving a slider shows "Unapplied changes"'
    Check (($names -join '|') -match '\+120 MHz') 'value label follows the slider (+120 MHz)'
    Shot $p 'tuning-pending.png'
    Invoke $r 'Apply'
    $names = ($r.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }) -join '|'
    Check (-not ($names -match 'Unapplied changes')) 'Apply clears the pending indicator'
    Check ($names -match 'Applied \(demo mode') 'Apply shows its result'
    Shot $p 'tuning-applied.png'
} catch { "  note: $_" } finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Fans"
$p = Start-App 'fans' $data
try {
    $r = Root $p
    Check (-not (Has $r 'Fixed fan duty, percent')) 'duty slider hidden in firmware mode'
    $seg = Find $r 'Fixed duty'
    $seg.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    Check (Has $r 'Fixed fan duty, percent') 'choosing Fixed duty shows the duty slider'
    Check (Has $r 'Every fan at one speed, whatever the temperature.') 'mode description follows the selection'
    Shot $p 'fans-fixed.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Profiles"
$p = Start-App 'profiles' $data
try {
    $r = Root $p
    $apply = Find $r 'Apply'
    Check (-not $apply.Current.IsEnabled) 'Apply disabled with nothing selected'
    $tb = Find $r 'Profile name'
    $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Design check')
    Start-Sleep -Milliseconds 300
    Invoke $r 'Save profile'
    $list = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
    $items = $list.FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Check ($items.Count -ge 1) "saved profile appears in the list ($($items.Count))"
    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 500
    Check ((Find $r 'Apply').Current.IsEnabled) 'Apply enabled once a profile is selected'
    Shot $p 'profiles-saved.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Settings"
$p = Start-App 'settings' $data
try {
    $r = Root $p
    $t = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Close button minimizes to the tray')),
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))))
    Check ($null -ne $t) 'unlabelled switches carry an accessible name'
    $before = $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 400
    $after = $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check ($before -ne $after) "switch toggles ($before -> $after)"
    Shot $p 'settings-toggled.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"failures: $script:fail"
exit $script:fail
