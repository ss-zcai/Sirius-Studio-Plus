# Sirius Studio Plus — Custom Node SDK

Interface version **1.0.0**

Write your own tools for Sirius Studio Plus in C#. Build them into a DLL,
and they show up in the toolbox next to the built-in nodes. Their settings
appear in the properties panel and they are saved in recipes like any other
node.

---

## What is in this folder

| | |
|---|---|
| `README.md` | this page — start here |
| `custom-nodes.md` | the full guide: the interface, the rules, and why |
| `custom-nodes-api.md` | every public type and signature, for looking things up |
| `AGENTS.md`, `CLAUDE.md` | instructions for a coding agent doing a migration |
| `packages/` | the `Sirius.Plugins` NuGet package — the only thing a library references |
| `nuget.config` | points NuGet at `packages/`, so nothing needs installing |
| `SampleNodes.sln` | opens both projects below in Visual Studio |
| `SampleNodes/` | a working library of three nodes: **Gamma**, **Bright Region**, **Ratio** |
| `SampleNodes.Tests/` | unit tests for those nodes, run without the application |

## You need

- **Windows, 64-bit**, and the **.NET 8 SDK**:
  <https://dotnet.microsoft.com/download/dotnet/8.0>
- **Sirius Studio Plus with Tools ▸ Custom Nodes in its menu**, to try a
  library. The test build that comes with this SDK has it.

Visual Studio 2022 or VS Code is fine, but neither is required.

---

## Ten minutes to a working node

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

## Rules that will bite if skipped

- **`TypeKey` never changes once a recipe uses the node.** Recipes find a
  node by its key. Write it as `yourteam.name`, with your team's name before
  the dot, so it cannot collide with anyone else's.
- **Read a `Connectable` setting with `context.Resolve(...)`**, not the
  property. Otherwise a wire into it is ignored, and the node shows a
  warning saying so.
- **Input pictures are read-only, and stop working when `Run` returns.**
  Use `Picture.Allocate(input)` or `input.Copy()` for anything you write to
  or keep.
- **Handle every sample type you accept.** A frame can be 8-bit, 16-bit or
  float. Check `picture.Pixels`, and either handle it or warn and pass the
  image through, as the Lift example in the guide does.
- **Bump `Version` in the project file** with every build you give to
  anyone. The Custom Nodes window shows it, which is how you tell two builds
  apart.

## Coming from Python

See **section 3.5 of the guide**. In short: `img[y, x, c]` becomes
`picture.Row<byte>(y)[x * picture.Channels + c]`.

Do the standard OpenCV steps — blur, threshold, colour conversion,
morphology — with the built-in nodes ahead of yours in the graph, and write
only the part that is yours. A plain C# loop is fast, so there is no need
to vectorise the way you would in NumPy.

## Using a coding agent

Codex, Claude Code and similar agents can do much of a migration. Open the
agent **in this folder**: it reads `AGENTS.md`, or `CLAUDE.md` for Claude
Code, which points to the same file. Give it the Python file and ask for a
node. For example:

> Migrate `weld_score.py` into a custom node. Our team prefix is `acme`.

`AGENTS.md` tells the agent to:

- port only the parts not already built in;
- keep keys stable;
- handle every sample type;
- write tests against the Python version's results;
- run the tests before reporting.

It also tells the agent to report which built-in nodes to wire ahead of the
new one. Review that list and the tests. Those are where a migration goes
quietly wrong.

## When something does not load

Open **Tools ▸ Custom Nodes**. Every library is listed with its path, the
nodes it added, and anything that went wrong, one sentence per problem.
That sentence is what to send back to whoever wrote the library.
