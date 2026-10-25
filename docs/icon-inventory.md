# Icon inventory

Every icon the application shows. 34 nodes and 23 UI icons, all 57 currently
drawn from **Phosphor Regular**, restyled into the house style.

Companion to `icon-style.md`, which specifies how to draw a new one.

## The sets in `res/`

| Folder | Count | Keys | Status |
|---|---|---|---|
| `ui icons/` | 57 | `ph_*` | **live** - the set the application loads |
| `phosphor-icons/Extension/` | 5 | - | **source** - hand-drawn additions, see below |
| `toolbox icons/` | 32 | `tool-icon-NN` | kept, unreferenced - the earlier mono set |
| `toolbar icons/` | 119 | `icon_*` | kept, unreferenced - the original multi-colour set |

Of the 57 live icons, 52 are restyled Phosphor Regular and 5 are drawn in
house. The per-row "Current" columns in sections 1-3 below still name the
pre-Phosphor keys they were written against; the `ph_*` key for any row is
`KEYS` in `install_phosphor.py`.

All three are converted into `Themes/Icons.xaml`, so every key stays
resolvable and reverting any single icon is a one-word change:

```
python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Icons.xaml "res/ui icons"
```

## How Phosphor was restyled

Phosphor Regular is stroked at 16 on a 256 canvas - 1/16 of the canvas, the
same relative weight as the old toolbox set at 8 of 128. So the weight needed
no change. Two things were added:

- `currentColor` replaced with `#fff`, since the converter has no notion of
  an inherited colour.
- A keyline: each shape redrawn behind with a stroke **32 units wider**. A
  centred stroke extends half its width either side, so +32 puts the dark
  edge exactly 16 units - 1/16 of the canvas - outside the white silhouette,
  matching the house proportion.

Phosphor is MIT. `res/ui icons/LICENSE-phosphor.txt` travels with the set and
must ship with the product.

## The hand-drawn extensions

Five icons are not Phosphor. They are drawn in Phosphor's geometry - 256
canvas, stroke 16, round caps - and live in
`res/phosphor-icons/Extension/`, which is the editable source; the installed
copies in `res/ui icons/` are generated from it.

| Source | Key | Replaces |
|---|---|---|
| `point-to-line.svg` | `ph_measure_p2l` | `vector-two` |
| `parallel-line.svg` | `ph_measure_parallel` | `equals` |
| `circle-centre-radius.svg` | `ph_measure_circle` | `circle-dashed` |
| `circle-3-points.svg` | `ph_measure_circle3` | `circles-three` |
| `highpass.svg` | `ph_highpass` | `wave-sine` |

The measurement tools are the four Phosphor could only approximate - a
generic two-point vector or an equals sign says nothing about *which*
measurement - so they were drawn to show the actual construction: the point
and its perpendicular, the caliper span between two parallels, the radius
from a centre, three points on an arc.

An Illustrator export differs from the Phosphor source in two ways the
restyler cannot read, so it is normalised before being restyled:

- Paint lives in a `<style>` block as CSS classes rather than inline
  attributes. The restyler looks for a `stroke` attribute, finds none, and
  skips the shape as if it were the transparent bounds rect - producing an
  empty icon rather than an error. Classes are resolved to inline attributes
  first, and `stroke-width: 16px` loses its unit, which is valid CSS but not
  a valid WPF `Double`.
- An export may carry a leftover `<image>` reference layer - a base64 sketch
  the artwork was traced over, usually parked outside the viewBox. The XAML
  converter rejects `<image>` outright. It is dropped.

Both are handled by `install_extension.py`, which then hands each file to the
same restyler the Phosphor set went through, so the keyline and the `#fff`
repaint are identical. It fails loudly if a file comes out with nothing
painted, which is what an unresolved `<style>` looks like.

## Drawing a new one to blend in

Match Phosphor rather than the old toolbox set:

- **256 canvas**, not 128.
- **Stroked, not filled**: `fill="none"`, `stroke="#fff"`, `stroke-width="16"`.
- **Round caps and joins** - `stroke-linecap="round" stroke-linejoin="round"`.
  This is the clearest difference from the old set, which had squared ends.
- Keyline: the same shapes again, behind, in a `<g opacity=".5">` with
  `stroke="#333"` and `stroke-width="48"`.
- Keep to Phosphor's rhythm: most geometry lands on multiples of 8, with the
  live area roughly 24..232.

The quickest way to start is to copy the nearest `res/ui icons/ph_*.svg` and
edit its geometry - the two layers and the paint are already correct.

---

## 1. Node icons — 34

Where two nodes share a key it is deliberate and noted. Any row marked
**toolbar set** has no mono icon and reads differently from the rest.

### Source — 6

| Node | Current | Note |
|---|---|---|
| Blank Image | `tool-icon-25` | |
| Image File | `tool-icon-26` | shared with Image Viewer |
| Image Sequence | `tool-icon-27` | |
| Integer | `tool-icon-28` | "128" |
| Float | `tool-icon-29` | "0.6" |
| Vector | `tool-icon-30` | "(3,3)" |

### Basic — 11

| Node | Current | Note |
|---|---|---|
| Brightness / Contrast | `tool-icon-01` | |
| Crop | `tool-icon-02` | shared with Region of Interest |
| Region of Interest | `tool-icon-02` | **wants its own** — rotated rectangle |
| Threshold | `tool-icon-03` | shared with Invert |
| Invert | `tool-icon-03` | **wants its own** |
| Adaptive Threshold | `tool-icon-04` | |
| Colour Range | `tool-icon-05` | shared with RGB Channel |
| RGB Channel | `tool-icon-05` | your sheet labelled 05 for both |
| Colour Convert | `tool-icon-06` | |
| Resize | `tool-icon-08` | |
| Canny | `tool-icon-16` | |

### Filtering — 6

| Node | Current | Note |
|---|---|---|
| Gaussian Blur | `tool-icon-09` | |
| Box Filter | `tool-icon-10` | |
| Median Blur | `tool-icon-11` | |
| High-pass Filter | `tool-icon-12` | |
| Normalise | `tool-icon-13` | |
| 2D Filter | `icon_grid` | **toolbar set** — a convolution kernel |

### Morphology — 3

| Node | Current | Note |
|---|---|---|
| Advanced Morphology | `tool-icon-07` | |
| Dilate | `tool-icon-14` | |
| Erode | `tool-icon-15` | |

### Blob — 3

| Node | Current | Note |
|---|---|---|
| Find Blobs | `tool-icon-21` | your sheet called 21 "Find Contour" |
| Select Shape | `icon_list` | **toolbar set** — pick one of many |
| Shape Style | `icon_colour_stroke` | **toolbar set** — colour and weight |

### Transformation — 1

| Node | Current | Note |
|---|---|---|
| Polar to Linear | `tool-icon-22` | |

### Analysis — 3

| Node | Current | Note |
|---|---|---|
| Histogram | `tool-icon-31` | |
| Line Profile | `tool-icon-32` | |
| Shape Table | `icon_table` | **toolbar set** — rows of measurements |

### Output — 1

| Node | Current | Note |
|---|---|---|
| Image Viewer | `tool-icon-26` | shared with Image File |

---

## 2. Application toolbar and menu — 6

| Action | Current |
|---|---|
| New project | `icon_file` |
| Open project | `icon_folder_open` |
| Save project | `icon_save` |
| Save project as | `icon_save_more` |
| Open an image | `icon_image` |
| Run the graph | `icon_circle_play` |

---

## 3. Image viewer toolbar — 17

### View — 4

| Action | Current |
|---|---|
| Zoom in | `icon_zoom_in` |
| Zoom out | `icon_zoom_out` |
| Fit to window | `icon_zoom_fit` |
| Actual size (1:1) | `icon_zoom_origin` |

### Tools — 9

These are a radio group; only one is active at a time.

| Tool | Current |
|---|---|
| Pointer (pan, select, edit regions) | `icon_cursor` |
| Point to point | `icon_measure_p2p` |
| Point to line | `icon_dimension` |
| Parallel lines (caliper) | `icon_measure_l2l` |
| Line to line (closest approach) | `icon_measure_aspect` |
| Angle | `icon_measure_angle` |
| Circle from centre | `icon_measure_diameter` |
| Circle through three points | `icon_measure_arc` |
| Line profile | `icon_chart_line` |

### Overlays and editing — 4

| Action | Current |
|---|---|
| Show results from the graph (toggle) | `icon_image_shape` |
| Show hand-drawn measurements (toggle) | `icon_ruler` |
| Delete the selected measurement | `icon_circle_cross` |
| Clear all measurements | `icon_cross` |

---

## 4. Not icons today

Worth knowing, in case the redesign wants to cover them:

- **Node status** — a coloured dot on the node header and in the properties
  panel: grey idle, green ready, amber warning, red error. Drawn as a shape,
  not an icon.
- **Ports** — squares for images, circles for values, drawn as shapes and
  coloured by data type.
- **Panel tabs** — text only, no icons.

---

## 5. Spare mono icons

Six from the current sheet have no node yet:

| Key | Drawn as | Waiting for |
|---|---|---|
| `tool-icon-17` | OCR | deferred |
| `tool-icon-18` | Bar Code | deferred |
| `tool-icon-19` | Data Matrix | deferred |
| `tool-icon-20` | QR Code | deferred |
| `tool-icon-23` | Find Circle | not built |
| `tool-icon-24` | Find Line | not built |

## 6. Gaps worth filling in a redesign

1. **Region of Interest** and **Invert** are borrowing Crop's and Threshold's
   icons.
2. **2D Filter**, **Select Shape**, **Shape Style** and **Shape Table** are
   still on the multi-colour toolbar set and stand out in the toolbox.
3. The **viewer toolbar** is entirely multi-colour. If the mono style is the
   direction, these 17 are the largest single block left.
4. Likely future nodes, if you want to draw ahead: Combine Shapes, Filter
   Shapes, Calibration, Template Match, Fit Line, Fit Circle, Measurement
   (graph-side), Camera source, and the pass/fail decision node.
