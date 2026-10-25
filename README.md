# Sirius Studio Plus

Node-based machine vision authoring — the successor to Sirius Studio, replacing
its linear tool rack with a graph.

A tool in the old app had exactly one image in and one image out, so a recipe
could only ever be a straight line. Here a node declares any number of typed
ports, so a result can feed several branches, two points in a graph can be
watched side by side, and a value measured in one place can drive a parameter
somewhere else.

## Running it

```bash
git clone https://github.com/ss-zcai/Sirius-Studio-Plus.git
dotnet run --project src/Sirius.Studio.App
```

Requires the .NET 8 Desktop runtime. Windows x64 only — the OpenCV natives are
architecture-specific.

The built executable is at
`src/Sirius.Studio.App/bin/Debug/net8.0-windows/SiriusStudioPlus.exe`, and must
stay beside its folder: the native OpenCV binaries live in
`runtimes/win-x64/native`.

## Layout

| Project | What it holds |
| --- | --- |
| `src/Sirius.Core` | Graph model, scheduler, serialisation. No OpenCV, no WPF. |
| `src/Sirius.Vision` | The node library, on Emgu.CV. No WPF. |
| `src/Sirius.Studio.App` | WPF shell: canvas, toolbox, inspector, panels. |
| `src/Sirius.NodeEditor` | Scaffolded, currently empty. |
| `tests/Sirius.Core.Tests` | All tests. |
| `tools/NodeDocGen` | Generates `docs/node-reference.md` from the node library. |

`Sirius.Core` has no dependency on OpenCV or WPF, which is what makes the
engine testable without either — and would make a change of CV backend a
rewrite of node bodies rather than an architectural one.

## Working on it

```bash
dotnet test                                              # the whole suite
dotnet run --project tools/NodeDocGen -- docs/node-reference.md
python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Icons.xaml \
  "res/ui icons"
```

**Close the app before rebuilding.** Windows locks its DLLs while it runs, and
the build fails with "being used by another process". The build reports this
as an error, but a copy launched from a *different* output folder fails the
same way while the build otherwise succeeds - so the running application
keeps its old DLLs and the change appears not to have worked.

### Adding an icon

Two steps, in order. The first restyles hand-drawn artwork into the house
style and copies it into the live set; the second regenerates the XAML
dictionary the application actually loads.

```bash
python tools/install_extension.py
python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Icons.xaml   "res/ui icons"
```

Drop the SVG in `res/phosphor-icons/Extension/`, add a line to `INSTALL` in
`tools/install_extension.py` naming the resource key it supplies, and run
both. A key supplied from the hand-drawn set should be removed from the
`PHOSPHOR` map above it rather than left as a dead entry.

Only the part of the Phosphor library the pipeline reads is in this
repository - `Extension/` and `SVGs/regular/`, about a megabyte. The fonts,
the PNG renders and the flat SVGs are another 72 MB that nothing here opens;
download the full set separately if you want them.

## Packaging a build

```powershell
pwsh tools/pack.ps1
```

Writes `dist/SiriusStudioPlus-<version>-win-x64.zip`, about 98 MB. The
version comes from `<Version>` in `Directory.Build.props`, so the package
name and the About box cannot disagree; bump it there and nowhere else.

The build is **self-contained**: a tester unzips it and runs
`SiriusStudioPlus.exe` with nothing installed. Framework-dependent would save
roughly 70 MB and cost every tester a runtime installer, which is the wrong
trade for a build that is handed around.

The script runs the tests, regenerates the node reference, publishes, and
then checks the output against a list of files that must be present -
including the licence notices and that node reference, both of which had
quietly been missing from packages before anything looked for them. It also
checks the executable's stamped version matches the package name. Any of
those failing stops the package being written.

### What ships beside the executable

| File | Why |
|---|---|
| `docs/node-reference.md` | Help ▸ Node Reference looks here first. Without it a packaged build tells the tester to run a `dotnet` command. |
| `THIRD-PARTY-NOTICES.txt` | Components and their terms, including the unresolved Emgu.CV position. |
| `LICENSE-phosphor.txt` | The icon set is derived from Phosphor; MIT requires the licence to travel with every copy. |

**Before this goes outside the company**, read the Emgu.CV entry in the
notices. It is dual licensed GPLv3 or commercial, and the GPLv3 option would
require releasing this product's source under GPLv3.

### Adding a node

Derive from `ImageFilterNode` for the common one-image-in, one-image-out case:

```csharp
[VisionNode("Invert", TypeKey = "basic.invert",
            Category = NodeCategory.Basic, Icon = "tool-icon-21")]
public sealed class InvertNode : ImageFilterNode
{
    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.BitwiseNot(source.Mat, destination);
}
```

`TypeKey` is what saved recipes reference, so it must stay stable even if the
class is renamed. Parameters are properties marked `[Param]`, which drives both
the inspector and serialisation; adding `Connectable = true` also exposes the
parameter as an input port.

Two rules the engine depends on:

- **Never dispose an input.** The same instance is handed to every consumer of
  that port. The scheduler owns cached values and releases them.
- **Let exceptions propagate.** They become `NodeStatus.Error` with the message
  attached, and the input is forwarded so the rest of the graph still runs.

## Documentation

- [`docs/decisions.md`](docs/decisions.md) — why the architecture is the way it
  is, including what went wrong getting there.
- [`docs/roadmap.md`](docs/roadmap.md) — what is done, what is next, known gaps.
- [`docs/node-reference.md`](docs/node-reference.md) — every node, port and
  parameter. Generated; do not edit.

## Licensing note

Emgu CV is dual-licensed GPLv3 / commercial. See `docs/decisions.md` §8 — this
needs resolving before the product ships.
