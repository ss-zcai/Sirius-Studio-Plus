# Custom nodes: the plugin interface

How an algorithm written outside this repository becomes a tool in the
toolbox.

Companion to `decisions.md`, which records what was settled and why, and
`node-reference.md`, which documents the built-in nodes.

> **Interface version 1.0.** Built and tested; ships with the next
> application release. The SDK is `dist/Sirius.Plugins-SDK-1.0.0.zip`,
> made by `tools/pack-sdk.ps1`: the `Sirius.Plugins` package, this guide,
> and a sample library that builds from the package alone.

---

## 1. What this is for

A vision team accumulates algorithms faster than any one application can
absorb them. Most are small - a flat-field correction, a particular way of
scoring a weld, a lookup table someone tuned over a fortnight - and each is
worth a tool but not worth a release of the host application.

So: write one as a C# class, build it into a class library, put the DLL
where the application looks, and it appears in the toolbox beside the
built-in nodes - its settings in the properties panel, its values in the
OUTPUT readout, saved into recipes like anything else.

**Two tiers exist, and only the first is supported.**

| | Tier 1 - `Sirius.Plugins` | Tier 2 - `VisionNode` |
|---|---|---|
| Reference | one package | `Sirius.Core` + `Sirius.Vision` |
| Brings in OpenCV | no | yes - Emgu.CV |
| Stability | versioned contract | changes without notice |
| Audience | **anyone** | this repository only |

Everything below describes Tier 1.

---

## 2. Why a plugin never sees an OpenCV `Mat`

The one design choice that shapes everything else.

Internally an image is `ImageData`, which owns an Emgu.CV `Mat`. The
obvious SDK would hand that straight to plugin authors. It is rejected for
three reasons, any one of which would be enough:

1. **Licensing.** Emgu.CV is GPLv3-or-commercial. An SDK exposing `Mat`
   would make every plugin a work linking Emgu, so each author would inherit
   the same licence question we have. Emgu's commercial agreement also bars
   redistributing it "as part of any product that can be described as a
   development toolkit or library" - a precise description of an SDK built
   on it. A buffer-based contract means the question never comes up.
2. **Versioning.** A plugin compiled against `Mat` is pinned to one Emgu
   version. Changing the OpenCV binding would break every plugin ever
   built, including ones we have never seen.
3. **It is the right shape for what comes next.** A future bridge to Python
   needs a pointer, dimensions and a stride - not a C++ class (§8).

So the contract is a plain pixel buffer, `Picture`, and the application
converts at the boundary in code this repository owns.

---

## 3. The interface

A marker attribute, a port declaration, a run method, and settings as
properties. This is a complete node:

```csharp
using Sirius.Plugins;

[CustomNode("Lift",
    TypeKey     = "acme.lift",
    Group       = NodeGroup.Basic,
    Icon        = "ph_brightness",
    Description = "Adds a constant to every pixel of an 8-bit image.")]
public sealed class LiftNode : ICustomNode
{
    private InPort<Picture>  _in    = null!;
    private OutPort<Picture> _out   = null!;
    private OutPort<double>  _mean  = null!;

    [Setting(Min = -255, Max = 255, Step = 1, Connectable = true,
             Description = "How much to add. Results stop at 0 and 255.")]
    public int Amount { get; set; } = 20;

    public void Declare(INodeShape shape)
    {
        _in   = shape.Input<Picture>("In");
        _out  = shape.Output<Picture>("Out");
        _mean = shape.Output<double>("Mean");
    }

    public void Run(IRunContext context)
    {
        var source = context.Read(_in);
        if (source is null)
            return;

        if (source.Pixels != PixelType.Byte8)
        {
            context.Warn("Lift handles 8-bit images; this one passed through unchanged.");
            context.Publish(_out, source);
            return;
        }

        // A connectable setting is read through Resolve, so a wire wins.
        var amount = context.Resolve(nameof(Amount), Amount);

        var result = Picture.Allocate(source);
        long total = 0;

        for (var y = 0; y < source.Height; y++)
        {
            context.ThrowIfCancelled();

            var from = source.Row<byte>(y);
            var to   = result.WritableRow<byte>(y);

            for (var x = 0; x < from.Length; x++)
            {
                to[x] = (byte)Math.Clamp(from[x] + amount, 0, 255);
                total += to[x];
            }
        }

        context.Publish(_out, result);
        context.Publish(_mean, (double)total / (source.Width * source.Height * source.Channels));
    }
}
```

The class needs a public parameterless constructor. One instance is made
for every node an operator places, so instance fields belong to one node.

### 3.1 `Picture`

The image a node sees.

| Member | |
|---|---|
| `Width`, `Height`, `Channels` | 1 to 4 channels, interleaved: B, G, R, B, G, R… |
| `Pixels` | `Byte8`, `UInt16` or `Float32` |
| `Layout` | what the channels mean: `Gray`, `Bgr`, `Bgra`, `Hsv`, `Hls`, `Lab`, `Luv`, `YCrCb`, `Xyz`, or `Unknown` |
| `Stride`, `RowLength` | bytes per row; samples per row (`Width * Channels`) |
| `Row<T>(y)`, `WritableRow<T>(y)` | one row of samples |
| `Samples<T>()`, `WritableSamples<T>()` | every sample, row after row |
| `Allocate(width, height, channels, pixels, layout)` | a new blank picture |
| `Allocate(like)` | same size, type and layout as another |
| `Copy()` | an independent, writable copy |

`T` must match `Pixels` - `byte`, `ushort` or `float`. Reading a 16-bit
picture as bytes is refused with a message saying which type to use, rather
than quietly halving every value.

Three rules, all load-bearing:

- **An input picture is read-only, and stops working when `Run` returns.**
  The same buffer is read by every other node wired to that output, and it
  belongs to the application. A node that keeps one in a field gets an
  `ObjectDisposedException` the next time it touches it, with a message
  saying to use `Copy()` - rather than reading memory that has been reused.
- **An output picture comes from `Allocate` or `Copy`.** The application
  copies it when it is published, so publishing the same one twice is fine.
- **Publishing an input passes it straight through**, without copying.
  That is the right thing to do when a node has nothing to change.

Buffers are always contiguous. Frames a plugin is not promised are
converted on the way in: a slice of a larger image is made contiguous, and
a sample type other than the three above (a 16-bit signed edge image, say)
arrives as `Float32`.

`Layout` matters because three channels are not automatically blue, green,
red: after a Colour Convert they may be hue, saturation, value, where hue
runs only to 179. A node that assumes one layout should check.

### 3.2 `INodeShape`

Called once, in `Declare`. Keep the handles it returns in fields.

```csharp
InPort<T>  Input<T>(string name, string? label = null, bool required = true);
OutPort<T> Output<T>(string name, string? label = null);
```

`T` is one of:

`Picture`, `ShapeSet`, `double`, `int`, `bool`, `string`, `Calibration`,
`Point2`, `NumberMatrix`, `Bounds`, `OrientedRect`, `ValueRange`, `Ring`.

All but `Picture` are the application's own types from `Sirius.Core`,
included in the package - so a node that only does geometry or arithmetic
needs nothing else. A port of any other type is refused when the library
loads, with the list above in the message.

`name` is written into saved recipes and must never change once a recipe
uses the node. `label` is what the operator sees, and may change freely. An
input and an output may share a name - "Shapes" in and out is common.

A required input with nothing wired stops the node running at all; it shows
as failed and says which input is missing. An optional one reads as null.

### 3.3 `IRunContext`

| Member | |
|---|---|
| `Read(port)` | a `Picture`, `ShapeSet`, `NumberMatrix` or `string` input, or null |
| `ReadValue(port)` | a number, point, calibration or other value-type input, or null |
| `IsWired(port)` | whether anything is connected |
| `Resolve(name, current)` | a connectable setting's value this run - the wire's if wired |
| `Publish(port, value)` | sets an output |
| `Warn(message)` | the node still produces, and shows amber with the message |
| `Cancellation`, `IsCancelled`, `ThrowIfCancelled()` | for long loops |

A number arriving as a different numeric type is converted - an integer
output can feed a `double` input - and a two-value matrix can feed a point,
as with the built-in nodes.

`Run` is called on a **worker thread**, never the user interface thread,
and other nodes - including other instances of the same class - may be
running at the same time. Instance state is safe; static mutable state
needs a lock. Long loops should call `ThrowIfCancelled`: the graph re-runs
while a slider is dragged, and a node that ignores cancellation makes the
slider unusable.

Throwing from `Run` is the right way to report a real failure. The node
shows as failed with the exception's message, and nothing else is affected.

### 3.4 `[Setting]`

A public property with a getter and a setter, of type `double`, `float`,
`int`, `long`, `bool`, `string`, any `enum`, `Point2`, `OrientedRect`,
`ValueRange` or `Ring`. Its value after construction is the default an
operator starts from. Nullable types are refused - give it a default.

| Property | Effect |
|---|---|
| `Min`, `Max`, `Step` | bounds; a number with both gets a slider |
| `Editor` | override the editor the type suggests: `Slider`, `TextBox`, `ComboBox`, `CheckBox`, `FilePath`, `Colour`, `Rect`, `Ring`, `Range` |
| `Group` | a heading in the properties panel |
| `Description` | tooltip, and the node reference |
| `Filter` | file dialog filter for `FilePath`, as `"Images|*.png;*.tif"` |
| `OddOnly` | snap to odd values, for kernel sizes |
| `Logarithmic` | a slider spanning orders of magnitude |
| `AllowsNoLimit` | for a `Range`: either end may be left off |
| `Connectable` | also an input, so another node can drive it |

An `enum` becomes a dropdown, and one with twelve or more entries can be
filtered by typing. An `OrientedRect`, `Ring` or `ValueRange` gets its
drawable editor without asking. `Colour` is a `string` such as `"#22C55E"`.

**A connectable setting must be read with `Resolve` during a run.** The
property holds what was typed in the properties panel; a wire overrides it,
and only `Resolve` knows about the wire. If a setting is wired and the node
never resolves it, the node shows a warning saying the wire was ignored -
because otherwise that mistake is invisible. An `enum` cannot be
connectable, since no node produces one.

### 3.5 Coming from Python and NumPy

Most algorithms being migrated were written against NumPy and
OpenCV-Python. The translations that come up every time:

| NumPy / OpenCV-Python | Here |
|---|---|
| `h, w, c = img.shape` | `picture.Height`, `picture.Width`, `picture.Channels` |
| `img.dtype` - `uint8`, `uint16`, `float32` | `picture.Pixels` - `Byte8`, `UInt16`, `Float32` |
| `img[y, x]` on a grey image | `picture.Row<byte>(y)[x]` |
| `img[y, x, c]` | `picture.Row<byte>(y)[x * picture.Channels + c]` |
| `img[y]`, one row | `picture.Row<byte>(y)` - a view, not a copy |
| `img.ravel()` | `picture.Samples<byte>()` |
| `np.zeros_like(img)` | `Picture.Allocate(img)` |
| `img.copy()` | `img.Copy()` |
| `img[...] = ...` in place | not on an input: allocate, write, publish |
| B, G, R channel order | the same - and check `Layout`, which may be HSV |
| `cv2.GaussianBlur`, `cv2.threshold`, `cv2.cvtColor`, `cv2.morphologyEx` | the built-in node, wired ahead of yours |

Three habits to change:

- **Write the loop.** In Python a per-pixel loop is a thousand times slower
  than the vectorised form, so NumPy code is written to avoid one. C# is
  compiled; the obvious loop over rows and then pixels is the fast way.
  Take the row span once per row, outside the inner loop, as the example
  above does.
- **Split off the standard steps.** A Python function that blurs,
  thresholds, finds contours and then does something particular usually
  only needs its last part ported. Blur, threshold, colour conversion,
  morphology, regions of interest and blob finding are built-in nodes, and
  are better tuned in the graph, where an operator can see each step.
- **State the sample type.** NumPy promotes types silently; `uint8 + 200`
  wraps. Here `Row<T>` must name the type the picture holds, and arithmetic
  on a `byte` is done in `int` - clamp before storing it back, as the
  example does.

Parallelising over rows is fine for a heavy node - `Run` is already on a
worker thread - provided the work for each row is independent and the
loop honours cancellation:

```csharp
Parallel.For(0, source.Height,
    new ParallelOptions { CancellationToken = context.Cancellation },
    y =>
    {
        var from = source.Row<byte>(y);
        var to = result.WritableRow<byte>(y);
        // ...
    });
```

---

## 4. Building and deploying a library

Unzip the SDK. Beside this guide are `packages/`, holding the
`Sirius.Plugins` package, a `nuget.config` pointing NuGet at it, and
`SampleNodes/`, a library of three working nodes. `dotnet build SampleNodes`
builds it from the package alone.

A library's project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Sirius.Plugins" Version="1.0.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

- `x64`: the application is 64-bit only.
- `EnableDynamicLoading` writes the library's own dependencies and a
  `.deps.json` beside it, which is how the application finds them.
- `ExcludeAssets="runtime"` leaves `Sirius.Plugins.dll` out of the output:
  the application supplies it. A copy left in by mistake does no harm - it
  is ignored.
- `Version` is what the Custom Nodes window shows. Bump it with each build
  handed to anybody.

A library may depend on any other managed package, and on native DLLs
placed beside it. Each library loads in isolation (§5), so two libraries may
use two versions of the same package.

**To deploy**, copy the build output folder - the DLL with its
dependencies - into a plugins folder, or add the DLL by hand:

| Where | Loads | Removed by |
|---|---|---|
| `plugins\` beside `SiriusStudioPlus.exe` | at every start, for every user | deleting it |
| `%APPDATA%\SiriusStudioPlus\plugins\` | at every start, for this user | deleting it |
| **Tools ▸ Custom Nodes ▸ Add Library…** | now, and remembered | **Remove** in the same window |

Folders are searched at any depth, so `plugins\Acme\Acme.Vision.dll` with
its dependencies beside it is fine. A DLL is recognised as a library by
reading its references, without loading it; dependencies are passed over.

---

## 5. Loading, isolation and versioning

**Tools ▸ Custom Nodes…** lists every library: where it came from, its
path, the nodes it added, and anything that went wrong - a sentence each,
since the person reading it is often not the person who wrote it. If a
library had problems at startup, the status bar says so once.

**Isolation.** Each library loads into its own `AssemblyLoadContext`.
`Sirius.Plugins`, `Sirius.Core` and `CommunityToolkit.Mvvm` always resolve
to the application's copies - they must, or the application could not
recognise a library's `ICustomNode` as its own. Everything else resolves
from the library's folder first.

**Files are not locked.** A library is read into memory rather than loaded
from its file, so it can be rebuilt with the application open. Restart the
application to pick up the new build.

**Removing, and why there is no reload.** Remove takes effect at the next
start; until then the nodes stay in the toolbox. .NET can unload a library
only once nothing anywhere refers to anything in it - and an open recipe
using one of its nodes does. Replacing a library under a live recipe would
mean rebuilding every such node from its saved settings, which is possible
but not built yet (§9).

**Version checking**, before any code from the library runs:

- The `Sirius.Plugins` **major** version must match the application's. A
  2.0 interface means something a 1.x library relies on has gone; the
  message says to rebuild against 2.x.
- Its **minor** version must be no newer than the application's. A library
  built for 1.3 may use something a 1.2 application does not have; the
  message says to update the application.
- The `Sirius.Core` it was built against must be no newer either, for the
  same reason.

Either way the operator gets a sentence, instead of a
`MissingMethodException` from inside somebody's algorithm an hour later.

**Failure containment.** A library that will not load is reported and
skipped. A library some of whose nodes are wrong loads the rest. A node
whose constructor throws, or whose `Declare` asks for a port nothing can
carry, is found once, when the library loads - not each time someone places
it.

**Type keys.** `TypeKey` is what a saved recipe records, so it is the one
string that can never change.

- It is required, and written `vendor.name` - letters, digits, `-` and `_`,
  with at least one dot: `acme.flatfield`. The dot is a namespace, so two
  teams choosing the same short name do not collide.
- It is saved as `custom/acme.flatfield`. No built-in key contains a slash,
  so a later release of the application can never claim a library's key.
- A key already taken - by another library, or a second copy of the same
  one - is refused, naming who has it.

**A recipe using a library you do not have** opens with that node skipped,
and the warning says it is a custom node, names its key, and says to add
the library under Tools ▸ Custom Nodes and reopen the recipe. Wires to the
missing node are dropped, each with a warning, as for any missing node.

---

## 6. What a plugin deliberately cannot do

Stated so it is a decision rather than a disappointment.

- **No custom user interface.** Settings get the standard editors.
- **No drawing on the viewer** beyond publishing a `ShapeSet`, which the
  viewer draws like any other overlay - boxes, lines, circles, polygons and
  labels, in a colour of the node's choosing.
- **No access to the graph.** A node sees its own inputs and nothing else -
  not other nodes, not the recipe, not the file being inspected.
- **No icons of its own.** `Icon` names one of the application's icons; a
  name it does not have shows the custom node puzzle piece, which is also
  the default.

Each of these can be added later without breaking anything already
written, which is the reason to leave them out of 1.0.

---

## 7. Testing a library

A node is an ordinary class, so it is testable without the application.
`Sirius.Plugins.Testing.TestRun` declares the node's ports as the
application would, runs it, and records what it published:

```csharp
using Sirius.Plugins;
using Sirius.Plugins.Testing;

var input = Picture.Allocate(16, 16);
input.WritableSamples<byte>().Fill(100);

var run = TestRun.For(new LiftNode { Amount = 20 })
    .Supply("In", input)
    .Execute();

Assert.Equal(120, run.Published<Picture>("Out").Row<byte>(0)[0]);
Assert.Equal(120.0, run.Published<double>("Mean"));
```

| Member | |
|---|---|
| `TestRun.For(node)` | declares the node's ports and checks its settings |
| `Supply(input, value)` | what every run sees on an input; null leaves it unwired |
| `Wire(setting, value)` | drives a connectable setting, as a wire would |
| `Cancellation` | set to test a node's cancellation |
| `Execute()` | runs once |
| `Published<T>(output)`, `WasPublished(output)` | what the run produced |
| `Warnings` | what it warned about |

It applies the application's rules, so a node that passes here behaves the
same in a recipe: the same refusal of a port or setting type, input pictures
that are read-only and expire after the run, numeric conversion on the way
in, the warning for a wired setting never resolved, and a failure - not a
null - for a required input left out.

There is nothing to mock. No graph, no scheduler, no OpenCV, no window.

---

## 8. Python

The decision for now is **C# first**: migrate an algorithm into a C# node
wherever that is not a significant difficulty. Where a Python algorithm
depends on a library with no C# equivalent, a Python host is the plan:

**One plugin that hosts Python.** A single `Python Script` node - settings
being a script path and a few typed arguments - embedding CPython and
handing each `Picture` to the script as a NumPy array over the same memory.
Every such algorithm then becomes a script, with no DLL each. The caveat is
the GIL: independent branches of a graph evaluate concurrently, and Python
calls queue behind one another. For one Python node in a recipe that is
invisible; for four in parallel it is not.

If that bites, the same node can move to **out-of-process workers** - a pool
of Python processes, buffers over shared memory - which removes the GIL
contention and means a crash in somebody's C extension cannot take the
application down, at the cost of a little latency and a Python runtime for
the installer to deal with.

Neither needs any change to this interface. That is the payoff of §2: the
contract is a pixel buffer, which is exactly what Python wants.

---

## 9. What is built, and what is not

Built, in interface 1.0:

- `Sirius.Plugins`: `ICustomNode`, `INodeShape`, `IRunContext`, `Picture`,
  `[CustomNode]`, `[Setting]`, `CustomNodeTypes`, and `TestRun`.
- The host node that wraps each custom node, converting `Mat` ⇄ `Picture`
  at the boundary, with settings that work with the properties panel, undo,
  saved recipes and wired inputs exactly as built-in parameters do.
- The loader: isolated load contexts, unlocked files, version checks, key
  rules, and per-library error reporting.
- **Tools ▸ Custom Nodes…**, the two plugins folders, and the remembered
  list of libraries added by hand.
- The named warning for a recipe using a missing custom node.
- `samples/SampleNodes` - Gamma, Bright Region and Ratio - with its tests
  in `samples/SampleNodes.Tests`, and `tools/pack-sdk.ps1`, which builds
  the SDK and proves the sample builds and passes its tests from the
  package alone.
- `custom-nodes-api.md`, the reference, and `AGENTS.md`/`CLAUDE.md`, the
  instructions a coding agent reads when it is asked to do a migration.

Not built, each of which can be added without breaking a 1.0 library:

- **Reloading a library without restarting** (§5).
- **Icons supplied by a library** (§6).
- **IntelliSense for the `Sirius.Core` types.** `custom-nodes-api.md`
  documents every one a node uses, and the `Sirius.Plugins` types show
  their documentation in the editor; `Shape`, `ShapeSet` and the rest do
  not yet.
- **The Python host** (§8).
