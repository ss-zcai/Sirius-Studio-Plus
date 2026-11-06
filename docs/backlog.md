# Backlog

What is left to do, in a proposed order, for review. `roadmap.md` says
where the product stands; this says what could come next. Updated
6 November 2026, with 1.0.9 released and 1.0.10 in progress.

Each item has an ID to refer to it by, a rough size, and where it came
from when that matters. The order is a proposal: change it, strike things
out, or move them between sections.

**Sizes:** **S** an hour or two. **M** a day or so. **L** several days,
or needs design first.

---

## 1. Before any build goes to a customer

Licence questions, not code. Internal testing is fine meanwhile; a
customer build is not, until these are settled.

- **L1. Emgu CV: GPLv3 or a commercial licence.** The one real blocker.
  Recommendation as discussed: buy the Windows tier, stay on 4.13 under
  the v4 rev 2 agreement (no post-termination clause), and get Emgu's
  written confirmation that §4 - no redistribution inside a developer
  toolkit, nothing of "substantially the same functionality" - does not
  catch a node-graph authoring tool. A procurement lead time, so worth
  starting early. Fallback: OpenCvSharp (Apache 2.0), a mechanical port.
  See `decisions.md` §8.
- **L2. pylon licence conditions 4-6.** Our own licence agreement at
  least as restrictive as Basler's, excluding Basler from liability, and
  indemnifying Basler. Legal review, with L1.
- **L3. Hikrobot MVS terms.** MVS's licence lists only its open-source
  parts; the appendix that should hold Hikrobot's own terms is empty.
  Find out whether `MvCameraControl.Net.dll` may ship beside the
  application.
- **L4. YOLO (Ultralytics) is AGPL-3.0.** Only if a Python node library
  we ship uses it: closed distribution needs Ultralytics' commercial
  licence.

Settled, for the record: AvalonDock (Ms-PL, notices fixed), Clipper2
(Boost), ZXing, OpenCV and Tesseract (Apache 2.0), Phosphor and the rest
(MIT).

---

## 2. Now: small, and worth doing next

- **N1. Pack 1.0.10** (S). Ready to go out: Crop folded into Region of
  Interest, ROI without its duplicate box, Offset Shapes and Transform
  Shapes, and the Camera node saying why a camera make is missing. The
  notes must lead with **pylon 26.10 or later** for Basler cameras.
- **N2. Test with real cameras** (M, needs hardware and a colleague).
  Basler is tested only on pylon's emulator, Hikrobot only on MVS's
  virtual cameras. Wanted: GigE timing and dropped frames, the hardware
  trigger (Basler Line 1, Hikrobot Line 0), colour cameras, and a camera
  unplugged mid-run. Also confirm the new "older pylon" note on a PC with
  pylon 7.3.
- **N3. Icons** (S each, drawn by you). Four node icons exist only in
  the generated `Themes/Icons.xaml`, with no SVG in `res/ui icons`:
  `ph_find_curve`, `ph_lens_calibration`, `ph_line_defects`,
  `ph_undistort` - so regenerating the dictionary deletes them. And
  Offset Shapes and Transform Shapes borrow Dilate's and Resize's icons.
- **N4. A `.gitattributes`** (S). Line endings are normalised per machine
  today; settle them before a second person works in the repository.
- **N5. Tidy-ups** (S each):
  - delete `src/Sirius.NodeEditor`, an empty project (one `Class1.cs`);
  - decide about the upright `Rectangle` region editor: no built-in
    node uses it since Crop went (decision 61). Delete it, or keep it for
    custom nodes and say so in the SDK.

---

## 3. Next: features

Larger pieces, roughly in order of value to testers. Several want a word
from you before they start.

### Bit depth

- **F1. Real bit depth through the tools** (L, your call on timing). A
  12-bit image needs thresholds sixteen times larger in Threshold, Find
  Blobs, Min Contrast and the rest. The fix: `ImageData` carries its
  significant bits, set by the source that knows them (a Mono12 camera,
  an Image File setting), and tool ranges and the histogram follow it.
  Then Image File's Read Mode default can change from Any Color (8-bit).
  Includes **F1a. Histogram beyond 8-bit**, which today warns and passes
  through.

### The runtime, HMI and PLC

- **F2. Write the runtime design document** (M, design only). The
  fourteen ideas agreed on 1 November - recipe vs project, published
  results and tunables, stations, the HMI designer, the runtime exe, PLC
  handshake and drivers, licensing per machine, edge vs cloud - into
  `docs/`, with the open questions, before any code. Needs from you:
  - what Sirius Client, Interface and Server are written in, and their
    protocols, and whether that code can be read;
  - how the Server stores results and images;
  - typical line speed and image size;
  - what happens today when the Server cannot be reached.
- **F3. Signal out** (M, after F2). The first PLC step: Modbus TCP, then
  digital I/O. Today a verdict reaches the status line and a CSV, and
  nothing outside the PC.
- **F4. Counts that survive a restart** (S-M). Counter starts from zero
  each session. Wanted by the runtime anyway.

### Sub-graphs

- **F5. Promoted parameters** (M). Change a setting inside a sub-graph
  from outside it, so two copies can differ.
- **F6. Reusable sub-graphs** (L). Saved as a file, placed in several
  recipes or several times in one, edited once.
- **F7. Group a selection into a sub-graph** (M), with sockets where its
  wires crossed.
- **F8. Sub-graph loose ends** (S each): panel titles inside carry only
  the node's name, so two copies open two panels titled alike; sub-graph
  tabs are not reopened on restart; a socket restored by undo comes back
  at the end of the node.

### Inspection tools

- **F9. Two-point references** (M). A region following two found holes,
  for the angle of a part with no straight edge.
- **F10. Offset in calibrated units** (S). Offset Shapes in mm when a
  Calibrate is wired, as Measure already reports.
- **F11. Data Matrix and QR print grading** (L). ISO 15415 quality
  grades from Read Code - what a customer with a code spec asks for.
- **F12. More text languages** (S-M). Read Text has English only; each
  language adds its model to the package.
- **F13. Real wide-lens test for Lens Calibration** (S, needs a lens).
  Tested on synthetic views only.
- **F14. More blob measures** (S-M) - fitted ellipse, inner and outer
  radius, moment invariants - when something needs them.

### Custom nodes

- **F15. Python nodes: faster and easier to debug** (M each): shared
  memory for images instead of the pipe (about 13 ms per 5 MP colour
  image now); a Python log view; warming workers in parallel at start-up,
  which waits on each library's imports in turn today; picking up changed
  declarations without a restart.
- **F16. C# custom nodes** (S-M each): reloading a library without a
  restart, icons a library supplies, IntelliSense for the `Sirius.Core`
  types in the SDK package.

### Editing

- **F17. A grid editor for Matrix** (M). Typed as text today: fine for
  3x3, tiresome beyond.
- **F18. More text parameters driven by a wire** (S each): a file path,
  an output name.
- **F19. Growing inputs on Blend and Logic** (S), so they take more than
  two images. Chaining serves until somebody asks.

---

## 4. Later, when there is a reason

- **R1. Rename the Blob group to Shapes** (S, but changes the SDK). It
  now holds the shape tools too, but its name is part of the custom node
  SDK (`NodeGroup.Blob`, `group="Blob"`). Bundle with the next SDK change
  that breaks things anyway.
- **R2. A true offset curve for ellipses** in Offset Shapes, rather than
  an ellipse with both axes changed - matters only for offsets a good
  part of the minor axis.
- **R3. Keep NodeGraphKit in step** with the application's theme, if the
  other team wants that. It is a copy and does not follow changes by
  itself.
- **R4. Matrix arithmetic**, if anything ever needs to add or multiply
  matrices.

---

## 5. On hold, by decision

Recorded so they are not picked up by accident.

- **H1. USB webcams** - on hold, your decision.
- **H2. Match Template turning to find a rotated feature** - kept
  position-only; angles come from other tools. So no angle from Match
  Template for a Reference either.
- **H3. Image File's Read Mode default** - stays Any Color until F1.
- **H4. Soft Light blend mode** - left out until someone says which of
  the incompatible definitions they mean.
