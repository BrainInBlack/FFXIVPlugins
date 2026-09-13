# Changelog

All notable changes to the plugins in this repo are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each plugin carries its own version in its `.csproj`; entries below are grouped
by plugin where it matters.

Versions marked **private build** were handed to a couple of people as a zip and
were never published. They are kept because the reasoning in them still explains
why the code looks the way it does - but no released artifact ever carried those
version numbers, so nothing outside this repository refers to them. The first
entry without the marker is the first public release.

## [Unreleased]

Nothing yet.

## [1.1.0] - 2026-09-13

### Changed

- **Changing the target date now asks what to do with the plan it belongs to.**
  It used to restart the plan on the spot, every time, discarding the carry with no
  warning - so moving a deadline out, which should keep a deficit and shrink it,
  instead wiped the record of ever having been behind. A small window now offers
  "Keep the plan" against "Restart today", and says how many quests are measured
  against the plan it is about to discard. The date itself applies immediately
  either way; only the plan start waits for the answer, and dismissing the window
  keeps the plan.
- Re-entering the target date that is already set no longer counts as a new plan.
  It restarted one before, which made retyping a date a way to lose its history
  without touching anything that said it would do that.

### Fixed

- **The plan standing ignored today.** "16 quests behind" sat frozen beside a Today
  row reading "10 of 21" and did not move however much of the day's quota was
  cleared, because the ahead/behind figure was measured over completed days only
  and could not change until the next day rolled over at the day-start hour. Quests
  done today now count toward it as soon as they are turned in - the part past
  today's own share, so an untouched morning still does not open a full day's quota
  behind. Today's quota itself is unchanged: what a day owes is still decided when
  the day starts.
- **The plan standing and today's quota could disagree by a quest.** "12 of 21" sat
  beside "8 behind" when clearing the quota is exactly what makes the day level, so
  both had to read 9 - the quota rounded up while the standing rounded to nearest.
  The standing is now taken from the quota itself rather than rounded separately,
  which also closes a rarer version of the same split where the two differed only
  because one was computed a floating-point bit either side of a whole number.
- The widget's ahead/behind label ignored a target date that had already passed.
  The plugin window and the popup both hide the plan standing once it has, and the
  figure behind it is the whole unfinished MSQ rather than a day or two of drift -
  a toolbar reading "465 Behind" states a number nobody can act on. It now says
  "Overdue", the way the days-left label already did.

## [1.0.0] - 2026-09-08

First public release. Everything below this line was a private build; the entries
are kept because the reasoning in them still explains the code.

### Fixed

- **The widget's progress bar read 100% when no target date was set.** Set to show
  "Today" with no target, it sat full forever beside a label correctly saying
  "No target".
- **A character switch showed the previous character's figures for a few seconds.**
  Everything gated on "logged in", which flips the instant a character loads, while
  the numbers behind it only changed on the next poll - so the window and the widget
  both reported the last character's progress, day and plan as the new one's. With
  "open on login" enabled that was the first thing on screen.
- "History cleared" was reported when there was no history to clear, which at the
  character-select screen was every time.
- A sample is no longer lost twice over when something throws while announcing it:
  the pace baseline now advances before the notification rather than after.
- The measured history is no longer read without a lock. The IPC gates run on
  whichever thread the caller used, so a consumer could enumerate the sample list
  while the game tick appended to it.
- **Right-clicking the widget with the plugin missing did nothing at all**, with no
  hint that the widget is only half of the thing. It now says so in chat.
- The widget stopped redrawing its "not running" message once a second while the
  plugin was absent, which was the one state in which nothing was changing.
- Building the MSQ index can no longer fail the whole plugin load. It kept a lookup
  table that threw on a duplicate short quest id, for a method nothing called.

### Changed

- KamiToolKit 2.2.27 -> 2.2.36. The only breaking part is that the library's
  `Initialize` became `InitializeAsync`; every node type the windows use is
  unchanged. Verified against a live client after the 2026-09-08 game patch.
- **The repository is now dual-licensed.** The plugin and the shared IPC contract
  stay MIT; the Umbra widget is AGPL-3.0-or-later, because Umbra and Una.Drawing are
  AGPL with no linking exception and a widget cannot be built without referencing
  them. The two are separate assemblies that only exchange strings over IPC, so the
  plugin carries nothing copyleft. See LICENSING.md.
- The widget now identifies itself properly in Umbra's plugin list, which reads the
  name, author and version out of the assembly rather than from a manifest.
- The progress breakdown is rebuilt only when a quest actually completes, rather
  than every poll.
- Nothing to fix for the 2026-09-08 patch: the Dalamud API level did not move, and
  the four new Main Scenario quests were picked up from the game's own sheets
  without a code change.

## [0.4.0] - 2026-09-07 (private build)

### Added

- **The Umbra widget has a popup.** Left click now opens a panel that mirrors the
  plugin window - the current quest over the overall bar, your pace and what is
  left, the target-date plan, and a bar per expansion - instead of only the one or
  two lines that fit on the toolbar. Each of those four sections can be switched off
  in the widget's settings, and a section with nothing to report hides itself.
- Pausing and resuming tracking from the widget's popup.

### Changed

- **Left click on the widget opens the popup; it no longer toggles the pause.** The
  pause moved into the popup's footer. As a click it was invisible in both
  directions: nothing showed you what it had done, and a misclick quietly stopped
  the measuring. Right click still opens the plugin window.
- The widget's hover tooltip is gone. The popup says all of it, and more.
- **"Paused" now shows on the widget's main label when the sub-label is switched
  off.** It was only ever written to the sub-label, so with sub-text disabled a
  paused tracker looked exactly like a running one.
- The days-left label reads "116 days left" rather than "116", says "1 day left"
  on the last day rather than "1 days left", and says "Overdue" once the date has
  passed. The window and the popup say it the same way; they used to hardcode the
  plural.
- "Ahead" and "Behind" are capitalised, and the setting no longer claims they are
  measured against today - they are measured against the whole plan.
- With no target date set, the target labels read "No target" instead of "--".
  "--" now means only "not enough samples to estimate yet", which is a different
  thing and resolves itself.
- The percent label uses the invariant decimal point, so it no longer disagrees
  with the same number elsewhere on machines with a comma locale.

- **Fixed:** the plugin window said "1 quests" on an overdue target with a single
  quest left. The popup already said "1 quest"; the two now share one helper.
- The widget and its popup only rebuild their text when the figures or the settings
  behind it actually change. Both ran every frame against data that moves once a
  second - the popup was allocating around 150 objects a frame to redraw the same
  report. The sub-label is no longer formatted at all when sub-text is switched off.

### Removed

- The widget's "Quests left today" label mode, which said very nearly what "Today's
  goal" already says. Anyone who had it selected now sees today's goal.
- The estimate's sample threshold and the expansion breakdown now cross the IPC
  gate, so a consumer can draw the whole picture rather than a line of it. This is
  contract v5: **the plugin and the widget have to be updated together.**

## [0.3.0] - 2026-09-06 (private build)

### Changed

- **The target-date plan is cumulative.** Days you overshoot bank against days you
  miss, so getting ahead on Sunday lightens Monday. Previously the figure was
  measured against today alone, which meant every morning opened by declaring you a
  full day's quota behind before you had played anything.
- The window now always shows where you stand against the plan, rather than only
  once the day's share was finished.
- **Both windows redesigned.** The main window leads with the current quest and its
  chapter over the overall bar; every expansion is an icon, a name, a count and a
  full-width bar; and the figures in between are laid out as label and value in two
  columns rather than as sentences. The settings window is narrower, and its title
  no longer repeats the plugin name beside itself.
- Dates are shown and typed in a format you choose - 31.12.2026, 2026-12-31 or
  12/31/2026. The config file keeps ISO whatever is picked, so the setting can be
  changed without invalidating a date already set.
- **The plan is per character.** The target date is shared, but the day each
  character's plan starts is its own: the carry-over is measured against that
  character's history, so one shared start date judged every alt against days it
  never played. Config schema 2 migrates by dropping the old shared value; each
  character re-establishes its plan the next time it is loaded.

### Added

- Completing an MSQ quest while tracking is paused resumes it and says so in chat.
  Forgetting to un-pause silently collected nothing; finishing MSQ is the one thing
  the pause is meant to exclude, so doing it is a clear sign the pause was
  forgotten. That quest counts as five minutes - the clock was stopped for an
  unknown part of it, so its real duration cannot be recovered and a plausible
  stand-in beats the near-zero a stopped clock reports.
- **A calendar for the target date.** Its own small window: pick a month, click a
  day. Typing a date in game needs keyboard focus and a format matched by hand, and
  the fixed offsets it replaces could only ever reach the few dates they named.
- **Tick marks on every progress bar.** The overall bar divides at expansion
  boundaries, an expansion at the journal's chapters within it, and today's goal one
  tick per quest owed - so a day reads as a count rather than a proportion.

## [0.2.0] - 2026-09-01 (private build)

First build that runs. Verified in game against Dalamud API 15 (assembly 15.0.3.2)
and Umbra 3.1.18.0: the index finds 1046 MSQ quests across 6 expansions, turn-ins
are measured and recorded, and the in-flight measurement survives a reload. Still
unverified: the AFK clock pause.

### Changed

- **Both windows are native game windows** (KamiToolKit AtkUnitBase) rather than
  ImGui - game frame, fonts, scaling and controls throughout.
- Quests you can never complete - the two starting cities you did not pick - are
  left out of the totals, so A Realm Reborn can reach 100%.
- The first turn-in after a login is recorded but not timed. It sets the starting
  point; timing it would measure from the login rather than from the previous
  turn-in and quietly drag the pace down.

### Added

- **Scenariometer.** Main Scenario progress per character, read from the game's
  completed-quest state, with a time-to-finish estimate built from the player's
  own measured pace: median play time over a recent window of MSQ quests, a band
  that widens with the square root of the quests remaining, and a clock that only
  runs while logged in and (optionally) not AFK, plus a pause button for when you
  step away from the story on purpose. The in-flight measurement survives a plugin
  reload, so updating mid-quest does not cost you the sample. Optionally a target
  completion date, which turns into a per-day quest quota and a count of what is
  left today; the hour a new day starts is configurable, so a session running past
  midnight stays one day. Progress and estimates for the whole MSQ and
  for the current expansion, a settings window, and `/msq`, `/msq config`,
  `/msq reset`, `/msq debug sections`, `/msq debug expansions`, `/msq debug missing`.
- **Scenariometer.Umbra.** An Umbra toolbar widget showing the same numbers:
  configurable label and sub-label, a progress bar for the whole MSQ or just the
  current expansion or today's goal, and the full breakdown in the tooltip. Left
  click pauses or resumes tracking, right click opens the plugin window. Talks to the plugin over Dalamud IPC and computes nothing
  itself.
- A generated plugin icon (`scripts/make-icon.py`, stdlib only) and a
  hand-maintained `repo.json` for installing through a custom Dalamud repository.

[Unreleased]: https://github.com/BrainInBlack/FFXIVPlugins/compare/1.1.0...develop
[1.1.0]: https://github.com/BrainInBlack/FFXIVPlugins/compare/1.0.0...1.1.0
[1.0.0]: https://github.com/BrainInBlack/FFXIVPlugins/releases/tag/1.0.0
