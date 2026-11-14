# Backlog

What is left to do, in a proposed order, for review. `roadmap.md` says
where the product stands; this says what could come next. Updated
14 November 2026.

**Since the last update** (11-13 November): Sirius IV gained its file
list, sorting, matching and Sync View, and a tab per viewer; the PLC
arrived as **controllers** in a project - the workflow engine, Builder's
editor and PLC window, the simulated PLC, controllers running in Test Run
and Edge (F23a-c, decisions 74-76); and the **PLC kit** went to the PLC
team: Builder, the driver SDK, a sample driver and the guides (F23e,
decision 77). Next on the PLC: the real S7 driver (F23d).

Each item has an ID to refer to it by, a rough size, and where it came
from when that matters. The order is a proposal: change it, strike things
out, or move them between sections.

**Sizes:** **S** an hour or two. **M** a day or so. **L** several days,
or needs design first.

---

## 1. Before any build goes to a customer

Licence questions, not code. Internal testing is fine meanwhile; a
customer build is not, until these are settled. **Everything about them
is in `licences.md`**; this is the list.

- **L1. Emgu CV: GPLv3 or a commercial licence.** The one real blocker.
  Buy the Windows tier on the v4 rev 2 agreement, with §4 confirmed in
  writing - the custom-node SDK and the PLC kit included in the question.
  Fallback: OpenCvSharp.
- **L2. pylon licence conditions 4-6**: our own licence agreement (L6),
  excluding Basler from liability, and indemnifying Basler. Legal review.
- **L3. Hikrobot MVS**: may `MvCameraControl.Net.dll` ship? Its terms
  are missing. (Listed in `THIRD-PARTY-NOTICES.txt` since 13 November.)
- ~~**L4. YOLO (Ultralytics) is AGPL-3.0**~~ - not ours: we do not use it.
  A matter for custom node developers, whom the guides warn.
- **L5. PLC communication libraries**: HslCommunication's **Enterprise
  licence is held**. Left: check that the contract's words allow shipping
  it in our product and in the PLC kit; agree how the activation code
  reaches a machine (the guide proposes `SIRIUS_HSL_CODE`); add it to the
  notices when the first driver using it ships.
- **L6. Our own licence agreement (EULA)**: none yet. Needed for any
  customer build, and by pylon (L2).

---

## 2. Now: small, and worth doing next

- ~~**N1. Pack 1.0.10**~~ - done, 6 November 2026.
- ~~**N2. Test with real cameras**~~ - done, 7 November 2026.
- ~~**N6. Pack 1.0.11 and SDK 1.0.3**~~ - done, 11 November 2026.
- **N7. Pack 1.0.12** (S). Since 1.0.11: Sirius IV's file list, sorting,
  matching, Sync View and viewer tabs; controllers in Builder and Edge;
  the Project panel and one toolbar for the tab in front. Release notes
  to write. The PLC kit is packed on its own (`pack-plc-kit.ps1`, 1.0.0).
- **N3. Icons** - mostly settled. The four that had no SVG
  (`ph_find_curve`, `ph_lens_calibration`, `ph_line_defects`,
  `ph_undistort`) have one now, so regenerating `Themes/Icons.xaml` is
  safe. Left: Offset Shapes and Transform Shapes borrow Dilate's and
  Resize's; the new PLC icons (controller, the nine workflow nodes) and
  BMP and TIF file icons are yours to redraw if you want.
- **N4. A `.gitattributes`** (S). Line endings are normalised per machine
  today; settle them before a second person works in the repository - the
  PLC team may be the first.
- **N5. Tidy-ups** (S each):
  - delete `src/Sirius.NodeEditor`, an empty project (one `Class1.cs`);
  - decide about the upright `Rectangle` region editor: no built-in
    node uses it since Crop went (decision 61). Delete it, or keep it for
    custom nodes and say so in the SDK.
- **N8. Tests that fail now and then** (S each), passing when run again:
  `HikrobotCameraTests.Two_stations_run_two_hikrobot_cameras_at_once`
  times out on a busy machine; `EditHistoryTests.The_styler_colours_the_whole_selection_in_one_step`
  failed once in a full run, 13 November. Worth finding out why before
  they hide a real failure.
- **N9. A station renamed leaves its workflows behind** (S). An Inspect or
  Station Result naming the old name is reported by the workflow's check
  ("There is no station called ..."), but not changed. Rename it in the
  workflows too, as the screen's widgets are.

---

## 3. Next: features

Larger pieces, roughly in order of value to testers. Several want a word
from you before they start.

### The PLC

- **F23. The PLC as controllers in a project** - decisions 74-77; the
  design is in `plc-workflows.md` and `plc-drivers.md`. A project holds
  stations and **controllers** - a PLC each, with the **workflow** that
  watches it, reads and writes it, and has stations inspect. A new brand
  is a driver DLL in `plc-drivers\`: no new nodes, no change to Builder.
  It replaces Sirius Interface, which was rebuilt for each customer.
  - ~~**F23a. The engine and nodes, with the simulated PLC**~~ - done 13
    November 2026 (decision 75): passes started by a PLC Monitor or a
    Station Result; branches and joins by wiring; one pass at a time;
    timeouts; reconnecting. PLC Monitor, Read, Write, Pulse, Wait For,
    Gather; Inspect, Station Result, Result Value. The part's values go
    into the picture's `.sann` as `"part"`.
  - ~~**F23b. Controllers in Builder**~~ - done 13 November 2026 (decision
    76): stations and controllers in one Project panel; each workflow in
    a tab on Studio's canvas; the PLC window - Test Connection, read and
    write any type, the simulator's memory by hand; one toolbar, tools
    panel and Properties for the tab in front.
  - **F23c. Controllers on the line** (M) - **mostly done**: the runtime
    window runs the controllers, so Builder's Test Run and Edge both do,
    their state along the bottom. Left:
    - a **Controller widget** for screens: connected or not, the last
      event, the last error;
    - a lost PLC as an **alarm** an operator cannot miss, not only a line
      of text;
    - in Edge, no way to look at the PLC: a PLC window for maintenance,
      behind a user level.
  - **F23d. The first real drivers** (M each) - **next**: **Siemens S7**,
    then **Modbus TCP**, on HslCommunication (L5), as `plc-drivers.md` §4
    designs them. Needs from you: permission to download the
    HslCommunication package, and an S7 on the bench to run the
    conformance check and the cable pull against. The guide's HSL code is
    from its documentation and not yet compiled. Or the PLC team writes
    them from the kit, and we review.
  - ~~**F23e. The start kit for the PLC team**~~ - done 13 November 2026
    (decision 77): driver discovery and its version check, driver
    settings in Builder, the conformance check, the guides, and the PLC
    kit zip (`tools/pack-plc-kit.ps1`) with a sample driver tested
    outside the repository. Left: `Sirius.Plc` as a NuGet package, if the
    team would rather than `sdk\lib` (S).
  - **F23f. Workflow loose ends** (S-M each), as use asks:
    - the **Result Log** writing the part's values, and a screen's Value
      widget showing one (PalletId);
    - the **workflow editor**: renaming a node on the canvas, as Studio
      does; each node's state lit while a Test Run runs; zoom to fit;
    - **two Inspects at once** - within a pass they run one after the
      other today;
    - reading many addresses **in one exchange** (the default reads them
      in turn), and a PLC's own subscriptions in place of polling;
    - nodes for the **handshake** (Ready, Busy), **recipe change by
      product code**, and a **timer** to start passes (runtime design
      §9);
    - one workflow driving **several PLCs**, if a sequence must span two;
    - **digital I/O** cards, as a driver.

### The runtime and HMI

- **F2. The runtime design** (`runtime-design.md`) - the MVP of its
  section 0 is built (decisions 65-71): published names, projects and
  stations, screens and the screen designer, settings changed on the line,
  Builder and Edge; and now its PLC (F23). Next on it: counts kept over a
  stop (F4), user levels, records and upload to the Server. Still needed
  from you, before records and upload:
  - what Sirius Client, Interface and Server are written in, and their
    protocols, and whether that code can be read;
  - how the Server stores results and images;
  - typical line speed and image size;
  - what happens today when the Server cannot be reached.
- ~~**F3. Signal out**~~ - done as F23: a verdict reaches the PLC.
- **F4. Counts that survive a restart** (S-M). Counts start from zero
  each session. Wanted by the runtime anyway.
- **F20. Saved images and their annotations** - the runtime's half done
  (decision 73, `annotation-format.md`): a station keeps none, failed or
  all of its pictures, with a `.sann` beside each; **Sirius IV**
  (`SiriusIV.exe`) browses them - file list, sorting, measuring, matching,
  Sync View, a tab per viewer. Left:
  - **F20a. The batch half** (S-M): Run Folder and `sirius-run.exe`
    writing the same `.sann` beside each image they run.
  - **F20b. Sirius IV, next panels** (M each): the annotations as a
    table (shapes and their measurements, results), a row double-clicked
    zooming to its shape, and exported as one CSV across the pictures
    chosen (the old ICB's Export Features); layers switched on and off.
  - **F20e. Matching on the part** (S-M) - **now possible**: matching by
    name is done (12 November); since F23a a picture's `.sann` carries the
    part's values (`"part": { "PalletId": 12 }`), so a match rule "same
    part value" pairs two stations' pictures of one part, which no name
    could.
  - **F20c. A disk quota** (S-M), beside Keep for: when a size is reached,
    oldest passes go first, then oldest failures (runtime design §8).
  - **F20d. Failed and one pass in N** (S), the policy the runtime design
    proposed beside none, failed and all.
  - **F20f. Sirius IV loose ends** (S each): the viewers' arrangement
    remembered between sessions; copy and move into class folders, and
    the old ICB's measuring in mm, if wanted.

### Bit depth

- ~~**F1. Real bit depth through the tools**~~ - done, 7 November 2026
  (decision 64), with the histogram (F1a) and Read Mode: Unchanged for
  new nodes.
- ~~**F1b. Bits for Python nodes**~~ - done, 7 November 2026: `image.bits`,
  `image.max_value`, `with_bits()`; SDK file 1.1, packed in SDK 1.0.3.

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
- ~~**F21. C++ custom nodes**~~ - done, 11 November 2026: a native DLL
  through a plain C interface (`sirius_node.h`, with `sirius_node.hpp`
  over it), in the application's process, with exceptions and hardware
  faults caught at the boundary. See `cpp-nodes.md` and decision 72.
- **F22. C++ nodes: what is left** (S-M each):
  - **built against real OpenCV and HALCON.** The OpenCV glue
    (`to_mat` / `from_mat`) is compiled and tested against a stand-in
    for OpenCV's header, and the HALCON lines in the guide are from its
    documentation: neither library is on the development PC. Needs a
    library of yours built against each;
  - **a process of its own, as an option** for a library that cannot be
    trusted not to crash in the ways that cannot be caught (its own
    threads, `abort()`), at the cost of a copy of each image;
  - **saying, at the next start, what closed the application** when one
    of those did - Windows records the faulting DLL;
  - a way to test a node without the application, as `NodeRunner` is for
    Python.

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
- ~~**H3. Image File's Read Mode default**~~ - now Unchanged for new nodes, with F1.
- **H4. Soft Light blend mode** - left out until someone says which of
  the incompatible definitions they mean.
