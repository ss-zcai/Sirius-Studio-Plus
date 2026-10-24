# Architecture decisions

Decisions taken while starting Sirius Studio Plus, the node-graph successor to
Sirius Studio. Each entry records what was chosen and why, so the reasoning does
not have to be reconstructed later.

---

## 1. The node graph replaces the linear tool rack

**Chosen:** a directed acyclic graph of typed nodes; no list-based editor ships
alongside it.

The old `IVisionTool` had exactly one `Mat InputImage` and one
`Mat OutputImage`, plus `object InputObject` / `OutputObject` as an untyped
escape hatch. That shape is why the old pipeline could only ever be a straight
line — `MainForm.Process()` walked an `ObservableCollection<IVisionTool>` in
order. A straight chain is still expressible as a graph, so nothing is lost.

---

## 2. UI stack: license-free WPF

**Chosen:** .NET 8 WPF, [Nodify](https://github.com/miroiu/nodify) 7.3 (MIT) for
the canvas, hand-written shell and inspector.

DevExpress is not installed on the development machine, and the version the old
project referenced (**v20.1**, 2020) predates .NET Core WPF support — it cannot
target .NET 8 at all. A current DX WPF licence is a different SKU from WinForms
unless the subscription is Universal.

Note the old app used `DiagramControl` as an *image annotation canvas* (ROI
rectangles, measurement lines, intensity profiles), not as a node editor. The
node editor and the annotation overlay are separate concerns and both are needed.

---

## 3. Custom inspector instead of a reflection property grid

**Chosen:** `[Param]` attributes carrying UI hints, rendered by
`ParameterViewModel`.

The old UI hints lived in `MainForm.toolPropertyGridControl_CustomRowCreated`,
which switched on `FieldName` string literals:

```csharp
if (e.Row.Properties.FieldName == "Threshold" || ... == "MaxValue")
{
    row.MinValue = 0; row.MaxValue = 255; row.ShowTrackBar = true;
}
```

Any new tool with a differently-scaled `Threshold` would silently inherit a
0–255 trackbar. Putting range, step and editor kind on the property removes the
coupling between a node's parameters and the shell.

---

## 4. Buffer ownership is explicit

**Chosen:** a node never disposes its inputs; the scheduler disposes cached
values it replaces or evicts. Pass-through values are flagged *unowned*.

The old tools never disposed a single `Mat` — every parameter change leaked
native memory, and `NormaliseTool` additionally leaked its `blurred`
intermediate on each call. The error path `OutputImage = InputImage` also
aliased the input, one refactor away from a double free once disposal existed.

`PortValue.IsOwned` encodes the distinction. Covered by
`Superseded_output_is_disposed` and
`Replacing_a_forwarded_value_does_not_dispose_the_upstream_buffer`.

---

## 5. Incremental, cancellable, off-thread evaluation

**Chosen:** dirty-set propagation over a topological order, run on a worker
thread, with the in-flight pass cancelled when a newer change arrives.

`MainForm.Process()` re-ran *every* tool on *every* parameter change, on the UI
thread. Dragging a slider over a large image froze the window and redid work
that had not changed.

A ~40 ms debounce in `MainViewModel` coalesces slider drags so each movement
does not start a pass the next one cancels.

---

## 6. Structuring elements are described, not stored as `Mat`

**Chosen:** morphology nodes expose `Shape` + `KernelSize` + `Anchor` as
serialisable scalars and build the element per evaluation.

The old tools held `public Mat Kernel { get; set; }`, which the property grid
could not meaningfully edit — its editor button opened a message box reading
`"Kernel Setting"` — and which no file format could round-trip.

---

## 7. Graphs are serialisable; nodes have stable type keys

**Chosen:** JSON (`.sgraph`), nodes referenced by `TypeKey` (e.g.
`basic.threshold`) rather than CLR type name.

The old app could not save a pipeline at all. Stable keys mean node classes can
be renamed or moved between assemblies without breaking saved recipes. Unknown
node types and unknown parameters degrade to warnings instead of failing the
load.

---

## 8. OpenCV binding: Emgu.CV 4.13

**Chosen:** `Emgu.CV` 4.13.0.5924, the latest 4.x.

Considered: Emgu.CV 5.0.0.6584 (published 2026-09-24, wraps the OpenCV 5 major
version — three weeks old at the time of the decision) and OpenCvSharp4
4.13 (Apache-2.0).

Note `ElementShape` was renamed to `MorphShapes` between 4.3 and 4.13.

### ⚠ Open item: Emgu CV licensing

**Emgu CV is dual-licensed GPLv3 / paid commercial.** Distributing Sirius Studio
Plus to customers under the GPLv3 option would require releasing its source.
When asked, the answer on whether a commercial Emgu licence is already held from
the original Sirius Studio was **"no / not sure"**.

This was raised and the decision to proceed on Emgu was taken deliberately. It
is recorded here because it needs resolving before the product ships, not
because it blocks development.

The exposure is contained: Emgu types appear only inside `Sirius.Vision` node
implementations and `MatImageConverter`. `Sirius.Core` has no OpenCV dependency.
A move to OpenCvSharp (Apache-2.0) would be a mechanical rewrite of the node
bodies, not an architectural change.

---

## 9. Mat → `BitmapSource` directly

**Chosen:** write from the `Mat` buffer into a `WriteableBitmap` via
`WritePixels`, then freeze.

The old code called `Mat.ToBitmap()` and handed a `System.Drawing.Bitmap` to a
WinForms control. Going through `System.Drawing` in WPF means an extra copy plus
a GDI handle to release on every frame. Freezing lets the bitmap cross threads.

Non-8-bit inputs are scaled for display: 16U is divided down, floating point is
min/max stretched.

---

## 10. Display nodes terminate a branch

**Chosen:** Image Viewer has no output at all; Histogram and Line Profile
expose their measurement but not a pass-through image.

A viewer presents an image, it does not transform one, so there is nothing
meaningful to pass on. Previously a viewer placed mid-chain became a link in
the pipeline, and deleting it broke the chain. Watching a mid-graph image is
now done by branching the producing output to a viewer as well as the next
stage, which the graph supports directly.

A node with no output cannot publish the image its panel needs, so
`GraphScheduler.GetInput` follows a connection back to the producer.

---

## 11. One panel per node, not one shared viewer

**Chosen:** every Viewer, Histogram and Line Profile node owns a dockable
panel, titled by the node name.

The shared viewer followed the selected node, which made the displayed image a
side effect of selection: clicking a node to edit its parameters silently
changed what you were looking at. Several panels also let two points in a graph
be compared at once, which is the reason to have a graph rather than a chain.

`NodePanelViewModel` is the one abstraction - a new panel-producing node needs
a view-model and a `DataTemplate`, nothing more.

### Placement, which took three attempts

Panels are attached to the layout by `PanelLayout`, deliberately split from the
window so the rules are testable. Two behaviours cost real time:

- **AvalonDock prunes empty containers** as they join a live layout. A pane
  attached before it is filled is discarded, and whatever is added afterwards
  is orphaned - silently, because adding to a detached pane still succeeds.
- **A restored layout wraps the document pane in a `LayoutDocumentPaneGroup`**,
  which accepts only document panes. Inserting an anchorable pane into it is
  rejected without error. Placement now climbs to the nearest real
  `LayoutPanel` before positioning.

Neither reproduced in a detached layout model, and the first two tests written
for them passed against the broken code. What found it was logging reachability
from the running app.

---

## 12. Parameters can be driven by connections

**Chosen:** a parameter marked `Connectable` also appears as an optional input
port. Wired, the upstream value wins; unwired, the inspector value is used.

This is what lets a threshold be a fixed number in one recipe and be driven by
a measurement in another, without two different nodes. Numeric types are
interchangeable across a connection, so an integer output can drive a double
parameter without an adapter.

Parameter ports are built lazily on first access, not in the constructor: the
base constructor runs before the derived one declares its data ports, so
building them eagerly would put tuning sockets above `In`/`Out`.

---

## 13. Only one evaluation pass at a time

**Chosen:** `GraphScheduler.RunAsync` throws if a pass is already running, and
the run loop cancels *and awaits* the previous pass before starting the next.

Cancellation is cooperative - the scheduler only checks between nodes - so
cancelling and immediately starting another pass put two passes on one result
cache, where one disposed a buffer the other was reading. Because the buffers
are native OpenCV memory, that surfaced as an `AccessViolationException` that
killed the process rather than an exception anything could catch.

The contract had been written in a comment since phase 1 and was violated
anyway. It is now enforced in code.

---

## 14. Icons are generated from SVG, preserving colour

**Chosen:** `tools/svg2xaml.py` emits a `DrawingImage` per icon into
`Themes/Icons.xaml`; nodes name an icon by key.

An earlier version flattened each file into a single tintable `Geometry`. That
suited the monochrome toolbox set but would have discarded the toolbar set,
which is multi-coloured. Node-to-icon assignments for the ported tools come
from `Properties/Resources.resx` in the original app rather than being matched
by eye.

Two traps worth remembering, both caught by tests now:

- SVG lengths carry units. A stroke width of `.5px` is valid SVG and not a
  valid WPF `Double`, and a `ResourceDictionary` realises entries lazily - so
  it failed only when that icon was first shown.
- An icon with no explicit bounds scales to its own artwork, so glyphs that do
  not reach the edge of the viewBox render larger than their neighbours. Each
  drawing gets a transparent rectangle over the full viewBox.

---

## 15. AvalonDock 4.74, not 5.0

**Chosen:** `Dirkster.AvalonDock` 4.74.1.

5.0.1 is the latest stable and **builds fine**, but ships no .NET 8 lib: NuGet
falls back to its .NET Framework 4.8 assemblies under warning NU1701, which
would fail at runtime rather than at compile time. It had also dropped
`XmlLayoutSerializer`, which the layout persistence uses. 4.74.1 resolves
cleanly to its `net5.0-windows` lib.

A package that restores with NU1701 is not a package that works.

---

## 16. Measurements are annotations, placed by gesture

**Chosen:** a list of `Measurement` objects, each placed by a per-tool sequence
of click and drag gestures.

The first version held one `List<Point>`: clicks accumulated, the reading went
to the status bar, and the next click wiped it. That makes comparison
impossible - you cannot check a gap against the one next to it - and it hides
the answer away from the thing being measured.

Three consequences shaped the design:

- **Readings are drawn on the image.** The label is a counter-scaled `Border`,
  so it holds its size on screen instead of growing with the zoom. The status
  bar keeps the reading for the selected measurement and for the one under the
  hand, which is a different job.
- **Colour is per measurement, not global.** Amber vanishes on a bright part.
  The picker follows the selection, so a reading already lost against the image
  can be recoloured rather than deleted and redone.
- **Each tool is operated the way the measurement is taken.** A span is a drag,
  not two clicks: press at one end, drag, release at the other, with the
  reading live under the cursor. The parallel-line tool is a caliper - the drag
  *is* the separation, and reference lines mark what it was taken across. Only
  tools with no natural direction (angle, three-point circle) stay click-based.

A press that never moves does **not** commit a zero-length measurement; it
places the near point and waits for the far end as a second click. A drag
threshold measured in screen pixels means a slip of the hand cannot produce a
reading of nothing.

Radius and diameter split into two tools rather than one with a mode. Centre
plus radius and three points on the rim are different gestures reporting the
same pair of numbers, and a mode switch would hide that in a dropdown.

Measurements are cleared when the frame size changes. They live in image
coordinates, so a different frame means they point at features that have moved.

Once placed they stay editable: handles on the selected measurement drag
individual points, and dragging its body moves it whole. The first click only
ever selects - moving needs a drag on something already selected - so reading a
value cannot nudge it off the feature it was taken from. Readings and label
positions are therefore derived on read rather than captured at placement.


---

## 17. The line profile is a viewer tool as well as a node

**Chosen:** one sampler, `LineProfileSource`, used by both `LineProfileNode`
and a profile tool in the image viewer.

A line typed into a node as four numbers is in *some* image coordinate space,
and nothing says which. Put a Crop or a Resize upstream of the viewer you drew
on and the numbers mean something else, with no error - just a profile of the
wrong place. A line dragged on the viewer has no such ambiguity: it is on the
image in front of you.

Sharing the sampler is the part worth defending. Two implementations of
sub-pixel sampling would drift, and the drift would be invisible - both would
return plausible curves. A test asserts the node and a direct sample agree.

The viewer keeps a prepared single-channel float copy of its frame, built in
`Prepare` while the graph buffer is still alive. Holding the `ImageData`
instead would be a use-after-free: the scheduler reclaims it when the pass
ends. The copy costs four bytes per pixel, so it is only kept while a profile
line exists or the tool is selected.

### The fault this uncovered

A `ComboBox` with a pre-selected item raises `SelectionChanged` while the
template is still being built, so a handler ran with only part of the tree in
place and dereferenced a control that did not exist yet. Guarding on a
different named element - the one checked happened to be a parent, created
earlier - was no protection at all. Hence a single `_templateReady` flag
rather than per-element null checks.

Far worse was how it reported itself. The exception escaped into a layout pass;
`App.OnUnhandledException` showed a `MessageBox`; the modal pump ran the
pending layout callback; that threw again, showed another dialog, and the
process died of a stack overflow - which cannot be caught and prints only
frames, so the original exception was lost. Diagnosing it took eight build
cycles of bisection.

The handler now refuses re-entry and writes the exception to stderr as well as
showing it. With that in place the same fault reproduces as one logged
`NullReferenceException` with a three-frame stack naming the cause, and the
application stays up.


---

## 18. A profile line is a named annotation with a window of its own

**Chosen:** each profile line drawn on the viewer gets a name, drawn beside it
on the image, and a dockable panel of that name holding its chart.

The strip under the image was the obvious first shape and the wrong one. Only
one profile could be seen at a time, two could not be compared, and it could not
be floated onto a second screen - which is much of what a profile is for.

This needed a panel belonging to no node. `PanelViewModel` is now the base the
docking layout works against, with `NodePanelViewModel` the case that happens to
have a node behind it. The viewer owns the lines and pushes readings into the
panels; the shell only mirrors the collection into the layout, which is the one
thing the viewer cannot reach.

The line is labelled with its **name** rather than its length, because its
reading is the chart and the name is what ties the two together. The length
stays in the status bar.

They are called "Profile 1", "Profile 2" rather than "Line Profile 1": the Line
Profile *node* already takes that name for its own panel, and two windows with
one title would be unreadable in a tab strip.

Panel identity is a fresh id per line, never restored from a saved layout.
Measurements do not outlive the frame they were drawn on, so a restored window
would have nothing to show.

### Two theme notes

Band widths are bound as plain numbers with `ItemStringFormat`, not as
hand-written `ComboBoxItem`s holding `TextBlock`s. A generated item inherits the
theme foreground through its `ContentPresenter`; a hand-written TextBlock does
not reliably do so in the *closed* selection box, and came out black on the dark
panel. The colour picker gets away with it only because every one of its labels
sets an explicit colour.

The profile chart now draws axes - intensity up the left, distance along the
bottom - with tick intervals rounded to 1, 2 or 5 times a power of ten. Dividing
the range into equal parts gives labels like 37.4 and 74.8, which no one can
interpolate between. The intensity range comes from the data rather than a fixed
0-255, because a profile may come from a 16-bit or float image.


---

## 19. The image sequence steps by parameter, not by playback

**Chosen:** `ImageSequenceNode` exposes the frame number as an ordinary
`[Param]`, with an editor kind that adds previous/next controls.

The alternative was a transport of its own - play, stop, a current-frame
notion owned by the shell. That would have needed a second way to make the
graph re-run, parallel to the one parameters already use. As a parameter the
frame invalidates the node like any other value, so viewers, histograms and
profiles follow with no extra plumbing.

`ISequenceNode` carries only what a parameter cannot: how many there are, and
which one is current. Both are derived from the folder listing rather than
stored, so neither is written into a recipe where it would go stale against a
folder that has since changed.

The path accepts **either a folder or any image inside one**. Accepting both
means the ordinary file dialog picks "the first image", which is how an
operator describes a sequence, while a pasted folder path still works.

Files are ordered naturally, comparing runs of digits as numbers. Ordinal
sorting puts "frame10" between "frame1" and "frame2", which scrambles exactly
the numbered sequences this node exists to walk; leading zeros are treated as
padding, so "007" and "7" sort together.

The listing is taken when the path changes, not on every evaluation. Re-reading
it mid-run would let the sequence shift underneath a comparison that is
half-finished.


---

## 20. Shapes are port data, and blobs are the first producer

**Chosen:** a `Shape`/`ShapeSet` type in `Sirius.Core`, emitted from an output
port and consumable anywhere - including a viewer's new `Shapes` input.

An "annotation" is not a mechanism here. It is a shape wired into a viewer.
The old app's `List<SvgElement> Annotations` was a side-channel hanging off
each tool; making shapes ordinary port data means the graph's own rules -
typing, one connection per input, evaluation order - already govern them, and
no drawing code needs to know what produced what.

Measurements ride on a shape as a **name-keyed bag** rather than typed fields.
A blob carries area, circularity and solidity; a found line would carry length
and angle. Keying by name is what will let a generic *Filter Shapes* node
select on any producer's output without knowing the producer.

A `ShapeSet` records the **frame size it was measured in**. Shapes found on a
full image and drawn over a cropped one would land somewhere they were never
measured, and nothing else would say so. The viewer dims them and explains.
Remapping shapes through Crop and Resize is the real fix and is not built.

### The blob finder

Rebuilt from the plan in `blob-analysis-plan.md`, after reading how HALCON and
Cognex VisionPro do it. Three things taken from them:

- **One measure list serves filtering and sorting.** Cognex uses a single
  enumeration for both. Two vocabularies would be two places to add each new
  measure.
- **A maximum count needs an ordering**, or "keep 50" means nothing. Sorting
  includes reading order, because an inspection that matches blob *n* to
  feature *n* needs stability that sorting by size cannot give. Ties break on
  position for the same reason.
- **Touching the border is a filter.** A part cut off by the frame cannot be
  measured. Both tools expose it; it is the one most easily forgotten.

Measures are computed in **cost order, and only when asked for**: the convex
hull behind solidity is never built unless a solidity filter or sort is set. A
test asserts the unused ones leave no trace on the output.

Worth stating plainly, because it is counter-intuitive: **the cap bounds the
output, not the work.** Ranking requires measuring everything. What bounds the
work is the area filter, applied first.

"Circularity" is ambiguous across packages - HALCON's is A/(pi r_max squared),
Cognex's acircularity and HALCON's compactness are both P squared/(4 pi A).
This project uses 4 pi A/P squared, 1.0 for a circle, and says so in the
parameter description so there is nothing to guess.

### The blob tool thresholds its own input

Corrected after first use. The node was built to take a binary image, with a
Threshold node upstream, which is how HALCON separates `threshold` from
`connection`. Fed a grey image it then found *one* region covering the whole
frame, because `FindContours` treats every non-zero pixel as foreground and a
photographed background is never actually zero.

The failure mode is what makes this worth a rule: it did not look like a
failure. An outline was drawn, a count was reported, and both were wrong.

**Cognex VisionPro thresholds inside the blob tool**, and that turns out to be
the right call, because it is also what the operator expects - choosing
"Light" polarity reads as an instruction to find the light things, not as a
hint about an image someone else was supposed to have binarised.

So segmentation is now part of the node: Automatic (Otsu) by default, Fixed,
or None for an input a Threshold node has already handled. Automatic is safe
even downstream of a Threshold node - Otsu on an already-binary image picks a
level between the two peaks and changes nothing.

The general lesson, and the reason it is written down: a tool that silently
produces a plausible wrong answer is worse than one that refuses. Where a
default can remove the trap entirely, it should.


---

## 21. Selection is shared state, not a property of a shape

**Chosen:** a `ShapeSelectionService` keyed by the **producing output port**,
observed by every panel showing that wire.

The question that prompted it was whether the viewer needed to be passed the
blob table so each blob could be one selectable object. It does not: the
scheduler hands the viewer and the table the *same* `ShapeSet` instance, so
they already show the same blobs in the same order. The only thing missing was
agreement on which one is selected.

Selection deliberately does **not** live on `Shape`:

- Shapes are port data, rebuilt every evaluation. A flag there would be wiped
  by the next pass.
- One shape set is handed to every consumer of that output, so a flag would
  leak between panels that should be independent.
- It would mean the UI writing into values the graph owns.

Keying on the producing port - rather than on the panel - is what makes two
viewers on one blob finder agree, while a second finder keeps its own
selection.

### Selection is positional, and that is a real limit

It remembers an index, not a blob. Nothing about a found region identifies it
across evaluations, so when the part shifts and the set re-sorts, index 5 may
be a different hole. Sorting the producer by Position is what makes the number
mean the same feature from one part to the next. Worth knowing before trusting
a selection across a sequence.

A stale index is dropped when a shorter set arrives, rather than left pointing
past the end.

### Numbering earns its place

Shapes are numbered on the image, matching the table's row numbers. That alone
answers "which blob is which" without any selection at all, and it is what
makes the shared selection legible once there is one. Numbering stops above
150 shapes: past that the labels overlap into noise, and drawing thousands of
text elements would cost more than the evaluation did.


---

## 22. Styling is a graph operation; the ROI follows HALCON's two modes

**Chosen:** `ShapeStyleNode` restyles a shape set, and `RegionOfInterestNode`
cuts a region out, straightening it when it sits at an angle.

### Styling in the graph, not in the viewer

Colour, weight, form and labels are set by a node rather than by a viewer
setting, because they are part of the result. Two finders can draw in two
colours on one image, and a pass/fail branch can colour the same features
differently, with the viewer knowing nothing about either.

Reducing a blob to its bounding or minimum-area box is more than decoration: a
thousand outlines of a thousand points each is a lot to look at, and four
corners often says what is needed.

The minimum-area rectangle is recomputed from the outline rather than read
from the shape's measurements, because the producer only takes those when a
filter asks - a blob finder with no aspect filter has no rectangle to report.

### The region of interest

The two modes come from HALCON's `crop_rectangle2`: aligned to the axes, which
must interpolate and fills outside the image; or the smallest surrounding
upright box, which resamples nothing and is clipped to the image. Straightening
itself is what Cognex's Affine Transform tool does - hand the next tool an
upright picture of a tilted thing.

The affine maps source straight to output rather than rotating the whole image
and cropping. On a large frame that is warping a few hundred pixels instead of
millions.

**The region can come from a wire.** Feed the node the shape of a located part
- Find Blobs, Select Shape, Shape Style set to Minimum Rect - and the cut
follows the part from frame to frame. That is Cognex fixturing in miniature,
without needing a coordinate-space system to express it. A test wires exactly
that chain and asserts the cut-out is the part, upright, with no centre or
angle typed anywhere.

### A 90-degree ambiguity worth normalising

OpenCV's minimum-area rectangle may label either side as the width, describing
the same rectangle two ways 90 degrees apart. Left alone, a fixtured cut comes
out landscape or portrait depending on which labelling happened to fall out of
the contour. The long side is therefore always the width, with the angle
following it.

This surfaced as a test failure - a cut 41 wide where 160 was expected - which
is the kind of thing that would otherwise have been found by someone wondering
why their part was sideways every other run.

### Angle convention

Degrees, turning clockwise as seen on the image, matching what OpenCV's
minimum-area rectangle reports. A sign error here produces a picture wrong by
exactly twice the angle, which looks like a tilted crop rather than a bug, so
the convention is pinned by tests at five angles including negative ones -
plus one asserting that a region 90 degrees out of true *misses* the feature,
so the others cannot pass by accident.


---

## 23. One palette, offered as swatches

**Chosen:** a `ParamEditor.Colour` editor backed by a single shared list,
`StandardColours`.

A hex field is a poor way to choose a colour: it cannot be browsed, and
nothing stops two places offering different greens. The parameter now shows
swatches, with the hex beside them for anything outside the list.

The viewer measurement picker was rebuilt on the same list rather than left
with its own hand-written items. A measurement and an annotation set to the
same green now match, because there is only one green. The viewer default
colour is read from the palette too, so it cannot drift from the swatch it is
supposed to be.

A value outside the palette selects **nothing** rather than the nearest
swatch. Showing a close one would suggest the value had been changed to it.

Two details that are not arbitrary:

- **Items are bound, not hand-written.** A generated item inherits the theme
  foreground through its ContentPresenter; a hand-written TextBlock does not
  reliably do so in the *closed* selection box, which is how the band picker
  came out black on a dark panel.
- **Black is named in grey.** Its swatch is black, but its label would be
  invisible against the panel. A test pins that, and pins that every other
  colour *is* named in itself.


---

## 24. The viewer draws labels, it does not invent them

**Corrected after use.** The viewer numbered every shape it was given. Once
the Shape Style node existed, setting its Labels to None changed nothing on
screen, because the viewer put its own number back.

Two things were controlling labels and the wrong one won. A label is data on
the shape, set by a node; the graph decides what a result says and the viewer
draws it. Automatic numbering was added before the styler existed and should
have gone when it arrived.

The viewer now draws a label only when the shape carries one, in the shape's
own colour so a styled set reads as one thing.

**One exception, and it is not an exception to the rule.** The selected shape
is numbered even when unlabelled. That number is not a property of the shape -
it is the row it corresponds to in a table, which is selection feedback rather
than a result. Without it, selecting one of twenty-five identical blobs tells
you nothing.

Labelling had no tests, which is how a dropdown that did nothing visible got
shipped. It has them now, including that None produces none.


---

## 25. Measure everything by default, and still stage the work

**Corrected after use.** Measures were taken only when a filter or the sort
asked for them, so a recipe with every filter at -1 produced a table of area,
box and holes - and nothing to choose a filter range from. You cannot set a
circularity limit for a number you have never seen.

Cognex has three measure modes - filter, **computed**, ignored. Only filter
and ignored were built; the middle one is what was missing.

A single "Measure Everything" switch covers it, defaulting **on**, rather than
one checkbox per measure. The per-measure form is more faithful but puts eight
more rows into an inspector that is already long, and the setup case wants all
of them anyway.

**The staging survives.** Cheap rejection still runs first - a blob dropped on
area costs nothing further - and only survivors are measured in full. What
changed is which measures a survivor gets, not the order the work happens in.
The tests that pin the staging now turn the switch off to ask for the lean
path, which is the honest way to keep testing something that is no longer the
default.

The tension is real and worth stating: the switch trades setup legibility
against speed on an image with thousands of blobs, where the convex hull
behind Solidity is the expensive one. Default to being able to see; turn it
off when the count makes it hurt.


---

## 26. A restored layout brings new anchorables, and they must be adopted

**Bug, found by opening a project then choosing New.** The graph emptied but
every viewer and table stayed on screen, and opening the same project again
produced a second set beside them.

Deserialising a layout does not reuse the existing panels - it builds **new**
anchorable objects. The window was still holding the ones it created before
the restore, which by then were detached orphans. So the panels on screen
belonged to nobody: nothing could close them when their nodes went away, and
nothing recognised them when the same ids came back, so duplicates were made.

The window now re-reads the node panels out of the restored tree. The shell's
own panels are excluded by the same rule that already distinguishes them - a
node panel's id carries a colon - because adopting the toolbox into the set
of panels-owned-by-nodes would let a later pass close it.

A second fault fell out of the same cause: a panel's title followed its node
through a subscription captured when the anchorable was created, so after a
restore renaming a node retitled the thrown-away anchorable. The subscription
is now made once per panel and looks up the current anchorable when it fires.

The lesson is narrow but sharp: **after deserialising, every reference to the
old tree is stale.** Identity lives in the content ids, not in the objects.

## 27. A tool that needs a frame must ask for one when it is picked

Interactive line profiling reads from a copy of the frame, taken during an
evaluation pass. It has to be a copy: the graph reclaims its buffers when a
pass ends, so holding the `ImageData` would be a use-after-free. The viewer
panel therefore keeps the switch (`IsProfilingEnabled`), raises a request
when it is turned on, and the shell answers with a pass.

The switch was never thrown. `OnActiveToolChanged` reset the gesture, redrew,
updated the hint and the cursor - and did not touch profiling. So picking the
tool captured nothing, and putting it away released nothing: the frame stayed
alive after the last line was deleted, and the windows belonging to deleted
lines stayed open.

Three things made this hard to see rather than obvious:

- Drawing a line *did* throw the switch, by a different route, so the feature
  half worked and the first chart filled in a beat late.
- Every layer passed its own tests. The panel went from "profiling on" to a
  sampled frame correctly; the shell answered a request correctly; the
  control charted correctly once it had a frame. Nothing tested whether the
  layers ever *asked* each other.
- The failure reads as "no data", which is also what an empty viewer and a
  zero-length line look like.

So the fix came with a test that spans the whole loop - control to panel to
scheduler and back through the binding - and answers the request on a later
dispatcher pass rather than inline, because answering it synchronously would
pass whether or not the application does.

A second, narrower fault turned up while pinning this down. `Prepare` reads
the switch on a worker thread while the UI thread flips it, so a pass already
past `Prepare` commits no frame even though profiling is now on. `Commit`
now asks again when it finds itself in that state, which costs one pass and
terminates: the next `Prepare` sees the switch. It is guarded on the panel
having an image, or a viewer with nothing wired into it would ask forever.

**The lesson: when a feature spans layers, test the joins, not the layers.**
Both halves working is not the same as the feature working.

## 28. A chart floats; it does not take a tab from the image it measures

`PanelLayout.Attach` puts a new panel in whichever pane the node panels
already occupy, which is right for a viewer or a table - they are part of the
arrangement the operator built. It is wrong for the chart behind a profile
line. That arrived as a tab in the pane holding the viewers, which on a
typical layout means it covers the very image the line was drawn on, and on a
crowded one it is simply hard to find.

Profile charts now open floating, cascaded so two do not land on top of each
other. Floating also keeps them out of the docking tree the viewers share, so
opening one cannot re-select or re-arrange their tabs.

The first attempt at this shipped and showed **no window at all**, which is
worth recording because every check passed. Adding a
`LayoutAnchorableFloatingWindow` to `Layout.FloatingWindows` builds a model
that says there is a floating window holding the panel: it is consistent, it
throws nothing, and every assertion that reads the model back is satisfied.
Only the docking manager can create the window, and it was never asked.
`AddToLayout` followed by `Float` - the path a panel dragged out by hand
takes - is what asks it.

So the test for this drives a real `DockingManager` in a real window and
asserts on `DockingManager.FloatingWindows`, the manager's own list of window
controls, because that is the only thing that distinguishes the two
implementations. It is a single case: AvalonDock keeps static state across
docking managers, and floating twice in one process reuses a window control
the first float closed. The manager also shows the window on a later
dispatcher pass, so the test waits for it - and that wait is the whole
difference between a passing test and one that tears the window down out
from under a deferred `Show`.

That has a consequence for saved layouts. A profile line's identity is a
fresh guid each session - the lines do not outlive the frame they were drawn
on, so there is nothing for a restored window to show - and the restore
already cancels content it cannot match. Cancelled content inside a floating
window would leave the window itself behind, empty, so the restore prunes
floating windows that end up holding nothing.

## 29. A recipe is framed when it opens, not left at the origin

Node positions are stored in the recipe's own coordinates, and a graph built
by dragging nodes around drifts: this project's test recipe has its nodes
past x=3340. The editor opens at the origin, so the recipe loaded correctly
onto an apparently empty canvas and had to be found by scrolling.

The editor now frames the nodes when a recipe loads. Two details earn their
keep:

- **Zoom is capped at 1:1.** A fit-to-window on a two-node recipe magnifies
  it to fill the screen, which reads as a fault rather than a convenience.
- **It runs a dispatcher pass later.** The editor reports an extent only once
  it has realised an item container per node, and a graph assigned a moment
  ago has none. An editor with no containers reports a zero-area extent, so
  that case is treated as nothing to frame rather than as a point to centre
  on - otherwise an empty recipe would open half a screen from the origin.

The arithmetic lives in `GraphView.Frame`, away from the window, so it can be
tested without a NodifyEditor and without a layout pass.

## 30. One user-interface thread for the tests, as the application has

Adding a test that shows a real window turned the suite from "fast and
boring" into a hang, then a crash, then a different crash. Each cause is
ordinary on its own and they are worth listing, because the next WPF test
will meet them again:

- **A thread per case.** WPF keeps static, thread-affine state -
  `Application.Current` is one per process, and AvalonDock's focus manager
  holds dispatcher references across docking managers. Given a second UI
  thread it calls back into the first, which has gone. The call is blocking
  and it happens inside a window procedure, where nothing can catch it, so
  the test *host* dies instead of a test failing. There is now one UI thread
  for the whole collection, living as long as the collection, running a real
  message loop.
- **No message loop.** Closing a window queues work. With no loop that work
  never runs, the window is never torn down, and the next one waits on it
  for ever.
- **`ShutdownMode.OnLastWindowClose`**, the default. The first case to close
  its window shut the application down; after that `Application.Current` is
  null, the next case built a second `Application`, and WPF refused.
- **Deferred `Show`.** AvalonDock shows a floating window on a later
  dispatcher pass. Asserting and tearing down immediately runs that `Show`
  against a window already closed.

Two habits came out of it. The harness waits on a **bounded** join, so a
deadlock fails by name instead of stopping the run, and its thread is a
background one so a wedged case cannot hold the process open. And anything
touching a window **settles** the dispatcher - invoking at `SystemIdle`,
repeatedly, which waits for every higher priority and pumps the message
queue - rather than assuming a pass has happened.

## 31. Writing a bound property its own value throws the binding away

The profile chart stayed empty through three rounds of fixing, and the cause
was one line in the viewer:

```csharp
IsProfilingEnabled = wanted;      // runs on every sync
```

`IsProfilingEnabled` is two-way bound to the panel, and that is how the
control tells the panel a frame is needed. Writing the property the value it
**already holds** replaces the binding with a plain local value: WPF takes
the shortcut for an unchanged value, and the binding does not survive it. A
write that actually changes the value goes through the binding and reaches
the source, which is why the mechanism looked sound whenever it was examined
directly.

The sequence was: the first profile source arrives, the control syncs itself,
the sync writes `false` over `false`, and from that moment the property is
dead. Every later "profiling is on" went into it and nothing came out. The
panel was never asked for a frame, so there was nothing to sample, so the
chart said "No profile" - permanently, and identically to having no line at
all.

The fix is to write it only when it changes. That is not a micro-optimisation
here; it is the difference between a live binding and a dead one.

Three attempts failed to find this because each tested a layer, or tested the
join with bindings written **by the test**:

- the panel captured a frame correctly;
- the shell answered a request correctly;
- the control charted correctly once it had a frame;
- the control told a hand-wired panel correctly.

What none of them used was the markup that ships. The binding that broke is
declared in `Dark.xaml`, and a test that writes its own equivalent cannot see
a fault in how the real one is treated. There is now a case that builds the
viewer through the application's own `DataTemplate`, by handing a
`ContentPresenter` the view-model, and it asserts the binding is **still
there** after the first frame arrives - not only that the value is right.

**The lesson: test through the composition that ships.** A hand-wired
equivalent of production markup tests the parts and quietly assumes the
assembly.

A trace file came out of this too, on the existing `SIRIUS_PANEL_LOG=1`
switch: the chain now records which layer asked which, and prints object
identities, because "no data" looks the same whichever link is broken.

## 32. A colour profile is one trace per channel

Profiling reduced a colour frame with `Bgr2Gray` and charted the result, so
three very different channels arrived as one middling trace. That throws away
exactly what a colour inspection is looking for: a red fiducial on green
solder resist is a strong signal in two channels and nearly nothing in
luminance.

Every channel is now sampled and charted, drawn in its own colour, with the
per-channel statistics beside a swatch in a legend. A greyscale frame still
gives one trace and the header it always had, so nothing changed for the
common case.

Two things worth recording:

- **The geometry is sampled once, the channels many times.** The maps, the
  bounds check and the band are built per line, not per channel, so a gap
  falls at the same index in every trace. Three traces that disagreed about
  where the line left the image would be worse than useless.
- **The cost is four bytes per pixel per channel**, held while the viewer's
  profile tool is in use - three times what it was for a colour frame. That
  is the reason profiling stays off until asked for.

### On naming the channels

`ImageData` records how many channels a frame has, not what they mean, so the
names come from the one convention the engine already follows everywhere:
OpenCV's, where three channels are blue, green, red in that order. That
assumption now lives in exactly one place, `ProfileChannels.NamesFor`.

Doing better means tagging `ImageData` with a colour space. The Colour
Convert node knows precisely what it produced and currently discards it, so
the information exists and is thrown away - but adding the tag touches every
node that creates an image, and nothing today reads it. Until something does,
an HSV image profiles correctly and is labelled B/G/R: wrong, and wrong in
one findable place rather than scattered.

## 33. A preview belongs to a port, not to a node

Interactive tools - find line, find circle, the measurement tools - need a
picture to be configured on. The question was whose picture.

Region editing today answers it by accident: the **selected** node's region
parameter is pushed to **every** viewer panel, and the operator draws on
whichever looks useful. Nothing checks that the viewer is showing the tool's
input. In the TestPCB recipe that means a region for a tool fed from RGB
Channel can be drawn on the rotated board, and the app accepts it - the
rectangle is stored in one coordinate space and was chosen in another. It
works while no node between them rotates, crops or resizes.

A preview bound to a **port** shows, by construction, the pixels that port
carries, so a point clicked in it is a point in that port's space. The
question stops being askable.

Binding to the port rather than the node also answers two things a node-level
preview could not. A node with several image ports - Find Blobs has an image
out, a count and a shape set - has no single "its" image. And a shape port
gets drawn over the node's own image **input**, because there is nothing else
to draw it on, which is the long-standing answer to "what is the out image of
a blob tool".

Previews differ from every panel before them in one way that had to be built
for: they exist because somebody opened one, not because a node exists. The
sweep that keeps panels in step with the graph recreates any panel whose node
is present, which for a preview would mean a closed one reopening. They opt
out of creation and keep the deletion half, so closing a node closes its
previews.

They float rather than dock, for the same reason profile charts do: a window
opened to glance at something should not rearrange a layout somebody set up.

Everything else came from making `ViewerPanelViewModel` abstract over one
question - "what do I read" - leaving the image conversion, the profiling
buffer, the shape selection and the whole viewer surface shared. The Image
Viewer node is unchanged, and still does the other job: an observation point
placed in the recipe and saved with its layout.

The plan this is the first step of is `interactive-tools-plan.md`.

## 34. Two-image operators, and the scaling that makes them right

Logic and Blend are the first nodes that take two images, which raised three
questions a one-in-one-out filter never has to answer.

**Mismatched frames are refused, by name.** Two images of different sizes,
channel counts or depths can only be combined by inventing something -
cropping to the overlap, padding, or resampling. Each of those hides a
mistake in the recipe instead of reporting it, and in a measurement recipe
that is the worst possible trade. The message says which input is which size,
so the operator does not have to compare two node panels by eye.

**Logic reads its inputs as set or not set, by default.** A bitwise AND of
two mid-grey frames is almost black - 0x7F AND 0x80 is zero - which is
correct about the bits and wrong about the question. Inputs are reduced to
zero or full scale first, so the operation means what its name says whatever
arrives, and the switch to operate on raw bits is there for anyone who wants
it. Not is absent: Invert already does it. Subtract is present, because "in
A but not B" is how a region is excluded and the other three cannot express
it between them.

**Blend modes are defined on values from zero to one.** Multiply, screen,
overlay and hard light all have to be scaled by the depth's full scale -
255 for 8-bit, 65535 for 16-bit, 1 for float. Miss it and every one of them
saturates to white, which looks like a lighting fault rather than a bug.
That scaling is the single most likely thing to be wrong here, so it is
tested at both ends: 128 multiplied by 128 is 64 on an 8-bit frame, and 0.5
by 0.5 is 0.25 on a float one.

Opacity fades the **result** of the mode towards the base, not the blend
image towards the base. The difference is invisible on Normal and obvious on
every other mode, and the first is what an image editor does.

### What was left out, and why

**Soft Light.** Several incompatible definitions are in common use. A tool
that quietly picks one is worse than one that does not offer it.

**More than two inputs.** Three images are combined by chaining, which is
also how layers work - each blends with everything beneath it. A variable
number of ports is a real feature touching serialisation, the editor and
connection handling, and it should be paid for by a case that chaining
cannot serve rather than by anticipation.

Several of these modes earn their place in inspection rather than in design:
Difference against a golden sample is change detection, Subtract removes a
captured background, and Divide by a blank field is flat-field correction -
the honest way to undo uneven lighting.

## 35. A band is one parameter, not two

Colour Range carried six parameters - a minimum and a maximum per channel -
and so appeared as six unrelated sliders. The pairing was left to be worked
out from the labels, the two halves of one decision sat in different groups,
and nothing stopped a minimum being dragged above its maximum.

It now carries three, each a `ValueRange`: one track with two handles and a
box at either end.

`ValueRange` is a plain value type in the core, ordered on construction, with
a JSON converter beside the ones already there for `Rectangle`, `Size`,
`Point` and `Color`. A compound parameter value was an established pattern -
a region is one - so this needed no new machinery, only a new
`ParamEditor.Range` and a control to draw it.

### The invariant belongs in the control, not the drag

The first version clamped where the drag happened, which passed by eye and
failed its first test. A value reaches a range slider from four directions:
a drag, an arrow key, a typed box, and a binding carrying whatever a saved
recipe held. Enforcing "inside the bounds, and the right way round" in one of
them leaves the other three able to draw handles off the end of the track or
crossed over.

It is a WPF coercion now, so every route goes through it, and each end
re-coerces the other when it moves. A handle pushed past its partner stops
rather than swapping: swapping loses track of which one is under the cursor,
and the range jitters as the mouse passes over.

Two smaller choices worth keeping: a press takes the **nearer** handle, and
the low one when they coincide - otherwise a range collapsed to a point
could only ever be widened upwards. And the step rounds input granularity
only; the bounds are the coercion's business, so the two concerns cannot
fight.

### Old recipes

Six parameters folding into three is a rename that loses data, and the
loader only warns about parameters it does not recognise - so a saved recipe
would have opened every channel wide, with a warning that gave no reason to
connect the two. `LowerBlue` and `UpperBlue` are now folded into `Blue` on
load, written end by end rather than through the ordering constructor: the
other end has not necessarily been read yet, and ordering a half-built range
swaps the two.

## 36. No limit is the absence of a limit, and area needs a log scale

Pairing the blob filters into ranges the way the colour channels were paired
ran into two things the colour node never had to face.

### The sentinel had to go

The filters used `-1` for "no limit". Every parameter description had to
explain it, a legitimate negative bound was unexpressible, and - once there
is a track with handles - it cannot be told apart from a handle parked at
the end.

That last one matters more than it sounds. If the end of an area track is
the image's area, a filter set by dragging to the end stores "5013504", and
the day the camera changes it is a real limit that rejects things. A filter
has to store what it means.

So a range's ends are now optional. Absent is absent: it serialises as
`null`, never rejects anything, and survives a change of image because it
says nothing about one. Blank boxes in the panel, and the description no
longer has to teach a convention.

### A flat track over the image area is unusable

The suggestion was to run the area slider from zero to width times height.
The arithmetic is right and the control is not: blobs of interest are
usually a tiny fraction of the frame, so every useful setting falls inside
the first one percent of the travel. There is a test asserting exactly that,
so the claim is not left as an opinion.

Area and Perimeter use a logarithmic track instead - each decade gets equal
travel, which is what makes a range from a few pixels to a few million
reachable by hand. Circularity and Solidity are flat 0 to 1, where a flat
scale is correct. Aspect is 1 to 20 flat, and anything beyond that is typed
into the box: the slider is the coarse tool, the box is the exact one, and
neither has to cover the whole domain alone.

### Old recipes

Twelve parameters became six, under a different naming convention from the
colour node's - `AreaMin` rather than `LowerBlue`. The fold knows both, and
reads `-1` as an absent end. The recipe in daily use here carries
`"AreaMin": 50000`, and losing it would have quietly stopped the node
filtering while still appearing to work.

## 37. The viewer toolbar is one line, and ToolBar earns its keep here

The viewer's toolbar wrapped onto a second row when the panel was narrow.
That is a row of chrome per viewer, and on a small screen with three
viewers open it is most of the window.

It is a `ToolBar` now, which measures what fits on one line and moves the
remainder into an overflow menu behind a "..." button, reparenting as the
viewer is resized.

This reverses the call made for the application's own toolbar, which is a
plain panel precisely to avoid ToolBar's chrome. The reasoning there still
holds - that bar never needed overflow, so the chrome was all cost. Here
overflow is the entire point, and the part being bought is the awkward
part: deciding what fits and moving controls between two panels during
layout, without losing their state or their handlers.

The chrome is dealt with by replacing ToolBar's style outright rather than
basing one on it. The default carries a drag gripper and a bevel, and -
less obviously - a resource dictionary that restyles every Button,
ToggleButton and ComboBox placed inside it. Replacing it leaves the
children looking like the rest of the application.

### What the split breaks

The tool buttons are a radio group, found by scanning the parent panel of
one of them. Once a toolbar can put half the group in an overflow popup,
that scan returns half the group - and picking a tool in the menu would
leave one on the bar still looking selected.

They are read from the toolbar's `Items` now, which is stable however the
panels are arranged. The same shape of mistake as the restored layout:
**a reference into the visual tree is a reference to where something
happened to be.**

The tests assert the property rather than the mechanism - wide shows no
menu, narrow does, the bar is no taller either way, every tool is still
reachable, and picking one from the menu does not switch the overlays off.

## 38. An image says what its channels mean

A frame recorded how many channels it had, not what they were, so
everything downstream guessed from the count: three meant blue, green,
red. Right for a camera frame, wrong the moment a Colour Convert has been
through - and wrong in a way that reads as a tuning problem rather than a
bug. An HSV frame profiled correctly and was labelled B/G/R, and a colour
range offered a 0-255 band for a hue that stops at 179, so a third of the
slider selected values that cannot occur.

The information already existed and was being thrown away: Colour Convert
knows exactly what it produced. `ImageData` now carries a `ColourSpace`,
Colour Convert sets it from the conversion, and the filters pass it along -
a blur of an HSV image is still HSV. An untagged frame defaults to what its
channel count suggests, so nothing that worked before behaves differently.

Two things read it so far. The line profile names its traces H/S/V or L/a/b
instead of always B/G/R, and Colour Range relabels its three bands and
re-bounds them, so hue gets a 0-179 track.

### Labels that depend on the data

A parameter's label came from its attribute, which is fixed per node type.
Colour Range needs "Blue" or "Hue" depending on what reached it, so
`NameSource` was added - the same mechanism as `MinSource` and `MaxSource`,
pointing at a property the node computes.

`MaxSource` turned out to only accept **another parameter**, which is not
what a derived bound is: nobody should be offered a box for "how far hue
goes". It now falls back to a plain property.

The node learns the space when it evaluates, so until the first pass the
inspector shows the camera-frame names. That is self-correcting and the
alternative - asking the operator to state the space on the range node as
well as the convert node - is asking them to keep two things in step.

## 39. Mask keeps the frame; Region of Interest cuts it

Both take a region and narrow an image to it, and the difference is the
reason to have both. Region of Interest **cuts**: the output is a new frame
of the region's size, in its own coordinates, which is what fixturing a
rotated part needs. Mask **keeps**: the frame is untouched and everything
outside the region is filled, so every measurement afterwards is still in
the coordinates of the original image.

The mask can be a binary image or a shape set. The second is what makes a
found feature usable as a region - locate the board, mask to it, and
everything downstream sees only the board - and it needed shapes to travel
back into pixels, which the engine had never had to do.

Two details in that rasteriser were wrong first time and both are the kind
that are never noticed:

- **OpenCV's rectangle includes both corners**, so a ten-wide region drawn
  with a width of ten covers eleven columns. Half a pixel of error wherever
  a mask meets an edge.
- **Anti-aliasing has no place in a mask.** A soft edge leaves a ring of
  partly-set pixels that every consumer then has to make a decision about,
  and most of them treat non-zero as yes - which makes the region a pixel
  bigger all the way round.

The node also emits the mask it used. A mask built from shapes is otherwise
invisible, and one that is subtly wrong looks exactly like a detection that
is subtly wrong.

## 40. Fitting is not finding, and the error is the point

Fit Circle, Fit Ellipse and Fit Line take shapes something has already
located - blobs, usually - and reduce their points to the ideal figure
underneath. A blob is a ragged contour; what is wanted from it is the
circle it is a picture of, to sub-pixel precision.

They are a separate thing from the finders still to come. A finder
searches an image; a fitter is handed points. The new `Fitting` category
holds both, since Find Line and Find Circle belong beside these.

**Every fit reports its error** - the root-mean-square distance from the
points to the figure - and this is the part most often left out. A circle
fitted to a clean ring and one fitted to a broken arc are both circles.
Without the residual there is nothing to tell them apart, and a recipe
cannot decide whether to trust the result. It is tested by comparing a
clean ring against a scattered one rather than by asserting a number.

### Choices worth recording

**Least squares, not the smallest enclosing circle.** An enclosing circle
is decided by three extreme points, so one bad pixel redefines it. That is
the opposite of what fitting is for. There is a test that pushes a single
point far out and asserts the radius moves a little rather than doubling.

**Kasa's algebraic fit** for the circle: a linear solve rather than an
iteration. It biases slightly small on a short arc, which on the
near-complete outlines a blob produces is well under the noise being
fitted through.

**The ellipse's angle is of the long axis.** OpenCV gives the angle of the
width axis, whichever that happens to be, so a nearly-square ellipse
reports an angle that jumps by ninety degrees as its axes swap. Reported
this way, "angle" means the same thing here as on a blob's oriented
rectangle.

**A line segment spans the points it was fitted to**, rather than running
to the frame edge. A fit describes the evidence it had.

**Centres as an alternative to outlines.** Fitting each shape on its own
cannot express "a line through this row of pads". Pooling one point per
shape can, and it costs one parameter.

### A new shape

An ellipse is now a shape in its own right, not the oriented rectangle
that bounds it. Drawn as a rectangle it would claim corners the feature
does not have, and a mask built from it would include them.

## 41. The help is the tool, not a page about the tool

Every node now has a "?" beside its name that opens what it is for, what
its ports carry and what its parameters mean.

None of it is written down. It is read off the descriptor and the ports at
the moment the window opens.

A written page has to be kept in step with the code and will not be. A
parameter renamed, a port added, a bound changed - each is a silent
contradiction from the day it ships, and the reader has no way to know
which of the two is lying. This cannot disagree with the node, because it
is the node. It also means a node added later, including one from a
plugin, is documented the day it appears, out of the descriptions already
on its attributes.

Two details earn their place. **Which inputs are optional** is the
question most often asked of a node with several, and the node knows.
And **a bound that follows the data says so** rather than printing
whatever it happens to be - hue stops at 179 only once an HSV frame has
arrived, so a fixed number there would be wrong half the time.

A window rather than another box in the properties panel: that panel is
already long on a node like Find Blobs, and this is read once when a tool
is new to somebody and then not again.

### A table that had already drifted

Port types are shown to operators as "Image" rather than "ImageData". That
mapping existed in three places - the node reference generator, the socket
tooltips, and now this - and they disagreed: one called a double "double"
and another "Number", and only one of them knew what a `ShapeSet` was.
There is one table now, in the core, keyed by type name so the shell can
use it without depending on the node library.

## 42. Measurements belong to the recipe

Lines, angles and circles drawn on a viewer are saved with the `.sgraph`
now, and drawing one counts as an unsaved change.

They used to live in `ImageViewer._measurements` and nowhere else. New and
Open did ask before discarding unsaved work - they had always asked - but
they ask by comparing the serialised graph against the last saved copy, and
the measurements were not in the graph. So an afternoon of measuring
vanished on File - New with no prompt at all, which is the worst shape this
kind of bug comes in: the mechanism that should have caught it was present,
working, and looking at the wrong thing.

The fix is one line of design: put them on the node. Everything else
followed. `IsModified` compares `GraphSerializer.Serialize(_graph)` against
the snapshot taken at the last save, so anything stored in the graph
participates in dirty tracking, in Save, in Open and in the title bar's
asterisk for free. Nothing had to be told about measurements.

### Keyed by view, not only by node

A node can be looked at through more than one window: its own panel if it
is a Viewer, and a preview of each of its ports. A preview of an input and
a preview of an output are not the same picture - ROI crops, Rotate turns,
Resize scales - so a measurement held against the node alone would reappear
on the other window at coordinates that mean something else there.

Each record therefore carries the port name it was drawn on, empty for a
Viewer node's own panel, and a panel reads and writes only its own view.
This is the same reasoning that made a preview bind to a port rather than
to a node (decision 36), arriving at the same answer a second time, which
is some evidence it is the right one.

### What it does not do

Drawing does not invalidate. A line on an image says nothing about what the
graph computes, and re-running a recipe because somebody measured something
would be absurd on a large frame. The record is also deliberately not the
viewer's own `Measurement` class: that one carries selection state, cached
geometry and a WPF colour, none of which belongs in a file. And the tool is
stored as a name, so a recipe from a newer build drops a measurement this
one cannot draw rather than failing to open.

## 43. Node colours are a fixed set, not two colour pickers

A node can be given one of seven colourings plus the default, for marking
up a large recipe: the three nodes being worked on, the branch kept only
for comparison, the one that fails on a bad part. A fifty-node graph in one
shade of grey cannot say any of that.

The request was to change each block's background and stroke. What shipped
is a fixed palette, because the obvious version does not survive contact
with a dark theme. A freely chosen background sits under a title the node
inherits at near-white, so the first pale choice makes an unreadable node;
and a saturated fill shouts loudly enough that two marked nodes are worse
than none. Anything that can be chosen freely will eventually be chosen
badly by somebody in a hurry, and the person who has to read the graph
afterwards is usually not that person.

So the rule the palette is built to: **the background stays dark and the
stroke carries the colour.** A dark tinted fill with a vivid border reads as
colour from across the canvas while the title stays exactly as legible as
it was. Marking a node must not cost anything to look at it.

Each preset fixes three colours rather than two - background, stroke and
text - because a background and the text on it have to be chosen together
or one of them loses. `NodeAccentTests` does the WCAG arithmetic: every
title clears 7:1 on its own node, every accent's stroke clears 3:1 against
both its fill and the canvas, no background exceeds a luminance a dark
canvas can carry, and no two strokes are within 60 of each other in RGB.
That last one caught the first attempt, where Teal and Green measured 60
apart and read as the same colour at canvas zoom.

The default is the theme's own node, unchanged to the hex, and an unmarked
recipe writes no `accent` key at all - so existing graphs look exactly as
they did and existing files stay byte-identical.

### Stored as an intent, not as hex

The node holds a `NodeAccent` enum and what it looks like is the theme's
business. Hex on the node would mean a recipe marked up today is unreadable
if a light theme is ever added, and a colour saved by a newer build that
this one does not have would be a colour nobody can name. As it is, an
accent this build lacks falls back to the default with a warning: a node in
the wrong colour beats a canvas that will not draw.

Offered in two places, which are two different moments. The swatch row on
the properties panel is for a node already selected to be edited; the Style
submenu on the canvas right-click menu is for working across a graph, which
is when marking up actually happens.

## 44. Undo records what happened, it does not snapshot

Undo, redo, cut, copy, paste, duplicate and select-all are on the Edit menu
and the toolbar now, and node colouring works on a whole selection.

The obvious implementation of undo is to keep a serialised copy of the graph
before each edit and reload it to go back. It is a dozen lines, it cannot
miss any state, and it is wrong here. Reloading replaces every node
instance, and the shell holds node references everywhere - panels, previews,
the scheduler's cache, the properties panel, the selection, every measurement
keyed to a node. After one undo all of them point at nodes that are not in
the graph, and nothing throws: the panels just quietly stop updating. The
symptom and the cause would be a long way apart.

So an edit changes the least it can and **keeps the instances it removed**.
Undoing a delete puts back the very same node, and everything holding it
reattaches without being told. There is a test for exactly that, asserting
reference equality, because it is the property the whole design rests on.

### The recorder, and why there is no command class per parameter

The roadmap called this the mechanism's weakness before it was built: a
command stack has to be told about each kind of state, and the one edit
somebody forgets to wire up is not an error anywhere - it is a step undo
silently skips, found by a user rather than by a test.

So nothing is wired up. Every node already raises `PropertyChanging` before
and `PropertyChanged` after, so `UndoRecorder` reads the value either side
and records the pair. A rename, a bypass, a threshold, a colour, a drag and
a measurement are all one `PropertyEdit`, and a parameter added to a node
tomorrow is undoable the day it ships.

What is undoable is an **allow-list**, which is the one deliberate asymmetry.
The engine writes `Status`, `StatusMessage` and timings on every pass; a
deny-list that forgot a new one of those would fill the stack with evaluation
noise and have Undo stepping backwards through things nobody did. An
allow-list that forgets a parameter loses one step - and
`Everything_saved_is_undoable` fails, by comparing the list against what the
serialiser writes. Anything saved with the recipe is work.

Two things this caught that guessing would not have:

- **Applying an edit sets properties, and the recorder watches properties.**
  Without the `IsApplying` guard an undo records itself, after which the
  stack describes a history that never happened and redo walks sideways.
- **Numbering a new node is part of adding it.** `EnsureDefaultNames` turns
  "Threshold" into "Threshold 1" after every add, and recording that put a
  separate "Rename" step on the stack above the add - so one undo took the
  number off and left the node. Recording is paused around it.

### Coalescing, and the canvas

A slider dragged across its range raises a change per pixel. Edits merge
when they are the same property of the same node within 500ms - longer than
the gap between two frames of a drag, shorter than the gap between two
decisions. A batch (`UndoStack.Begin`) covers the actions that are one thing
to the operator and several to the graph: a paste, a multi-node delete, a
drag that moves X and Y, colouring a selection.

The canvas collections are built incrementally as the operator works, so
after an undo they describe a graph that no longer exists. `Resync()` reads
the graph back rather than replaying each edit's effect in reverse - the
replay version is the one that goes wrong quietly. It restores selection by
node id, because the view-models are new ones and an undo that emptied the
properties panel would be right about the graph and wrong about what the
operator was doing.

### The clipboard is the save format

Copy writes a `.sgraph` document holding just the copied nodes and the wires
between them. Parameters, colours and measurements survive a paste with
nothing written about any of them, and the format's version check and legacy
migrations come along free. Pasting the text of a whole saved recipe works,
and falls out rather than being built.

Each pasted node gets a fresh id - two nodes claiming to be the same one is
not expressible in the file, and connection records resolve by id, so a
second paste would silently rewire the first. Names are left alone: two
nodes called "Threshold" is normal here, and inventing "Threshold (2)" would
be the only place in the application that renames somebody's node for them.

### Ctrl+C has to mean two things

Cut, copy, paste, select-all, undo and redo are routed through WPF's
`ApplicationCommands` rather than through `KeyBindings` on the window. A
window-level `KeyBinding` on Ctrl+C fires wherever the focus is, so renaming
a node and copying part of the name would copy the node instead - maddening,
and hard to describe to whoever has to fix it. Routed as commands, a focused
`TextBox` handles its own and marks it handled; only what nothing closer
wanted reaches the window.

### The clipboard is slow, and that was measurable

`Paste.CanExecute` originally asked the clipboard directly. WPF reaches it
through OLE, which belongs to whichever process last opened it: the call
blocks and retries, and `CanExecute` is re-asked on every
`RequerySuggested`, which is to say constantly. The new tests took two
minutes and twenty-three seconds; with the answer cached and refreshed on
window activation and after our own copies, four. The same cost was being
paid by the window on every mouse move.

## 45. The dialogs are ours, and no test can open one

Every prompt the shell shows is a themed window now, not a stock
`MessageBox`. Decision 41 left them deliberately - the stock dialog gets the
keyboard right for free, and a hand-built modal that did not would be worse
than an ugly one - so the replacement takes that on explicitly: one default
button answering Enter, Escape taking the way out that changes nothing
(Cancel where there is one, No where there is not), an access key on every
button, and the focus on the default when it opens. `MessageDialogTests`
checks each of those, because they are the part that is invisible when it
goes wrong.

Two small choices worth recording. Yes/No/Cancel does **not** default to Yes
by accident: a stray Enter on "Save this project before closing it?" saves,
and Escape cancels, so neither key loses work. And the message is a
read-only `TextBox` rather than a `TextBlock`, so the text of a failure can
be copied into a bug report instead of retyped from a photograph of a
screen.

The unhandled-exception handler keeps the stock dialog as a fallback behind
the themed one. It runs when something has already gone wrong, and a themed
window needs the theme's resources - which may be exactly what failed.

### The prompt that interrupted the test run

A test added a node and then asked for a new project. That is an
unsaved-changes prompt by definition, so `ConfirmDiscard` opened a real
modal on the test thread and the suite stopped - silently, because the
dialog appeared on whoever's screen was attached rather than in the test
output, with nothing to say which test had asked or what answering would do.

The test was wrong and is fixed: it saves first, which is what it meant. But
the failure mode is bad enough to be worth a net, so the test assembly
replaces `Dialog.Present` in a module initializer and answers its own
prompts - No to a save prompt, Cancel to a confirmation, the answers that
discard nothing. It records them too, so a test can assert it was never put
in that position, and two now do: one that the new-project path asks nothing
when the work is saved, and one that it still asks when the work is not.

This is a net rather than a licence. A test that reaches a prompt has
usually set up the wrong state.

### And the verification that caused it to be seen

Launching the application to check it starts cleanly used
`CloseMainWindow()`, which is a close request and therefore runs the
unsaved-changes prompt. It kills the process instead now. Checking that an
application starts should not ask anybody whether to save.

## 46. More sockets, not more wires per socket

The Image Viewer takes as many shape sets as are wired into it. It starts
with one empty Shapes socket; fill it and a second appears, fill that and a
third. Free one up and the spares collapse back to a single empty slot.

The question this answers is "how do I draw more than one annotation", and
the obvious reading of it is that an input should accept several
connections. It should not. `InputPort.Connection` is a single reference
that the scheduler, cycle detection, the connect-and-replace logic, the
serialiser and every node in the library read as one thing, and widening it
would touch all of them to solve a problem that is really about sockets
rather than wires. Five wires converging on one socket also reads worse on a
canvas than five labelled sockets.

Ports were never frozen anyway: `_inputs` is a list, and
`EnsureParameterPorts` has always appended to it after the constructor ran.
So a node can simply make more.

### The count is derived, never stored

`Fit()` computes the sockets from the connections - one spare at the end -
so there is no saved number that can disagree with the saved wiring. That
removes one class of bug and creates another: a freshly built node has fewer
sockets than a saved recipe names, so loading a graph with four overlays
would wire the first and silently drop three.

`IGrowingPorts.EnsureInput` is the answer. The serialiser asks the node for
a port it does not have rather than skipping the connection, and the node
builds up to the slot the name asks for. The first socket keeps the bare
name "Shapes" it has always had, so recipes written before any of this still
find their port; the rest are "Shapes 2", "Shapes 3", which is readable in a
diff in a way a repeated "Shapes" would not be.

Two details that are easy to get wrong:

- **A gap in the middle is left alone.** Only trailing spares are removed.
  Closing a gap would renumber the sockets below it, which moves somebody
  else's wire to a different row for a reason they did not ask for.
- **Growth is capped at twelve.** Past that the picture is unreadable
  anyway, and a loop that adds sockets for ever is worse than a refusal.

### What merging costs, and what it does not

Nothing, for styling: `Colour`, `StrokeWidth` and `Label` live on each
`Shape`, not on the set, so concatenating two styled sets keeps both
stylings. That is what makes this work at all - the Shape Style node can
colour each branch before it reaches the viewer.

It does cost the shared selection. The key that ties a blob on the image to
a row in a table is the producing port, and the correspondence is by index;
merge two producers and the fifth shape on the image is no longer the fifth
row of either table. So a viewer with one producer shares its selection
exactly as before, and a viewer with several shares none. A wrong
highlight would be worse than no highlight.

A set measured in a different image size is dropped rather than drawn in the
wrong place, and the **node** says so through `context.Warn` - on its header
and in the properties panel, where this application already reports that
sort of thing. The first version put the message on the panel view-model,
where nothing read it.

## 47. An access key nobody could press

The themed dialog's buttons read "_Yes", "_No", "_Cancel" - with the
underscores drawn on screen, and Alt+Y doing nothing.

A hand-written `Button` template defaults `ContentPresenter`'s
`RecognizesAccessKey` to false, where the stock template sets it. So the
underscore was never interpreted: it was rendered. The markup was right, the
captions were right, and the whole feature was absent.

The test that was supposed to cover this asserted that each caption contains
an underscore. It passed throughout. That is the lesson worth keeping: it
checked the half that was never in doubt, and the half that mattered - that
anything honours the underscore - was not expressed anywhere. It now reads
the Button template and asserts `RecognizesAccessKey`.

### And a bare letter answers as well

An underscore means Alt+Y on Windows, but the stock `MessageBox` this
replaced also takes a bare Y or N, and the first thing anybody tries is N.
Not matching that would have been a small regression dressed up as
correctness.

Safe only because there is nothing on this dialog to type into; the same
handler on a window with a text field would eat the typing. The rules are
narrow and tested: only a button this dialog actually has, only the letter
that button's own caption offers, and nothing at all when a modifier is
down - Ctrl+C is a copy, not a cancel. The decision is a method rather than
a block inside the key handler, so it can be tested without a shown window
and a live keyboard device.

### Three drawn icons

Fit Circle, Fit Ellipse and Fit Line were standing in with Phosphor icons
that read as something else: circle-dashed as a selection marquee,
orange-slice as fruit, chart-line-up as a trend rather than a fitted line.
They are hand-drawn now, installed through `tools/install_extension.py` like
the rest, and the three Phosphor entries are gone rather than left as dead
map lines.

## 48. A readout belongs to the thing it describes

The image dimensions moved out of the main status bar and into each
viewer's own.

They were never "the" image size. The shell read them from whichever viewer
panel happened to be first in its list:

```csharp
panels.OfType<ViewerPanelViewModel>()
      .FirstOrDefault(panel => panel.SizeText is not null)?.SizeText;
```

With one viewer open that looked correct. With two it showed one of them
and gave no clue which - and since Crop, Resize and Region of Interest all
change the size, the number on screen routinely described a different
picture than the one being looked at. A window-level readout for a
per-panel fact cannot be right; the only question was how long it would
take to notice.

The panels had been computing `SizeText` individually the whole time. It
was only ever displayed in the wrong place, which is why the fix removed
code rather than adding it: the shell's `ImageSizeText` property is gone
rather than left bound to nothing.

### Order matters in a status bar

Size reads first, then the cursor position, then the pixel value. The last
two reserve a fixed width so the bar does not jitter as their digits
change, and while they came first those reservations were two blank gaps
with the size stranded in the middle of them whenever the pointer was
elsewhere - which is most of the time. The one readout that is always
present goes at the edge; the reserved space trails behind it where it
cannot be seen.

## 49. A vector is however many numbers it needs

The Vector node had an X field and a Y field. Two is the wrong number as
soon as anything wants three: a kernel is two, an RGB offset is three, a
per-channel threshold is however many channels the frame has. It is one
text box now - "3, 3" or "3, 3, 1" - which takes any count, and is quicker
to type and to read than a column of boxes that would have to appear and
disappear as the count changed.

`Vector2` is replaced by `NumberVector`. Nothing else used it; the roadmap
had already noted it had no consumer, which made this free to change and is
still the honest state of it - the node produces a value nothing reads yet.

Reading past the end gives zero rather than throwing, because what counts
as the right number of components depends entirely on what is reading them.
A consumer asks for what it needs and complains in its own words, where
this could only ever say "3 is not 2".

### Lenient about separators, strict about numbers

Typing "3, 3, 1" passes through "3, 3," on the way. A node that went to
Warning on that keystroke would be complaining at somebody who is doing
nothing wrong, so a trailing separator reads as the components before it.
Text that cannot be read at all still warns - and warns rather than
throwing, because a half-typed vector is a normal state for a text box to
be in and failing the whole pass over it would be unusable.

This was wrong in the tests before it was right in the code: the first
version refused a trailing comma, and the test asserting that was the thing
that had to change.

Recipes that stored X and Y as separate parameters fold into the component
list on load, in either order, through the same legacy-parameter mechanism
that already handles renamed bounds and rectangle parts.

### Why Integer and Float were not merged at the same time

The question was whether one Number node with a type dropdown would mean
more checking when connecting to other ports. It would mean none, and the
reason is worth recording because it is not obvious from the node list:

- `VisionGraph.IsCompatible` returns true for **any** two numeric types, so
  an `int` output already connects to a `double` input and the reverse.
- `EvalContext.Param<T>` already calls `Convert.ChangeType`, with a comment
  saying exactly why: "an integer output can drive a double parameter
  without an adapter node".

So the distinction is already invisible at both the connection and the
evaluation boundary. Which cuts deeper than the question asked: a dropdown
that switched the *port type* would be machinery with no observable effect,
and mutating `OutputPort.DataType` on a node that is already wired is the
one way to actually introduce the fragility the question was worried about.

What is worth building is one node whose output is always `double`, with a
dropdown over how the value is *entered* - whole numbers or free. That is a
presentation choice, nothing moves, and the two nodes become one in the
toolbox. It needs a legacy type key mapping, so it is left on the roadmap
rather than done in passing.

## 50. Find Circle: calipers through a ring

A circle finder that is told roughly where to look, rather than one that
searches. Given a centre and an inner and outer radius, it casts radial
scans through the band, finds the intensity step along each, and fits a
circle to the points.

The same answer Find Blobs and Fit Circle already give together, arrived at
directly - and the comparison is the interesting part, because both stay.

**Where the ring wins.** Blob finding thresholds the whole frame, labels
every connected region, measures them all and discards what is not wanted.
This visits a few dozen short lines and never looks at the rest of the
image. It is also steadier under uneven lighting: a threshold is one number
for the entire frame and a gradient across the field defeats it, while a
caliper only asks that the edge be locally the steepest thing along its own
scan, which stays true when one side of the part is brighter than the other.

**Where it loses.** It finds the circle it was pointed at. Find Blobs finds
circles nobody knew were there. The ring is the whole of the tool's prior,
which is why a badly placed one returns nothing rather than something
wrong - a property worth having, and the reason the node warns about a
missed band instead of fitting through noise.

### The caliper is its own thing

`Caliper.TryFind` takes samples and returns a sub-pixel edge; it does no
sampling of its own. `LineProfileSource.Sample` already walks a line at a
chosen spacing, averages across a band and interpolates - which is exactly
a caliper's sampling, and already used by the profile chart. Find Line and
Find Curve differ only in where the lines go, so the primitive is shared
from the start rather than extracted later.

Three details in it are load-bearing:

- **Peaks, not steep places.** An edge is where the derivative *turns*.
  Taking the first sample over a contrast threshold makes a long ramp
  report an arbitrary point part-way up it, and a slightly different ramp
  report a different one.
- **Odd, centred smoothing.** An even window biases every reading half a
  sample in the scan direction. On a circle that is a radius wrong by a
  consistent amount, which reads as a calibration error rather than a bug.
- **Parabolic interpolation** of the derivative peak. Without it the best
  possible is half a pixel; with it the measured error against a known edge
  is zero.

### The tests had the bug, not the code

The first version drew its test circles with `CvInvoke.Circle` and measured
against the radius it had asked for. Every reading came out about 0.56
pixels large, consistently, across radii - exactly the shape of a
half-sample offset error, and the obvious conclusion was that the caliper
had one.

It did not. A filled circle of radius R rasterises to cover pixels out to
roughly R + 0.5, so the intensity edge in the drawn image genuinely is half
a pixel outside the nominal radius. The truth was wrong, not the
measurement.

Confirmed by a separate probe against an analytically rendered disc, whose
intensity ramps across exactly one pixel so the 50% crossing is at the
stated radius by construction: error 0.0000 at two radii, and 0.13 at one
deliberately placed between samples. The tests render that way now, which
is what makes a sub-pixel claim testable at all.

The lesson is older than this tool: a consistent error that matches a
plausible bug is still worth measuring against a known truth before fixing.

## 51. Find Line, and the sub-pixel estimator that was wrong

Find Line is Find Circle's calipers aimed along a rectangle instead of
around a ring. The region is an `OrientedRect` - the same type the Region
of Interest node uses - so it is typed as "x, y, w, h, angle" and inherits
the drawing and turning the viewer already does for ROIs. Scans are spaced
along the region's length and cross its width, which means the region is
placed with its long axis running *along* the expected edge.

`LineFit` is total least squares by the scatter matrix, not an ordinary
y-on-x regression. The ordinary one minimises vertical error and cannot
express a vertical line at all - and a caliper line finder aimed at a
vertical edge is not an edge case, it is half of all uses.

### The parabola was the wrong estimator

Find Circle's caliper interpolated its sub-pixel position with a parabola
through the derivative peak and its two neighbours. Against the circle's
anti-aliased edges that measured exact, and it shipped.

Find Line's first failure was an edge placed at y = 190.5 - exactly between
two pixel rows. Every pixel is then fully dark or fully light, so the image
holds a perfectly hard step; smoothing turns a hard step into a straight
ramp; and the derivative of a straight ramp is a flat plateau rather than a
peak. A parabola through three equal numbers says nothing, and the code
took the first of them, putting the edge at the plateau's leading corner.

Patching that to find the plateau's centre by exact equality worked at
smoothing 5 and failed at 7, because the plateau's members are computed by
separate summations and agree to within rounding rather than bit for bit.
Measured against a known edge, the error grew with the window: nothing at
5, three quarters of a pixel at 11, two and a quarter at 21.

The estimator is a **centroid** now - the intensity-weighted centre of the
derivative across the run either side of the peak that stays above half its
height. It assumes nothing about the shape of the top. Over a symmetric
plateau it returns the middle, over a rounded peak the peak, over an
asymmetric blur the balance point, which is what an edge position means.
Measured across smoothing widths 1 to 21 and three edge positions the error
is now zero everywhere, against up to 2.25 pixels before.

The differences are central rather than forward at the same time. A forward
difference sits half a sample along from the index it is stored at, and
every reading then needs that half added back - a correction that was
written once, correctly, and would have been missed the next time the peak
finding changed. It just had been.

### Two tests that were wrong, in opposite directions

`An_empty_region_is_an_error` expected the node to reject a zero-area
region. It could not: `OrientedRect` clamps both sides to at least one
pixel, so the guard being tested was unreachable. The guard is gone and the
test asserts the clamp instead.

And the hard-step case now runs at three smoothing widths rather than one,
because one width is what let the parabola through in the first place.
