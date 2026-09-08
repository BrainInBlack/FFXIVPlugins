# Contributing

This repo holds Dalamud plugins for Final Fantasy XIV. One repo, one solution,
one plugin per folder under `src/`, sharing a Dalamud API pin and a plugin
repository manifest.

For what the plugins do, see [README.md](../README.md). For how the code is put
together, read [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) before changing
anything that measures, estimates, or crosses a plugin boundary.

## Prerequisites

- The .NET SDK named in [global.json](../global.json) (currently .NET 10).
- **Dalamud**, installed through [XIVLauncher](https://goatcorp.github.io/) -
  the build references its assemblies directly. They are found automatically at
  `%AppData%\XIVLauncher\addon\Hooks\dev\`; set `DALAMUD_HOME` if yours lives
  elsewhere.
- **Umbra**, only if you touch `src/Scenariometer.Umbra`. Either install it
  through Dalamud or point `UMBRA_HOME` at a directory holding `Umbra.dll`,
  `Umbra.Common.dll` and `Una.Drawing.dll` (published in
  [umbra-dist](https://github.com/una-xiv/umbra-dist)).

Windows only in practice. The plugin targets `net10.0-windows` and can only be
run and tested against a live game client.

## Building

If you have not built this on your machine before, read
[docs/FIRST-BUILD.md](../docs/FIRST-BUILD.md) first - it covers the
prerequisites, where the build expects Dalamud and Umbra to be, and how to
confirm a build actually works in game.

```sh
dotnet build -c Release
```

That builds the whole solution, which includes the Umbra widget and therefore
needs Umbra's assemblies. Without Umbra, build just the plugin:

```sh
dotnet build src/Scenariometer/Scenariometer.csproj -c Release
```

A Release build runs DalamudPackager and leaves the installable zip in
`src/<Plugin>/bin/Release/`. The Umbra widget builds to a plain
`Scenariometer.Umbra.dll`; Umbra loads it from a path you add under its
`Settings -> Plugins`.

The plugin icon is generated rather than drawn - edit
[scripts/make-icon.py](../scripts/make-icon.py) and re-run it
(`python scripts/make-icon.py`, or `python3` on macOS/Linux), never the PNG.

## Testing

There is no test project and no CI. **The quality bar for a change is a clean
Release build plus verifying the behavior in-game**, and a change that cannot be
checked in-game should say so in the PR.

To run a build in the game:

1. Add the build output folder under Dalamud's
   `Settings -> Experimental -> Dev Plugin Locations`.
2. `/xlplugins` to load or reload it.
3. `/xllog` for the log - everything the plugin writes goes there.

Useful when working on quest data: `/msq debug sections` dumps the journal
sections to the log, which is how the Main Scenario section id is confirmed
against a live client rather than assumed.

Nullable warnings are build errors (see [Directory.Build.props](../Directory.Build.props)) -
a plugin runs inside someone else's game process, and a null dereference there
takes the client down, not just the plugin.

## Ground rules for this codebase

- **Keep FFXIVClientStructs contained.** It breaks between game patches. All
  of it lives in `Msq/QuestState.cs`; that is why a patch-day fix is one file.
- **Measure, don't guess.** The estimate is built from the player's own samples.
  Do not add hardcoded "average time per quest" tables from a guide site.
- **Nothing leaves the machine.** No telemetry, no accounts, no network calls.
  Measured history stays in the plugin's config directory.
- **No automation of gameplay.** This plugin reads state and draws windows. It
  does not move a character, accept or turn in quests, or interact with the
  world, and PRs that add any of that will not be merged.
- Comments explain *why*, especially where a simpler-looking alternative was
  rejected. Match the density of what is already there.
- `TODO(verify)` marks an API detail that has not been confirmed against a live
  client. If you confirm one, fix the code and delete the marker in the same PR.
  Exactly one is left - the AFK online status - and `docs/FIRST-BUILD.md` says
  what confirming it involves.

## Branches & pull requests

- `main` - stable releases.
- `develop` - active development.

Branch off `develop`, then open a pull request into `develop` (or `main` for an
urgent fix). Before opening it:

- the Release build succeeds,
- you have loaded the build in-game and exercised what you changed,
- there is a note under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md).

The [pull request template](PULL_REQUEST_TEMPLATE.md) has the full checklist.

## License

By contributing you agree your contributions are licensed under the
the license that covers the part of the tree you touched: [MIT](../LICENSE) for
the plugin and the shared contract, [AGPL-3.0-or-later](../src/Scenariometer.Umbra/LICENSE)
for the Umbra widget. See [LICENSING.md](../LICENSING.md).
