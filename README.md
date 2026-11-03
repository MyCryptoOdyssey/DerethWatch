# DerethWatch

A Decal plugin for Asheron's Call multiboxers. One DLL that keeps an eye on every client:
it brings a crashed bot back to work, notices when a character is stuck on its route,
shows session stats, and puts a live strip of all your clients on screen.

Built and tested against an ACEmulator server with Decal 2.9.8.3, Virindi View Service,
Virindi Tank 1.0.0.0 and UtilityBelt.

## What it does

The panel has two tabs. The window resizes to the open tab.

### Stats tab

- **Session stats** for this character since login (or the **Reset** button): XP, luminance
  earned, killing blows (each with a per-hour rate; rates show `--` for the first minute), deaths and
  rares found, with the last rare and its tier (1-6).
  - XP: Decal `CharacterFilter.TotalXP`.
  - Luminance: read from the server's property updates (login description + updates, key 6),
    gains only, so spending luminance doesn't lower "earned".
  - Kills: the server's killer notification (event 0x01AD), i.e. killing blows only. Pet kills and
    kills finished by someone else don't count.
  - Deaths: the server's death-count update (sent on every death, `@die` included), plus its death message when there is one.
  - Rares: the "*name* has discovered the *item*!" announcement for this character. Every rare
    counts; the tier comes from a built-in table (`src/rares.tsv`, 295 names, from ACEmulator's rare
    tier lists). Every rare is also written to `rares.log`.
- **Character Views (all clients)**: one strip on the desktop showing every running client.
  - **Show**: `Text` (names + stats), `Text + Client View` (live pictures with a stats caption under
    each) or `Client View` (live pictures only).
  - **Layout**: `Horizontal` (bottom-centre) or `Vertical` (right edge).
  - **In Game View Only**: hide the strip when something other than AC is in front.
  - Caption: `Lv | xp/h | kills/h | rares | died | VT` in gold (rares and died only once there are any); **VT** is green while Virindi Tank's macro
    runs, red when it is off; grey "no stats" = that client has Stats off or isn't in game.
  - Click a character to switch to that client. **Ctrl + drag** moves the whole strip; each
    Show/Layout combination remembers its own position (`/dw strip reset` clears them).
  - It's one strip opened by one client (that client's Show/Layout decide its look). Ticking it on
    any client flips it on every client, and they all switch UtilityBelt's video patch off so the
    pictures render. Every client must keep **Stats** on for its caption to fill in.

### Character Settings tab (all per character)

- **Memory guard**: memory % of the client process, warnings at 70% / 85%, optional lifestone
  recall and/or logout at 90%.
- **Crash resume**: every 5 s it saves whether the VTank macro is running, the meta state, the
  route waypoint and the character's position. After an unclean exit (crash, kill, hang) and a
  relaunch (e.g. ThwargLauncher's Auto Relaunch), it starts the macro again 5 s after login.
  - **Only if same place**: skip the resume if the character isn't within 50 m of where it was
    (it died, or was moved) - restarting a route from the lifestone can get a character killed.
  - **Restore meta state** (default off): off = the meta starts from `Default`, because meta
    `setvar` values live only in VTank's memory and a crash wipes them; on = jump back to the saved
    state.
  - **Planned restart** (`/dw restart <hours>`): stop the macro, log out, close the client
    politely (so the server frees the account at once), let the launcher relaunch it, resume.
- **Stop VT on death** (default on): every death is logged (`DIED`, with place and the death message)
  and counted in Stats. With this ticked the VTank macro is stopped, so the respawned character waits at
  the lifestone instead of running its route from there; start VTank again when ready. Untick it if
  your meta handles death itself (rebuff, walk back).
- **Stuck detection**: macro on with VTank's Navigation on (macro on + Navigation off, e.g. parked at a
  vendor for autostack, pauses it until Navigation is back on), VTank not busy and not fighting (no hit, being hit, evade, own spell cast or health loss in the last 15 s), moved less than 1.5 m in 12 s -> logged with the
  route waypoint and why it isn't "busy". With **Fix automatically**: up to 3 attempts, each leaving
  the spot a different way (straight back; angled back-right with a running jump; angled back-left
  with a running jump + reload the route), judged by forward progress along the route. Fixes only run when not fighting and no monster is within 5 m. After 3 failures it
  stops the macro, names the waypoint ("fix the route"), and by default recalls to the lifestone
  (**+ log out** optional).

A module that throws an error switches itself off and logs `MODULE-ERROR`; the others keep running.

## Requirements

| Piece | Needed? |
|---|---|
| [Decal](https://www.decaldev.com/) 2.9.8.x | Required - loads the plugin |
| Virindi View Service | Required - draws the panel |
| Virindi Tank | Optional - crash resume, stuck detection, the VT caption and the meta option read it (read-only) |
| UtilityBelt | Optional - Character Views toggles its video patch so the pictures render |
| `tools/ACRecovery` | Optional - outside-the-game crash/relaunch logger and login nudge (see its README) |

## Install

1. Download `DerethWatch.dll` from the latest release (or build it, below).
2. Close all AC clients. Put the DLL in a folder of its own, e.g. `C:\Games\Decal Plugins\DerethWatch\`.
3. Decal Agent -> **Add** -> select the DLL, and make sure it is ticked.
4. Start a client. The DerethWatch panel appears in the VVS bar.

Updating = close all clients and copy the new DLL over the old one (Windows locks it while any
client has it loaded).

## Commands

```
/dw status
/dw on <module>    /dw off <module>      modules: views, memory, resume, stuck, stats, death
/dw stats          /dw stats reset
/dw rares test <item name>               dry run: shows the tier, logs nothing
/dw resume on|off|sameplace|anywhere|meta on|meta off
/dw restart <hours>|off|now
/dw unstick        /dw stuckfix on|off   /dw stuckgiveup stay|ls|lslogout
/dw strip reset
```

## Files it writes

- `<plugin folder>\state\<server>__<character>.settings.txt / .state.txt / .stats.txt` - per character.
- `<plugin folder>\state\strip.txt` - strip positions.
- `C:\Games\ACRecovery\recovery.log` - shared event log (crash, resume, stuck, rare...).
- `C:\Games\ACRecovery\rares.log` - every rare found.

Those two log paths are fixed in `src/Util.cs` and `src/StatsModule.cs`; change them there if you
keep things elsewhere.

## Build

Needs the .NET Framework 4 C# compiler (ships with Windows) and installed copies of Decal and
Virindi View Service. Check the paths at the top of `build.bat`, then run it; output is
`bin\DerethWatch.dll` (x86).

## Notes

- Virindi Tank has no public API that works for other plugins, so DerethWatch reads a few values
  (macro on/off, meta state, route, busy flag) from its loaded assembly by reflection. Read-only;
  commands go through VTank's normal `/vt` chat commands.
- Decal unloads plugins at logout, so nothing in a plugin runs after logout; the planned restart
  hands the client close to a small detached `taskkill` helper.
- Decal, Virindi Tank, Virindi View Service and UtilityBelt belong to their authors and are not
  included here.

## License

MIT - see [LICENSE](LICENSE).
