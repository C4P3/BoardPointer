# Builds self-contained single-file executables into .\publish
#
# Notes (see README for the Japanese version):
#  - PublishTrimmed is deliberately OFF. InTheHand.Net.Personal.dll is a .NET Framework
#    era assembly and WinForms relies on reflection; trimming breaks both.
#  - EnableCompressionInSingleFile roughly halves the size (140MB -> 70MB).
#  - IncludeNativeLibrariesForSelfExtract keeps it to one file instead of one exe plus
#    five native DLLs.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'publish'

# Wipe the previous build, but keep .\publish\debug.
#
# The Viewer writes its recordings next to the exe (AppContext.BaseDirectory\debug), so
# session_*.csv and aimtest_*.csv pile up inside .\publish. Removing the whole folder threw
# them away on every re-publish -- and those recordings are the only thing the diagnosis
# thresholds can be calibrated against. They are not build output; do not treat them as such.
if (Test-Path $out) {
    Get-ChildItem $out -Force | Where-Object { $_.Name -ne 'debug' } | Remove-Item -Recurse -Force
}

$common = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-o', $out,
    '--nologo'
)

foreach ($project in @('BoardPointer.Viewer', 'BoardPointer.Replay')) {
    Write-Host "publishing $project ..."
    & dotnet publish (Join-Path $root "src\$project\$project.csproj") @common
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

Write-Host ''
Get-ChildItem $out | Format-Table Name, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }

Write-Host 'smoke tests:'
& (Join-Path $out 'BoardPointer.Replay.exe') --bluetooth-selftest
& (Join-Path $out 'BoardPointer.Replay.exe') --mouse-selftest
