# Builds the launcher into app\ and the tools into tools\bin\<tool>\ (needs the .NET 10 SDK).
#   powershell -File build.ps1            launcher + all tools
#   powershell -File build.ps1 -Launcher  launcher only
param([switch]$Launcher)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build "$root\launcher" -c Release -o "$root\app"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Launcher) { exit 0 }

foreach ($tool in 'dupshot', 'aimrig', 'mempatch', 'rpcs3hold', 'ps2mem', 'ps3mem') {
    dotnet build "$root\tools\$tool" -c Release -o "$root\tools\bin\$tool"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
"Built: app\LightgunLauncher.exe and tools\bin\*"
