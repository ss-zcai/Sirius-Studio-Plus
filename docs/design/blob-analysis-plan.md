# Blob analysis: plan

> **Status: built.** Steps 1-4 are done and tested.
> What was deliberately left out is listed at the end and has moved to the roadmap.

Redesign of `BlobFilterNode` into a proper blob finder, and the shape output
that carries its results into the rest of the graph.

Written after reading how HALCON and Cognex VisionPro do it. Sources at the
end.

## What the established tools do

**HALCON** (`select_shape`) filters regions on a long list of named features -
`area`, `circularity`, `compactness`, `convexity`, `rectangularity`,
`anisometry`, `holes_num`, `max_diameter`, `ra`/`rb`/`phi` for the equivalent
ellipse, and so on. Several criteria combine with an explicit `'and'`/`'or'`
operation, and either limit can be left open with `'min'`/`'max'`.

**Cognex VisionPro** (`CogBlobTool`) exposes 43 per-blob measurements through
one enumeration. Three things about its shape are worth copying:

1. **One list of measures serves filtering *and* sorting.** Not two concepts.
2. **Each measure has a mode** - filter, compute, or ignore - so the tool only
   pays for what the recipe asks for.
3. **`NotClipped`** flags a blob touching the ROI or image edge. A part cut off
   by the frame is not a part, and this is the filter most easily forgotten.

Both treat holes as first-class (`holes_num`, `NumUnfilteredChildren`).

### Naming trap

"Circularity" means different things in different packages:

| Package | Name | Formula | Circle |
|---|---|---|---|
| HALCON | `circularity` | A / (π·r_max²) | 1, max |
| HALCON | `compactness` | P² / (4πA) | 1, min |
| Cognex | `Acircularity` | P² / (4πA) | 1, min |
| ImageJ, common usage | circularity | 4πA / P² | 1, max |

This project uses **4πA / P², 1.0 = a perfect circle**, the common usage, and
states the formula in the parameter description so there is nothing to guess.

## Design

### 1. Shapes are port data

Following the graph's own logic: a node emits shapes, and anything can consume
them - including a viewer, which is all an "annotation" is.

```
Shape          Point | LineSegment | Rect | Circle | Polygon
               + Metrics: name -> double
               + optional Label, Colour
ShapeSet       IReadOnlyList<Shape> + the frame size they were found in
```

Metrics ride on the shape as a name-keyed bag rather than typed fields. That is
what lets a later generic *Filter Shapes* node work on any producer's output
without knowing what produced it.

`ShapeSet` carries the frame size so a viewer can say when it is being asked to
draw shapes found on a differently sized image. Remapping shapes through Crop
and Resize is the proper fix and is **not** in this plan.

### 2. Measures

One enum, used for filtering and for sorting, as Cognex does.

| Measure | Cost | Notes |
|---|---|---|
| Area | free | already computed, holes subtracted |
| CentroidX, CentroidY | cheap | moments |
| BoundsX/Y/Width/Height | cheap | axis-aligned |
| Extent | cheap | area / bounding-box area |
| Perimeter | cheap | `ArcLength`; slight positive bias on pixel contours |
| Circularity | cheap | 4πA / P² |
| AspectRatio | medium | from the **min-area rotated rect**, not the bounding box - a tilted part has a misleading axis-aligned ratio |
| Width, Length | medium | the rotated rect's short and long sides |
| Angle | medium | rotated rect orientation |
| Solidity | expensive | area / convex-hull area. This is the "concave factor" |
| HoleCount | free | already walked for hole subtraction |
| TouchesBorder | free | from the bounding box against the frame |

### 3. Filtering

Each filterable measure gets a Min/Max pair, following the convention already
in this node: **-1 means no limit**. Grouped in the inspector under Size,
Shape and Position.

Combination is **and** only. HALCON's `'or'` exists, but an or-combination of
size and shape criteria is rare in practice and hard to read in a property
grid; a graph can express it by branching instead.

### 4. The cap needs an order

"Keep at most 50" is meaningless without saying which 50:

- **Sort By** - any measure, plus **Position (row-major)**
- **Descending**
- **Max Count** - -1 for unlimited

Position ordering matters as much as area: in inspection, blob *n* should be
the same physical feature on every part. Ties break on position so the order is
deterministic.

When the cap truncates, the node reports "found 3,412, kept 50" rather than
quietly returning a round number.

**The cap bounds the output, not the work.** Ranking requires measuring
everything. What bounds the work is the area filter, applied first.

### 5. Staged evaluation

Measures are computed in cost order, and only when a filter or the sort needs
them. The convex hull - the expensive one - is computed only if a solidity
filter or a solidity sort is set.

1. contour + area (+ hole subtraction) -> area filter
2. bounding box -> bounds, extent, touches-border filters
3. perimeter -> circularity filter
4. min-area rect -> aspect, width, length, angle filters
5. convex hull -> solidity filter

### 6. Segmentation and polarity

**Revised after first use.** The plan assumed a binary input with a Threshold
node upstream. That is how HALCON splits it, but fed a grey image the node
found one region covering the whole frame - every non-zero pixel is
foreground, and a real background is never zero. It looked like a result.

Cognex thresholds inside the blob tool, and that matches what the operator
expects of a polarity setting. So the node segments:

- **Automatic** (default) - Otsu, picked from the image.
- **Fixed** - a given grey level.
- **None** - the input is already binary.

Polarity chooses which side of the threshold is foreground, and still inverts
a binary input under None.

### 7. Outputs

| Port | Type | Why |
|---|---|---|
| Out | Image | the filtered mask, as now - still how you feed a mask downstream |
| Count | int | as now |
| Shapes | ShapeSet | new: the blobs themselves, with their metrics |

### 8. Name

"Find Blobs". It detects, measures and filters; "Blob Filter" describes only
the last part. The type key stays `blob.filter` so existing saved graphs load.

## Order of work

1. `Shape`, `ShapeSet` and the metric bag in `Sirius.Core`.
2. Rebuild the node: polarity, measures, filters, sort, cap, shape output.
3. Viewer `Shapes` input, drawn on its own non-interactive layer, with the
   frame-size mismatch reported.
4. Tests: metric correctness against synthetic shapes, filter and sort
   behaviour, the cap, staged evaluation not computing what it need not.

## Deliberately not in this plan

- Remapping shapes through geometry-changing nodes (Crop, Resize, Polar).
- A generic *Filter Shapes* node over any `ShapeSet`.
- *Combine Shapes*, needed before two producers can draw on one viewer.
- Ellipse fitting, inner/outer radius, moment invariants.
- `connectedComponentsWithStats` as a faster first pass; `FindContours` keeps
  the hole hierarchy that hole subtraction depends on, and is fast enough.

## Sources

- [HALCON `select_shape`](https://www.mvtec.com/doc/halcon/2411/en/select_shape.html)
- [HALCON `circularity`](https://www.mvtec.com/doc/halcon/2411/en/circularity.html)
- [Cognex `CogBlobMeasureConstants`](https://docs.cognex.com/vpro_980/web/EN/help/html/T_Cognex_VisionPro_Blob_CogBlobMeasureConstants.htm)
