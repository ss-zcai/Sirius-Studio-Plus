# Saved pictures and the `.sann` annotation file

What a running station writes when it keeps its pictures, and what
**Sirius IV** reads. Decision 73 has the reasons.

---

## 1. Where pictures go

Chosen per station in **Sirius Builder ▸ Station ▸ Image Saving…**:

| Setting | |
|---|---|
| **Save** | None, Failed only, or All. *Failed only* keeps failures and inspections a node went wrong in. |
| **Format** | PNG (the default: lossless, keeps 10-, 12- and 16-bit), JPEG or BMP. JPEG and BMP hold 8 bits, so a deeper picture is written as PNG whichever is chosen. |
| **Annotations** | A `.sann` file beside each picture. On by default. |
| **Folder** | Blank for the project's `images` folder. A folder inside the project is kept relative to it; another drive suits a line that saves everything. |
| **Keep for (days)** | Older days' folders are deleted when the station starts and as each day begins. 0 keeps everything. |

The settings are the station's, in the `.sproj`:

```json
{ "name": "Top", "recipe": "recipes/Top.sgraph",
  "images": { "save": "failed", "format": "png", "keepDays": 30 } }
```

A station saves into a folder of its own name, with a folder for each day:

```
Line 3/
  images/
    Top/
      2026-11-11/
        20261111-142305-123_000042_FAIL.png
        20261111-142305-123_000042_FAIL.sann
```

The name is **when** (to the millisecond), **which inspection** (the
station's count since it started) and **how it went**: `PASS`, `FAIL`,
`ERROR` (a node went wrong) or `NONE` (nothing judges). Sorted by name, a
folder is in the order the pictures were taken.

**The picture** is the one the recipe's camera took, as it took it - its
Camera node, or else its Image Sequence or Image File - so it can be run
through the recipe again and give the same answer. A recipe with none of
those keeps its first image viewer's picture.

**Saving never slows the line.** The station copies the picture and
carries on; a thread of its own encodes and writes. If the disk falls 16
pictures behind, the next is not saved and the station says so. Below
1 GB free, saving stops and says so; inspecting carries on. Problems show
along the bottom of the running screen.

---

## 2. The annotation file

Beside each picture, with the same name and the extension `.sann`. JSON,
in UTF-8:

```json
{
  "format": "sirius-annotations",
  "version": 1,
  "image": "20261111-142305-123_000042_FAIL.png",
  "width": 2448,
  "height": 2048,
  "bits": 12,
  "station": "Top",
  "recipe": "Top.sgraph",
  "serial": 42,
  "time": "2026-11-11T14:23:05.123",
  "verdict": "FAIL",
  "passed": false,
  "grade": "Reject",
  "nodeErrors": 0,
  "results": { "Diameter": 12.483, "Count": 3, "Code": "LOT 4471", "Sealed": true },
  "layers": [
    { "name": "Main View",
      "shapes": [
        { "kind": "circle", "cx": 1204.5, "cy": 980.2, "radius": 310.4,
          "label": "cap", "colour": 4294901760,
          "metrics": { "area": 302700, "circularity": 0.998 } },
        { "kind": "box", "cx": 640, "cy": 512, "width": 80, "height": 24, "angle": 30 }
      ] }
  ]
}
```

| Field | |
|---|---|
| `format`, `version` | Always `sirius-annotations`; version 1. A reader ignores fields it does not know, so later versions add rather than change. |
| `image` | The picture's file name, without its folder. |
| `width`, `height`, `bits` | The picture's size, and the bits that carry it (12 for a 12-bit camera's frame); `bits` absent when not known. |
| `station`, `recipe`, `serial`, `time` | Which station, running which recipe, which inspection, when. |
| `verdict`, `passed`, `grade`, `nodeErrors` | How it went. `passed` is `null` when nothing judges. |
| `results` | Every number, yes/no, text and grade the recipe publishes, by published name, in the recipe's order. |
| `measurements` | Measurements drawn on the picture by hand afterwards, in Sirius IV - absent until there are some. Written as a recipe keeps a viewer's: `{ "tool": "PointToPoint", "points": [x, y, x, y], "colour": "#F59E0B" }`. |
| `layers` | One for each image viewer in the recipe, named as the viewer is, holding the shapes drawn on this picture; and one for each set of shapes the recipe publishes as a result. Shapes measured on a picture of another size - a cut-out region's - are left out: they would not sit where they belong. |

**Shapes**, in the picture's pixels and degrees - the same words the
Python and C++ node interfaces use:

| `kind` | Fields |
|---|---|
| `point` | `x`, `y` |
| `line` | `x1`, `y1`, `x2`, `y2` |
| `rect` | `x`, `y` (top left), `width`, `height` |
| `box` | `cx`, `cy`, `width`, `height`, `angle` - a turned rectangle |
| `circle` | `cx`, `cy`, `radius` |
| `ellipse` | `cx`, `cy`, `major`, `minor`, `angle` |
| `polygon` | `points`: `[[x, y], ...]`, `closed` |

Each may also have `label`, `colour` (`0xAARRGGBB` as a number), `stroke`,
`labelSize`, `anchor` and `labelOnly`, and `metrics`: the measurements it
carries, by name - what Filter Shapes and Shape Table work on.

In code: `Sirius.Core.Annotations.AnnotationFile` reads and writes it,
`ShapeJson` the shapes.

---

## 3. Sirius IV

`SiriusIV.exe`, beside Studio. **Station ▸ Browse Saved Images** in
Builder opens a station's folder in it; so does
`SiriusIV.exe "D:\Line 3\images\Top"`, or dropping a folder on it.

- **Files**, on the left: the folder's pictures, with its sub-folders - a
  station's days - by default, in the order taken, under a heading for
  each sub-folder with its count. Each has an icon saying its type (PNG,
  JPG, BMP, TIF, each a colour of its own), and under its name when it
  was written, its size and its width × height - read from the file's
  header as it comes into view, never by opening the picture. A
  station's pictures show PASS, FAIL or ERROR. **Show** lists every
  picture, or only failed, or only passed; **Search** (Ctrl+F) only those
  whose name or folder holds what is typed - a serial number, `FAIL`, a
  day. **Sort** by name - folder by folder, a station's pictures in the
  order taken, under their day headings - or by date modified, size,
  dimensions (all the pictures' headers read first, with progress shown)
  or type, either way round; ties keep the name order, and the viewers
  step along in the order shown. The sort is remembered.
  Right-click, or Ctrl+C and Ctrl+Shift+C, copies the names or full
  paths of those chosen; **Show in Explorer** opens their folder. The
  count along the top adds up their size. **F5** reads the folder again.
- **Viewers**: one, two side by side, 2 × 2 or 3 × 3 (Ctrl+1, 2, 4, 9).
  Choose several pictures in the list to see those; choose one to see it
  and the ones after it, so stepping down the list steps them all along.
- Each viewer is Studio's: zoom, pan, pixel values, 12-bit display, save.
  The shapes from the annotation file are drawn over the picture; the
  header's tooltip lists the station, time, results and layers.
- **Measurements belong to their picture.** Drawn with the viewer's
  measuring tools, they stay with the picture they were drawn on when the
  list moves on, and come back with it. A picture measured and not saved
  has an amber dot in the list, and the title a `*`. **Save** (Ctrl+S)
  writes each one's measurements into its `.sann` - beside what a station
  wrote there, or in a new one for a picture that had none. Closing, or
  opening another folder, with measurements not saved asks first.

**To come:** a panel listing the annotations as a table, exported as CSV;
switching layers on and off; matching pictures - by part of their names,
or in groups of N - shown and stepped together, with their viewers zoomed
and scrolled as one (Sync View); annotations from Run Folder and
`sirius-run.exe` (F20's batch half).
