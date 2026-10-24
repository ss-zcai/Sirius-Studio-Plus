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

## The standard operation that is missing

**Undo/redo.** Every editor has it, this one does not, and that is not a
refinement to be weighed against features - it is a gap in what the
application is expected to do. Somebody who deletes a node, drags a region
off target, or retypes a threshold over a good one has no way back except
reopening the file and losing everything since the last save.

It is a command stack over `VisionGraph` mutations, and the work is in
deciding what a command is rather than in the stack:

- **Graph structure** - add, delete, connect, disconnect. The easy half:
  each is already a single call on `VisionGraph`.
- **Parameter edits**, coalesced per gesture. A slider dragged across its
  range is one undo, not two hundred, so a command has to be able to absorb
  the next one while the same gesture continues.
- **Positions**, which raise no invalidation but are saved, so they are
  work that can be lost like any other.
- **Measurements and anything else held on a node** that is not a
  parameter. The dirty check catches these by re-serialising; a command
  stack has to be told about each one, which is the mechanism's weakness and
  worth knowing before it is built.
- **Not** evaluation results. Undo restores the recipe and lets the
  scheduler recompute; keeping old buffers to put back would cost more than
  recomputing them.

It wants doing before geometry is dragged rather than typed, because a
dragged caliper makes a wrong edit effortless in a way a typed one never
was.

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

**The viewer.** Pan and zoom with a pixel readout, nine measurement tools as
selectable annotations with their own colours and on-image labels,
interactive multi-channel line profiling with a value cursor, overlay
toggles, shape selection shared with the tables, a region that can be drawn,
moved, resized and turned, and a toolbar that stays on one line by moving
what does not fit into a menu.

**Measurements are part of the recipe.** Drawn lines, angles and circles are
saved with the `.sgraph`, keyed by the node *and the view* they were drawn
on - a preview of an input and one of an output are different pictures - and
they count as an unsaved change, so New and Open ask before discarding
them.

**Node colours.** Seven presets plus the default, for marking up a large
recipe, set from the properties panel or the canvas menu. Each fixes
background, stroke and text together, and the set is checked for contrast
by test rather than by eye - every title clears 7:1 on its own node.

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

Undo/redo has a section of its own, above.

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
- **`Vector2` has no consumer.** The Vector node produces one and nothing
  takes one as input. Either wire it into something or drop the node.
- **`Sirius.NodeEditor` is an empty scaffold** - one `Class1.cs`. The canvas
  lives in the app project. Extract it when a second host needs it, or
  delete the project.
- **Find Blobs does not measure** fitted ellipse, inner/outer radius or
  moment invariants. Add them when something consumes them.
- **Node colours cannot be applied to a selection.** One node at a time,
  which is the wrong granularity for marking up a branch. The menu and the
  swatches both act on the node under them.
- **Two icons are weak**: `ph_zoom_actual` reads as cropping rather than
  1:1, and `ph_find_blobs` now collides visually with the hand-drawn
  three-point circle.
- Diagnostics are opt-in via `SIRIUS_PANEL_LOG=1`, writing to
  `%AppData%\SiriusStudioPlus\panels.log`. An unreachable panel is always
  logged.
