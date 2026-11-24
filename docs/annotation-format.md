# Saved pictures and the `.sann` annotation file

What a running station writes when it keeps its pictures, and what
**Sirius IV** reads. Decision 73 has the reasons.

---

## 1. Where pictures go

Chosen per station in **Sirius Builder ▸ Station ▸ Image Saving…**:

| Setting | |
|---|---|
| **Save** | None, Failed only, or All. *Failed only* keeps failures and inspections a node went wrong in. |
| **Format** | TIF (the default since 22 November 2026: lossless, keeps 10-, 12- and 16-bit), PNG (the same), JPEG or BMP. JPEG and BMP hold 8 bits, so a deeper picture is written as PNG whichever of them is chosen. A project written before TIF, saying no format, means PNG. |
| **Annotations** | A `.sann` file beside each picture. On by default. |
| **Folder** | Blank for the project's `images` folder. A folder inside the project is kept relative to it; another drive suits a line that saves everything. |
| **Keep for (days)** | Older days' folders are deleted when the station starts and as each day begins. 0 keeps everything. |
| **Most size (GB)** | The most the station's pictures may take, its Kept sample not counted. Over it, the oldest passed pictures go first, then the oldest failures, down to 90% of it. 0 for no limit. |
| **Leave free (GB)** | Free space always left on the drive. Under it, pictures go as for Most size, until 1 GB more is free. 0 for none. |
| **Keep a sample (%)** | Of the pictures housekeeping deletes, by days or by size, the share kept for records instead: moved with their `.sann` to the station's `Kept` folder, never deleted by itself. 1 keeps the inspections numbered a multiple of 100 - spread through the day, passed and failed alike. 0 keeps none. |

The settings are the station's, in the `.sproj` (the three limits only
when set):

```json
{ "name": "Top", "recipe": "recipes/Top.sgraph",
  "images": { "save": "failed", "format": "tiff", "keepDays": 30,
              "maxGB": 200, "leaveFreeGB": 20, "samplePercent": 1 } }
```

A station saves into a folder of its own name, with a folder for each day:

```
Line 3/
  images/
    Top/
      2026-11-11/
        20261111-142305-123_000042_FAIL.tif
        20261111-142305-123_000042_FAIL.sann
      Kept/
        2026-10-02/
          20261002-081500-007_000100_PASS.tif
          20261002-081500-007_000100_PASS.sann
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
1 GB free, saving stops and says so; inspecting carries on, and saving
starts again once there is room - housekeeping's, or made by hand.
Problems show along the bottom of the running screen.

**Housekeeping** runs on a thread of its own too - when the station
starts, as each day begins, and when a limit is passed - so it never
holds up saving. Within a day folder it deletes only pictures named as a
station names them, and their `.sann`; a day past the days kept goes
whole. A picture written in the last minute is left, its `.sann` perhaps
not yet beside it. Folders that are not days - `Kept` among them - are
never touched.

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
| `part` | What whatever asked for the inspection knew about the part - a PLC's `{ "PalletId": 12, "PositionId": 3 }`, from a controller's Gather and Inspect nodes - absent when nothing said. The part ID pictures from two stations can be matched on. |
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
  each sub-folder with its count. Each has an icon saying its type - the
  icon set's file-png and file-jpg; file-image for BMP and TIF until they
  have their own - and under its name when it
  was written, its size and its width × height - read from the file's
  header as it comes into view, never by opening the picture. A
  station's pictures show PASS, FAIL or ERROR. **Show** lists every
  picture, or only failed, or only passed; **Search** (Ctrl+F) only those
  whose name or folder holds what is typed - a serial number, `FAIL`, a
  day. Beside the search box, two buttons: which way round, and **Sort
  by**, a menu. Sorted by name - folder by folder, a station's pictures in the
  order taken, under their day headings - or by date modified, size,
  dimensions (all the pictures' headers read first, with progress shown)
  or type, either way round; ties keep the name order, and the viewers
  step along in the order shown. The sort is remembered.
  Right-click, or Ctrl+C and Ctrl+Shift+C, copies the names or full
  paths of those chosen; **Show in Explorer** opens their folder. The
  count along the top adds up their size. **F5** reads the folder again.
- **Viewers**: one, two or three side by side, 2 × 2 or 3 × 3 (Ctrl+1,
  2, 3, 4, 9). Choose several pictures in the list to see those; choose
  one to see it and the ones after it, so stepping down the list steps
  them all along. Each viewer is a tab, titled with its picture's name
  (with its folder too when two shown share a name) and its verdict, and
  can be dragged anywhere - one above another, as tabs in one place, or
  out onto a screen of its own; it keeps its place as the list moves on.
  Choosing a layout, even the one in use, puts them back in its grid.
- **Match** (Ctrl+M) makes pictures that belong together - two cameras'
  views of one part, a part lit three ways - into sets, by one of:
  - **in groups of N** (2 to 9) down the list, as it is sorted - for
    names that already sort a part's pictures together;
  - **names alike in their first N characters** - as the old ICB matched,
    on 20;
  - **names alike in the part a pattern finds** - a regular expression on
    the name without its folder or extension, ignoring case. The part in
    brackets must be alike (a group named `key` wins over the others);
    without brackets, all it finds. `^(.+)_cam\d+$` matches
    `part0042_cam1` with `part0042_cam2`. A name it does not find is a
    set of its own.

  Names are compared without their folders, so `cam1\0042.png` and
  `cam2\0042.png` - cameras saving into folders of their own - are alike.
  The dialog shows what a rule makes of the pictures listed before it is
  used. Matched, the list has a line for each set, by its first picture,
  with **+N** for the rest and the worst verdict among them; choosing it
  shows the set, a picture to a viewer, each camera in the same place
  every time (folder-and-name order), and enough viewers are laid out for
  the largest set. Sets are made before the filter and search, so
  **Failed** shows each set with a failure in it whole.
- **Sync** (Ctrl+L, on by default): the viewers zoomed and scrolled as one
  - the same pixel in the same place in each - and the pointer's place on
  one marked with a small cross on the others.
- Each viewer is Studio's: zoom, pan, pixel values, save. A **16-bit
  picture** - 10, 12 or 14 bits carried in 16 - is shown by the bits its
  `.sann` says; with no `.sann`, as a crop has none, by its brightest
  value (up to 1023 is 10-bit, 4095 12-bit, 16383 14-bit). The viewer's
  picker - Auto, 16, 14, 12, 10-bit or Stretch - shows beside its tools
  for a 16-bit picture, and the choice is every viewer's.
  The shapes from the annotation file are drawn over the picture; the
  tab's tooltip lists the station, time, results and layers.
- **Measurements belong to their picture.** Drawn with the viewer's
  measuring tools, they stay with the picture they were drawn on when the
  list moves on, and come back with it. A picture measured and not saved
  has an amber dot in the list, and the title a `*`. **Save** (Ctrl+S)
  writes each one's measurements into its `.sann` - beside what a station
  wrote there, or in a new one for a picture that had none. Closing, or
  opening another folder, with measurements not saved asks first.

- **Annotations**, docked below the viewers (View ▸ Annotations): the
  picture in the viewer last clicked, with its verdict, and two tabs.
  **Results**: station, recipe, inspection, time, verdict and grade,
  size; what was known about the part (`Part · PalletId`); then every
  result, by published name. **Shapes**: a row for each shape of the
  layers switched on - its number, layer, kind, label, centre (X, Y) and
  size (W, H: a turned box's sides, an ellipse's axes, a circle's
  diameter, else the box it fits in), then a column for each measurement
  any of them carries. A row clicked picks its shape on the picture, and
  a shape clicked there picks its row; double-clicked (or Enter), the
  viewer zooms to it - the others with it, while Sync is on. Sorting a
  column keeps each row with its shape.
- **Layers on and off**: a tick box for each of the picture's layers,
  with its count, along the top of the panel. A layer switched off is
  gone from every viewer and every picture after, until switched on - for
  comparing one viewer's shapes across pictures. Not kept between
  sessions.
- **Export Annotations as CSV…** (Ctrl+E, File menu, or the panel's
  button): the pictures chosen in the list, or all listed as filtered and
  searched - with each matched set's other pictures - as one CSV, a row
  for each **picture** (file, station, recipe, inspection, time, verdict,
  grade, node errors, a `Part: name` column for each part value, then a
  column for each result) or for each **shape** (file, station,
  inspection, verdict, the shape's number on its picture, layer, kind,
  label, X, Y, W, H, then a column for each measurement). Columns are
  gathered across all the pictures, in the order first met, so a recipe
  changed part way keeps both its old and its new results; a picture
  without an annotation file has a row of just its file and verdict, or
  no shape rows. Every layer is exported, switched on or not. Numbers
  with a full stop and to six places, whatever the machine's language;
  times as `2026-11-21 14:23:05.123`; yes/no as `TRUE`/`FALSE`; UTF-8
  with its mark, so Excel opens it as written. In code:
  `Sirius.Core.Annotations.AnnotationCsv`, `AnnotationTable` the panel's
  tables.

- **To Folder** (the toolbar, or right-click in the list): **Copy to
  Folder** or **Move to Folder** - the pictures chosen, several with Ctrl
  or Shift, a matched set whole - each with its `.sann`, side by side in
  the folder chosen whatever day they came from: sorting pictures into
  class folders. Each opens the folder dialog in the folder last used,
  remembered between sessions, so the same one again is a click. Names there already
  are asked about - Yes replaces them, No leaves them and goes on with the
  rest. A move is confirmed, and the list read again after it.
  Measurements drawn and not saved are saved into their `.sann` first, so
  they go with their pictures.
- **Export Pictures with Annotations Drawn…** (Ctrl+Shift+E, File menu):
  the pictures chosen, or all listed, each written as a PNG with its
  annotations drawn on - as the viewer's Save Image with Overlays draws
  them: the layers switched on, and measurements drawn by hand, saved or
  not. For showing pictures to someone without Sirius IV. Each keeps its
  name, as `.png`, in its own sub-folder (a station's day) under the
  folder chosen; two of one name and different kinds (`part.tif`,
  `part.bmp`) become `part_tif.png` and `part_bmp.png`. A folder that
  would draw a PNG over its own picture is refused. A station never saves
  drawn pictures itself: it keeps the picture as taken and its `.sann`,
  and drawn copies are made here when wanted.

---

## 4. A folder run's annotations

**Run Folder** in Studio and **`sirius-run.exe`** write a `.sann` beside
each picture they run, in the picture's own folder, as a station writes
one beside each picture it saves: the recipe (its file name), the verdict,
every published result, and the shapes each image viewer drew. Sirius IV
then shows a folder judged at the desk as it shows a station's.

A picture with a `.sann` already - a station's, being run again - keeps
what is about the picture: its station, inspection, time, part, bits and
measurements drawn by hand. What the run found replaces the rest. A
`.sann` that cannot be written - a read-only folder - is said at the end,
and the run goes on. `sirius-run --no-annotations` writes none.

In code: `FolderRun.RunAsync`'s `keep`, given each inspection as a
station's recorder is (`InspectionCapture`), and
`Sirius.Vision.Runtime.FolderAnnotations`, which writes them.

## 5. The earlier image viewer's files, read

The earlier vision system wrote a picture's annotations beside it as
JSON named after the **whole** picture name: `picture.tiff.json`. Sirius
IV reads them - **never writes or changes them** - wherever a picture has
no `.sann` (decision 92). The viewers, the Annotations panel, layers, the
CSV and drawn exports and ROIs from All take them as a `.sann`.

| Earlier file | Read as |
|---|---|
| `output.annotations` (JSON in a string): `Circle` - Centre, Radius | a circle |
| `RotatedRectangle` - Centre, Width, Height, Rotation | a turned rectangle; Rotation clockwise, as ours and OpenCV's |
| `Name` | the shape's label, and its **layer** - "inspect area", "mark", "dm", "text", "defect" - switched on and off in the panel |
| `Colour` [b, g, r] | the shape's colour - **BGR**, as OpenCV's, until confirmed |
| `LineWidth` | not kept: drawn at the viewer's width |
| `meta` (JSON in a string) | the part: batch, pallet, position, QR code, station, `MultiSeq`, `GroupId`, `Requests` |
| `output` - result, defectCode, message, processingTime; `algo`, `algoVersion`, `args` | the results |
| `output.info` (JSON in a string, its Windows paths unescaped) | more results, read leniently: `processing_time`, `features`… |
| `requestId` | the serial, and the time: `260511155932312022` is 11 May 2026 15:59:32.312 |
| `meta.PixelFormat` `Mono8`, `Mono12` | the bits |

**The verdict is not judged yet**: which result and defect codes pass is
still to be learned from more examples. Until then the code and message
are its grade - "2002 Scratch", "202 Image received" - and its verdict
NONE.

Measurements drawn and saved go into a `.sann` beside it, which starts
from what the earlier file says; the earlier file stays as it was, and
from then the `.sann` is read. **To Folder** copies or moves the earlier
file with its picture.

A multi-image part's shots share `GroupId`: matching by the pattern
`^\d+-(\d{17})` puts them side by side.

In code: `Sirius.Core.Annotations.LegacyAnnotations`, and
`AnnotationFile.ReadFor`, which tries the `.sann` first.
