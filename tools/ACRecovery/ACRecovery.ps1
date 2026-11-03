# ACRecovery.ps1 - crash / relaunch logger for the multibox AC clients (2026-11-02)
#
# Who does what:
#   ThwargLauncher (Auto Relaunch ticked) - relaunches any checked account whose client died,
#       logs in its chosen character, kills hung clients (no heartbeat 60s / char screen 120s).
#   DerethWatch v2 Crash resume (Decal)    - restarts the VTank macro + meta state after an
#       unclean exit, optional planned restart every N hours; writes RESUMED / PLANNED-RESTART.
#   This script                            - notices each client exit, works out WHY (Windows
#       crash event, Thwarg hang kill, planned restart, normal logout) and when it came back.
#
# Log: C:\Games\ACRecovery\recovery.log   (time | character | server | EVENT | details)
# Run: ACRecovery_start.bat (hidden). Stop: ACRecovery_stop.bat. Safe to run only once (mutex).
# Never reads or logs passwords: identity comes from ThwargFilter heartbeat files only.

$ErrorActionPreference = 'Continue'
$Root      = 'C:\Games\ACRecovery'
$LogPath   = Join-Path $Root 'recovery.log'
$Running   = Join-Path $env:APPDATA 'ThwargLauncher\Running'
$DwState   = 'C:\Games\Decal Plugins\DerethWatch\state'   # DerethWatch v2 (crash resume module)
$LwState   = 'C:\Games\Decal Plugins\LeakWatch\state'     # LeakWatch v2, before the merge
$PollSec   = 10
$ResolveSec = 20      # wait for Windows Error Reporting to write the crash event
$NoRelaunchSec = 420  # warn if not back by then (normal recovery ~4-5 min: first relaunch hits the server's ghost session)

$ThwargLogs = Join-Path $env:APPDATA 'ThwargLauncher\Logs'

$mutex = New-Object System.Threading.Mutex($false, 'Global\ACRecoveryWatchdog')
if (-not $mutex.WaitOne(0)) { exit 0 }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ACRWin {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
}
"@
[void][ACRWin]::SetProcessDPIAware()   # acclient is DPI-aware (HIGHDPIAWARE): screen maths in real pixels

# Real mouse click on the client's Enter button: bring it forward, move the cursor there, click,
# then put the cursor and the previously active window back (well under a second).
function Invoke-RealEnterClick([IntPtr]$h) {
    $prevFg = [ACRWin]::GetForegroundWindow(); $old = New-Object ACRWin+P; [void][ACRWin]::GetCursorPos([ref]$old)
    $pt = New-Object ACRWin+P; $pt.X = 0x015C; $pt.Y = 0x0185; [void][ACRWin]::ClientToScreen($h, [ref]$pt)
    [ACRWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [ACRWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)   # ALT tap: allowed to take foreground
    [void][ACRWin]::ShowWindow($h, 9); [void][ACRWin]::SetForegroundWindow($h); Start-Sleep -Milliseconds 250
    [void][ACRWin]::SetCursorPos($pt.X - 4, $pt.Y); Start-Sleep -Milliseconds 80
    [void][ACRWin]::SetCursorPos($pt.X, $pt.Y); Start-Sleep -Milliseconds 120
    [ACRWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 90
    [ACRWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
    [void][ACRWin]::SetCursorPos($old.X, $old.Y)
    if ($prevFg -ne [IntPtr]::Zero) { [void][ACRWin]::SetForegroundWindow($prevFg) }
}

# ThwargFilter auto-login clicks Enter on the character screen, but AC ignores that click when
# its window is not the active one (a relaunched client opens behind other windows). If the
# click did not take, tell the window it is active (WM_ACTIVATE/WM_NCACTIVATE, no focus stealing)
# and click Enter again at ThwargFilter's own spot (0x15C,0x185). Witnessed working 2026-11-02.
function Invoke-LoginNudge($s, $proc) {
    $lf = Join-Path $ThwargLogs "ThwargFilter_$($s.Pid)_log.txt"
    if (-not (Test-Path $lf)) { return }
    $lines = @(Get-Content $lf -ErrorAction SilentlyContinue)
    $last = $null
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match 'ClientDispatch: Character') { return }          # already entering the world
        if ($lines[$i] -match '^(\S+ \S+) .*LoginByIndex') { $last = [datetime]$Matches[1]; break }
    }
    if (-not $last -or ((Get-Date) - $last).TotalSeconds -lt 10) { return }
    if ($s.Nudges -ge 3 -or ((Get-Date) - $s.LastNudge).TotalSeconds -lt 10) { return }
    $h = $proc.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { return }
    if ($s.Nudges -ge 1) {
        # 2026-11-02 14:45-14:59: posted clicks stopped registering (even with focus); a real click
        # always worked. So try 1 = quiet posted click, tries 2-3 = real click.
        Invoke-RealEnterClick $h
        $s.Nudges++; $s.LastNudge = Get-Date
        Write-Log $(if ($s.Char) { $s.Char } else { '?' }) $s.Server 'LOGIN-NUDGE' "pid $($s.Pid) account $($s.Account) stuck on character screen; REAL mouse click on Enter (try $($s.Nudges) of 3)"
        return
    }
    [void][ACRWin]::PostMessage($h, 0x0006, [IntPtr]1, [IntPtr]::Zero)    # WM_ACTIVATE
    [void][ACRWin]::PostMessage($h, 0x0086, [IntPtr]1, [IntPtr]::Zero)    # WM_NCACTIVATE
    Start-Sleep -Milliseconds 400
    $lp = [IntPtr]((0x0185 -shl 16) -bor 0x015C)
    # AC buttons only take a click after the pointer has hovered over them: move onto the
    # button first (a real click always arrives after mouse moves). 2026-11-02 14:45: clicks
    # without the hover stopped working; a manual click logged in.
    foreach ($dx in -6, -3, 0) {
        $mv = [IntPtr]((0x0185 -shl 16) -bor (0x015C + $dx))
        [void][ACRWin]::PostMessage($h, 0x0200, [IntPtr]0, $mv); Start-Sleep -Milliseconds 120   # WM_MOUSEMOVE
    }
    [void][ACRWin]::PostMessage($h, 0x0201, [IntPtr]1, $lp); Start-Sleep -Milliseconds 120          # WM_LBUTTONDOWN
    [void][ACRWin]::PostMessage($h, 0x0202, [IntPtr]0, $lp)                                           # WM_LBUTTONUP
    $s.Nudges++; $s.LastNudge = Get-Date
    Write-Log $(if ($s.Char) { $s.Char } else { '?' }) $s.Server 'LOGIN-NUDGE' "pid $($s.Pid) account $($s.Account) stuck on character screen; re-clicked Enter (try $($s.Nudges) of 3)"
}

function Write-Log([string]$char, [string]$server, [string]$evt, [string]$detail) {
    $line = '{0} | {1} | {2} | {3} | {4}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $char, $server, $evt, $detail
    for ($i = 0; $i -lt 5; $i++) {
        try { [IO.File]::AppendAllText($LogPath, $line + "`r`n"); return } catch { Start-Sleep -Milliseconds 100 }
    }
}

function Read-Kv([string]$path) {
    $d = @{}
    try {
        foreach ($l in [IO.File]::ReadAllLines($path)) {
            $i = $l.IndexOf(':'); $j = $l.IndexOf('=')
            if ($j -gt 0 -and ($i -lt 0 -or $j -lt $i)) { $d[$l.Substring(0, $j).Trim([char]0xFEFF)] = $l.Substring($j + 1) }
            elseif ($i -gt 0) { $d[$l.Substring(0, $i).Trim([char]0xFEFF)] = $l.Substring($i + 1) }
        }
    } catch {}
    return $d
}

function Get-LwState([string]$server, [string]$char) {
    $key = "${server}__${char}"
    foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $key = $key.Replace([string]$c, '_') }
    $dw = Join-Path $DwState "$key.state.txt"
    if (Test-Path $dw) { return Read-Kv $dw }
    return Read-Kv (Join-Path $LwState "$key.state.txt")
}

function Get-ExitReason($s) {
    # 1) Windows crash event for this exact process id (Event 1000 'Application Error')
    $pidHex = '0x{0:x}' -f $s.Pid
    try {
        $evs = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $s.Start } -ErrorAction Stop |
              Where-Object { $_.Message -match 'acclient\.exe' -and $_.Message -match "(?i)process id: $pidHex\b" } |
              Sort-Object TimeCreated)
    } catch { $evs = @() }
    if ($evs.Count -gt 0) {
        # VTank finalizer race shows as acclient.exe AV, then 'unknown' (JIT code) AV a few seconds later
        $faults = foreach ($e in $evs) {
            $m = $e.Message
            $mod  = if ($m -match '(?i)Faulting module name: ([^,\r\n]+)') { $Matches[1] } else { '?' }
            $code = if ($m -match '(?i)Exception code: (0x[0-9a-f]+)') { $Matches[1] } else { '?' }
            $off  = if ($m -match '(?i)Fault offset: (0x[0-9a-f]+)') { $Matches[1] } else { '?' }
            "$mod+$off $code"
        }
        $ev = $evs[0]
        $net = ''
        try {
            $n = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1026; StartTime = $ev.TimeCreated.AddSeconds(-30); EndTime = $ev.TimeCreated.AddSeconds(30) } -ErrorAction Stop |
                 Where-Object { $_.Message -match 'acclient\.exe' } | Select-Object -First 1
            if ($n -and $n.Message -match '(?i)Exception Info: ([^\r\n]+)') { $net = "; .NET: " + $Matches[1] }
        } catch {}
        $vt = if ($faults.Count -ge 2 -and ($faults -join ' ') -match '^acclient\.exe.*unknown') { ' [VTank finalizer signature]' } else { '' }
        return @('CRASH', ("pid $($s.Pid) fault " + ($faults -join ' then ') + "$net$vt"))
    }
    # 2) DerethWatch planned restart (a helper it starts closes the client after logout)
    $lw = Get-LwState $s.Server $s.Char
    if ($lw['Planned'] -eq '1' -and $lw['Pid'] -eq [string]$s.Pid) { return @('EXIT', "pid $($s.Pid) planned restart by DerethWatch") }
    # 3) heartbeat had gone stale -> ThwargLauncher's hang kill (or a hard freeze)
    $stale = [int]((Get-Date) - $s.LastBeat).TotalSeconds - $ResolveSec
    if ($s.LastBeat -ne [datetime]::MinValue -and $stale -ge 45) { return @('HUNG', "pid $($s.Pid) heartbeat stopped ~${stale}s before exit (ThwargLauncher kills clients with no heartbeat for 60s)") }
    if ($s.LastOnline -eq $false) { return @('EXIT', "pid $($s.Pid) was not in game (character screen / offline) - ThwargLauncher closes those after 120s") }
    if ($lw['Clean'] -eq '1' -and $lw['Pid'] -eq [string]$s.Pid) { return @('EXIT', "pid $($s.Pid) normal close after logout") }
    return @('EXIT', "pid $($s.Pid) closed or killed while in game (no Windows crash event)")
}

$sessions = @{}   # pid -> session info
$deaths   = @{}   # "server|char" -> @{ Time; Warned }
$pending  = New-Object System.Collections.ArrayList

Write-Log '-' '-' 'WATCHDOG-START' "polling every ${PollSec}s (pid $PID)"

while ($true) {
    $now = Get-Date
    $procs = @{}
    foreach ($p in @(Get-Process -Name acclient -ErrorAction SilentlyContinue)) { $procs[$p.Id] = $p }

    # running clients: learn identity from the ThwargFilter heartbeat
    foreach ($procId in $procs.Keys) {
        if (-not $sessions.ContainsKey($procId)) {
            $st = try { $procs[$procId].StartTime } catch { $now }
            $sessions[$procId] = @{ Pid = $procId; Start = $st; Char = ''; Server = ''; Account = ''; LastBeat = [datetime]::MinValue; LastOnline = $null; Announced = $false; Nudges = 0; LastNudge = [datetime]::MinValue }
        }
        $s = $sessions[$procId]
        $hb = Join-Path $Running "game_$procId.txt"
        if (Test-Path $hb) {
            $kv = Read-Kv $hb
            $c = if ($kv['ActualCharacterName']) { $kv['ActualCharacterName'] } else { $kv['CharacterName'] }
            if ($c -and $c -ne 'LoginNotComplete') { $s.Char = $c }   # ThwargFilter placeholder before login
            if ($kv['ServerName']) { $s.Server = $kv['ServerName'] }
            if ($kv['AccountName']) { $s.Account = $kv['AccountName'] }
            if ($kv['IsOnline']) { $s.LastOnline = ($kv['IsOnline'] -eq 'True') }
            $s.LastBeat = (Get-Item $hb).LastWriteTime
        }
        if (($now - $s.Start).TotalMinutes -lt 5) { try { Invoke-LoginNudge $s $procs[$procId] } catch {} }
        if (-not $s.Announced -and $s.Char -and $s.LastOnline) {
            $s.Announced = $true
            $key = "$($s.Server)|$($s.Char)"
            if ($deaths.ContainsKey($key)) {
                $down = [int]($now - $deaths[$key].Time).TotalSeconds
                Write-Log $s.Char $s.Server 'RELAUNCHED' "pid $procId account $($s.Account) in game ${down}s after the previous client died"
                $deaths.Remove($key)
            } else {
                $up = [math]::Round(($now - $s.Start).TotalHours, 1)
                Write-Log $s.Char $s.Server 'CLIENT-UP' "pid $procId account $($s.Account) (client uptime ${up}h)"
            }
        }
    }

    # clients that vanished: resolve the reason a little later
    foreach ($procId in @($sessions.Keys)) {
        if (-not $procs.ContainsKey($procId)) {
            $s = $sessions[$procId]; $sessions.Remove($procId)
            $s.Gone = $now
            [void]$pending.Add($s)
        }
    }
    foreach ($s in @($pending)) {
        if (($now - $s.Gone).TotalSeconds -lt $ResolveSec) { continue }
        $pending.Remove($s)
        if (-not $s.Char) { Write-Log '?' $s.Server 'EXIT' "pid $($s.Pid) account $($s.Account) closed before reaching a character"; continue }
        $r = Get-ExitReason $s
        $up = [math]::Round(($s.Gone - $s.Start).TotalHours, 1)
        Write-Log $s.Char $s.Server $r[0] ("{0}; uptime {1}h; account {2}" -f $r[1], $up, $s.Account)
        if ($r[1] -notmatch 'normal close after logout') {   # deliberate logouts are not expected back
            $deaths["$($s.Server)|$($s.Char)"] = @{ Time = $s.Gone; Warned = $false }
        }
    }

    # nobody came back?
    foreach ($key in @($deaths.Keys)) {
        $d = $deaths[$key]
        if (-not $d.Warned -and ($now - $d.Time).TotalSeconds -ge $NoRelaunchSec) {
            $d.Warned = $true
            $parts = $key.Split('|')
            $thwarg = if (Get-Process -Name ThwargLauncher -ErrorAction SilentlyContinue) { 'ThwargLauncher is running' } else { 'ThwargLauncher is NOT running' }
            Write-Log $parts[1] $parts[0] 'NOT-RELAUNCHED' "still down after ${NoRelaunchSec}s; $thwarg - check Auto Relaunch is ticked and the account is checked"
        }
        if (($now - $d.Time).TotalHours -ge 6) { $deaths.Remove($key) }
    }

    Start-Sleep -Seconds $PollSec
}
