# Building on a new machine

What to install, how to build, and how to confirm the result actually works in
game. Written for someone setting this up for the first time.

## 1. Prerequisites

| What | Notes |
|------|-------|
| .NET SDK 10 | `winget install Microsoft.DotNet.SDK.10` - the version is pinned in [global.json](../global.json). |
| XIVLauncher + Dalamud | The build references Dalamud's assemblies from `%AppData%\XIVLauncher\addon\Hooks\dev\`. |
| Umbra | Only for `src/Scenariometer.Umbra`. Install through Dalamud, or set `UMBRA_HOME`. |
| Python 3 | Only to regenerate the icon. On Windows the command is `python`, not `python3`. |
| An IDE | Visual Studio 2022+ or Rider. The solution is `.slnx`, which needs a recent version of either. |

Windows only in practice. The plugin targets `net10.0-windows` and can only be
exercised against a live client.

### If Dalamud or Umbra are somewhere unusual

Copy `Directory.Build.local.props.example` to `Directory.Build.local.props` (it
is git-ignored) and set the paths there, or set the `DALAMUD_HOME` / `UMBRA_HOME`
environment variables. The Umbra project fails with an explanatory message rather
than a wall of missing-reference errors if it cannot find `Umbra.dll`.

## 2. Check the Dalamud API level

`global.json` pins `Dalamud.NET.Sdk` to a major version that **is** the Dalamud
API level, and `repo.json`'s `DalamudApiLevel` must match it.

In game: `Dalamud Settings -> Experimental` shows the API level. If it is not the
one pinned, update both files together before building. .NET 10 corresponds to
API 15; an older API level also means an older `sdk.version`.

This is the pin that moves on a Dalamud major bump, and the plugin will not load
until it does.

## 3. Build

```
dotnet build -c Release
```

That builds the whole solution, which includes the Umbra widget and therefore
needs Umbra's assemblies. Without Umbra installed, build the plugin alone:

```
dotnet build src/Scenariometer/Scenariometer.csproj -c Release
```

Nullable warnings are errors here (see `Directory.Build.props`). Fix the
nullability rather than relaxing the setting - a null dereference inside the game
process takes the client down.

Release builds run DalamudPackager, which produces the installable zip under
`src/Scenariometer/bin/Release/`.

## 4. Load it in game

1. `Dalamud Settings -> Experimental -> Dev Plugin Locations`, add the build
   output folder (`src/Scenariometer/bin/Release/`).
2. `/xlplugins` to load or reload.
3. `/xllog` for the log.
4. `/msq` opens the window.

Smoke test, in this order:

- **Progress is plausible.** The completed count and the current quest should
  match where the character actually is. A character mid-Heavensward showing "0
  completed" means the quest-id conversion or the journal section ids are wrong -
  see `/msq debug sections`.
- **The expansion breakdown is sane** - each expansion's total in the low
  hundreds, not zero.
- **The clock runs.** `/msq` shows time on the current quest a minute or so after
  the first turn-in of the session (the first one only sets a baseline, by
  design - it is not a bug).
- **A turn-in records a sample.** Complete an MSQ quest, then check
  `%AppData%\XIVLauncher\pluginConfigs\Scenariometer\history-<contentId>.json`
  exists and has an entry with a believable `ActiveSeconds`.
- **AFK pauses the clock.** Go AFK, wait, come back: the time on the current
  quest should not have advanced. **This is the one thing still unconfirmed** -
  see the open item below.
- **Estimates appear** once there are more samples than `MinimumSamples`
  (default 5).

Delete the history file to start measuring from scratch; `/msq reset` does the
same from in game.

## 5. Then the Umbra widget

1. Build the solution (step 3).
2. In Umbra: `Settings -> Plugins`, enable custom plugins, then add
   `src/Scenariometer.Umbra/bin/Release/Scenariometer.Umbra.dll`.
3. Add the "MSQ Progress" widget to a toolbar.

It should show the same numbers as `/msq`. **Disable the Scenariometer plugin for
a moment** and confirm the widget greys out, the popup says it is not running,
and right-clicking prints an explanation to chat - that is the path users hit
most often, and it is the one least likely to be exercised by accident.

Umbra hot-reloads a changed widget DLL on its own, so a rebuild is picked up
without restarting the game.

## 6. The one unverified assumption

`Tracking/ActiveTimeClock.cs` pauses the clock when your character's
`OnlineStatus` is row 17, "Away from Keyboard". The row id is right; what is not
established is whether the game sets that status on your **own** character, or
only reports it to other players. A sheet cannot answer it.

Confirming it means idling until the AFK flag appears and checking the clock
actually stopped. If it never stops, the optional AFK pause is doing nothing, and
that file is where to look. It is the only `TODO(verify)` left in the source.

## 7. Housekeeping after a change

- Note anything user-visible under `## [Unreleased]` in
  [CHANGELOG.md](../CHANGELOG.md).
- Update [docs/ARCHITECTURE.md](ARCHITECTURE.md) if the design changed.
- There is no CI and no test project. The bar for a change is a clean Release
  build plus loading it in game and exercising what changed - see step 4.
