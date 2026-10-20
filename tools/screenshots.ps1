<#
.SYNOPSIS
    Renders every page to a PNG with the app's --screenshot mode (how the README images
    are made).

.DESCRIPTION
    One launch per page. -Demo uses the synthetic GPU; without it the real GPU is read
    (screenshot mode never applies tuning, restores fans or applies a startup profile).
    Run it elevated for pages without the "monitoring only" banner. -Size renders at
    another window size (e.g. 1024x660, the minimum). State comes from a throwaway folder
    unless -DataDir names one, or -RealData uses the installed copy's own (your profiles and
    measured V/F curve; screenshot mode only reads it).
#>
param([Parameter(Mandatory)] [string]$Exe,
      [string]$Out = (Join-Path $env:TEMP 'afterglow-screens'),
      [int]$Delay = 8,
      [string]$Size = '',
      [switch]$Demo,
      [string]$Prefix = '',
      [string]$DataDir = '',
      [switch]$RealData,
      [string[]]$Pages = @('dashboard', 'tuning', 'fans', 'metrics', 'vfcurve', 'stability', 'profiles', 'settings'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$data = if ($DataDir) { $DataDir } else { Join-Path $env:TEMP 'afterglow-screens-data' }
foreach ($page in $Pages) {
    $png = Join-Path $Out "$Prefix$page.png"
    if (Test-Path $png) { Remove-Item $png }
    $argList = @('--page', $page, '--screenshot', "`"$png`"", '--screenshot-delay', $Delay)
    if (-not $RealData) { $argList += @('--data-dir', "`"$data`"") }
    if ($Size) { $argList += @('--screenshot-size', $Size) }
    if ($Demo) { $argList += '--demo' }
    $p = Start-Process $Exe -ArgumentList $argList -PassThru
    if (-not $p.WaitForExit(($Delay + 30) * 1000)) { Stop-Process -Id $p.Id -Force; "$page : TIMEOUT"; continue }
    "{0,-10} exit={1} {2}" -f $page, $p.ExitCode, ($(if (Test-Path $png) { "{0:N0} KB" -f ((Get-Item $png).Length / 1KB) } else { 'NO FILE' }))
}
