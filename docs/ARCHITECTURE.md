# Architecture

How the code in this repo is put together, and why it is put together that way.
Read this before changing anything that measures, estimates, or crosses a plugin
boundary. For building and testing, see
[CONTRIBUTING.md](../.github/CONTRIBUTING.md).

## Repository shape

One git repo, one solution, one plugin per folder under `src/`:

| Path                        | What it is                                                    |
|-----------------------------|---------------------------------------------------------------|
| `src/Scenariometer`         | The Dalamud plugin. Does all the measuring.                   |
| `src/Scenariometer.Umbra`   | An Umbra toolbar widget. Displays what the plugin reports.    |
| `src/Shared`                | The IPC contract, compiled into both (see below).             |
| `scripts/make-icon.py`      | Renders the plugin icon. The PNG is generated, never hand-edited. |

A collection rather than one repo per plugin, because they share a Dalamud API
level, a build, and one `repo.json`; splitting those across repos would mean
bumping the API pin in several places on every patch day.

## Version pins

`global.json` holds both, and they are the only places to change them:

- `msbuild-sdks.Dalamud.NET.Sdk` - **the Dalamud API level for the whole repo**
  (the major version *is* the API level). Bumping it retargets every plugin.
- `sdk.version` - the .NET SDK. API 15 is .NET 10.

`repo.json`'s `DalamudApiLevel` has to be bumped with it, or Dalamud hides the
plugin from users on the new API.

## Scenariometer

Data flows one way. Each stage is replaceable without touching the others.

```
Lumina sheets --> MsqIndex        (the MSQ, in story order, built once at load)
game memory  --> QuestState      (the only quest-reading ClientStructs code)
                     |
ActiveTimeClock --> ProgressTracker --> QuestSample --> QuestHistory (per-character JSON)
                     |                                      |
                  MsqProgress                            Estimator --> Estimate
                  \_________ Windows/ (native, KamiToolKit) _________/
                                    |
                            Ipc/IpcProvider (JSON over Dalamud call gates)
                                    |
                     src/Scenariometer.Umbra (separate DLL, loaded by Umbra)
```

### Identifying the Main Scenario

`Msq/MsqIndex` walks `Quest -> JournalGenre -> JournalCategory -> JournalSection`
and keeps the Main Scenario section - the top-level tab in the in-game journal,
which every expansion's MSQ hangs off and nothing else does. Matching on quest
names or hardcoded id ranges breaks on patch day; this does not.

Ordering is `(JournalGenre.RowId, Quest.SortKey)`, which reproduces the journal's
own order. It deliberately does **not** chain-walk `PreviousQuest`: that graph
branches (class-specific starts, the pre-Heavensward city splits), and all the
estimate needs is a stable count of "how many before and after here", not a
strict DAG.

### Measuring

**A sample is the interval between two consecutive MSQ turn-ins**, not from
accepting a quest to completing it. Travel, cutscenes and duty-finder queues sit
between the turn-in and the next accept, and they are most of the real elapsed
time; measuring accept-to-complete systematically under-reports.

Time comes from `ActiveTimeClock`, a monotonic counter that only advances while
logged in - and, by default, not while flagged AFK. Wall-clock time would count
the night the client sat at the title screen.

Two cases produce no usable sample, and both record rather than guess:

- **Right after login there is no baseline.** The first turn-in of a session only
  establishes one; it is not measured.
- **Several quests completing in one poll** cannot have the interval attributed
  to any one of them, so they are all flagged as outliers.

Anything over the configured outlier threshold (default 90 minutes - a raid night
in the middle of the MSQ) is kept in the log but left out of the pace.

Completion is polled every five seconds on the framework tick rather than hooked:
quest completion has no stable public event, a pass over the MSQ list is a few
thousand bitfield reads, and the thing being measured takes minutes. The poll
interval is far below the noise floor of the estimate.

### Estimating

`Estimation/` is pure functions over samples, with no game or UI dependencies.

- **Median, not mean.** MSQ quest times are heavily right-skewed - most quests are
  short, a few are a dungeon plus a queue. One 45-minute outlier would drag a mean
  far away from what the next twenty quests will feel like.
- **A recent window, not a lifetime average.** Pace changes as the player levels,
  unlocks flight and teleports, or switches from skipping cutscenes to watching
  them. The default window is the last 30 usable samples.
- **The band widens with `sqrt(n)`, not `n`.** Per-quest times are spread wide,
  but over a long run those differences cancel out; the uncertainty of a sum of n
  draws grows with the square root of n. Multiplying the per-quest quartiles
  straight through would produce a "somewhere between 40 and 400 hours" band that
  tells nobody anything.

### Storage

Measured samples are **not** in the Dalamud plugin config. They live in
per-character files (`history-<contentId>.json`) in the plugin's
`ConfigDirectory`, written write-then-move so a crash mid-write cannot eat the
history. Per character because MSQ progress is per character: a level 90 alt and
a first-time sprout say nothing about each other's pace. Out of the config
because the config is rewritten whenever a checkbox changes, and the history
grows to thousands of rows.

An unreadable history file is set aside and logged, not fatal.

## The Umbra widget

An Umbra plugin is **not** a Dalamud plugin: it is a DLL that Umbra loads itself,
from a path the user adds under Umbra's `Settings -> Plugins`. It runs in Umbra's
assembly load context, so it cannot call into the Dalamud plugin directly. The
two halves talk over Dalamud IPC.

That boundary is also a **licensing** boundary. Umbra and Una.Drawing are AGPL-3.0,
so the widget is AGPL-3.0-or-later while the plugin stays MIT. Nothing in
`src/Scenariometer` may reference Umbra, and Umbra's assemblies are never
redistributed - every reference to them is `<Private>false</Private>`. See
`LICENSING.md`.

- `src/Shared/ScenariometerContract.cs` is `<Compile Include>`-**linked** into
  both projects rather than built into a shared assembly. Across load contexts a
  type from one side is not the same type to the other, so only primitives and
  strings may cross the gate: the snapshot travels as JSON and each side parses
  it into its own copy of the class.
- `ContractVersion` exists because the two DLLs are installed and updated
  independently. A mismatch is a normal state, and the widget says so rather than
  displaying numbers from a payload it cannot read.
- The gates are **pull, not push**. A toolbar widget redraws every frame and only
  wants the latest state; pushing would mean holding delegates from another load
  context alive across a reload. The widget polls once a second.
- The widget computes nothing. The MSQ index, the clock and the estimator all
  live on the plugin side, which is the only side that can measure anything.
- The widget is the toolbar button; the **popup** it opens on left click mirrors
  the plugin window, section for section. It is built from Una.Drawing nodes -
  Umbra's own UI layer - rather than from KamiToolKit, which draws the plugin's
  windows. The two look alike on purpose and share nothing but the contract.

Things to know before editing it:

- **Never change the widget's `[ToolbarWidget]` id.** Umbra keys saved user
  settings on it.
- The root namespace is `ScenariometerUmbra`, not `Scenariometer.Umbra`. A
  namespace segment called `Umbra` inside our own root shadows Umbra's real
  namespaces at every reference site inside the namespace body.
- **Two Una.Drawing rules, both learned the hard way, both in the popup's bars.**
  `Anchor` decides where a node is *drawn* but not whether it takes up room: an
  anchored node is painted at its anchor while its siblings are still pushed
  along as though it sat in the flow. And siblings paint back to front, so the
  *first* child ends up on top. Anything that stacks one node over another needs
  both of those right at once; the bars gave up and lay their fill and their tick
  marks end to end in a single row instead.

## Gotchas

- **Quest ids come in two forms.** The Excel `Quest.RowId` is `0x1xxxx`; the
  game's `QuestManager` uses the low 16 bits. Mixing them up silently reports
  "nothing completed" instead of throwing. `QuestState.ToShortId` is the only
  conversion in the codebase.
- **FFXIVClientStructs is confined to `Msq/QuestState.cs`** - two calls and that
  id conversion. It breaks between game patches, so keeping it in one file makes
  a patch-day fix a one-file change. Keep it that way.
- **`TODO(verify)` markers** flag API details written without a live client to
  check against: the JournalSection id, Lumina's `RowRef` / `ReadOnlySeString`
  accessors, the `QuestManager` signatures, the AFK `OnlineStatus` row, the ImGui
  binding namespace, and the `ClientState.Login` delegate shape. `/msq debug
  sections` exists to check the section id against a live client. Confirm one,
  fix the code, delete the marker.
- **Both windows are native; there is no ImGui left.** `NativeMainWindow` and
  `NativeConfigWindow` are real AtkUnitBases built with KamiToolKit, so they look and
  behave like game windows and follow the game's UI scale. The settings window uses
  the game's own checkbox, slider, numeric and text-input nodes.
- **Native UI is the one part that is patch-fragile by design.** It touches
  FFXIVClientStructs directly (the addon callbacks take `AtkUnitBase*`), so
  `NativeMainWindow` and `QuestState` are the two files a ClientStructs break can
  reach. KamiToolKit absorbs most of it, but it is a per-patch dependency in a way
  the rest of the plugin is not.
