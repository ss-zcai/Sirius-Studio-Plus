# Icon style

A specification for redrawing the icon set in one monographic style, derived
by measuring the existing `res/toolbox icons/` artwork so a redesign matches
rather than approximates it.

Companion to `icon-inventory.md`, which lists what needs drawing.

---

## 1. The system you already have

All 32 toolbox icons agree on this, which makes it the house style:

| Property | Value |
|---|---|
| Canvas | `viewBox="0 0 128 128"` |
| Construction | **filled paths only** — no `stroke` attribute anywhere |
| Palette | exactly two: `#fff` glyph, `#333` keyline at 50% opacity |
| Module | 8 units. Coordinates cluster on 8, 16, 24, 32, 40, 48, 64, 80, 96, 104 |
| Stroke weight | 8 units, drawn as a filled shape |
| Corner radius | 8 outer, 4 inner |
| Keyline | an 8-unit outline offset outward from the silhouette |

"Stroke weight 8, drawn as a fill" is the important one. A line is a rectangle,
a ring is a donut path. Nothing relies on stroke rendering, which is why the
converter never has to resolve stroke widths and why the icons scale exactly.

### The 8-unit rule

Rendered at 16 px, **8 units = 1 pixel**. That is the whole detail budget:

- Nothing thinner than 8 units. It will vanish.
- No gap smaller than 8 units. It will fill in.
- Detail below 16 units reads as a smudge at toolbar size.

A 128 canvas is not 128 pixels of freedom. It is a **16×16 grid** with
half-steps available for optical correction.

---

## 2. The keyline is insurance, not decoration

Worth knowing before designing around it: `#333` at 50% opacity over the dark
panel (`#1E1F22`) is **almost invisible**. On the application's own chrome the
icons read as flat white glyphs; the keyline only does work when an icon sits
over a light background or over an image.

Two consequences:

1. **Design for white-only legibility.** If a glyph only resolves because of
   its keyline, it will not resolve in this application.
2. **Do not use the two tones semantically.** A scheme where "dark = the
   subject, white = the measurement" collapses to one tone on the dark theme.
   Differentiate by *shape*, never by tone.

Keep the keyline. It costs nothing and it is what lets the same file be
dropped onto a white report or a bright image later.

---

## 3. Constraints from the build pipeline

`tools/svg2xaml.py` converts SVG to WPF `DrawingImage`. It supports `path`,
`rect`, `circle`, `ellipse`, `line`, `polygon`, `polyline`, nested `g`, CSS
classes in `<style>`, inline styles, presentation attributes, and
translate/rotate/scale/matrix transforms. Anything else **raises** rather than
silently dropping shapes.

So, when exporting:

- **Convert text to outlines.** There is no `text` support. This is why the
  "128", "0.6" and "(3,3)" icons carry large path data.
- No gradients, filters, masks, clip paths, patterns or `use`.
- Flat fills only, and keep to the two palette entries.
- Units are fine (`2px` parses), but plain numbers are cleaner.

After dropping files in `res/`, run the converter and the test suite: one test
force-realises every entry in the dictionary, so a malformed value fails the
build instead of appearing as a blank square months later.

---

## 4. The standard actions — redesign direction

These are the six on the application toolbar, currently borrowed from the
multi-colour set. They should be redrawn in the house style.

The principle: **for file operations, recognition beats novelty.** An operator
should not have to learn what Save looks like. Modernise the drawing, not the
metaphor — with one exception, noted below.

| Action | Direction |
|---|---|
| **New** | A sheet with one corner cut at 45°, plus a small plus mark at lower right. Resist the temptation to draw a node graph: it is distinctive but nobody reads it as "new". |
| **Open** | A folder with the front panel skewed open. Flat, no perspective, one 8-unit gap between the back and front planes. |
| **Save** | **The one metaphor worth changing.** A floppy disk is a machine nobody under forty has used. A down arrow into an open tray — arrow stem 8 wide, tray walls 8, open at the top — reads as "commit this" and matches the Save As and export family. |
| **Save As** | The same tray, with a pencil at lower right. Keeps the pair visibly related, which a floppy-plus-ellipsis never does. |
| **Open image** | A frame with a diagonal ridge and a disc in the upper corner — the same picture motif as the Image File node, so the toolbar action and the node that performs it agree. |
| **Run** | A filled triangle. No surrounding circle: at 18 px the ring eats the triangle's size for no information. Shift the triangle right by about 4 units — a centred triangle looks left-heavy. |

### On colour for Run

Green is conventional for Run and the set is monochrome. Keep the **glyph**
white and let the button carry any state colour. One coloured icon in a
monochrome toolbar reads as a mistake, and the button already has hover and
pressed states to work with.

---

## 5. The viewer toolbar — 17 icons, one family

The largest remaining block, and the most visible, since it sits above every
image. Three sub-families, and they should be distinguishable at a glance
before any individual icon is read.

**View (4)** — magnifier-based. One magnifier silhouette, four different
interiors: plus, minus, frame corners, "1:1". Keep the handle at a consistent
45° so the four read as a row.

**Measurement tools (9)** — a radio group, so they need the strongest family
resemblance. Give every one the same skeleton: a **subject** drawn as a thin
form in the lower-left, and the **measurement** drawn over it. Differentiate
by the annotation, not by the subject:

| Tool | Annotation |
|---|---|
| Pointer | arrow cursor, no subject |
| Point to point | two end dots, line between |
| Point to line | dot, perpendicular dropped to a line |
| Parallel lines | two parallel rules with a caliper span across |
| Line to line | two converging lines with a short gap mark |
| Angle | two arms and an arc |
| Circle from centre | centre dot and a radius to a ring |
| Circle 3-point | three dots on a ring |
| Line profile | a line with a waveform rising from it |

**Overlays and editing (4)** — the two overlay switches are toggles and should
look like *content*, not actions: a shapes glyph and a ruler glyph. The two
destructive ones stay as marks: a cross in a circle for "delete this one", a
bare cross for "clear all". The distinction between enclosed and bare is doing
real work; keep it.

---

## 6. Node icon conventions

The existing set already follows these. Worth writing down so a redesign keeps
them:

- **Source** nodes are literal objects: a blank rectangle, a picture, a stack,
  a numeral.
- **Basic** and **Filtering** nodes show the *effect* on a square field —
  a gradient, a split, a blurred edge — not an abstract symbol.
- **Morphology** uses the structuring element: a ring of dashes for dilate,
  a ring of dots for erode. The pair must differ at 16 px, which these do
  only just; consider widening the difference.
- **Blob** and **Analysis** show the result: an outlined region, a histogram,
  a profile trace.
- **Output** shows the destination: a framed picture.

Two nodes share an icon with another deliberately (Colour Range / RGB
Channel). Two more share by accident and want their own: **Region of
Interest** (a rotated rectangle with corner handles, distinct from Crop's
corner marks) and **Invert** (a square split black/white on the diagonal,
distinct from Threshold's vertical split).

---

> **Note.** Sections 1-6 describe the original 128-grid filled set, which is
> still in `res/toolbox icons/` but is no longer referenced. The application
> now runs on restyled Phosphor Regular - 256 canvas, stroked, round caps.
> `icon-inventory.md` has the specification for drawing a new one that blends
> in with it. The reasoning below still applies: the 8-unit rule becomes a
> 16-unit rule on the larger canvas, and the keyline argument is unchanged.

## 7. Checklist for a new icon

1. 128 canvas, 8-unit grid, 8-unit minimum weight and gap.
2. Two fills only: `#fff`, and `#333` at 50% for the keyline.
3. Filled paths, no strokes. Text converted to outlines.
4. Check it at **16 px on the dark panel, keyline ignored**. If it does not
   read there, it does not read.
5. Check it beside its neighbours in the same toolbar group — family
   resemblance matters more than any single icon.
6. Drop in `res/`, run `tools/svg2xaml.py`, run the tests.
