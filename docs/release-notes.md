# Release notes

Internal test builds. Each entry says what changed, and — more usefully —
what is worth pointing a tester at.

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
- **Match Template: Teach.** Draw **Teach Region** round the feature on a
  good part and press **Teach**: the feature is kept in the recipe. A
  template wired into **Template** is still used instead while it is
  wired. A new **Template** output shows the one in use.
- **Buttons on nodes.** Some nodes have buttons above their settings in
  the properties panel: Teach on Match Template, Reset on Counter.

### Setting up a Basler camera

1. Install the **pylon Runtime** on the PC (the full pylon Software Suite
   also works). Without it, Basler cameras are simply not listed.
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
