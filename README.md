# FFXIV plugins

![License: MIT and AGPL-3.0](https://img.shields.io/badge/license-MIT%20%2B%20AGPL--3.0-blue) ![.NET](https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet&logoColor=white) ![Dalamud](https://img.shields.io/badge/Dalamud-API%2015-6f42c1)

Dalamud plugins for Final Fantasy XIV. One repo, one solution, one plugin per
folder under `src/` - they share the Dalamud API pin, the build, and the plugin
repository manifest, so a Dalamud API bump is a one-line change for all of them.

| Component                                         | What it does                                                  |
|---------------------------------------------------|---------------------------------------------------------------|
| [Scenariometer](src/Scenariometer)                | MSQ progress and a time-to-finish estimate from your own pace |
| [Scenariometer.Umbra](src/Scenariometer.Umbra)    | The same numbers as an Umbra toolbar widget                   |

**These are distributed from this repository, not through the official Dalamud
plugin repository.** See [Installing](#installing), and read
[Written with AI](#written-with-ai) before you decide to install anything that
runs inside your game process.

## Scenariometer

Answers "how much Main Scenario is left, and how long will that take *me*".

- Progress per expansion and overall, read straight from the game's completed
  quest state - no manual check-off, and it is correct the moment you install it.
- The estimate uses your own measured pace, not a guide's average: the median
  play time over your last 30 MSQ quests, with a band around it.
- Only play time counts. The clock stops at the login screen and (optionally)
  while you are flagged AFK, and a quest that took absurdly long - you went and
  did a raid night in the middle - is logged but left out of the pace.
- Optionally takes a target date and turns it into "do N quests today", with a
  cumulative plan: overshoot today and tomorrow asks for less.
- Per character, stored locally in the plugin's config directory. Nothing leaves
  your machine.

Commands: `/msq` (window), `/msq config`, `/msq reset`, `/msq debug sections`.

## Umbra widget

[Umbra](https://github.com/una-xiv/umbra) users can put the same numbers on the
toolbar: quests done, percent, time left, today's goal or how you stand against
the plan on the label, and a progress bar for the MSQ, the current expansion or
today.

**Left click opens a popup that mirrors the plugin window** - the current quest
over the overall bar, your pace and what is left, the target-date plan, and a bar
per expansion - and each of those four sections can be switched off. Pausing and
resuming lives in its footer. **Right click** opens the plugin window itself.

It is a **separate DLL, loaded by Umbra rather than by Dalamud** - that is how
Umbra plugins work. The plugin does the measuring; the widget only displays what
the plugin reports over Dalamud IPC, so **both halves have to be installed**. With
the plugin missing the widget greys out and says so rather than showing numbers.

The two are versioned together. A widget and a plugin from different releases will
refuse to talk and tell you to update both, rather than quietly reporting
something wrong.

## Installing

### The plugin

Add this as a custom repository in Dalamud
(`/xlsettings -> Experimental -> Custom Plugin Repositories`):

```
https://raw.githubusercontent.com/BrainInBlack/FFXIVPlugins/main/repo.json
```

Then install **Scenariometer** from `/xlplugins`. It updates through Dalamud from
then on, like any other plugin.

### The Umbra widget

Only if you use Umbra. In Umbra: `Settings -> Plugins`, and tick the box that
enables custom plugins - Umbra asks for that once, because a widget DLL has the
same access to your machine that any Dalamud plugin does.

Then, **download `Scenariometer.Umbra.dll` from
[Releases](https://github.com/BrainInBlack/FFXIVPlugins/releases) and add it with
"Install from file"**, pointing Umbra at wherever you saved it.

Umbra can also install from a GitHub repository - owner `BrainInBlack`, repository
`FFXIVPlugins`. That works, and it will check for updates on its own, but it pulls
*every* asset from the latest release, including the plugin archive it has no use
for. The file method is tidier.

Finally, add the **MSQ Progress** widget to a toolbar.

## Written with AI

**Most of the code in this repository was written by Claude (Anthropic), working
from my direction.** That is worth stating plainly, because a Dalamud plugin runs
inside your game process with full access to your machine, and you should know
what you are installing.

What that means concretely:

- Every design decision, and every rejection of one, was mine. The architecture,
  the measurement approach, and the way the UI reads were specified and revised by
  a human across many iterations.
- Every change was tested in a live client before it landed. Nothing here was
  written blind and shipped.
- The code has been through several review passes - correctness, API usage checked
  against Dalamud's own documentation and source, and a pass over licensing.
- The comments are unusually dense on purpose. Where something looks like it could
  be simpler, the comment usually says which simpler thing was tried and why it
  did not work.

**This is not in the official Dalamud plugin repository, and it is not going to
be.** That repository has an AI policy which requires disclosure and reserves
rejection for heavily AI-written submissions; rather than argue the edges of it,
these plugins are distributed from here. If you would rather only run plugins that
have been through the official review process, that is an entirely reasonable
position and you should not install this.

The source is public precisely so the claim above can be checked rather than
taken on trust.

## Building

Requires the .NET SDK from `global.json` and a local Dalamud install, which the
build resolves through `DALAMUD_HOME` (XIVLauncher's `addon/Hooks/dev` on
Windows). Windows only in practice - the plugin targets `net10.0-windows` and can
only be exercised against a live game client.

```bash
dotnet build -c Release
```

Release builds run DalamudPackager, which produces the installable zip under
`src/Scenariometer/bin/Release/`.

**Building the solution also builds the Umbra widget**, which needs Umbra's
assemblies - from an installed copy, or from
[umbra-dist](https://github.com/una-xiv/umbra-dist). Set `UMBRA_HOME` (or
`-p:UmbraLibPath=`) if Umbra is not installed in the default XIVLauncher location.
On a machine without Umbra, build the plugin alone:

```bash
dotnet build src/Scenariometer/Scenariometer.csproj -c Release
```

There is **no CI**, deliberately. A build that cannot load the plugin into a
running game has not shown much, and that is the only test that matters here.
Everything is built and exercised by hand against a live client.

The plugin icon is generated - `python scripts/make-icon.py` re-renders
`src/Scenariometer/images/icon.png` from the script (stdlib only, nothing to
install). Change the script, not the PNG.

To run a build in-game, add the built folder under Dalamud's
`Settings -> Experimental -> Dev Plugin Locations`.
[docs/FIRST-BUILD.md](docs/FIRST-BUILD.md) is the bring-up runbook for a machine
that has never built this before.

## Releasing

`repo.json` is hand-maintained. When cutting a release, bump `AssemblyVersion`
there to match `<Version>` in the plugin's csproj, and attach both artifacts to the
GitHub release:

- `latest.zip` - the DalamudPackager output, from
  `src/Scenariometer/bin/Release/Scenariometer/`. Upload it under that exact name;
  the three `DownloadLink*` fields in `repo.json` point at it.
- `Scenariometer.Umbra.dll` - the widget, as its own asset rather than inside the
  archive, so Umbra can be pointed straight at it.

## License

Two licenses, because there are two binaries:

- **The plugin** (`src/Scenariometer`) and the shared IPC contract (`src/Shared`)
  are **[MIT](LICENSE)**.
- **The Umbra widget** (`src/Scenariometer.Umbra`) is
  **[AGPL-3.0-or-later](src/Scenariometer.Umbra/LICENSE)**, because Umbra and
  Una.Drawing are AGPL and a widget cannot be built without referencing them.

The two are separate assemblies that never link to each other - they talk over
Dalamud IPC, and only strings cross - so the plugin carries nothing copyleft.
[LICENSING.md](LICENSING.md) explains the split, what it means for redistribution,
and why Umbra's assemblies are referenced but never shipped.

Commons Clause is deliberately not used anywhere here: it is not OSI-approved.

## Contributing

Bug reports and pull requests are welcome. Start with
[CONTRIBUTING.md](.github/CONTRIBUTING.md) for the build and the ground rules, and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit together and
why. Security issues go through the [security policy](.github/SECURITY.md),
privately - not a public issue.

Changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Status

Working and in daily use, but young, and it has not yet been through a game patch
that broke anything. The parts most likely to need attention on a patch day are
the quest-state reading and the native windows, both deliberately confined to as
few files as possible - `docs/ARCHITECTURE.md` says which.

One thing remains unverified against a live client: whether the game reports your
*own* character as "Away from Keyboard", which is what the optional AFK pause
depends on. It is marked `TODO(verify)` in the source.
