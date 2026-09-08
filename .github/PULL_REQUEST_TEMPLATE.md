<!--
Thanks for contributing! Please keep PRs focused - one change per PR.
See .github/CONTRIBUTING.md for the ground rules. PRs target `develop`.
-->

## What & why

<!-- What does this change do, and why? Link any related issue, e.g. "Closes #12". -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Documentation only
- [ ] Refactor / cleanup (no behavior change)
- [ ] Dalamud API / game patch compatibility

## Checklist

- [ ] One focused change, with a short description of the why (above).
- [ ] Targets the `develop` branch (or `main` for an urgent fix).
- [ ] `dotnet build -c Release` succeeds with no warnings.
- [ ] Loaded in-game as a dev plugin and exercised the changed behavior.
- [ ] If I touched `src/Scenariometer.Umbra`, I checked the widget on an actual Umbra toolbar.
- [ ] If I changed the IPC contract, I bumped `ContractVersion` and updated both sides.
- [ ] If I confirmed a `TODO(verify)` against a live client, I removed the marker.
- [ ] If I changed the icon, I edited `scripts/make-icon.py` and re-ran it.
- [ ] Added a note under `## [Unreleased]` in `CHANGELOG.md`.
- [ ] Kept the docs in sync: `README.md`, `docs/ARCHITECTURE.md`.
- [ ] No telemetry, no network calls, no gameplay automation.

## Testing

<!--
How did you verify this? Which character / expansion / point in the MSQ, and what
you saw. Screenshots of the window or the toolbar widget are welcome.
-->
