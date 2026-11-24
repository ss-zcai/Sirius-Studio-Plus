# Sirius Studio Plus

Machine vision on one PC: recipes built as node graphs in **Studio**, put
on a line - stations, PLCs, the operator's screen - in **Builder**, run on
the machine by **Edge**, and the pictures kept looked at in **Sirius IV**.

**What the programs are and how they fit together: [`docs/overview.md`](docs/overview.md).**
It ships as the README of every package. This file is for working on the
code.

## Running it

```bash
git clone https://github.com/ss-zcai/Sirius-Studio-Plus.git
dotnet run --project src/Apps/Sirius.Studio     # Studio Plus
dotnet run --project src/Apps/Sirius.Builder    # Builder
dotnet run --project src/Apps/Sirius.Edge       # Edge
dotnet run --project src/Apps/Sirius.IV         # Image Viewer
```

Needs the .NET 8 SDK. Windows x64 only - the OpenCV natives are
architecture-specific. Each program's build output must stay whole: the
native OpenCV binaries are in `runtimes/win-x64/native` beside it.

Camera drivers build against the makers' SDKs when installed - pylon 26.10
or later (`PYLON_DEV_DIR`) and MVS (`MVCAM_COMMON_RUNENV`). Without them
the drivers build empty and the programs list no such cameras.

## Layout

| Project | What it holds |
| --- | --- |
| `src/Core/Sirius.Core` | The graph model and scheduler, serialisation, projects and stations, screens, saved-picture annotations. No OpenCV, no WPF. |
| `src/Core/Sirius.Vision` | The node library on Emgu.CV, the camera layer, and the hosts for custom nodes in C#, Python and C++. No WPF. |
| `src/Core/Sirius.Plugins` | The custom node interface - the SDK's package (versioned on its own). |
| `src/Core/Sirius.Plc` | Controllers: the PLC driver interface and loader, the simulated PLC, the workflow runner and its nodes (versioned on its own: the driver interface). |
| `src/Drivers/Sirius.Cameras.Basler`, `src/Drivers/Sirius.Cameras.Hikrobot` | Camera drivers on pylon and MVS. |
| `src/Shared/Sirius.UI` | What the programs share: the theme, icons, dark controls, image viewer, dialogs, the node editor's view-models, screens, the runtime and PLC windows, and the host that starts a program. |
| `src/Apps/Sirius.Studio` | **Studio Plus** (`SiriusStudioPlus.exe`): its window and start-up; the rest is `Sirius.UI`'s. |
| `src/Apps/Sirius.Builder` | **Builder** (`SiriusBuilder.exe`): projects, the screen designer, controllers and workflows. Versioned on its own. |
| `src/Apps/Sirius.Edge` | **Edge** (`SiriusEdge.exe`): runs a project. |
| `src/Apps/Sirius.IV` | **Sirius IV** (`SiriusIV.exe`): saved pictures. Versioned on its own. |
| `src/Apps/Sirius.Runner` | **sirius-run** (`sirius-run.exe`): recipes over folders, from a script. |
| `tests/Sirius.Core.Tests` | The tests - all of them, the programs' windows included, built but not shown. |
| `tests/TestPlcDriver` | The smallest PLC driver library, loaded by the tests from a folder. |
| `samples/SampleNodes`, `PythonNodes`, `CppNodes` | Custom node samples, shipped in the package and the SDK. |
| `samples/PlcDrivers` | The PLC kit's sample driver and its conformance tests - built only in the kit (`pack-plc-kit.ps1`). |
| `tools/NodeDocGen` | Generates `docs/reference/node-reference.md` from the node library. |

`Sirius.Core` has no dependency on OpenCV or WPF, which is what makes the
engine testable without either - and would make a change of CV backend a
rewrite of node bodies rather than an architectural one.

## Working on it

```bash
dotnet test                                              # the whole suite, about two minutes
dotnet run --project tools/NodeDocGen -- docs/reference/node-reference.md
python tools/svg2xaml.py src/Shared/Sirius.UI/Themes/Icons.xaml "res/ui icons"
```

**A running program locks its build folder.** Builder, Edge and Sirius IV
are built on Studio's assembly, so any of them open locks the Debug folders
the tests build into: the build fails with "being used by another
process". Close them, or build elsewhere with
`-p:BaseOutputPath=<folder>/`. A copy launched from a *different* output
folder fails the same way while the build otherwise succeeds - so the
running program keeps its old DLLs and a change appears not to have
worked.

Two tests are known to fail now and then on a busy machine and pass when
run again (backlog N8); believe a failure only once it repeats.

### Adding an icon

Two steps, in order. The first restyles hand-drawn artwork into the house
style and copies it into the live set; the second regenerates the XAML
dictionary the programs load.

```bash
python tools/install_extension.py
python tools/svg2xaml.py src/Shared/Sirius.UI/Themes/Icons.xaml "res/ui icons"
```

Drop the SVG in `res/phosphor-icons/Extension/`, add a line to `INSTALL` in
`tools/install_extension.py` naming the resource key it supplies, and run
both. A key supplied from the hand-drawn set should be removed from the
`PHOSPHOR` map above it rather than left as a dead entry. Every key in
`Icons.xaml` has its SVG in `res/ui icons`, so regenerating loses nothing.

Only the part of the Phosphor library the pipeline reads is in this
repository - `Extension/` and `SVGs/regular/`, about a megabyte.

### Adding a node

Derive from `ImageFilterNode` for the common one-image-in, one-image-out case:

```csharp
[VisionNode("Invert", TypeKey = "basic.invert",
            Category = NodeCategory.Basic, Icon = "ph_invert")]
public sealed class InvertNode : ImageFilterNode
{
    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.BitwiseNot(source.Mat, destination);
}
```

`TypeKey` is what saved recipes reference, so it must stay stable even if the
class is renamed. Parameters are properties marked `[Param]`, which drives both
the inspector and serialisation; `Connectable = true` also exposes the
parameter as an input port, and `Editor = ParamEditor.FilePath` makes it a
file the scheduler watches (decision 78).

Two rules the engine depends on:

- **Never dispose an input.** The same instance is handed to every consumer of
  that port. The scheduler owns cached values and releases them.
- **Let exceptions propagate.** They become `NodeStatus.Error` with the message
  attached, and the input is forwarded so the rest of the graph still runs.

## Packaging

| Script | Writes | For |
|---|---|---|
| `tools/pack.ps1` | `dist/SiriusStudioPlus-<version>-win-x64.zip` | testers: all five programs, the samples, the guides |
| `tools/pack-sdk.ps1` | `dist/Sirius.Plugins-SDK-<version>.zip` | teams writing custom nodes |
| `tools/pack-plc-kit.ps1` | `dist/Sirius-PLC-Kit-<interface>-Builder-<builder>-win-x64.zip` | the PLC team: Builder, the driver SDK, a sample driver |

Every package is self-contained - unzip and run, nothing installed - and
each script checks its output before zipping: the tests pass, the files
that must ship are there (licence notices, the node reference), the
programs' stamped versions match, and the samples build and pass their
tests from the package alone, outside the repository.

`pack.ps1` needs a shell with `PYLON_DEV_DIR` and `MVCAM_COMMON_RUNENV`
set, and Visual Studio's C++ compiler for the C++ sample. `pack-sdk.ps1`
tests the Python sample when `SIRIUS_SDK_PYTHON` names a Python with numpy
and opencv-python.

**Versions.** Studio, Edge and sirius-run take `<Version>` in
`Directory.Build.props`; Builder, Sirius IV, the plugin interface and the
PLC driver interface each have their own in their project file. A release
bumps them, adds its entry to `docs/project/release-notes.md`, is tagged `vX.Y.Z`,
and is packed from the tagged commit.

## Documentation

In `docs/`: `overview.md`, every package's README, and four folders -
`design/` (how things work and why: the runtime, workflows, the
annotation file), `guides/` (for people extending the suite: custom
nodes, PLC drivers, the SDK and kit READMEs), `reference/` (generated or
looked up: the node reference, the icons) and `project/` (backlog,
roadmap, decisions, release notes, licences). The packages carry their
documents flat, as before.

For people using the programs:

- [`docs/overview.md`](docs/overview.md) - what each program does, and how they fit together.
- [`docs/project/release-notes.md`](docs/project/release-notes.md) - what changed in each build, and what to try first.
- [`docs/reference/node-reference.md`](docs/reference/node-reference.md) - every node, port and parameter. Generated; do not edit.
- [`docs/design/annotation-format.md`](docs/design/annotation-format.md) - saved pictures, the `.sann` file, Sirius IV.
- [`docs/design/plc-workflows.md`](docs/design/plc-workflows.md) - controllers and their workflows.

For people extending them:

- [`docs/guides/sdk-readme.md`](docs/guides/sdk-readme.md), [`docs/guides/custom-nodes.md`](docs/guides/custom-nodes.md), [`docs/guides/python-nodes.md`](docs/guides/python-nodes.md), [`docs/guides/cpp-nodes.md`](docs/guides/cpp-nodes.md) - custom nodes.
- [`docs/guides/plc-drivers.md`](docs/guides/plc-drivers.md) - PLC drivers; [`docs/guides/plc-kit-readme.md`](docs/guides/plc-kit-readme.md) is the PLC kit's README.

For working on the product:

- [`docs/project/decisions.md`](docs/project/decisions.md) - why it is the way it is, including what went wrong getting there.
- [`docs/project/roadmap.md`](docs/project/roadmap.md) - where it stands: what is done, known limits.
- [`docs/project/backlog.md`](docs/project/backlog.md) - what is left to do, in a proposed order.
- [`docs/design/runtime-design.md`](docs/design/runtime-design.md) - the runtime, screens and PLC: the design.
- [`docs/project/licences.md`](docs/project/licences.md) - every licence question, and what must be settled before a customer build.

## Licensing

Internal test builds only. Emgu CV is dual-licensed GPLv3 or commercial,
and other conditions are open: [`docs/project/licences.md`](docs/project/licences.md) has
them all, and what must be settled before a build goes to a customer.
