param(
    [Parameter(Mandatory)][ValidateSet('start','cmd','stop')][string]$Action,
    [string]$Game,
    [string[]]$Commands = @(),
    [int]$WaitSec = 90,
    # Folder holding mame.exe. Default: <launcher root>\emulators\mame.
    [string]$MameDir = (Join-Path $PSScriptRoot '..\..\emulators\mame')
)
# Drives MAME through ctl.lua: start a game, send commands (waits until they finish), print the newest snapshot.
# Handy for setting free play / difficulty in service menus by script. See docs\tools.md.
$ctl = $PSScriptRoot
$mame = (Resolve-Path $MameDir).Path
$snapdir = Join-Path $ctl 'snap'
Add-Type -Name F -Namespace MCtl -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);' -ErrorAction SilentlyContinue
function Focus { $mp = Get-Process mame -ErrorAction SilentlyContinue | Select-Object -First 1; if ($mp) { [MCtl.F]::SetForegroundWindow($mp.MainWindowHandle) | Out-Null } }
switch ($Action) {
    'start' {
        Remove-Item "$ctl\ctl.log", "$ctl\cmd.txt" -ErrorAction SilentlyContinue
        $env:LIGHTGUN_CTL = $ctl -replace '\\', '/'
        Start-Process "$mame\mame.exe" -ArgumentList $Game, '-autoboot_script', "`"$ctl\ctl.lua`"", '-snapshot_directory', "`"$snapdir`"", '-nomaximize', '-skip_gameinfo', '-ctrlr', 'none' -WorkingDirectory $mame | Out-Null
        $t = 0
        while ($t -lt $WaitSec -and -not (Select-String "$ctl\ctl.log" -Pattern 'ctl ready' -Quiet -ErrorAction SilentlyContinue)) { Start-Sleep 1; $t++ }
        Focus
        Get-Content "$ctl\ctl.log" -ErrorAction SilentlyContinue
    }
    'cmd' {
        Focus
        $id = [guid]::NewGuid().ToString('N').Substring(0, 6)
        ($Commands + "done $id") | Set-Content "$ctl\cmd.txt" -Encoding ascii
        $t = 0
        while ($t -lt $WaitSec -and -not (Select-String "$ctl\ctl.log" -Pattern "done $id" -Quiet -ErrorAction SilentlyContinue)) { Start-Sleep 1; $t++ }
        if ($t -ge $WaitSec) { "TIMEOUT" } else { "took ${t}s" }
        $s = Get-ChildItem $snapdir -Recurse -Filter *.png -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($s) { "latest snap: $($s.FullName)" }
    }
    'stop' {
        Focus
        'exit' | Set-Content "$ctl\cmd.txt" -Encoding ascii
        $t = 0
        while ($t -lt 20 -and (Get-Process mame -ErrorAction SilentlyContinue)) { Start-Sleep 1; $t++ }
        Get-Process mame -ErrorAction SilentlyContinue | Stop-Process -Force
        Get-Content "$ctl\ctl.log" -Tail 2 -ErrorAction SilentlyContinue
    }
}
