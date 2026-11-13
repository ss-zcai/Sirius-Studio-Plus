# Roadmap

Where the product stands. **1.0.11** is the latest package (11 November
2026). What could come next, in a proposed
order, is in **`backlog.md`** - this page is what is done, and the one
thing that blocks shipping.

Companion to `decisions.md`, which records why things are the way they
are, and `release-notes.md`, which is what testers read.

---

## The one thing that blocks shipping

**Emgu.CV is dual licensed: GPLv3, or a commercial licence.** Distributing
under the GPLv3 option would require releasing this product's source under
GPLv3. That is written at the top of `THIRD-PARTY-NOTICES.txt`, which
ships inside the package, so whoever holds a build can see it.

Internal testing is fine. A build going to a customer is not, until a
commercial licence is in place - a procurement lead time, so it wants
starting well before it is needed. The pylon and MVS terms, our own
licence agreement and the PLC library's contract want settling at the
same time: backlog items L1-L6, all set out in `licences.md`.

---

## New in 1.0.11

- **Three programs.** Sirius Studio builds recipes; **Sirius Builder**
  builds a project - stations and the line's screen; **Sirius Edge** runs
  it on the machine. Decisions 68 to 71, and `runtime-design.md`.
- **Real bit depth.** Images carry how many bits they use, and level
  settings run to the image's own top - 4095 for 12-bit. Decision 64.
- **Custom nodes in C++**, in the application's process, with their
  faults caught. Decision 72 and `cpp-nodes.md`. SDK 1.0.3.
- **Rotate and Mirror**, and **Resize to a size in pixels**.
- **Label** has no mark at its point, and a choice of anchor.

---

## Done

### The engine and the shell

**The engine.** Ports, typed connections, cycle detection, topological
order, a dirty-tracked cancellable scheduler with explicit buffer
ownership, `.sgraph` serialisation with legacy migrations, and partial
evaluation: a recipe slower than 60 ms runs the edited node and its
ancestors at once and the rest when the edits pause.

**72 nodes** across Source, Basic, Combine, Filtering, Morphology, Blob,
Fitting, Transformation, Analysis, Calibration, Text and Code Reading,
Judging, Output and Sub Graph. `node-reference.md` lists every one.

**The shell.** Node canvas, toolbox with search, inspector, dockable
panels, saved per-recipe layouts, unsaved-change prompts, Save/Save As, a
Run that re-evaluates everything and reports the timing, a graph that
frames itself when a recipe opens and on demand (Fit Graph to Window,
Zoom In, Zoom Out on the toolbar), a help popup per node built from its
descriptor, and one dark title bar on every window including About.

**Undo and redo** over a command stack that keeps the instances it
removed, so undoing a delete restores the same node and every panel
holding it. Property edits are recorded by watching the nodes, so a new
parameter is undoable the day it ships. Gestures coalesce.

**Cut, copy, paste, duplicate and select all**, carrying `.sgraph`
documents, so parameters, colours, measurements and the wires between
copied nodes survive a paste.

**Dialogs in the shell's own colours**, keeping the stock dialog's
keyboard behaviour. **Node colours**: seven presets, checked for contrast
by test.

**The logos.** Each program has its own, drawn by the user: `res/Sirius
Studio Plus.svg`, `res/Sirius Builder.svg`, `res/Sirius Edge.svg`, and
`res/Sirius Image Viewer.svg` for the image viewer still to come (redrawn
10 November 2026 as two offset rounded squares). The SVG is the master,
exported with its text converted to outlines: `tools/svg2xaml.py` puts it
in `Themes/Logo.xaml` (Studio's About box draws it as vectors) and
`tools/make-app-icons.ps1` renders the `.ico` each program carries, at 16
to 256 px. A logo whose small frames need drawing by hand can have its
`.ico` replaced; nothing regenerates it unless the script is run. The
earlier designs, including `Sirius Stu`, are no longer used.

**Packaging.** `tools/pack.ps1` produces a self-contained zip, runs the
tests, regenerates the node reference, checks the licence notices,
camera APIs and other required files are in it, and refuses to write one
whose stamped version disagrees with its name. `tools/pack-sdk.ps1` packs
the custom node SDK (1.0.3) and proves its sample builds from the package
alone.

### The viewer

Pan and zoom with a pixel readout, nine measurement tools as selectable
annotations with on-image labels, multi-channel line profiling, overlay
toggles, shape selection shared with the tables, and regions drawn, moved,
resized and turned on the image. Each viewer states its own frame size.
Measurements are saved with the recipe, keyed by node and view. The Image
Viewer grows a shape socket whenever the last is filled. Any port opens in
a preview window of its own. Pictures save at full resolution, with or
without overlays, 16-bit kept as 16-bit. A 16-bit picture offers
**Display** - Auto (its own bits), 16, 14, 12 or 10 bits, or stretched -
and the readout gives its own value.

### Bit depth

Images carry how many bits they use - 12 for a 12-bit camera's frame -
set by the camera, or by Image File's **Bits** setting, and kept through
the tools. Settings are in the image's own values (a threshold of 2000 is
pixel value 2000), and their sliders run to the image's top: 4095 for
12-bit. Read Code, Read Text, Find Blobs' automatic threshold, Adaptive
Threshold, the Histogram and Blend all work on deep images by their own
depth. New Image File nodes keep a file's depth (Read Mode: Unchanged).
Decision 64.

### Interactive tools

The plan in `interactive-tools-plan.md` is complete: regions are drawn
only on the preview of their own node's input; Find Line, Find Circle and
Find Curve fit edges to a fraction of a pixel; every region can take a
shape on a wire instead of a drawn value (`ShapeRegions`); and partial
evaluation keeps dragging a caliper responsive. A node with several
regions shows the one whose pick button was pressed, in its own colour.

### Shapes

- **Filtering, selecting, styling**: Filter Shapes on any measurement,
  Select Shape, Shape Style.
- **Carried through image changes**: Resize, Rotate and Mirror, Region of
  Interest and Polar to Linear map shapes with their pixels (`ShapeMap`).
- **Rotate and Mirror** (11 November 2026): quarter turns and mirrors,
  exact, for an image that arrives the wrong way round.

### Saved images and Sirius IV

- **A station keeps its pictures** (11 November 2026, F20): none, failed
  or all, set per station in Builder, in day folders, each with a `.sann`
  annotation file - shapes, verdict, results. `annotation-format.md`.
- **Sirius IV** (`SiriusIV.exe`, 1.0.0): a folder's pictures, one to
  nine at once, with their annotations drawn on them. Decision 73.
- **Measure** reads a distance or angle between two shapes, or one
  shape's size; **Calibrate** turns pixels into units; **Lens
  Calibration** and **Undistort** take out lens distortion.
- **Judging**: Limit Check, Compare, Check Logic, Grade, Worst Grade,
  Counter and Result Log; a Run is an inspection.
- **Defects**: Circle Defects (round, local form or fitted ellipse) and
  Line Defects; Line Calipers reports straightness.
- **Regions follow a found result** (1.0.8): a Reference input on every
  tool with a region.
- **Roundness three ways** (1.0.9): Circularity and HALCON's Radial
  Circularity and Roundness; a closed outline is measured like a blob.
- **Offset Shapes** (1.0.10): grows or shrinks any shape by a distance -
  a circle's radius, a rectangle's sides, an outline all round through
  Clipper2 with sharp or round corners, splitting as it shrinks; a line
  shifts or extends; a point grows into a circle. **Transform Shapes**
  moves, turns and scales, each about its own centre or all about one
  point.

### Node library

- **Reading**: Read Code through ZXing (1D codes, Data Matrix, QR,
  PDF-417, Aztec), Read Text through Tesseract with the fast English
  model; both straighten a turned region first.
- **Match Template** with Teach, position only by decision.
- **Region Stats** for mean intensity over a region.
- **Values**: Number, Matrix and Text sources, and Label to write values
  on the image.

### Cameras

The Camera node and Run Continuously, with a simulated camera, **Basler**
(GigE and USB3) through pylon and **Hikrobot** (GigE and USB3) through
MVS, behind `ICameraDriver`. Basler needs **pylon 26.10 or later**: the
.NET API it ships calls into that runtime. A make whose software is
missing or too old is named under the Device list with the reason
(1.0.10). Every SDK call that lists, opens or closes a camera takes its
turn through one gate (1.0.9): two at once crashed inside the SDKs.
Tested so far only on pylon's emulator and MVS's virtual cameras -
backlog N2.

### Platform

- **Sub-graphs**, first version: a node holding a graph of its own,
  opened in a tab, with Graph Input and Graph Output as its sockets.
- **Custom nodes in C#** (interface 1.0, SDK 1.0.3), **in Python**,
  each Python library in its own process and environment, and **in
  C++** (11 November 2026), a native DLL in the application's process
  with its faults caught at the boundary. See `custom-nodes.md`,
  `python-nodes.md` and `cpp-nodes.md`.
- **Batch and headless running**: Run Folder in the application, and
  `sirius-run.exe` for scripts.
- **NodeGraphKit/**: the editor and dark theme on their own, for other
  teams - a standalone copy with its own solution, not shared code.
- Diagnostics are opt-in through `SIRIUS_PANEL_LOG=1`, writing to
  `%AppData%\SiriusStudioPlus\panels.log`.

---

## Known limits

The ones a tester will meet; the fixes are in the backlog.


- **No signal out.** A verdict reaches the status line and a CSV, not a
  PLC. Planned: PLC controllers in a project, with the PLC team's
  workflow editor - backlog F23.
- **Four icons have no SVG source** (`ph_find_curve`,
  `ph_lens_calibration`, `ph_line_defects`, `ph_undistort`): regenerating
  `Themes/Icons.xaml` from `res/ui icons` deletes them. Backlog N3.
