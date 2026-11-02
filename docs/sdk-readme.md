# Sirius Studio Plus — Custom Node SDK

Interface version **1.0.1**

Write your own tools for Sirius Studio Plus, in **C#** or in **Python**.
They show up in the toolbox next to the built-in nodes. Their settings
appear in the properties panel and they are saved in recipes like any other
node.

---

## C# or Python?

| | C# | Python |
|---|---|---|
| Best for | new code, per-pixel work, nodes that must be fast | code you already have, and libraries only Python has - PyTorch, ONNX Runtime, a vendor's OCR |
| Runs | inside the application | in its own Python process, one per library |
| The line PC needs | nothing beyond the application | a Python with the library's packages installed |
| Cost per image | none | images cross to Python and back: a few milliseconds for a 5 MP frame |
| A crash | stops the inspection | ends only that library's process; the next run starts a new one |

Both kinds of node look the same to the operator, are saved in recipes the
same way, and can be mixed in one recipe.

---

## What is in this folder

| | |
|---|---|
| `README.md` | this page — start here |
| `custom-nodes.md` | the full C# guide: the interface, the rules, and why |
| `custom-nodes-api.md` | every public C# type and signature, for looking things up |
| `python-nodes.md` | the full Python guide |
| `AGENTS.md`, `CLAUDE.md` | instructions for a coding agent writing or migrating a node |
| `packages/` | the `Sirius.Plugins` NuGet package — the only thing a C# library references |
| `nuget.config` | points NuGet at `packages/`, so nothing needs installing |
| `SampleNodes.sln` | opens both C# projects below in Visual Studio |
| `SampleNodes/` | a working C# library of three nodes: **Gamma**, **Bright Region**, **Ratio** |
| `SampleNodes.Tests/` | unit tests for those nodes, run without the application |
| `python/sirius_nodes.py` | the Python SDK: `@node`, inputs, outputs, settings, shapes, and `NodeRunner` for tests |
| `PythonNodes/` | a working Python library of two nodes: **Dark Spots**, **Sharpness**, with its tests |

## You need

- **Sirius Studio Plus with Tools ▸ Custom Nodes in its menu**, to try a
  library. The test build that comes with this SDK has it.
- For C#: **Windows, 64-bit**, and the **.NET 8 SDK**:
  <https://dotnet.microsoft.com/download/dotnet/8.0>. Visual Studio 2022
  or VS Code is fine, but neither is required.
- For Python: **Python 3** from <https://www.python.org>, with the packages
  your library imports. The sample needs `numpy` and `opencv-python`, and
  `pytest` to run its tests.

---

## Ten minutes to a working node in C#

**1. Build and test the sample.** In this folder:

```
dotnet build SampleNodes.sln -c Release
dotnet test  SampleNodes.sln -c Release
```

**2. Try it in the application.** Open Sirius Studio Plus and choose
**Tools ▸ Custom Nodes ▸ Add Library…**. Pick:

```
SampleNodes\bin\Release\net8.0\Sirius.SampleNodes.dll
```

**Gamma** appears in a new **Custom** group in the toolbox. **Bright
Region** and **Ratio** appear under **Analysis**. Wire
*Image File → Gamma → Image Viewer* and drag the Gamma slider.

The application remembers the library from now on. You can also copy the
whole `bin\Release\net8.0` folder into
`%APPDATA%\SiriusStudioPlus\plugins\SampleNodes\`; the application loads
everything in that folder each time it starts.

**3. Make it yours.** Copy `SampleNodes` and `SampleNodes.Tests` and
rename them. Change `AssemblyName` and `RootNamespace` in the project file,
then edit or replace the nodes. Each node is one class:

- `[CustomNode("Name", TypeKey = "yourteam.name")]` on the class;
- `[Setting(Min = …, Max = …)]` on each property the operator should tune;
- `Declare` lists the inputs and outputs;
- `Run` does the work.

`GammaNode.cs` is the smallest one to start from.

**4. Rebuild, then restart the application.** The DLL is not locked while
the application is running, so you can rebuild at any time. The
application loads the new build the next time it starts.

---

## Ten minutes to a working node in Python

**1. Test the sample.** With `numpy`, `opencv-python` and `pytest`
installed, in the `PythonNodes` folder:

```
set PYTHONPATH=..\python
python -m pytest
```

**2. Try it in the application.** Choose **Tools ▸ Custom Nodes ▸ Add
Library…** and pick `PythonNodes\dark_spots.py`. **Dark Spots** appears
under **Blob** and **Sharpness** under **Analysis**. Wire
*Image File → Dark Spots → Image Viewer*.

The application runs the library with the Python on PATH. To name a
particular one - a virtual environment, a conda environment, a GPU build -
copy `sirius.json.example` to `sirius.json` beside the library and edit
the path. `python-nodes.md` §2 has the full search order.

**3. Make it yours.** A node is a class:

```python
from sirius_nodes import node, Input, Output, Setting

@node("Name", key="yourteam.name", group="Analysis",
      description="One sentence an operator understands.")
class Name:
    image = Input.image("Image")
    score = Output.number("Score")
    level = Setting.integer("Level", 60, min=0, max=255)

    def run(self, image):
        return {"score": float((image < self.level).mean())}
```

Images arrive as numpy arrays, BGR like OpenCV. Return a dict of outputs
by name.

**4. Edit, then press Restart Python** on the node. That re-reads the file.
A change to a node's inputs, outputs or settings needs the application
restarted.

---

## Rules that will bite if skipped

- **The key never changes once a recipe uses the node** - `TypeKey` in C#,
  `key=` in Python. Recipes find a node by its key. Write it as
  `yourteam.name`, with your team's name before the dot, so it cannot
  collide with anyone else's.
- **Setting names are saved in recipes.** Renaming one loses the values
  saved under the old name.
- **C#: read a `Connectable` setting with `context.Resolve(...)`**, not the
  property. Otherwise a wire into it is ignored, and the node shows a
  warning saying so.
- **C#: input pictures are read-only, and stop working when `Run`
  returns.** Use `Picture.Allocate(input)` or `input.Copy()` for anything
  you write to or keep.
- **Handle every sample type you accept.** A frame can be 8-bit, 16-bit or
  float, in either language. Handle it, or warn and pass the image through.
- **Python: load models in `load()`, not in `run()`.** `load()` runs once;
  `run()` runs for every image.
- **C#: bump `Version` in the project file** with every build you give to
  anyone. The Custom Nodes window shows it, which is how you tell two builds
  apart.

## Coming from Python

Two routes:

- **Keep it in Python.** Wrap the function in a `@node` class and it runs
  as it is, with the packages it already uses. Quickest, and the only route
  for code built on PyTorch, ONNX Runtime or another Python-only library.
- **Port it to C#.** Worth it for code that must be fast, or where the line
  PC should not need Python. See **section 3.5 of the guide**. In short:
  `img[y, x, c]` becomes `picture.Row<byte>(y)[x * picture.Channels + c]`.

Either way, do the standard OpenCV steps - blur, threshold, colour
conversion, morphology - with the built-in nodes ahead of yours in the
graph, and write only the part that is yours.

## Using a coding agent

Codex, Claude Code and similar agents can write a node, or port one. Open
the agent **in this folder**: it reads `AGENTS.md`, or `CLAUDE.md` for
Claude Code, which points to the same file. For example:

> Migrate `weld_score.py` into a C# custom node. Our team prefix is `acme`.

> Wrap `label_ocr.py` as a Python node. Our team prefix is `acme`.

`AGENTS.md` tells the agent to:

- write only the parts not already built in;
- keep keys stable;
- handle every sample type;
- write tests against the original's results;
- run the tests before reporting.

It also tells the agent to report which built-in nodes to wire ahead of the
new one. Review that list and the tests. Those are where a migration goes
quietly wrong.

## When something does not load

Open **Tools ▸ Custom Nodes**. Every library is listed with its path, the
nodes it added, and anything that went wrong, one sentence per problem -
for Python, which Python it tried and what it said. That sentence is what
to send back to whoever wrote the library.
