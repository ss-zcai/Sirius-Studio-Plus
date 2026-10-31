# Roadmap

Where the product stands at **1.0.6**, and what is left.

Companion to `decisions.md`, which records why things are the way they are,
and `interactive-tools-plan.md`, which is the plan for the next large piece
of work.

---

## The one thing that blocks shipping

**Emgu.CV is dual licensed: GPLv3, or a commercial licence.** Distributing
under the GPLv3 option would require releasing this product's source under
GPLv3. That is now written at the top of `THIRD-PARTY-NOTICES.txt`, which
ships inside the package, so whoever holds a build can see it.

Internal testing is fine. A build going to a customer is not, until a
commercial licence is in place - and that is a procurement lead time, so it
wants starting well before it is needed. Nothing else on this page blocks a
release; this does.

---

## Immediately outstanding

Small, known, and not yet done.

- **A `.gitattributes`.** The repository was created without one, so line
  endings are normalised per machine rather than per repository. Harmless
  today, confusing the first time two people work on it.
- **A vector logo**, if the product icon is ever wanted larger than the 256
  pixel frame in `res/Sirius Stu.ico` - a splash, a README header,
  packaging. The useful shape is an SVG master that the `.ico` is generated
  from, so there is still one source; `tools/svg2xaml.py` already preserves
  an artwork's own colours, so a colour logo converts cleanly.

---

## Done

**The engine.** Ports, typed connections, cycle detection, topological
order, a dirty-tracked cancellable scheduler with explicit buffer ownership,
`.sgraph` serialisation with legacy migrations.

**66 nodes** (65 in the toolbox) across Source, Basic, Combine, Filtering, Morphology, Blob,
Fitting, Transformation, Analysis and Output - including masking from an
image or a shape, image logic and eleven blend modes, colour-space
conversion, and circle, ellipse and line fits that report their residual.

**The shell.** Node canvas, toolbox, inspector, dockable panels, saved
per-recipe layouts, unsaved-change prompts, Save/Save As, a Run that
re-evaluates everything and reports the timing, a graph that frames itself
when a recipe opens, a help popup per node built from its descriptor, and
one dark title bar on every window including About.

**Undo and redo**, over a command stack that keeps the instances it removed
- so undoing a delete restores the same node and every panel holding it
reattaches. Property edits are recorded by watching the nodes rather than
by a command class each, so a parameter added tomorrow is undoable the day
it ships; a test holds that list against what the serialiser writes.
Gestures coalesce, and a paste, a multi-node delete or colouring a
selection is one step back.

**Cut, copy, paste, duplicate and select all**, carrying `.sgraph`
documents, so parameters, colours, measurements and the wires between
copied nodes survive a paste with nothing written about any of them. Routed
through `ApplicationCommands`, so Ctrl+C in a rename box still copies text.

**Dialogs in the shell's own colours**, with the keyboard behaviour the
stock `MessageBox` gave for free written out explicitly - one default
button, Escape taking the way out that changes nothing, access keys, and a
bare Y/N/C as the dialog they replaced accepted.

**The viewer.** Pan and zoom with a pixel readout, nine measurement tools as
selectable annotations with their own colours and on-image labels,
interactive multi-channel line profiling with a value cursor, overlay
toggles, shape selection shared with the tables, a region that can be drawn,
moved, resized and turned, and a toolbar that stays on one line by moving
what does not fit into a menu.

**As many overlays as are wired in.** The Image Viewer grows a shape socket
whenever the last one is filled, so several producers draw on one image
without a node between them - each styled separately, because colour and
stroke live on the shape rather than on the set.

**Each viewer states its own frame size**, beside the cursor position and
pixel value. It used to be one number in the main status bar, read from
whichever viewer happened to be first, which said nothing about which - and
after a Crop or an ROI belonged to a different picture than the one being
looked at.

**Measurements are part of the recipe.** Drawn lines, angles and circles are
saved with the `.sgraph`, keyed by the node *and the view* they were drawn
on - a preview of an input and one of an output are different pictures - and
they count as an unsaved change, so New and Open ask before discarding
them.

**Node colours.** Seven presets plus the default, for marking up a large
recipe, set from the toolbar dropdown for the whole selection at once or
from the canvas menu for one node. Each fixes background, stroke and text
together, and the set is checked for contrast by test rather than by eye -
every title clears 7:1 on its own node, and no two strokes are close enough
to be confused.

**Previews.** Any node's port can be opened in a window of its own -
double-click the port, the buttons in the properties panel, or the canvas
menu. A shape port shows the annotated image and the measurements together.

**Parameters.** Range sliders with two handles, logarithmic where the
quantity spans decades, optional ends where "no limit" has to be
expressible, a colour picker, connectable parameters, and Enter to commit.

**Packaging.** `tools/pack.ps1` produces a self-contained zip, checks the
licence notices and the node reference are in it, and refuses to write one
whose stamped version disagrees with its name.

---

## Next: interactive tools

The plan is `interactive-tools-plan.md`. Step 1 - port-bound previews - is
done. What remains, in order:

1. **Arming the Draw button - done for a ring.**
   `MainViewModel.DrawRegionCommand` opens the preview of the input the
   geometry is measured in, and arms the ring tool when the parameter is
   one. A rectangle still is not armed, because it is dragged straight
   onto the image rather than placed with a tool - which is defensible
   until a second kind of region needs a gesture of its own.
2. **Find Line is done.** Calipers across an `OrientedRect` - the same
   type the ROI node uses, so the region can be drawn and turned with the
   machinery that already exists - fitted by total least squares with
   outlier rejection. Measured error against a known edge is zero across
   smoothing widths 1 to 21.
3. **Geometry parameters** - `Shape`-valued, with handle editing and
   serialisation. The `Shape` vocabulary built for blob *output* is the one
   a search region needs as *input*. This is also what retires
   `ActiveRegionParameter`, the mechanism that pushes the selected node's
   region to every viewer and lets a region be drawn in the wrong
   coordinate space.
4. **Find Curve** on the same caliper core. **Find Circle is done** - a
   centre and a band, radial calipers through it, a circle fitted to the
   edges with outlier rejection, accurate to better than a fifth of a pixel
   against a known edge. Its ring is drawn on the viewer now, and can be
   moved and resized there; what item 3 would add is holding it as a
   `Shape` rather than as a `Ring` parameter of its own.
5. **Partial evaluation - done.** A recipe slower than 60 ms runs the
   edited node and its ancestors at once and the rest when the edits
   pause, so dragging a caliper does not re-run the whole recipe on
   every movement.

---

## Shapes

- **Filter Shapes is done**: any producer's shapes filtered on any
  measurement, with the rejected ones given too. Combining is done as
  well: the Image Viewer grows a socket per shape set, so several
  producers draw on one image without a node between them.
- **Remapping shapes is done**: Crop, Resize, Region of Interest and Polar
  to Linear carry an Overlay of shapes through the same mapping as their
  pixels (`ShapeMap`).
- **Measurement in the graph is done.** The Measure node takes two shape
  sets and reads a distance or an angle between them - or one shape's
  radius, diameter, axes, orientation, area, perimeter or length -
  publishing the number and an annotation drawn the way a hand-drawn
  measurement looks.
  The reference mechanism this was waiting for turned out not to be
  needed: wiring two producers into two inputs says "the line from Find
  Line 1, the circle from Find Circle 2" in the vocabulary the graph
  already had.
- **Limit checks are done.** Limit Check compares a number against a
  band, a floor, a ceiling or a nominal with a tolerance, and publishes
  a verdict three ways: a boolean for the graph, the incoming annotation
  recoloured green or red, and the number passed through so a check can
  sit mid-chain. The verdict leaves the recipe through `IJudgingNode`,
  which the shell aggregates into the status line.

  **Judging is done for 1.0.4**: Compare, Check Logic, Grade (bands of
  A, B, C, Reject), Worst Grade, Counter and Result Log, which writes a
  CSV row per inspection. A Run is an inspection (`InspectAsync`); the
  re-runs after an edit are not. Still missing: a **signal out** - a
  PLC over Modbus TCP, digital I/O - and counts that survive a restart.
- **Calibration is done** for the fixtured case: a Calibrate node says
  what a pixel is worth, wired into Measure, which then reports in that
  unit and labels it so. From a known length measured on the image, or
  from a scale typed straight in.

  On a wire rather than as a setting on the recipe, because a scale is
  not a property of a recipe - two cameras have two, and a Resize
  changes one.

  Areas are calibrated too: Shape Table and Measure report them in
  square units. Still open: **lens calibration** from a target grid, for
  optics where a single scale is not enough - not worth building until
  somebody's lens needs it.
- **Circular and polygonal regions.** Rectangles are handled at any angle.

---

## Node library

- **Text and code reading is done**, with real decoders behind it -
  which is what it was waiting for, both earlier builds having shipped
  stubs. Read Code uses ZXing (Code 39/93/128, ITF, Codabar, MSI, Data
  Matrix, PDF-417, Aztec, QR, EAN/UPC) rather than OpenCV's barcode
  module, which reads only EAN and UPC. Read Text uses Tesseract, which
  was already compiled into the Emgu build; the fast English model ships
  with the package.

  Both take an `OrientedRect` and straighten the crop before reading, so
  a code or a label on a turned part reads as if it were square on.

  Still open in that direction: more languages than English, and a
  grading output for 2D codes - print quality against ISO 15415 is what
  a customer with a Data Matrix spec will eventually ask for.
- **Template matching.** Match Template, with Teach keeping the template
  in the recipe. Left: rotation.
- **Camera acquisition.** The Camera node, Run Continuously, a simulated
  camera, and Basler (GigE and USB3) through pylon, behind
  `ICameraDriver`. Left, on hold: Hikrobot (MVS) and plain USB webcams.
  Basler is tested only against pylon's emulator so far.
- **Circle Defects for parts that are not round is done**: Local Form
  and Fitted Ellipse references, and a Form output.
- **Mean intensity over a region is done** - Region Stats.
- **Circular edge defects are done** - Circle Defects.
- **Soft Light** blend mode, left out because several incompatible
  definitions are in common use. Add it when somebody says which one.

---

## Platform

- **Growing inputs on Blend and Logic**, so they could take more than two
  images. The mechanism exists now - `IGrowingPorts`, built for the Image
  Viewer's shape sockets - and these two are the obvious next customers;
  chaining serves until somebody asks.
- **Sub-graphs - done, first version.** A **Sub Graph** node holds a
  graph of its own, opened by double-clicking it into a tab beside the
  recipe's. **Graph Input** and **Graph Output** nodes inside are its
  sockets: added, removed and renamed with them, typed "any" until the
  first wire on either side gives them a type. The inside has its own
  scheduler and cache, so a change inside re-runs only what it affects;
  each tab has its own undo history; viewers, previews, readouts and
  limit checks inside work as they do outside. Saved nested in the
  recipe, so a recipe without sub-graphs is byte-identical to before.

  Left, in rough order of value:

  - **Promoted parameters.** A threshold inside can only be changed by
    opening the sub-graph, so two copies of one cannot be told to differ
    from outside. The way to want this is to have used sub-graphs.
  - **Reusable sub-graphs** - saved as a file and placed in several
    recipes or several times in one, edited once. Today each copy is its
    own.
  - **Group selection into a sub-graph**, turning nodes already on the
    canvas into one, with boundaries where their wires crossed.
  - **Panel titles inside sub-graphs** carry only the node's name, so two
    copies of a sub-graph with a viewer each open two panels with the
    same title.
  - Sub-graph tabs are not reopened when the application restarts, and a
    socket whose boundary was deleted and restored by undo comes back at
    the end of the node rather than where it was.
- **Custom nodes - done, interface 1.0.** See `custom-nodes.md`. Left for
  later: reloading a library without a restart, icons a library supplies,
  IntelliSense for the `Sirius.Core` types in the package, and the Python
  host for algorithms with no C# equivalent.
- **Batch/headless runner - done.** Run Folder in the application, and
  `sirius-run.exe` for scripts, sharing `FolderRun`.

---

## Smaller known gaps

- **10-bit and 12-bit images load, and then look almost black.** Measured,
  in `HighBitDepthTests`. A camera delivers 10 or 12 bits inside a 16-bit
  container, so the values stop at 1023 or 4095 rather than 65535, and
  nothing in the file says which. Two separate problems follow:

  1. **The Image File node defaults to `ImreadModes.AnyColor`**, which is
     8-bit, so the extra bits are discarded on load unless the read mode is
     changed to `Unchanged` or `AnyDepth`. The dropdown is there; the
     default is wrong for anybody who has a high-depth camera.
  2. **The display assumes a full 16-bit range.** `MatImageConverter`
     divides by 257, which is right for true 16-bit and takes a 12-bit peak
     to 16/255 and a 10-bit peak to 4/255 - six and two per cent of full
     scale. It reads as a lighting fault rather than a software one.

  The converter cannot guess: scaling by the brightest pixel would make a
  genuinely dark 16-bit frame jump to full scale. The fix is for
  `ImageData` to carry the significant bit count, set by the source that
  knows it, and for the converter and the histogram to scale by that.

- **Histogram handles 8-bit only.** Other depths warn and pass through.
- **One ROI at a time.** A node with more than one region parameter exposes
  only the first to the viewer.
- **`NumberVector` has no consumer.** The Vector node produces one and
  nothing takes one as input. It takes any number of components now, which
  makes it useful to more things than the old fixed pair would have been -
  a kernel size, an RGB offset, a per-channel threshold - but until
  something reads one, the node is a value with nowhere to go.
- **`Sirius.NodeEditor` is an empty scaffold** - one `Class1.cs`. The canvas
  lives in the app project. Extract it when a second host needs it, or
  delete the project.
- **Find Blobs does not measure** fitted ellipse, inner/outer radius or
  moment invariants. Add them when something consumes them.
- **One icon is weak**: `ph_find_blobs` collides visually with the
  hand-drawn three-point circle.
- Diagnostics are opt-in via `SIRIUS_PANEL_LOG=1`, writing to
  `%AppData%\SiriusStudioPlus\panels.log`. An unreachable panel is always
  logged.

---

## Value nodes, and labels - done

Asked for together, because they are one idea: values that are not images
flowing through a recipe and ending up readable on a picture. All four
shipped; what is left of the idea is below.

- **Number** replaces Integer and Float. The dropdown chooses how the
  value is entered, not what crosses the wire - see `decisions.md` entry
  52 for why a port-type switch would have been machinery with no
  observable effect.
- **Matrix** replaces Vector, with a vector as the 1xN or Nx1 case. The
  2D Filter's kernel can arrive on a wire, which is the first real
  consumer either type ever had.
- **Text** publishes a string.
- **Label** draws a number or a line of text on the image - one per shape
  wired into it, or at a given point.

Still open in that direction:

- **A matrix editor.** A Matrix is typed as text today, which is fine for
  a 3x3 kernel and tiresome for anything larger. A grid editor is the
  obvious next step, and `ParamEditor` already has the shape of a place
  to put one.
- **Text parameters driven by a wire.** A Text node can drive the Label's
  caption, because that parameter is `Connectable`. Nothing else takes
  text that way yet - a file path or an output name would be the first
  real uses.
- **Matrix arithmetic**, if anything ever needs it. There is no Add or
  Multiply for matrices, and inventing them before something consumes one
  would be guessing at what shape they should take.
