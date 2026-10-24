# Roadmap

Where the product stands at **1.0.1**, and what is left.

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

- **The 1.0.1 package is stale.** `dist/` predates undo/redo, the clipboard,
  node colours, themed dialogs, the growing shape sockets, the per-viewer
  size readout and the hand-drawn fitting icons. Re-run `tools/pack.ps1`
  before anybody tests from a zip again.
- **Decide on merging Integer and Float** into one Number node. The
  analysis is in `decisions.md` entry 49: it costs nothing in connection
  checks, because `VisionGraph.IsCompatible` already treats every numeric
  type as interchangeable and `EvalContext.Param` converts on read. What is
  worth building is one node emitting `double` with a dropdown that
  constrains *entry* to whole numbers - not one that changes the port type,
  which would be machinery with no observable effect. Needs a legacy type
  key mapping so existing recipes open.
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

**40 nodes** across Source, Basic, Combine, Filtering, Morphology, Blob,
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

1. **A Draw button on geometry parameters**, opening the preview of the
   input the geometry is measured in and arming drawing on it.
2. **Find Line.** Calipers over `LineProfileSource`, which already does the
   sub-pixel band sampling a caliper is; the new parts are peak picking by
   polarity and contrast, and a fit with outlier rejection.
3. **Geometry parameters** - `Shape`-valued, with handle editing and
   serialisation. The `Shape` vocabulary built for blob *output* is the one
   a search region needs as *input*. This is also what retires
   `ActiveRegionParameter`, the mechanism that pushes the selected node's
   region to every viewer and lets a region be drawn in the wrong
   coordinate space.
4. **Find Circle and Find Curve** on the same caliper core.
5. **Partial evaluation** - evaluate a tool and its ancestors only, so
   dragging a caliper does not re-run the whole recipe on every frame.

---

## Shapes

- **Filter Shapes**, so any producer's metrics can be filtered generically.
  Combining is done: the Image Viewer grows a socket per shape set, so
  several producers draw on one image without a node between them.
- **Remapping shapes through Crop, Resize and Polar.** A `ShapeSet` reports
  that its frame no longer matches; it cannot follow the image through a
  geometry change.
- **Measurement in the graph.** The viewer measures interactively, but
  nothing emits a number a recipe can pass or fail on. This is not a
  region-drawing problem: it needs a way to *reference* two tools' outputs -
  "the line from Find Line 1, the circle from Find Circle 2".
- **Calibration.** Every reading is in pixels. The scale belongs to the
  recipe, not the viewer.
- **Circular and polygonal regions.** Rectangles are handled at any angle.

---

## Node library

- **Text and code reading** - OCR, Barcode, Data Matrix, QR. Deliberately
  not ported: both earlier builds left them as stubs, with Barcode returning
  a hardcoded result. They need real decoders behind them.
- **Template matching.**
- **Camera acquisition.** Both earlier builds were file-only.
- **Mean intensity over a region**, which belongs with the analysis nodes.
- **Soft Light** blend mode, left out because several incompatible
  definitions are in common use. Add it when somebody says which one.

---

## Platform

- **Growing inputs on Blend and Logic**, so they could take more than two
  images. The mechanism exists now - `IGrowingPorts`, built for the Image
  Viewer's shape sockets - and these two are the obvious next customers;
  chaining serves until somebody asks.
- **Node groups and sub-graphs** for reusable sub-recipes.
- **Plugin loading** from a `plugins/` folder. `NodeCatalog.Register(Assembly)`
  already supports it; discovery, isolation and version checks do not exist.
- **Batch/headless runner** - execute a `.sgraph` over a folder.

---

## Smaller known gaps

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
- **Two icons are weak**: `ph_zoom_actual` reads as cropping rather than
  1:1, and `ph_find_blobs` now collides visually with the hand-drawn
  three-point circle.
- Diagnostics are opt-in via `SIRIUS_PANEL_LOG=1`, writing to
  `%AppData%\SiriusStudioPlus\panels.log`. An unreachable panel is always
  logged.
