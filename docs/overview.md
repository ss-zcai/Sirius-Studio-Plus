# Sirius Studio Plus

Machine vision on one PC: build an inspection from ready-made tools, put
it on a line with its camera, its PLC and an operator's screen, and look
back at what it saw. Five programs share the work, each with one job.

It is the light, local member of **Sirius Vision**, Sigma Squares' vision
suite (§6): a station decides at the machine, quickly and on its own,
without a server - for the jobs the full suite is too heavy for.

> **Internal test builds.** Not for customers yet: some libraries' licences
> are still being settled (`licences.md` in the repository).

---

## 1. The programs

| Program | What it is for | Who uses it |
|---|---|---|
| **Sirius Studio** `SiriusStudioPlus.exe` | building and tuning an inspection - a **recipe** | the vision engineer |
| **Sirius Builder** `SiriusBuilder.exe` | putting recipes on a line - a **project**: stations, PLCs, the operator's screen | the integrator |
| **Sirius Edge** `SiriusEdge.exe` | running a project on the machine | the operator |
| **Sirius IV** `SiriusIV.exe` | looking back at the pictures a station saved | quality, the engineer |
| **sirius-run** `sirius-run.exe` | a recipe over a folder of pictures, from a script | the engineer, automation |

All five are in the same folder, and nothing needs installing: unzip and
run.

### Sirius Studio - the recipe

A recipe is a **graph of tools**: a camera or picture goes in, tools find,
measure, read and judge, and a verdict comes out. Drag tools from the
toolbox, wire them, and watch every step's picture as the settings change.

- **72 tools**: filters, thresholds, blobs, edges and lines, circles and
  arcs, calipers, measurements, template matching, codes (QR, Data
  Matrix, barcodes) and text, calibration and lens correction, checks and
  grades, logs. Help ▸ Node Reference lists them all.
- **Cameras**: Basler (pylon) and Hikrobot (MVS), GigE and USB3, and a
  simulated camera. An Image File or Image Sequence stands in for a camera
  at the desk; 10- and 12-bit cameras keep their bits.
- **Run** once, **Run Continuously**, or **Run Folder** over a folder of
  saved pictures.
- **Sub-graphs**: a group of tools as one node.
- **Publish** what a screen may show or change - a result, a setting, a
  viewer - by name (right-click a node). Builder sees only what is
  published.
- **Custom nodes** in C#, Python or C++, made by your own team, appear in
  the toolbox beside the built-in ones (§5).

Saves `<name>.sgraph`.

### Sirius Builder - the project

A project is a folder holding everything a line needs:

- **Stations** - a camera and the recipe made for it in Studio. A line
  may have several, each inspecting on its own.
- **Controllers** - a PLC each, and its **workflow**: a small graph that
  watches the PLC, has stations inspect, and writes the result back
  (`plc-workflows.md`). A simulated PLC is there for the desk.
- **The screens** the operator sees, laid out by dragging widgets:
  pictures, PASS / FAIL, values, counts, buttons, settings, text and a
  logo. Builder makes a standard **Main** screen from what the recipes
  publish; **pages** and **popups** beside it - settings, details, later
  a login - are opened from its buttons, a popup over the page, which
  goes on running.
- **What the line may change**: for each published setting, how far an
  operator may move it.
- **Which pictures each station keeps**: none, failed or all, for how
  many days.

**Test Run** (F5) runs the whole project in a window, exactly as Edge
will. Double-click a station to see the screen, or a screen to design
it; right-click a station to open its recipe in Studio.

Saves `<project>.sproj`, with the recipes, screens and workflows in
folders beside it.

### Sirius Edge - on the machine

Opens a project and runs it full screen: every station, every
controller, the operator's screen. It edits nothing. Stopping finishes
the inspection in hand; closing asks first.

```
SiriusEdge.exe "D:\Lines\Line 3\Line 3.sproj"            full screen
SiriusEdge.exe "D:\Lines\Line 3\Line 3.sproj" --windowed  at a desk
```

### Sirius IV - the pictures

Opens a station's saved pictures - Builder ▸ station ▸ **Browse Saved
Images**, or `SiriusIV.exe <folder>` - by day, each with PASS or FAIL
and the shapes the recipe found drawn over it, its results in a tooltip.

- Sort, search, show only failures.
- One, two, three, four or nine viewers, each a tab you can arrange.
- **Match** pictures into sets - two cameras' views of one part - and
  **Sync** the viewers, so the same pixel is in the same place in each.
- Measure, and save the measurements with the picture.

### sirius-run - from a script

```
sirius-run "C:\Recipes\cap.sgraph" --folder "D:\Images\Batch 12"
```

Runs a recipe over every picture of a folder, prints each verdict and a
summary, and exits 0 if all passed, 1 if any failed, 2 if it could not
run - for a scheduled task, or a check after changing a recipe.

---

## 2. How they fit together

```
   Studio                      Builder                            Edge
 ┌───────────┐            ┌────────────────────────┐        ┌──────────────┐
 │  recipe   │──station──▶│  project               │──.sproj──▶│ runs it on   │
 │  .sgraph  │            │   stations  (recipes)  │        │ the machine  │
 └───────────┘            │   controllers (PLCs)   │        └──────┬───────┘
                          │   screen               │               │ pictures
                          └──────────┬─────────────┘               │ + .sann
                                     │ Test Run                    ▼
                                     └──────────────────────▶  Sirius IV
```

1. **Studio**: the engineer builds a recipe on sample pictures, tunes
   it, and publishes what the line will show and change.
2. **Builder**: the integrator adds it to a project as a station, adds
   the PLC as a controller, lays out the screen, and Test Runs.
3. **Edge**: the project folder is copied to the machine and runs there -
   from a Windows start-up task naming the project, if it should start
   with the PC.
4. **Sirius IV**: what a station kept is looked at afterwards, by
   anyone, anywhere the folder can be reached.

A recipe changed in Studio is picked up by Builder at once, and by Edge
when it next opens the project.

---

## 3. A first hour

1. **Studio**: drag an **Image File** onto the canvas and choose a
   picture, then a **Threshold** and a **Find Blobs**, and wire them. Run
   (F5). Click a node to see its picture and settings; F6 runs
   continuously.
2. Right-click a result ▸ **Publish**; the same on a setting and on an
   Image Viewer. Save.
3. **Builder**: File ▸ New Project; **+ ▸ Add Station from Recipe…** with
   that file. A screen is made. **Test Run** (F5): Trigger, Run, watch the
   counts.
4. **+ ▸ Add Controller**. Test Run again: a simulated PLC opens beside
   the screen. Set **M0.0** to true in it - the station inspects, and the
   PLC is answered.
5. The station ▸ **Image Saving…** ▸ All. Test Run a few times, then the
   station ▸ **Browse Saved Images**.
6. **Edge**: open the project's `.sproj`.

---

## 4. The files

| File | Made by | Holds |
|---|---|---|
| `*.sgraph` | Studio | a recipe: its tools, settings and wires |
| `*.line.json` | Edge, Builder | settings changed on the line, kept beside the recipe, not in it |
| `*.sproj` | Builder | a project: its stations, controllers, limits, image saving |
| `screens\*.screen` | Builder | the operator's screens: `Main.screen`, and a file per page or popup |
| `controllers\*.sflow` | Builder | a controller's workflow |
| `*.sann` | a station; Sirius IV | beside each saved picture: verdict, results, shapes, the part's values, measurements |

A project is a folder: `Line 3.sproj` with `recipes\`, `screens\`,
`controllers\` and `images\` beside it. Copy the folder and the project
goes with it.

---

## 5. Making it your own

**Custom nodes** - your own algorithms as tools in Studio's toolbox, in
**C#**, **Python** or **C++**. The **custom node SDK**
(`Sirius.Plugins-SDK-<version>.zip`) has the interface, a sample in each
language and the guides: `sdk-readme.md` first, then `custom-nodes.md`,
`python-nodes.md`, `cpp-nodes.md`.

**PLC drivers** - a new brand of PLC is a driver DLL in `plc-drivers\`.
The **PLC kit** (`Sirius-PLC-Kit-<version>.zip`) has Builder, the driver
SDK, a sample driver and `plc-drivers.md`; workflows are in
`plc-workflows.md`. Today the simulated PLC is built in; Siemens S7 and
Modbus TCP come next.

---

## 6. In the Sirius Vision family

| Product | What it does |
|---|---|
| **Sirius Micro Services (MS)** | the vision engine on a local server; logs results and images |
| **Sirius Client** | the runtime at the camera: triggers and sends images to MS |
| **Sirius Interface** | the PLC link for Client, built per customer |
| **Sirius Library** | the vision algorithm library |
| **Sirius Studio** | the predecessor of this product's Studio |
| **Sirius Image Cloud (IC)** | stores, searches and organises images with their information |
| **Sirius Data Insight (DI)** | industrial data dashboards |

**Sirius Studio Plus** is the all-in-one, local version: the inspection
runs at the machine, answers the PLC there, and needs no server - quicker
to set up for a small job, and still working when the network is not. Its
controllers replace a per-customer Interface with drivers written once.
The direction is *decide at the edge, analyse in the cloud*: later, a
station sends its results and pictures to Image Cloud and Data Insight
when they can be reached, without changing its recipes.

---

## 7. Further reading

In the `docs` folder beside the programs:

| | |
|---|---|
| `release-notes.md` | what changed in each build, and what to try first |
| `node-reference.md` | every tool, its inputs, outputs and settings (also Help ▸ Node Reference) |
| `annotation-format.md` | saved pictures, the `.sann` file, Sirius IV |
| `plc-workflows.md` | controllers and workflows, every PLC node |
| `python-nodes.md`, `cpp-nodes.md` | custom nodes in Python and C++ |

In the custom node SDK: its `README.md` first, then `custom-nodes.md` for
C#. In the PLC kit: `plc-drivers.md`, for writing a PLC driver.
