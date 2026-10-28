# Release notes

Internal test builds. Each entry says what changed, and — more usefully —
what is worth pointing a tester at.

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
