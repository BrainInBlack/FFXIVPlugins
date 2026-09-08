# Licensing

This repository ships two binaries under two different licenses. They are separate
assemblies that never link to each other, so the split is real rather than
bookkeeping.

| Path | License | Why |
|---|---|---|
| `src/Scenariometer` | MIT ([LICENSE](LICENSE)) | The Dalamud plugin. Depends only on Dalamud and KamiToolKit, both permissive. |
| `src/Shared` | MIT ([LICENSE](LICENSE)) | The IPC contract. Compiled into both projects. |
| `src/Scenariometer.Umbra` | AGPL-3.0-or-later ([LICENSE](src/Scenariometer.Umbra/LICENSE)) | The Umbra toolbar widget. Umbra is AGPL. |

Everything else in the repo - docs, scripts, the icon generator - is MIT.

## Why the widget is AGPL

[Umbra](https://github.com/una-xiv/umbra) and its rendering library
[Una.Drawing](https://github.com/una-xiv/drawing) are both licensed
**AGPL-3.0**, in its stock form with no linking exception. An Umbra widget cannot
compile without referencing `Umbra.dll`, `Umbra.Common.dll` and `Una.Drawing.dll`,
and the AGPL grants no permission to combine a covered work with a differently
licensed one except GPLv3.

una-xiv's own `Umbra.SamplePlugin` - the template every widget author is told to
copy - declares `<PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>`,
and the third-party widgets in the wild that state a license state the same. So this
is the ecosystem's settled reading, not an interpretation invented here.

## Why the plugin stays MIT

`src/Scenariometer` does not reference Umbra, directly or transitively. It is a
Dalamud plugin that publishes JSON over Dalamud IPC call gates; anything at all may
read those, and the widget is merely one such reader. The two run in different
assembly load contexts and share no types - only strings cross the gate.

That separation predates the licensing question and was built for a different reason
(see `docs/ARCHITECTURE.md`), but it is what makes the split clean: nothing AGPL is
present in the plugin's build, its output, or its distributed zip.

## The shared contract file

`src/Shared/ScenariometerContract.cs` is `<Compile Include>`-linked into **both**
projects, so its source ends up inside the AGPL widget assembly as well as the MIT
plugin. That direction is fine - MIT permits incorporation into an AGPL work.

What it does mean is that the *widget binary* is AGPL as a whole, contract file
included. The file's own source stays MIT and can be reused anywhere.

## What redistribution requires

- **Umbra's own assemblies are never redistributed.** Every reference to them is
  `<Private>false</Private>`, because Umbra forward-references them into its plugin
  load context at runtime. Shipping copies would both break loading and redistribute
  AGPL binaries. Do not change that flag.
- **The widget's source must be available to anyone who receives the binary.** This
  repository being public satisfies that. If you distribute a built
  `Scenariometer.Umbra.dll` anywhere else, point recipients at the repository.
