# Interactive tools: previews, drawn geometry, and the finders

A plan for tools that need a picture to be configured — Find Line, Find
Circle, Find Curve, and the measurement tools behind them.

Companion to `decisions.md`, which records what was settled and why.

---

## 1. The problem this solves

Region editing today works by accident. `MainViewModel` takes the **selected**
node's region parameter and pushes it to **every** viewer panel, so the
operator draws on whichever viewer looks useful:

```csharp
ActiveRegionParameter = Editor.SelectedNode?.Parameters
    .FirstOrDefault(parameter => parameter.Kind == ParamEditor.Region);

foreach (var panel in NodePanels.OfType<ViewerPanelViewModel>())
    panel.RegionParameter = ActiveRegionParameter;
```

Nothing checks that the viewer drawn on is showing the tool's input.

In the TestPCB recipe there are three viewers — the original, the binary, and
the rotated board. Select a tool fed from RGB Channel, draw its region on
"Rotated Image", and the app accepts it. The rectangle is stored in the
tool's input coordinates but was chosen in a different image's coordinates.
Once a Region of Interest node has straightened the board those spaces have
nothing to do with each other. It works when the chain is short and nothing
rotates or resizes, which is not a property a measurement recipe should rest
on.

**A preview bound to a port shows, by construction, the pixels that port
carries.** A point clicked is a point in that port's coordinate space. The
question stops being askable.

## 2. What is being added

Three things, in the operator's terms:

1. **Preview** — double-click a node, or right-click it and pick a port, and
   a viewer opens on that port. Input or output, chosen explicitly.
2. **Draw** — a geometry parameter in the properties panel grows a button
   that opens the right preview and arms drawing on it.
3. **Finders** — Find Line first, taking a region and emitting line segments
   as shapes.

The Image Viewer node is untouched. It does a different job: a deliberate
observation point, placed in the recipe, saved with the layout, arranged for
whoever runs it. A preview belongs to configuring one tool and is opened on
demand. Both exist in VisionPro for the same reason.

## 3. Previews are bound to a port, not to a node

This is the decision everything else follows from.

A node can have several image ports — Find Blobs has `Out`, `Count` and
`Shapes`; a future Template Match has an image and a template. "Preview the
node" has no answer for those. "Preview `Find Blobs.Shapes`" does.

| Port type | What the preview shows |
|---|---|
| `ImageData` | The image. |
| `ShapeSet` | The shapes, drawn over the node's first image **input** — there is nothing else to draw them on. This is the answer to "what is the out image from a blob tool". |
| anything else | Not previewable, and not offered. |

**Default on double-click**: the first image output, falling back to the
first image input. "What did this do" is asked more often than "what did it
get", and a node with no output image is one being configured.

**Identity**: `preview:{nodeId}:{portName}`. Stable, so asking twice focuses
the existing window instead of opening a second, and so a saved layout can
restore it. The existing restore already cancels content it cannot match, so
a preview whose node has gone is dropped.

**Lifecycle**: a preview exists because somebody opened it. This differs from
every panel today, which exists because its node does, and which
`SyncNodePanels` recreates on every pass. Previews must opt out of that
reconciliation, and be closed only when their node is deleted.

**Cost**: a preview holds a converted bitmap of its port, around 20 MB for a
2448x2048 frame. It reads the scheduler's cache and triggers no extra
evaluation. Several open at once is the operator's choice, the same as
several Image Viewers.

## 4. Drawing geometry

A geometry parameter renders with a **Draw** button beside its numbers.

Pressing it opens — or focuses — a preview of **the image input the
parameter is expressed in**, and arms drawing on that preview. The rule is
the correctness property from section 1, made operational:

> Geometry is drawn on the image it is measured in, and nowhere else.

For a search region that is the tool's own input, so Draw always targets the
input port however the operator last left the preview.

While armed the preview shows the current geometry with handles. Dragging
rewrites the parameter; Escape, or pressing Draw again, disarms.

### Retiring the old path, later rather than now

`ActiveRegionParameter` stays working until the finders are using the new
route. Deleting it in the first step would take away region editing that
works today for the existing nodes, to fix a fault that only bites on chains
with a geometric transform in them. It is removed in step 4, when there is
something better for every node that has a region.

### Geometry parameters beyond a rectangle

`ParamEditor.Region` carries a `System.Drawing.Rectangle`. A caliper box is
an oriented rectangle, a circle finder wants an annulus, a curve finder a
polyline. `Sirius.Core/Shapes` already has `OrientedRectShape`,
`CircleShape` and `PolygonShape`, built for blob **output**.

The same vocabulary serves as **input**. One type system, used in both
directions. What it needs is a parameter kind that holds a `Shape`, an editor
that knows which handles that shape has, and serialisation so a saved recipe
keeps it.

## 5. Find Line

The first finder, and the pattern for the rest.

**Inputs**: an image, and a region to search in.

**Outputs**: `Shapes` carrying `LineShape` segments, `Count`, and the input
image passed through so a viewer can be wired downstream.

**Parameters**: polarity (dark to light, light to dark, either), minimum
contrast, caliper count, caliper width and length, search direction,
expected number of lines, and an outlier tolerance for the fit.

### The core already exists

A caliper is a short line profile: sub-pixel sampling along a line with a
perpendicular band averaged to cut noise. That is exactly what
`LineProfileSource` does, and it is tested.

So Find Line is: lay N calipers across the region, project each with
`LineProfileSource`, differentiate, pick peaks subject to polarity and
contrast, refine each peak to sub-pixel by parabolic interpolation, then fit
a line to the hits with outlier rejection. The novel part is the peak picking
and the fit; the sampling is done.

Showing the individual caliper hits alongside the fitted line is what makes
the tool tunable — a line fitted through three good hits and four bad ones
looks identical to a good fit until the hits are drawn.

### What a preview must show while configuring it

Three layers: the input image, the editable region, and the tool's own
results drawn read-only. The viewer already has an image layer, a shape layer
for graph results, and a measurement layer with handle editing. The work is
recombination, not new machinery.

## 6. What this does not solve

"Measurement tools" belongs in a different bucket from the finders.

Measuring between two found lines has nothing to draw. It is a matter of
referencing two tools' **outputs** — a Measurement node with two shape
inputs, or a picker that says "the line from Find Line 1, the circle from
Find Circle 2". A preview helps the operator see the result; it will not help
them define it. Designing measurement tools as though they need a region they
do not have is the mistake to avoid.

## 7. Order of work

1. **Previews.** Port-bound panels, context menu and double-click. Worth
   having on its own: any node's output becomes inspectable without wiring a
   viewer to it.
2. **Draw button** on `ParamEditor.Region`, arming the input preview. The old
   path still works.
3. **Find Line**, on calipers over `LineProfileSource`, emitting
   `LineShape`. Axis-aligned region to begin with.
4. **Geometry parameters** — `Shape`-valued, with handle editing and
   serialisation. Prove it on the oriented region both Find Line and the
   existing Region of Interest node want. `ActiveRegionParameter` goes.
5. **Find Circle and Find Curve** on the same caliper core.
6. **Live preview during a drag** — evaluate the tool and its ancestors only,
   rather than the whole graph on every mouse-move frame. The scheduler is
   dirty-tracked and cancellable already; this is a new entry point, not a
   redesign. Deferred until a finder makes it necessary.
