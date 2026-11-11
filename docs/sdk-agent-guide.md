# AGENTS.md — writing Sirius Studio Plus custom nodes

Instructions for a coding agent (Codex, Claude Code, or similar) working in
this SDK folder. The usual jobs are **migrating a Python algorithm into a C#
custom node**, **wrapping Python code as a Python node** and **wrapping C++
code as a C++ node**. Humans should start with `README.md`. These rules
apply to you as well.

## C#, Python or C++

Do what the user asked for. If they did not say:

- **C++ node** when the code is C++ already, or depends on a C++ library
  (OpenCV's C++ API, HALCON, TensorRT, libtorch). Never port working C++
  to C# unasked.
- **Python node** when the code depends on a Python-only library (PyTorch,
  ONNX Runtime, TensorFlow, a vendor SDK), or the user wants the original
  code kept as it is.
- **C# node** when the code is NumPy/OpenCV arithmetic that is new, or
  must be fast, or the line PC should not need Python.

Say which you chose and why. The C# rules follow; the Python ones are in
**Writing a Python node** below, and the C++ ones in **Writing a C++
node** after it.

## Read first (C#)

1. `custom-nodes-api.md`: every public type and signature you may use.
   Do not guess an API; look it up here.
2. `SampleNodes/GammaNode.cs`: the reference pattern for a picture-in,
   picture-out node.
3. `SampleNodes/BrightRegionNode.cs`: a node that outputs shapes, numbers
   and a bool.
4. `SampleNodes.Tests/SampleNodeTests.cs`: how a node is tested.
5. `custom-nodes.md` §3.5 covers the translations from NumPy and OpenCV-Python.
   The rest of that file explains the rules below.

## Commands (C#)

```
dotnet build SampleNodes.sln -c Release
dotnet test  SampleNodes.sln -c Release
```

Both must pass before you report a task done. `nuget.config` points NuGet at
`packages/`, so no other setup is needed.

## Hard rules (C#)

- **Reference only the `Sirius.Plugins` package.** It already contains
  `Sirius.Core`. Do not add OpenCvSharp, Emgu.CV, System.Drawing,
  ImageSharp or any other imaging library unless the user asks for it.
  Do not modify `packages/`.
- **Never reimplement a step the application already has as a built-in
  node.** That means Gaussian/median/box blur, threshold, adaptive
  threshold, colour conversion, morphology, crop, resize, ROI, Canny, blob
  finding, line/circle fitting, histogram, OCR and barcode reading. Port
  only the part of the algorithm that is new. In your summary, list the
  built-in nodes the operator should wire ahead of yours, in order.
- **`TypeKey` is permanent.** Use `<team>.<name>` (letters, digits, `-`,
  `_`, one or more dots). Ask the user for the team prefix if it is not
  clear from existing code. Never change a key that already exists.
- **Port names are permanent too.** Put display text in `label`, not in
  `name`.
- **Never write to an input `Picture`, and never keep one past `Run`.**
  Write into `Picture.Allocate(input)` or `input.Copy()`.
- **`Row<T>` must match `Pixels`**: `Byte8` → `byte`, `UInt16` → `ushort`,
  `Float32` → `float`. Handle every type the node accepts. For any other
  type, call `context.Warn(...)` and publish the input unchanged. Do not
  throw for that case.
- **Clamp before narrowing.** `byte`/`ushort` arithmetic happens in `int`.
  NumPy's `uint8` wraparound is almost never what the Python author meant.
  If the original relies on it, keep it and say so in a comment.
- **Read every `Connectable` setting through
  `context.Resolve(nameof(X), X)`**, never through the property directly.
  The same for every region setting (`OrientedRect`, `Ring`, `PathBand`),
  connectable or not: that is how it follows a wired Reference.
- **Call `context.ThrowIfCancelled()` once per row**, in any loop over
  rows.
- **Throw for real failures** such as invalid settings or an impossible
  input. Use `context.Warn` for "produced a result, but look at this".
  Do not catch and swallow exceptions inside `Run`.
- **No static mutable state** unless it is locked. Several instances may
  run concurrently.
- **Every setting gets a sensible default and a `Description`.** Numbers the
  operator will tune get `Min`, `Max` and `Step`.

## Migrating a Python function: the procedure

1. **Read the whole Python function** and list its inputs, outputs, and
   tunable constants (thresholds, sizes, magic numbers).
2. **Split it.** Mark each step as either *built-in* (see the list above) or
   *new*. Only the new steps become the node.
3. **Map types.** An `np.ndarray` image becomes `Picture`. A scalar becomes
   `double`/`int`/`bool`. Contours, boxes and points become a `ShapeSet` of
   the matching `Shape`s. A small array of numbers becomes `NumberMatrix`.
   Each tunable constant becomes a `[Setting]` with the Python value as its
   default.
4. **Translate indexing.** `img[y, x, c]` becomes
   `picture.Row<T>(y)[x * picture.Channels + c]`. Take the row span once per
   `y`, outside the inner loop. Write plain loops; there is nothing to
   vectorise.
5. **Keep coordinates and angles as they were.** Pixel coordinates are the
   same (y down, origin top-left). Angles here are degrees, clockwise.
   OpenCV-Python's `minAreaRect` angle already matches; convert radians.
6. **Write tests before calling it done.** Add a test class in the tests
   project using `TestRun` (see `SampleNodeTests.cs`) with at least:
   - one test checking a known output value computed from the Python
     version on the same synthetic input (say how you got the number);
   - one test per sample type the node handles;
   - one test for each warning or failure path;
   - one test for each connectable setting, using `.Wire(...)`.
7. **Build and test**, then report:
   - the node(s) added and their `TypeKey`s;
   - which settings map to which Python constants;
   - the built-in nodes to wire ahead of it;
   - any behaviour that differs from the Python original, and why.

## Skeleton

```csharp
using Sirius.Plugins;

namespace YourTeam.Nodes;

[CustomNode("Display Name",
    TypeKey = "yourteam.name",
    Group = NodeGroup.Analysis,
    Description = "One sentence an operator understands.")]
public sealed class NameNode : ICustomNode
{
    private InPort<Picture> _in = null!;
    private OutPort<Picture> _out = null!;

    [Setting(Min = 0, Max = 255, Step = 1, Description = "What it does to the result.")]
    public int Threshold { get; set; } = 128;

    public void Declare(INodeShape shape)
    {
        _in = shape.Input<Picture>("In");
        _out = shape.Output<Picture>("Out");
    }

    public void Run(IRunContext context)
    {
        var source = context.Read(_in);
        if (source is null)
            return;

        if (source.Pixels != PixelType.Byte8)
        {
            context.Warn("Name handles 8-bit images; this one passed through unchanged.");
            context.Publish(_out, source);
            return;
        }

        var result = Picture.Allocate(source);

        for (var y = 0; y < source.Height; y++)
        {
            context.ThrowIfCancelled();

            var from = source.Row<byte>(y);
            var to = result.WritableRow<byte>(y);

            for (var x = 0; x < from.Length; x++)
                to[x] = from[x] > Threshold ? (byte)255 : (byte)0;
        }

        context.Publish(_out, result);
    }
}
```

## Writing a Python node

Read `python-nodes.md` first: the types, settings and shapes are all there.
`PythonNodes/dark_spots.py` is the reference pattern and
`PythonNodes/test_dark_spots.py` how one is tested.

- **The two C# rules that hold here too:** never reimplement a step the
  application has as a built-in node, and the `key=` is permanent.
- **Import only from `sirius_nodes`** for the node machinery; never copy or
  edit `python/sirius_nodes.py`. The application always runs a library
  with its own copy.
- **One `.py` file declares the nodes**; helpers go in modules beside it,
  which the library imports. Files named `test_*.py` are not loaded as
  libraries.
- **Load models and open files in `load()`**, not in `run()` and not at
  import. Mark settings that change what `load()` does with `reload=True`.
- **Images are numpy arrays in BGR**, `uint8`, `uint16` or `float32`.
  Handle each dtype the node accepts; for any other, `self.warn(...)` and
  return the input unchanged.
- **Detections, words and features are shapes** (`Rect`, `Box`, `Circle`,
  ...), with measurements as keywords - `score=`, `class_id=` - so Filter
  Shapes and Shape Table can use them. Do not return them as text.
- **Raise for real failures; `self.warn` for "produced a result, but look
  at this".**
- **Every setting gets a default and a `description=`.** Numbers the
  operator tunes get `min`, `max` and `step`.
- **List the packages the library needs** in its docstring, and if it needs
  a particular Python, a `sirius.json` beside it (see
  `PythonNodes/sirius.json.example`). Do not install packages unless the
  user asks.

Test it with `NodeRunner`, as `test_dark_spots.py` does: one test checking
a known output computed from the original code on the same synthetic
input, one per dtype handled, and one per warning or failure path. Run

```
set PYTHONPATH=..\python
python -m pytest
```

in the library's folder, and report the node(s), their keys, the
packages needed, and the built-in nodes to wire ahead of them.

## Writing a C++ node

Read `cpp-nodes.md` first, and `cpp/sirius_node.hpp`: it is short, and it
is the whole API. `CppNodes/sample_nodes.cpp` is the reference pattern;
`CppNodes/build.cmd` builds it.

- **The two C# rules that hold here too:** never reimplement a step the
  application has as a built-in node, and the `.key(...)` is permanent.
- **Include `sirius_node.hpp`; never edit it or `sirius_node.h`**, and
  write nothing against `sirius_node.h` directly. The guard that turns a
  crash into the node's error is in the `.hpp`.
- **`SIRIUS_NODES(...)` once, in one source file,** naming every node
  class.
- **The library runs inside the application. A bug in it can end the
  application.** So:
  - **Never write to an input image**, and never keep its pointer or a
    `cv::Mat` over it after `run()` returns. `clone()` first.
  - **Index pixels through the stride**: `sirius::to_mat`, or
    `image.row<T>(y)`. Never `data + y * width * channels`.
  - **Check sizes before indexing.** An empty image, a kernel larger than
    the image, a region outside it: throw `sirius::error` with a sentence
    an operator can act on.
  - **Check `image.pixels` and `image.channels`** and throw
    `sirius::error` for a type the node does not handle. Do not cast and
    hope.
  - **No `abort()`, `exit()`, `assert()` or `std::terminate()`**, nothing
    thrown from a destructor or a `noexcept` function, and no detached
    threads that outlive `run()`. None of those can be caught.
  - Throw for failures - `sirius::error`, or let `std::exception` and
    `cv::Exception` through; do not catch and swallow them.
- **Load models and open files in `load()`**, not in `run()` and not in a
  global object's constructor. Mark the settings `load()` reads with
  `.reload()`.
- **Detections, words and features are shapes** with `.with_metric(...)`,
  so Filter Shapes and Shape Table can use them. Do not return them as
  text.
- **`run.warn(...)` for "produced a result, but look at this".**
- **Every setting gets a default and a `.description(...)`.** Numbers the
  operator tunes get `.range(...)` and `.step(...)`.
- **Build for x64, Release, `/MD`, with a `.pdb`.** Say which DLLs the
  library needs beside it; if they live elsewhere, write a `sirius.json`
  beside it with `dllFolders`. Do not download or install libraries unless
  the user asks.

There is no test runner for C++ nodes outside the application. Keep the
algorithm in a function of its own, taking plain pointers or a `cv::Mat`,
so it can be tested with whatever the project already uses; keep the node
class a thin wrapper around it. Build with `build.cmd` (or the project's
own build) and report the node(s), their keys, the DLLs needed, the
built-in nodes to wire ahead of them, and that the build succeeded - and
say plainly that it has not been run in the application.

## Do not

- Add a `Version=` to `Sirius.Plugins` other than the one already in the
  project file, or remove `ExcludeAssets="runtime"` from the library
  project. The tests project is the only one that references it without
  `ExcludeAssets`.
- Add UI code, WPF references, or file I/O of images inside a node. Images
  arrive on ports.
- Reach for reflection, `unsafe` code or `Marshal` to get at a picture's
  memory. `Row<T>` and `Samples<T>` are spans and already fast.
- Report success without having run `dotnet test`, or `pytest` for a
  Python node, or the build for a C++ one.
