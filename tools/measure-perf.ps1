<#
.SYNOPSIS
    Measures Afterglow's resident cost: time to first window, steady-state CPU,
    memory, and (optionally) the managed allocation rate.

.DESCRIPTION
    Every run uses a fresh --data-dir under -OutDir, so the installed copy's
    settings, profiles and flight recordings are never read or rotated, and
    runs unelevated (--no-elevate): monitoring only, no hardware writes.

    Scenarios:
      visible    the window open on the dashboard (graphs drawing every tick)
      tray       started with --minimized: hidden in the tray, the way the
                 logon task runs it most of the day
      minimized  shown on the dashboard, then minimized after 5 s — the
                 window and its views exist but nothing is on screen

    CPU is the process's own TotalProcessorTime delta over the window, as a
    percentage of ONE logical core. The default 90 s warm-up matters: methods
    called once per tick are re-JIT'd at tier 1 only after ~30-60 s, and those
    bursts would otherwise dominate a steady-state reading. StartupCpuMs is the
    CPU burned during the warm-up itself (startup + JIT), reported separately.
    Startup is launch -> main window handle (visible/minimized). Each metric is
    the median of -Runs.

.EXAMPLE
    ./tools/measure-perf.ps1 -Exe artifacts\perf\after\Afterglow.exe -Scenario tray -Runs 3
#>
param(
    [string]$Exe = '',
    [ValidateSet('visible', 'tray', 'minimized')] [string]$Scenario = 'visible',
    [int]$WarmupSeconds = 90,
    [int]$MeasureSeconds = 120,
    [int]$Runs = 3,
    # Optional path to dotnet-counters.exe; adds allocation rate and GC counts.
    [string]$Counters = '',
    [string]$OutDir = (Join-Path $env:TEMP 'afterglow-perf'),
    [string]$Label = '',
    # Recompute the summary from a previous run's saved CSVs and JSON.
    [switch]$SummarizeOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace AgPerf -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@
if (-not $SummarizeOnly) {
    $Exe = (Resolve-Path $Exe).Path
    if (-not $Label) { $Label = Split-Path (Split-Path $Exe -Parent) -Leaf }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $n = $sorted.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2) { return $sorted[[int][math]::Floor($n / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

function Get-CounterMetrics([string]$path, [int]$seconds) {
    # dotnet-counters CSV: Timestamp,Provider,Counter Name,Counter Type,Mean/Increment.
    # Rate counters ("/ 1 sec") log one increment per refresh interval.
    $m = @{ AllocKBps = [double]::NaN; Gen0PerMin = [double]::NaN; GcCommittedMB = [double]::NaN; JitMethods = [double]::NaN }
    if (-not (Test-Path $path)) { return $m }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $alloc = @(); $gen0 = 0.0; $committed = @(); $jit = 0.0
    foreach ($row in Import-Csv $path) {
        $name = $row.'Counter Name'
        $v = [double]::Parse($row.'Mean/Increment', $inv)
        if ($name -like 'dotnet.gc.heap.total_allocated*') { $alloc += $v }
        elseif ($name -like 'dotnet.gc.collections*gen0*') { $gen0 += $v }
        elseif ($name -like 'dotnet.gc.last_collection.memory.committed_size*') { $committed += $v }
        elseif ($name -like 'dotnet.jit.compiled_methods*') { $jit += $v }
    }
    if ($alloc.Count) { $m.AllocKBps = [math]::Round((($alloc | Measure-Object -Average).Average) / 1KB, 1) }
    $m.Gen0PerMin = [math]::Round($gen0 * 60 / $seconds, 2)
    if ($committed.Count) { $m.GcCommittedMB = [math]::Round(($committed | Measure-Object -Maximum).Maximum / 1MB, 1) }
    $m.JitMethods = $jit
    return $m
}

function Write-Summary($rows) {
    $summary = [ordered]@{
        Label          = $Label
        Scenario       = $Scenario
        Runs           = @($rows).Count
        MeasureSeconds = $MeasureSeconds
    }
    foreach ($k in 'StartupMs', 'StartupCpuMs', 'CpuPctOneCore', 'WorkingSetMB', 'PrivateMB', 'Threads', 'Handles',
                   'AllocKBps', 'Gen0PerMin', 'GcCommittedMB', 'JitMethods') {
        $summary[$k] = Get-Median (@($rows) | ForEach-Object { [double]$_.$k })
    }
    $json = Join-Path $OutDir "summary-$Label-$Scenario.json"
    [pscustomobject]@{ Summary = $summary; Runs = @($rows) } | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $json
    [pscustomobject]$summary | Format-List
    Write-Host "Saved $json"
}

if ($SummarizeOnly) {
    # Re-derive the counter columns from the saved CSVs (e.g. after a parser fix).
    $saved = Get-Content (Join-Path $OutDir "summary-$Label-$Scenario.json") -Raw | ConvertFrom-Json
    $rows = foreach ($r in $saved.Runs) {
        $c = Get-CounterMetrics (Join-Path $OutDir "counters-$Label-$Scenario-$($r.Run).csv") $saved.Summary.MeasureSeconds
        $o = [ordered]@{}
        foreach ($p in $r.PSObject.Properties) { $o[$p.Name] = $p.Value }
        foreach ($k in $c.Keys) { $o[$k] = $c[$k] }
        [pscustomobject]$o
    }
    $MeasureSeconds = $saved.Summary.MeasureSeconds
    Write-Summary $rows
    return
}

$rows = @()
for ($run = 1; $run -le $Runs; $run++) {
    $data = Join-Path $OutDir "data-$Label-$Scenario-$run"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    New-Item -ItemType Directory -Force $data | Out-Null

    $argList = @('--no-elevate', '--data-dir', "`"$data`"")
    if ($Scenario -eq 'tray') { $argList += '--minimized' }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
    $startupMs = [double]::NaN
    if ($Scenario -ne 'tray') {
        while ($sw.Elapsed.TotalSeconds -lt 60) {
            $p.Refresh()
            if ($p.HasExited) { throw "Afterglow exited during startup (code $($p.ExitCode))" }
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $startupMs = $sw.Elapsed.TotalMilliseconds; break }
            Start-Sleep -Milliseconds 10
        }
    }

    if ($Scenario -eq 'minimized') {
        Start-Sleep -Seconds 5
        [AgPerf.Win32]::ShowWindow($p.MainWindowHandle, 6) | Out-Null   # SW_MINIMIZE
    }

    Start-Sleep -Seconds $WarmupSeconds
    $p.Refresh()
    if ($p.HasExited) { throw "Afterglow exited during warm-up (code $($p.ExitCode))" }
    $startupCpuMs = $p.TotalProcessorTime.TotalMilliseconds

    $counterProc = $null
    $csv = Join-Path $OutDir "counters-$Label-$Scenario-$run.csv"
    if ($Counters) {
        if (Test-Path $csv) { Remove-Item $csv }
        $duration = [TimeSpan]::FromSeconds($MeasureSeconds).ToString('hh\:mm\:ss')
        $counterProc = Start-Process -FilePath $Counters -PassThru -WindowStyle Hidden -ArgumentList @(
            'collect', '-p', $p.Id, '--counters', 'System.Runtime', '--refresh-interval', '1',
            '--format', 'csv', '-o', "`"$csv`"", '--duration', $duration)
        Start-Sleep -Milliseconds 1500   # let the session attach before the CPU window opens
    }

    $cpu0 = $p.TotalProcessorTime
    $wall = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $MeasureSeconds
    $p.Refresh()
    $cpuMs = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds
    $wallMs = $wall.Elapsed.TotalMilliseconds

    $row = [ordered]@{
        Run          = $run
        StartupMs    = [math]::Round($startupMs, 0)
        StartupCpuMs = [math]::Round($startupCpuMs, 0)
        CpuPctOneCore = [math]::Round(100 * $cpuMs / $wallMs, 3)
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Threads      = $p.Threads.Count
        Handles      = $p.HandleCount
        AllocKBps    = [double]::NaN
        Gen0PerMin   = [double]::NaN
        GcCommittedMB = [double]::NaN
        JitMethods   = [double]::NaN
    }

    if ($counterProc) {
        $counterProc.WaitForExit(($MeasureSeconds + 30) * 1000) | Out-Null
        $c = Get-CounterMetrics $csv $MeasureSeconds
        foreach ($k in $c.Keys) { $row[$k] = $c[$k] }
    }

    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(10000) | Out-Null
    $rows += [pscustomobject]$row
    Write-Host ("run {0}: startup {1} ms, CPU {2}% of a core, WS {3} MB, private {4} MB, alloc {5} KB/s" -f `
        $run, $row.StartupMs, $row.CpuPctOneCore, $row.WorkingSetMB, $row.PrivateMB, $row.AllocKBps)
}

Write-Summary $rows
