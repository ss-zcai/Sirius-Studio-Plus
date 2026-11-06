# Release notes

Internal test builds. Each entry says what changed, and — more usefully —
what is worth pointing a tester at.

---

## 1.0.10 — 6 November 2026

Shapes you can grow, shrink, move and turn; Crop folded into Region of
Interest; picking that follows through a filter; and the Camera node
saying why a camera is missing, on top of everything in 1.0.9 below.
Recipes saved in 1.0.9 open, with the changes noted under Crop and
Region of Interest. 71 nodes.

### Before you start: Basler cameras need pylon 26.10 or later

**An older pylon - 7.x, say - does not work**, even though its own pylon
Viewer finds the camera: this build uses pylon 26.10's .NET API, which
needs that runtime. Install the **pylon Runtime 26.10 or later** and
restart Sirius Studio Plus. If Basler cameras are still missing, the
Camera node now says why under its **Device** list.

### What to try first

> Find Circle or Fit Circle → **Offset Shapes** (Offset: -5) → Mask or Region Stats

The circle shrunk by 5 pixels: measure the face of a part while keeping
off its edge. A positive offset grows it. Works on ellipses, rectangles,
blob outlines, lines and points too.

> Find Blobs → Shape Table, and Find Blobs → **Filter Shapes** → Kept and Rejected → a viewer

Click a row in the table: the blob is picked out on the image, whether
it was kept or rejected - and clicking it on the image selects its row.

> Any found circle → click its **+**

Circles and ellipses now have a + at their centre, and clicking it picks
the circle. The number beside a picked circle is in a round badge, a
blob's in a square one.

### New and changed

- **Offset Shapes**: grows or shrinks shapes by a distance in pixels, the
  shape version of Dilate and Erode. A circle's radius changes by
  exactly the offset; an ellipse's axes by twice it; a rectangle's sides
  move out by it (**Corners: Sharp** keeps it a rectangle, **Round**
  rounds them off as it grows). A blob outline is offset all round, and
  splits into pieces if it shrinks through a narrow neck. A line moves
  sideways (**Lines: Shift**) or lengthens (**Extend**); a point grows
  into a circle. Shapes that shrink to nothing are dropped, with a
  warning. **Offset** can be wired, so a measured number can drive it.
- **Transform Shapes**: moves, turns (clockwise on the image) and scales
  shapes, each about its own centre or all about a **Pivot** - which a
  found shape can be wired into, to turn a set about its centre.
- **Crop is gone: Region of Interest does it.** A recipe with a Crop
  opens with a Region of Interest in its place, cutting exactly the same
  pixels (Alignment: Bounding Box), keeping its name and wires. Two
  differences: a Crop with no region drawn gets a default one and the
  recipe warns as it opens; and with a **Reference** wired and a part
  that turns, the region now turns with it.
- **Region of Interest has no Shapes output.** It repeated the region and
  was drawn as a second, purple box exactly over the one being edited.
  A wire from it is dropped, with a warning, when a recipe opens. A
  region placed by a shape wired into **Region** is now shown where it
  was placed.
- **Selection follows through Filter Shapes and Select Shape.** A table
  and a viewer on either side of one share their selection, each in its
  own numbering; the number beside the picked shape is the row on the
  wire it was picked from.
- **A + at the centre of circles and ellipses**, on screen and in saved
  pictures; clicking it picks the shape.
- **The Camera node says why a camera make is missing**, under its
  Device list: pylon not installed, too old, or not on the PATH; MVS
  failing to start. It used to be an empty list with no reason given.

### Known issues, in addition to those below

- **Offset Shapes and Transform Shapes borrow Dilate's and Resize's
  icons** for now.
- **Offsetting an ellipse keeps it an ellipse**, both axes changed. The
  curve truly the offset away is not quite an ellipse; the difference is
  a fraction of a pixel unless the offset is a good part of its width.
- **Real cameras are still untested**: Basler only on pylon's emulator,
  Hikrobot only on MVS's virtual cameras. Reports from a real camera are
  very welcome.

---

## 1.0.9 — 4 November 2026

12-bit pictures shown as they should be, two better measures of
roundness, zoom buttons for the graph, and the exit hang fixed, on top of
everything in 1.0.8 below. Recipes saved in 1.0.8 open unchanged. Still
70 nodes.

### What to try first

> A 12-bit image (Image File with **Read Mode: Unchanged**, or a 12-bit camera) → any viewer → **Display: 12-bit**

A 12-bit frame used to look nearly black, shown across the full 16 bits.
The readout now gives the pixel's own value (up to 4095), whatever it is
shown at.

> Find Blobs → Shape Table, or Filter Shapes with **Measure: Radial Circularity** or **Roundness**

Two measures that see an oval the old Circularity misses: an oval 2%
long reads 0.9999 on Circularity.

> The main toolbar → **Zoom In**, **Zoom Out**, **Fit Graph to Window**

Also on the View menu. Fit frames every node with a margin, as when a
recipe opens.

### New and changed

- **Display for 16-bit pictures**: 16, 14, 12 or 10 bits, or stretched,
  in every viewer and preview showing one. A Viewer node keeps its
  choice in the recipe; previews start from the last one used. **Read
  Mode** on Image File and Image Sequence now says that Any Color loads
  8 bits, squeezing a 12-bit file, and that Unchanged keeps its depth.
- **Radial Circularity and Roundness**, HALCON's two measures, beside
  the existing Circularity, for blobs and closed outlines: in the Measure
  list of Filter Shapes, Select Shape and Shape Style, and in the Shape
  Table. Ratios there show three decimals, so 0.999 no longer reads 1.
- **Closed outlines are measured like blobs.** An outline from Points
  to Polygon, or Ring Calipers' contour, carries circularity, solidity,
  the minimum rectangle's size and angle, extent and bounds, so it shows
  in the Shape Table and filters in Filter Shapes like a blob. **Its
  centre is now the centre of its area**, not the average of its points:
  a recipe using an uneven outline's centre may move slightly.
- **Ring Calipers' "Roundness" output is now "Out of Round"**, which is
  what it measures: the spread of radii in pixels. Wires to the old name
  still load.
- **Zoom In, Zoom Out and Fit Graph to Window** for the graph (above).
- **Fixed: the application could stay running after its window closed**
  once a camera had been used. It was a crash inside a camera maker's
  SDK when cameras were listed from two places at once. Camera access
  now takes turns, and closing the window stops a continuous or folder
  run first.
- The viewer's Display box shows "16-bit", not "DisplayRange { ... }".
- The About box shows the logo drawn as vectors, sharp at any size.
- **Notices corrected**: the docking panels (AvalonDock) are under the
  Microsoft Public License, not MIT. Its text ships beside the
  executable as `LICENSE-avalondock-mspl.txt`.
- **Custom node SDK 1.0.2**: the guides now say to read a region with
  `context.Resolve`, so it follows the node's Reference. Nothing else
  changed; libraries built against 1.0.0 or 1.0.1 load unchanged.

### Known issues, in addition to those below

- **The tools still assume 8 bits.** Threshold, Find Blobs and the
  others take 0-255 settings, so a 12-bit image needs values sixteen
  times larger. Image File still loads 8 bits by default: choose
  **Read Mode: Unchanged** for a 12-bit file.
- 1.0.8's "application can stay running" issue is fixed (above).

---

## 1.0.8 — 3 November 2026

Regions that follow the part, saving pictures from any viewer, and a
crash fixed, on top of everything in 1.0.7 below. Recipes saved in 1.0.7
open unchanged. Still 70 nodes.

### What to try first

> Find Blobs → Shape Style (Minimum Rect) → **Reference** of Find Line, Region Stats, Read Code …

Every tool with a region has a new **Reference** input. Draw the tool's
region on a good part, then wire a found result into Reference: from then
on the region keeps its place relative to the part as it moves and turns.
A turned box or a line gives the angle; a circle, a point or a blob gives
the position only. **Follow Reference Angle** turns the turning off.

> Any viewer → the **save** button beside the zoom buttons

**Save Image…** saves the picture itself; **Save Image with Overlays…**
saves it with the results and measurements on show.

> Find Blobs → **Fit Circle** (From: Centres, Group: All Together)

The centre of a ring of holes. For a pattern that is not a circle - a
grid, a cluster - use **Points to Polygon** (From: Centres, Order: Around
Centre) and its new **Centre** output.

### New and changed

- **Regions follow a found result** (above). The image shows each region
  where it was carried, and dragging it on any part keeps it relative to
  the reference. Where the reference was when wired is saved in the
  recipe; unwiring forgets it, and wiring again starts afresh. **A wired
  reference that finds nothing fails the tool** rather than searching
  where the region was drawn.
- **Saving a viewer's picture**, from every viewer and preview, at full
  resolution. PNG by default; JPEG, BMP and TIFF offered. A 12- or 16-bit
  image is saved **16-bit** as a PNG or TIFF, and a float image keeps its
  values as a TIFF - the picture's own values, not the 8-bit view of it.
  With overlays, lines and labels are drawn to be readable at full size;
  hide results or measurements on the toolbar to leave them out.
- **A band or region placed by a wire is shown where it is searched.**
  With a circle wired into Ring Calipers, Circle Defects or Polar to
  Linear, the image shows the band round the circle, and drawing over it
  sets **Offset** and **Band Width**. A note under the setting says what
  placed it.
- **Points to Polygon** joins one point per blob with **From: Centres**,
  and gives the pattern's **Centre**. Before, given blobs, it joined every
  pixel of every outline.
- **Fit Circle, Fit Ellipse and Fit Line take each blob's centroid** with
  From: Centres, not the middle of its box. **A recipe fitting through the
  centres of irregular blobs may move slightly**; round dots do not.
- **Fixed: Clear All could show an "Unexpected error"** after profile
  chart windows had been closed by hand. Closing a chart window now
  removes its profile line too.

### Known issues, in addition to those below

- **The application can stay running after its window closes**, with a
  camera used during the session. End `SiriusStudioPlus.exe` in Task
  Manager if it does; it is being looked into.
- **A reference's angle is known to within half a turn.** A part turned
  more than 90° from where it was taught is read the wrong way round.
- **Crop's region follows where a reference goes, but cannot turn** - it
  is upright by nature.
- **A custom C# node's region follows a reference only if the node reads
  it with `context.Resolve`**; otherwise the node warns.

---

## 1.0.7 — 2 November 2026

Custom nodes written in Python, Hikrobot cameras, two new measuring
tools and lens calibration, on top of everything in 1.0.6 below. Recipes
saved in 1.0.6 open unchanged. **If 1.0.6 was skipped, read its notes
too** - Circle Defects' Local Form, Basler cameras and Match Template's
Teach arrived there.

**66 nodes → 70**: Find Curve, Line Defects, Lens Calibration, Undistort.

### What to try first

> Image File → **Line Defects** → Image Viewer

On a part with a straight edge - a cut, a lid, a strip - draw the Region
across the edge with the part on the side **Direction** says. Notches and
burrs are outlined with their depth and length; the line they are
measured from is fitted so that a long chip does not tilt it.

> Image File → **Find Curve** → Image Viewer

For an edge that is neither a line nor a circle. Press **Draw** beside
Path, click along the edge on the input preview, double-click to finish
(or click the first corner to close it). Drag a corner, the band or its
edge afterwards.

> **Tools ▸ Custom Nodes ▸ Add Library** → `samples\PythonNodes\dark_spots.py`

A node library written in Python. Needs Python 3 with `numpy` and
`opencv-python` installed. **Dark Spots** appears under Blob and
**Sharpness** under Analysis; `docs\python-nodes.md` says how to write one.

> **Camera** (Device: a Hikrobot camera) → … , then **F6**

With MVS installed, Hikrobot cameras appear in the Camera node's Device
list beside Basler ones. Run Continuously now shows the picture at up to
30 frames a second.

### New and changed

- **Python custom nodes.** A `.py` file declares nodes with inputs,
  outputs and settings; it runs in its own Python - named in a
  `sirius.json` beside it, a `.venv`, or the Python on PATH - so two
  libraries can use different Pythons and packages in one recipe. Errors
  say which Python ran and what went wrong, in the Custom Nodes window or
  on the node. **Restart Python** on a node re-reads the file after an
  edit.
- **Custom nodes say where they came from**: their tooltip names the
  library, and searching the toolbox for a library's name finds its nodes.
- **Hikrobot cameras** through MVS, GigE and USB3. Exposure, gain, and
  free-run, software or hardware trigger (**Line 0**, rising edge - Basler's
  is Line 1). A camera another program has open says so.
- **Line Defects** (Judging): Circle Defects for a straight edge. Reference
  *Fitted*, *Wired* (a line from Find Line) or *Local Form* for an edge
  that bows a little.
- **Line Calipers: Straightness and Profile outputs.**
- **Find Curve** (Fitting): calipers along a drawn path, a smooth curve
  fitted through the edge, stray points set aside. The new **Path** tool
  draws and edits the path; it can also be typed as `x, y; x, y | width`.
- **A shape can drive a region.** Wire Shapes - Find Blobs' boxes, a
  found circle - into a region input such as Find Line's Region or Find
  Circle's Band, and the tool searches where the shape is. What the shape
  does not say (a band's width) comes from the drawn region.
- **Lens Calibration and Undistort** (Calibration): press **Add View** a
  few times with a chessboard or dot grid in different places and tilts;
  Undistort then takes the lens distortion out of every picture.
- **Run Continuously shows frames at the screen's rate**, up to 30 a
  second, and fewer when drawing is slow. The status line says both rates:
  *412 frames/s inspected, 30 shown*.
- **Picking shapes on a viewer with several shape wires** now selects the
  row in the Shape Table on that set, through a Shape Style as well. The
  selected shape is drawn heavier in its own colour, numbered by its row.
- **Unstyled shapes are green**, not violet, which disappeared on grey
  parts.
- **The help window** lines its text up and colours each type as its
  socket is coloured on the canvas; the Preview buttons carry the same
  swatches.

### Setting up a Hikrobot camera

1. Install **MVS** from Hikrobot's site (machine vision, service,
   download). Without it, Hikrobot cameras are simply not listed.
2. For GigE: set the camera's IP address with MVS's IP Configurator, and
   turn on jumbo frames on the network card.
3. Start Sirius Studio Plus **after** installing MVS, so it finds MVS's
   runtime.

To try the Camera node with no camera, make a virtual camera in MVS's
**Virtual Device Tool**; it is listed like a real one.

### Known issues, in addition to those below

- **Real Basler and Hikrobot cameras are untested.** Both drivers are
  tested only against their makers' emulators; GigE timing, hardware
  triggering and colour need checking on the bench.
- **A virtual Hikrobot camera can stay "in use"** after a program that had
  it open ended. Restart it in the Virtual Device Tool.
- **Lens Calibration is tested only on synthetic views.** A real wide lens
  is the test still to do.
- **A wired shape is used as it is**: its position is in the image it was
  found in. Between a finder and the tool it drives, nothing may move the
  pixels - a crop, a turn, Polar to Linear.
- **A Python node's inputs, outputs or settings changed** need the
  application restarted; Restart Python only re-reads `run()` and the rest
  of the code.
- **Images cross to Python and back** for each run: about 13 ms for a 5 MP
  colour frame.

---

## 1.0.6 — 31 October 2026

Defect inspection for parts that are not perfectly round, Basler cameras,
and a Teach button for Match Template, on top of everything in 1.0.5
below. Recipes saved in 1.0.5 open unchanged.

### What to try first

> Image File → **Circle Defects** (Reference: *Local Form*) → Image Viewer, with a Line Chart on its Profile

On a real part that is a little out of round, Circle Defects now measures
the edge against its own smoothed shape, so the part's waviness is not a
defect and only sharp notches and burrs are. The chart shows the
deviation as a flat line with the defects standing out, and the form as
a second trace.

> **Camera** (Device: a Basler camera) → … , then **F6**

With pylon installed, Basler cameras - GigE and USB3 - appear in the
Camera node's Device list.

### New and changed

- **Circle Defects: Reference *Local Form*** - the default for new nodes.
  The edge's own shape, smoothed over **Form Window** degrees (default 15),
  is the reference. Make the window about three times the widest defect.
  On a test lens it found two notches of about 2 px in an edge that waves
  ±4 px, with no false rejects.
- **Circle Defects: Reference *Fitted Ellipse*** - for parts that are oval.
- **Circle Defects: Form output** - the part's own out-of-roundness
  (largest radius less smallest), for a Limit Check of its own. The
  Profile adds the form as a second trace.
- **Basler cameras** through pylon, GigE and USB3. Exposure, gain, and
  free-run, software or hardware trigger (Line 1, rising edge).
- **Match Template: Teach.** **Teach Region** is a rectangle you can turn,
  like Region of Interest's - typed, or drawn with the pick-up button -
  with **Teach** beside it. Draw it round the feature on a good part and
  press Teach: what it holds on the current image is kept in the recipe
  as the template. A template wired into **Template** is still used
  instead while it is wired. A new **Template** output shows the one in
  use.
- **Match Template: a turned Teach Region.** The feature is straightened
  as it is cut and the angle is kept; each image is then searched at that
  angle and the matches drawn turned - for a feature at a known angle on
  a fixtured part.
- **Match Template: Search Region** is a turnable rectangle too. A match
  counts when its middle is inside. Left at 1 x 1, the whole image.
- **Pick-up buttons edit their own region.** A node with two regions
  shows the one whose button was pressed, drawn in its own colour - Teach
  Region amber, Search Region violet - with its name at the corner.
- **Buttons on nodes.** Some nodes have buttons in the properties panel:
  Teach beside Match Template's Teach Region, Reset on Counter.

### Setting up a Basler camera

1. Install the **pylon Runtime 26.10 or later** on the PC (the full pylon
   Software Suite also works). **An older pylon - 7.x, say - does not
   work**, even though its own pylon Viewer finds the camera: this build
   uses pylon 26.10's .NET API, which needs that runtime. Restart Sirius
   Studio Plus after installing. Builds from 1.0.10 say under the Camera
   node's Device list why Basler cameras are missing; earlier ones simply
   do not list them.
2. For GigE: give the camera an IP address on the same subnet as the
   network card (pylon IP Configurator), turn on **jumbo frames** on that
   card, and install pylon's **GigE performance driver**.
3. The recipe remembers the camera by serial number, so it is found again
   after its IP address changes.

To try the Camera node with no camera, pylon's camera emulator appears
when the application is started with `PYLON_CAMEMU=1` set:

```powershell
$env:PYLON_CAMEMU = 1; & ".\SiriusStudioPlus.exe"
```

### Known issues, in addition to those below

- **Real Basler cameras are untested.** The driver is tested against
  pylon's emulator, which is monochrome; real GigE timing, hardware
  triggering and colour cameras need checking on the bench.
- **No Hikrobot or plain USB webcam support yet.** Both are on hold.
- **Local Form needs a sensible Form Window.** A defect wider than about a
  third of the window starts to be taken for form and reads shallower.
- **Match Template does not search over angles.** It finds the feature at
  the angle it was taught at, give or take a few degrees. For parts that
  turn, find the angle another way (Find Line, a blob's orientation) and
  straighten with Region of Interest first.

---

## 1.0.5 — 30 October 2026

Inspection over a folder, defects round a circular edge, and the first of
the camera support, on top of everything in 1.0.4 below. Recipes saved in
1.0.4 open unchanged.

**62 nodes → 66** (65 in the toolbox).

The camera support is a first version: the Camera node and Run
Continuously work with the built-in *simulated camera*, but no real
camera driver is in this build yet. Basler (pylon) comes next, then
Hikrobot.

### What to try first

> Image Sequence → Find Circle → **Circle Defects** → Image Viewer, then **Edit ▸ Run Folder**

Point the Image Sequence at a folder of parts. Circle Defects outlines
missing material in red and extra in blue, each with its depth; Run
Folder inspects every image and ends with a summary of how many passed.
Add a **Counter** and a **Result Log** and they count and log each image.

> **Camera** (Device: *Simulated test pattern*) → Find Circle → Limit Check, then **F6**

Run Continuously inspects frame after frame. The status bar shows the
rate and the verdict; press F6 again to stop.

### New tools

- **Circle Defects** — missing and extra material round a circular edge.
  Calipers round a ring, measured against the circle most of the edge
  agrees with (fitted so that a large defect does not drag it), or a
  nominal radius, or a circle wired in. Separate **Inner** and **Outer
  Tolerance**; **Min Length** ignores specks; **Merge Gap** joins near
  neighbours. Each defect is outlined with its angle, depth, length and
  area. Outputs Count, Worst and Pass, so it judges like Limit Check.
- **Region Stats** — mean, standard deviation, darkest and brightest
  inside a drawn region, or inside each shape wired in. The readings are
  written onto the shapes, so a Shape Table lists them and Filter Shapes
  can keep, say, the dark ones.
- **Match Template** — finds where a template image appears, scored 0 to 1,
  best first, positioned to a fraction of a pixel. Wire the template from
  an Image File of a good part through Crop. Position only, not rotation.
- **Camera** — takes a frame on each inspection and keeps it while you
  tune, so a threshold is set against one picture. Exposure, gain, and
  free-run, software or hardware trigger. Device lists the cameras found;
  in this build that is the simulated one.

### Changes you will notice

- **Edit ▸ Run Folder** (Ctrl+F5, or the toolbar) inspects every image of
  the Image Sequence - counted by Counter, written by Result Log - and ends
  with a summary: passed, failed, yield, grades. **Stop Folder Run** stops
  after the image in hand.
- **Edit ▸ Run Continuously** (F6, or the toolbar) inspects frame after
  frame until pressed again.
- **sirius-run.exe** is in the package, beside the application: the same
  folder run from a command line or a scheduled task -
  `sirius-run recipe.sgraph --folder D:\parts`. It prints a line per image
  and a summary, and exits 0 when all passed, 1 when some failed, 2 when
  it could not run.
- **Shapes follow the image.** Crop, Resize, Region of Interest and Polar
  to Linear have an **Overlay** input and output: shapes found on the
  input come out placed on the output.
- **Label** has a **Result** input: wire a pass/fail or a grade in and it
  writes PASS or FAIL in green or red. Text, if set, goes in front:
  "Overall PASS".
- **Slow recipes stay responsive.** When the recipe takes 60 ms or more,
  dragging a band or a slider re-runs only the tool being changed; the
  rest catches up when you stop.
- **If OpenCV cannot load on a PC**, the application now says so at
  start-up with the fix (usually the Windows Media Feature Pack), and
  node errors show the cause rather than only "type initializer threw
  an exception".
- The Zoom to 100% button reads **1:1** again, and the Node Style palette
  has its dots back.

### Known issues, in addition to those below

- **No real camera driver yet.** Only the simulated camera is listed.
- **Match Template finds position only.** A part turned by more than a few
  degrees scores poorly. The template is cut by hand with Crop; a Teach
  button is to come.
- **Run Continuously refreshes the panels about five times a second**,
  not on every frame.

---

## 1.0.4 — 29 October 2026

Sub-graphs, edge points without a fit, and a full set of judging tools,
on top of everything in 1.0.3 below. Recipes saved in 1.0.3 open
unchanged.

**48 nodes → 62.** One of them, Line Profile, has left the toolbox (see
below), so 61 are offered there.

### What to try first

> Image File → **Find Circle** → **Ring Calipers** (Circle wired from Find Circle, Radius From = *Fitted*) → **Line Chart**

Find a circle, then measure its edge against it: the chart shows how far
each point round the ring is from the fitted circle - a roundness
profile - and Ring Calipers' **Roundness** output is the spread. Cut a
notch in a test image and it shows as a dip at its angle.

> … → **Measure** (Radius, A only) → **Grade** → **Counter** and **Result Log**

Grade a reading A / B / C / Reject, then press **Run** a few times: the
Counter tallies each grade and the yield, and the Result Log writes a CSV
row per run.

### New tools

**Edge points without a fit**

- **Ring Calipers** — Find Circle's calipers without the circle: every
  edge point round a ring, as points and as a closed contour (an open one
  for an arc). Wire a found circle into **Circle** and the band follows it,
  **Band Width** wide. **Radius From** measures each point from the band's
  centre or from a circle fitted through the points; each point carries
  its angle, radius, deviation and contrast. Outputs a **Profile** (deviation,
  radius or contrast against angle, gaps where a caliper missed) and
  **Roundness** (largest radius less smallest).
- **Line Calipers** — the same for a straight edge: every point along a
  rectangle, with its distance along and offset from the middle.
- **Points to Polygon** — joins points into an outline, in the order they
  arrive or sorted round their middle, open or closed, with its perimeter
  and area.
- **Fit Circle** and **Fit Ellipse** gain **Reject Above**, to drop stray
  points before the final fit; and take a set of loose points straight
  from the calipers without being told to pool them.

**Judging** — a new group in the toolbox; Limit Check has moved into it.

- **Compare** — A against B: less than, at most, equal within a
  tolerance, not equal, at least, greater than.
- **Check Logic** — All / Any / None / Exactly One over pass/fail results.
  Wiring one input adds the next.
- **Grade** — a number into bands, best first (default `A, B, C, Reject`),
  with the limits between them, whether lower or higher is better, and
  which grades fail. Colours the measurement: green for the best, amber for
  the others that pass, red for those that fail.
- **Worst Grade** — the part's overall grade from any mix of grades and
  pass/fail results.
- **Counter** — inspections, passed, failed, yield, and a tally per grade.
  **Edit ▸ Reset Counters** zeroes them.
- **Result Log** — a CSV row per inspection: time, image, and every value
  wired in. A new file each day, a new file when the columns change, and
  rows held rather than lost while the file is open in Excel. Choose a
  **Log File** first; it writes nothing until one is set.

**Shapes and output**

- **Filter Shapes** — keeps the shapes whose measurement is in a range,
  from any tool, and gives the rejected ones too.
- **Line Chart** — charts profiles in a panel of its own. Wiring one adds
  another socket, so several overlay to compare, each placed by its own
  angle or distance.
- **Sub Graph** — a graph used as one node. Double-click it to edit it in
  a tab beside the recipe; **Graph Input** and **Graph Output** inside are
  its sockets, typed by the first wire on either side.

### Changes you will notice

- **A Run is an inspection.** Counter and Result Log record a press of
  **Run** (F5), not the re-runs after every slider movement.
- **The status bar counts one verdict per decision.** Checks wired into
  Check Logic or Worst Grade are left to it rather than counted again, and
  the worst grade is shown: `PASS (2 checks)  grade B`.
- **Measure reads one shape.** Wire A alone and choose Radius, Diameter,
  Major Axis, Minor Axis, Orientation, Area, Perimeter or Length. Drawn on
  the image like the other readings, and in millimetres (areas in mm²)
  with a Calibration wired.
- **Shape Table takes a Calibration**: areas in mm², lengths in mm, the
  unit in each heading. Positions stay in pixels.
- **Polar to Linear** uses the same drawn ring as Find Circle, and unwraps
  around a wired circle with a chosen band width.
- **Toolbox search** — type at the top of the toolbox to find a tool,
  Enter to place the first match.
- **Dropdowns read as words** — "Centre To Centre" rather than
  "CentreToCentre".
- **Menus open from the left edge of their heading**, whatever the Windows
  handedness setting.
- **Line Profile is no longer in the toolbox**: a profile line drawn on
  any image viewer does the same. Recipes that use one still open and run.
- **Paste keeps wires** into a viewer's second and later shapes sockets.
- New icons for Line Calipers, Ring Calipers, Calibrate and Zoom to 100%.

### Known issues, in addition to those below

- **Only Run counts as an inspection.** Stepping an Image Sequence by hand
  is not counted or logged; a folder run is next.
- **Counts last for the session.** They are not saved with the recipe.
- **Filter Shapes' range slider is coarse**, since it has to span every
  kind of measurement. Type the ends.
- **Sub-graph tabs are not reopened** when the application restarts;
  double-click the node again.

---

## 1.0.3 — 28 October 2026

Judging, reading, calibration and custom nodes, on top of everything in
1.0.2 below. Recipes saved in 1.0.2 open unchanged.

**44 nodes → 48.**

### What to try first

> Image File → **Find Circle** ×2 → **Measure** → **Limit Check** → Image Viewer

Measure a distance and judge it. The annotation turns green or red, and
the status bar shows the verdict for the whole recipe. Then add a
**Calibrate** node and wire it into Measure, and the reading is in
millimetres instead of pixels.

### New tools

- **Limit Check** — pass or fail a number. The modes are *between* two
  limits, *at least*, *at most*, or *nominal ± tolerance*. A value exactly
  on a limit passes; a reading that is not a number fails. Wire a Measure's
  annotation through it and the shape is redrawn green or red with the
  value and PASS/FAIL beside it. The status bar adds up every Limit Check
  in the recipe: `PASS`, or `FAIL (2 of 5)`.
- **Read Code** — barcodes and 2D codes inside a drawn, rotatable region:
  QR, Data Matrix, Aztec, PDF417, Code 128, Code 39, Interleaved 2 of 5,
  Codabar and EAN/UPC, or *Any*. Outputs the text, the outline of each
  code found, and the count.
- **Read Text** — OCR inside a drawn, rotatable region, using Tesseract's
  fast English model. Pick a layout (a line, a single character, a block,
  or scattered text). **Allowed** limits it to the characters you expect,
  such as digits only for a date code. Outputs the text, a confidence
  score and the individual words. A short region is enlarged before
  reading, so small print still reads.
- **Calibrate** — turns pixels into real units. Either wire in a found
  feature of known size (or type its length in pixels) and enter its real
  length, or type the scale directly (e.g. `0.05 mm` per pixel). Wire it
  into **Measure** and the reading and its label come out in those units.

### Custom nodes

Tools can now be added from outside the application. **Tools ▸ Custom
Nodes…** lists the custom node libraries that are loaded, what each one
added, and anything that went wrong.

To try it, choose **Add Library…** and pick
`samples\SampleNodes\Sirius.SampleNodes.dll` from this package. Three
nodes appear:

- **Gamma**, in a new *Custom* group;
- **Bright Region** and **Ratio**, under *Analysis*.

Worth checking:

- the nodes work like built-in ones: their settings, undo, saving and
  reopening a recipe, and wiring a Number into Gamma's setting;
- the library is still there after restarting the application;
- **Remove** in the same window takes it away at the *next* start, which is
  intended;
- a recipe using Gamma, opened after removing the library, says which
  custom node is missing and where to add it.

### Changes you will notice

- **An OUTPUT section in the properties panel** shows what each of the
  selected node's non-image outputs is carrying: a measurement, decoded
  text, a pass/fail, a calibration. It updates on every run. The values
  can be selected and copied.
- **Shape Style no longer erases labels.** It used to wipe the text off
  anything it restyled, a Read Text result for instance. It now keeps the
  labels unless told otherwise.

### Known issues, in addition to those below

- **Areas are not calibrated yet.** Lengths come out in millimetres; a
  blob's area is still in square pixels, and says so.
- **A custom node library is not reloaded while the application is
  open.** After rebuilding one, restart the application.

---

## 1.0.2 — 26 October 2026

Measurement is the theme. 1.0.1 could find shapes; this one can measure
between them and write the answer on the picture.

**40 nodes → 44.** Four new, three replaced by three better ones.

### What to try first

Build this and you have exercised most of the release:

> Image File → **Find Circle** ×2 → **Measure** → **Label** → Image Viewer

Two circles found by their own search bands, the distance between them
measured, and the reading written on the image.

### New tools

- **Find Circle** — give it a centre and a ring-shaped band to search in,
  and it fits a circle to the edge it finds. Click **Draw** on the
  properties panel and place the band on the image: click the centre,
  click where the edge is, then click to either side to set how wide a
  band to search. The band can be moved and resized afterwards by
  dragging its circles. Outputs the circle, the edge points it fitted,
  and the radius.
- **Find Line** — the same idea along a rotated rectangle instead of a
  ring. Outputs the line, the points, and the angle.
- **Measure** — a distance or an angle between two found shapes.
  Centre-to-centre, edge-to-edge, angle, or perpendicular distance from
  one to the other. Outputs the number *and* an annotation you can wire
  straight to a viewer.
- **Label** — writes a number or a line of text onto the image. Wire a
  measurement into it, or shapes, and it labels every one of them at its
  centre.

Find Circle and Find Line share one sub-pixel edge finder, checked
against synthetic edges at zero error. Worth testing on real parts with
soft or noisy edges, which is where it has not been.

### Value nodes reworked

**Integer, Float and Vector are gone.** In their place:

| Was | Now | Why |
| --- | --- | --- |
| Integer, Float | **Number** | One node; a dropdown picks how the value is *entered*. The wire was always a number either way. |
| Vector | **Matrix** | Rows and columns, typed as `1 0 -1; 2 0 -2; 1 0 -1`. A single row is a vector. |
| — | **Text** | A line of text, for labels and text parameters. |

A **Matrix** can now drive the **2D Filter**'s kernel, so a convolution
kernel can be shared between filters or built upstream instead of typed
into each one.

> **Recipes saved in 1.0.1 that use Integer, Float or Vector will load
> with those nodes missing**, and a warning naming each one. Everything
> else in the recipe survives. This is expected at this stage — rebuild
> the affected part rather than reporting it.

### Changes you will notice

- **The window reopens where you left it**, maximised included. No more
  maximising on every launch.
- **Long dropdowns are searchable.** Colour Convert's list is the reason:
  start typing and it narrows. Any part of the name matches, in any
  order — `hsv`, `2gray`, `gray bgr`. Lists under twelve entries are
  unchanged.
- **Colour Convert's list is also shorter and correct**: 242 entries down
  to 138. OpenCV names most conversions twice, and the duplicates could
  never be selected. Note that `Gray2Rgb` and `Gray2Bgr` genuinely are
  one conversion — three equal channels have no order — while
  `Gray2Rgb` (24-bit) and `Gray2Rgba` (32-bit) remain separate.
- **Shapes are picked by their outline**, not their bounding box. Clicking
  inside a large circle no longer selects it in preference to whatever is
  actually under the pointer.
- **Each image viewer shows its own image size** in its status bar, so
  Crop and Region of Interest no longer leave a misleading number in the
  main window. Order is now size, then X/Y, then the pixel value.
- **The right-click Style menu shows its colour swatches**, and marks the
  one in use. Neither had ever drawn.
- **A result's label is drawn like a hand-placed measurement** — boxed, on
  the shape rather than at the corner of its bounding box. Label can set
  its own typeface and size, and its size is in image pixels so the text
  zooms with the picture.
- **Region of Interest no longer draws its rectangle on the output image.**
- **Pick/Draw buttons open the input viewer** if it is closed, rather than
  doing nothing.
- The properties panel is blank when nothing is selected, instead of
  showing empty controls.

### Known issues

- **10-bit and 12-bit images load and look almost black.** Measured and
  understood, not yet fixed. Two separate causes: the Image File node
  defaults to an 8-bit read mode (set **Read Mode** to `Unchanged` or
  `AnyDepth` to keep the bits), and the display then assumes a full
  16-bit range, so a 12-bit peak shows at 6% brightness and a 10-bit peak
  at 2%. True 8-bit and true 16-bit images are fine.
- **Histogram is 8-bit only.** Other depths warn and pass through.
- **One region of interest at a time.** A node with more than one region
  parameter only exposes the first one to the viewer.
- **Find Blobs does not report** fitted ellipse, inner/outer radius or
  moment invariants.

### Reporting

Say which node, what you did, and what you expected. A saved `.sgraph`
that shows the problem is worth more than a description.

Diagnostics are off by default. To get a log, set the environment
variable `SIRIUS_PANEL_LOG=1` before launching; it writes to
`%APPDATA%\SiriusStudioPlus\panels.log`. Attach it if a panel fails to
appear.

---

## 1.0.1 — 23 October 2026

First packaged build. 40 nodes, self-contained — nothing to install.
