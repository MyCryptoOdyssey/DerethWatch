# ACRecovery - crash / relaunch logger for multiboxed AC clients

A small PowerShell watcher that runs outside the game, next to DerethWatch.

Why: Virindi Tank 1.0.0.0 (final build, no updates) can crash the client from its .NET finalizer
thread, mostly after 15-35 h of uptime. Nothing can fix VTank, so the setup recovers instead.

## The three pieces

| Piece | Job |
|---|---|
| ThwargLauncher **Auto Relaunch** (built in) | Launches any account that is CHECKED in the current profile and not running, and logs in that account's chosen character. Also kills hung clients (no heartbeat 60 s) and clients stuck on the character screen (120 s). |
| **DerethWatch** Crash resume (Decal plugin) | Restarts the VTank macro after an unclean exit, only if the character is still where it was. |
| **ACRecovery.ps1** (this folder) | Logs every client exit with the reason (Windows crash event, launcher hang kill, planned restart, normal close) and when the character came back. **Login nudge**: if a relaunched client sits on the character screen, it re-clicks Enter (try 1 a posted click; tries 2-3 a real mouse click that briefly raises the AC window, then puts the cursor and focus back). |

It never reads or logs passwords: identity comes from ThwargFilter's heartbeat files only.

## Setup

1. Copy this folder to `C:\Games\ACRecovery\` (the paths are set at the top of `ACRecovery.ps1`
   and in the two `.bat` files; change all three if you use another folder).
2. Start: `ACRecovery_start.bat` (runs hidden; only one copy ever runs). Stop: `ACRecovery_stop.bat`.
3. Optional: a shortcut to `ACRecovery_start.bat` in `shell:startup` starts it with Windows.

## The log: `recovery.log`

`time | character | server | EVENT | details`

| EVENT | Written by | Meaning |
|---|---|---|
| WATCHDOG-START | ACRecovery | logger started |
| CLIENT-UP | ACRecovery | a client reached the game (first time seen) |
| CRASH | ACRecovery | Windows crash event for that pid; `[VTank finalizer signature]` = acclient.exe AV then an `unknown` module AV |
| HUNG | ACRecovery | heartbeat stopped before the exit (launcher's hang kill) |
| EXIT | ACRecovery | other exits: planned restart, character screen, normal close, killed |
| RELAUNCHED | ACRecovery | same character back in game, with seconds of downtime |
| NOT-RELAUNCHED | ACRecovery | still down after 7 min - check the launcher |
| LOGIN-NUDGE | ACRecovery | relaunched client stuck on the character screen; Enter re-clicked |
| RESUMED / RESUME-FAILED / RESUME-SKIPPED | DerethWatch | macro restarted / not restarted / skipped (resume off, or the character moved or died) |
| PLANNED-RESTART / CLIENT-CLOSE | DerethWatch | planned restart started / client being closed |
| STUCK, STUCK-FIX, STUCK-FIXED, STUCK-CLEARED | DerethWatch | character not moving with the macro on; unstick attempts |
| STUCK-GAVE-UP / STUCK-RECALL / STUCK-LOGOUT | DerethWatch | 3 attempts failed: macro stopped, lifestone recall, optional logout |
| RARE | DerethWatch | a rare found (also in `rares.log`) |
| MODULE-ERROR | DerethWatch | one module hit an error and switched itself off |

## Rules of thumb (Auto Relaunch on)

- The launcher relaunches EVERYTHING checked in the current profile. Use a profile with only the
  bot accounts checked (each with its character chosen).
- To stop a bot for real, untick its account first, then log it out. Unticking while the client
  runs leaves it playing but it won't be brought back after a crash (good for "AFK for days in a
  dangerous area").
- ThwargLauncher must stay open, and **Never Kill Clients** must stay unticked.
- A killed client's session lingers on the server for about a minute, so the first relaunch after
  a crash is usually refused ("account in use") and the next one gets in: a crash recovery takes
  about 4 minutes. A planned restart closes the client politely and is back in about 70 s.
- If the launcher's `LauncherGameTimeoutSeconds` / `RelaunchIntervalSeconds` (in
  `ThwargLauncher.exe.config`) are shorter than that ghost session, it can loop on "account in
  use"; 120 / 90 worked here.
