param(
    [string]$Version = "v0.0.0-dev"
)

# Publishes the self-contained portable build into artifacts/.
#
# Layout: the app and the CLI share ONE self-contained runtime in one folder,
# ReadyToRun-compiled, English resources only. Measured against the previous
# compressed single-file pair (docs/performance.md, tray scenario): working set
# 233 -> ~137 MB, private bytes 178 -> ~119 MB, a smaller download (89 -> ~59 MB
# zip), at the cost of ~250 files instead of two exes and ~+40 MB on disk.
# A compressed single-file exe decompresses every assembly into private memory
# at each launch; here they are memory-mapped and shared by app and CLI.
$ErrorActionPreference = "Stop"
$clean = $Version.TrimStart("v")
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\portable"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$publish = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained',
    '-p:PublishReadyToRun=true',
    '-p:SatelliteResourceLanguages=en',
    "-p:Version=$clean",
    '-o', $out
)

dotnet publish (Join-Path $root "src\Afterglow.App") @publish
if ($LASTEXITCODE -ne 0) { exit 1 }

# Same folder: the CLI reuses the app's runtime files (identical versions).
dotnet publish (Join-Path $root "src\Afterglow.Cli") @publish
if ($LASTEXITCODE -ne 0) { exit 1 }

# Trim publish noise.
Get-ChildItem $out -Filter "*.pdb" | Remove-Item

# Belt-and-suspenders: earlier single-file publishes were seen to skip bundled
# native exes, so the release never depends on the item metadata alone.
# A wildcard Copy-Item never creates its destination, so make sure it exists —
# otherwise the repair fails in exactly the case it exists for (F-69).
$presentMonOut = Join-Path $out "ThirdParty\PresentMon"
New-Item -ItemType Directory -Force $presentMonOut | Out-Null
Copy-Item (Join-Path $root "ThirdParty\PresentMon\*") $presentMonOut -Force
if (-not (Test-Path (Join-Path $out "ThirdParty\PresentMon\PresentMon-2.5.1-x64.exe"))) {
    Write-Error "PresentMon missing from publish output"
    exit 1
}

# Both entry points must be present and runnable from the shared folder.
foreach ($exe in 'Afterglow.exe', 'afterglow-cli.exe') {
    if (-not (Test-Path (Join-Path $out $exe))) {
        Write-Error "$exe missing from publish output"
        exit 1
    }
}

& (Join-Path $out 'afterglow-cli.exe') help | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "afterglow-cli.exe does not run from the publish folder (exit $LASTEXITCODE)"
    exit 1
}

Copy-Item (Join-Path $root "LICENSE") $out
Copy-Item (Join-Path $root "THIRD_PARTY.md") $out
Copy-Item (Join-Path $root "README.md") $out -ErrorAction SilentlyContinue

$zip = Join-Path $root "artifacts\Afterglow-$clean-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip

Write-Host "Portable build: $zip"
Get-ChildItem (Join-Path $root "artifacts")
