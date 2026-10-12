<#
.SYNOPSIS
    Time-to-window and CLI time-to-exit, repeated launches, median reported.

.DESCRIPTION
    GUI: launch -> main window handle (window created and shown), in an
    isolated --data-dir, unelevated, then kill. The first launch is reported
    separately as "cold-ish" (file cache may still be warm from the build) and
    excluded from the median.
    CLI (optional): `afterglow-cli --data-dir X <args>` launch -> exit.
    Run on a quiet machine: wall-clock startup is sensitive to CPU contention.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string]$Cli = '',
    [string[]]$CliArgs = @('get', '--json'),
    [int]$Launches = 11,
    [string]$OutDir = (Join-Path $env:TEMP 'afterglow-startup'),
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if (-not $Label) { $Label = Split-Path (Split-Path $Exe -Parent) -Leaf }
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Get-Median([double[]]$v) {
    $s = $v | Sort-Object; $n = $s.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2) { return $s[[int][math]::Floor($n / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2
}

$gui = @()
for ($i = 0; $i -lt $Launches; $i++) {
    $data = Join-Path $OutDir "data-$Label"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Exe -ArgumentList @('--no-elevate', '--data-dir', "`"$data`"") -PassThru
    $ms = [double]::NaN
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $ms = $sw.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 5
    }
    Start-Sleep -Milliseconds 1500
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $p.WaitForExit(5000) | Out-Null
    Start-Sleep -Milliseconds 500
    $gui += $ms
}

$cliTimes = @()
if ($Cli) {
    $Cli = (Resolve-Path $Cli).Path
    $data = Join-Path $OutDir "cli-$Label"
    for ($i = 0; $i -lt $Launches; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $Cli --data-dir $data @CliArgs | Out-Null
        $cliTimes += $sw.Elapsed.TotalMilliseconds
    }
}

$result = [ordered]@{
    Label          = $Label
    GuiFirstMs     = [math]::Round($gui[0], 0)
    GuiMedianMs    = [math]::Round((Get-Median ($gui | Select-Object -Skip 1)), 0)
    GuiAllMs       = ($gui | ForEach-Object { [math]::Round($_, 0) }) -join ' '
    CliFirstMs     = if ($cliTimes) { [math]::Round($cliTimes[0], 0) } else { $null }
    CliMedianMs    = if ($cliTimes) { [math]::Round((Get-Median ($cliTimes | Select-Object -Skip 1)), 0) } else { $null }
    CliAllMs       = ($cliTimes | ForEach-Object { [math]::Round($_, 0) }) -join ' '
}
[pscustomobject]$result | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $OutDir "startup-$Label.json")
[pscustomobject]$result | Format-List
