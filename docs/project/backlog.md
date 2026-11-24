# Backlog

What is left to do, in a proposed order, for review. `roadmap.md` says
where the product stands; this says what could come next. Updated
24 November 2026.

**Since the last update** (18-24 November):

- **1.0.14 packed** (23 November): the part and its counts (F25, F4),
  user levels, cameras in the project (F29, two slices), a controller on
  the line - widget, alarm, PLC window in Edge (F23c) - the workflow
  editor's loose ends (F23f), Find Rectangle and ROIs (F30-F32), saved
  pictures kept in check (F20a/b/c/f, TIF). Builder 1.0.4, Sirius IV
  1.0.3; the PLC kit packed again.
- **Sirius IV 1.0.4** packed alone (24 November): the earlier image
  viewer's `name.tiff.json` annotation files read (decision 92).
- **Tidy-ups**: N5 and N8 closed; F33 (result codes) designed.

**Proposed order from here:**

1. ~~**F26a, a set of shots at one station**~~ - done 24 November
   (decision 95).
2. **F33, result codes beyond PASS/FAIL** - REVIEW and NoLens, which
   cases 2 and 4 answer, and a set that times out as FAIL or REVIEW.
3. **F26b** (two cameras on one trigger), **F26c** (a product across
   stations), and **F20e** (an item's pictures side by side).
4. **F23d** - the S7 driver, by us or the PLC team from the kit; needs
   the HslCommunication download and a bench PLC from you.
5. **F29's commissioning slice** - a GigE camera's IP, Replace…, a
   Simulated or Folder camera - when a machine is being set up.
6. **F27** and **F28** - speed and the hardware trigger - when a line
   asks for them.

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
- ~~**N7. Pack 1.0.12**~~ - done, 16 November 2026, with SDK 1.0.4: the
  custom node fix the test team asked for, saved pictures and Sirius IV,
  controllers. The PLC kit is packed on its own (`pack-plc-kit.ps1`, 1.0.0).
- ~~**N3. Icons**~~ - done, 18 November 2026: every icon has its SVG, so
  regenerating `Themes/Icons.xaml` is safe; Lens Calibration has
  Phosphor's checkerboard, Offset Shapes its cards; Sirius IV's list has
  BMP and TIF icons of yours. Only Transform Shapes still borrows another
  node's icon (Resize's). The PLC and screen icons are Phosphor's, yours
  to redraw if you want.
- ~~**N4. A `.gitattributes`**~~ - done, 18 November 2026: text stored
  with LF, checked out with each machine's endings; nothing stored had to
  change.
- ~~**N5. Tidy-ups**~~ - done 23 November 2026: `src/Sirius.NodeEditor`,
  an empty project, deleted. The upright `Rectangle` region editor is
  kept: custom nodes cannot reach it (the SDK offers the turned
  rectangle, `SettingEditor.Rect`), and it costs nothing; taking it out
  would touch saving, references and two dozen tests for no gain. A node
  of ours may use it again; `UprightRegionNode` keeps it tested.
- ~~**N8. Tests that fail now and then**~~ - done 23 November 2026, as
  far as can be. The two camera tests timed opening as well as grabbing,
  and a camera opens only in its turn behind any other test's search for
  real cameras (`CameraDrivers`' SDK gate, once 87 s): their clocks now
  start once both stations are grabbing, with three minutes to get there.
  The styler test did not fail again in twelve runs of its class and ten
  days of full runs; it now says what the nodes and the undo stack held,
  if it does. As found:
  Tests that fail now and then, passing when run again:
  `HikrobotCameraTests.Two_stations_run_two_hikrobot_cameras_at_once`
  times out on a busy machine; `EditHistoryTests.The_styler_colours_the_whole_selection_in_one_step`
  failed once in a full run, 13 November;
  `StationRunnerTests.Two_stations_grab_from_two_cameras_at_once` once on
  18 November, passing three times alone - the same day camera discovery
  took 87 s in `CameraSdkGateTests`. Worth finding out why before they
  hide a real failure.
  Seen since (open): `CameraUnitTests.The_camera_window_keeps_the_camera_over_a_frame_lost_on_the_network`
  once in a full run, 23 November; the Hikrobot two-station test once on
  24 November with a new cause - `MV_E_HANDLE` triggering the virtual
  colour camera after 16 frames - not a timeout. Both pass alone.
- ~~**N9. A station renamed leaves its workflows behind**~~ - done, 18
  November 2026, in 1.0.13: Inspect and Station Result follow, open
  workflows as a change to save, the rest in their files.
- ~~**N10. Pack 1.0.13**~~ - done, 18 November 2026: several screens
  (F24), Builder 1.0.3, Sirius IV 1.0.1; the PLC kit packed again with
  that Builder, its zip now named for both versions.

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
  - ~~**F23c. Controllers on the line**~~ - done 23 November 2026
    (decision 91): the runtime window runs the controllers, so Builder's
    Test Run and Edge both do, their state along the bottom; a
    **Controller widget** for screens; a lost PLC as a red **alarm**
    across the top; the **PLC window in Edge**, behind an engineer's
    login.
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
    - ~~the **workflow editor**~~ - done 23 November 2026: a node renamed
      by double-clicking its caption, as in Studio; while a Test Run runs,
      the node running outlined in blue and how each went in the latest
      pass on its dot (green done, red failed, why on hovering), matched
      by node ID to the workflow as saved; Zoom In, Zoom Out and Fit on
      the toolbar and View menu, the workflow framed when first shown;
      a node's help from Properties (?);
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
  Builder and Edge; its PLC (F23); several screens (F24). Next on it:
  the part and its counts (F25, F4), cameras in the project (F29), user
  levels, then records and upload to the Server. Line speeds and image
  sizes are now asked in sections 14.6 and 15.6; still needed from you,
  before records and upload:
  - what Sirius Client, Interface and Server are written in, and their
    protocols, and whether that code can be read - and, for F27, how
    Client and MS handle queues, late results and part IDs;
  - how the Server stores results and images;
  - what happens today when the Server cannot be reached.
- ~~**F24. Several screens: pages and popups**~~ - done 17 November 2026
  (decision 79): a project's screens are Main and any pages and popups
  beside it; buttons Open Screen and Close; a popup over the dimmed page,
  still running; each screen in its own tab in Builder, listed in the
  Project panel. Left, when asked:
  - ~~a screen or button **behind a user level**, with a login popup~~ -
    done with user levels (decision 84);
  - the **standard screen with a Settings popup** made for it, rather than
    the settings listed under each station (S);
  - a **tab bar** of pages along the top of the running window, if
    buttons are not enough (S).
- ~~**F3. Signal out**~~ - done as F23: a verdict reaches the PLC.
- ~~**F4. Counts that survive a restart**~~ - done 20 November with F25
  (decision 83): a station's counts and timing beside its recipe, a
  controller's part counts beside its workflow.
- **F20. Saved images and their annotations** - the runtime's half done
  (decision 73, `annotation-format.md`): a station keeps none, failed or
  all of its pictures, with a `.sann` beside each; **Sirius IV**
  (`SiriusIV.exe`) browses them - file list, sorting, measuring, matching,
  Sync View, a tab per viewer. Left:
  - ~~**F20a. The batch half**~~ - done 22 November 2026 (decision 88):
    Run Folder and `sirius-run.exe` write a `.sann` beside each picture
    they run; a station's picture run again keeps what is about it.
  - ~~**F20b. Sirius IV, next panels**~~ - done 21 November 2026
    (decision 85): the Annotations panel below the viewers - Results and
    Shapes tabs, a row picking its shape and double-clicked zooming to
    it; layers switched on and off in every viewer; Export Annotations
    as CSV, a row per picture or per shape, the pictures chosen or all
    listed.
  - **F20e. Matching on the part** (S-M) - **after F26**, several shots
    of one part (user, 22 November): since F23a a picture's `.sann`
    carries the part's values (`"part": { "PalletId": 12 }`), so a match
    rule "same part value" pairs two stations' pictures of one part, which
    no name could.
  - ~~**F20c. A disk quota**~~ - done 22 November 2026 (decision 87): a
    most size and a free space left beside Keep for, oldest passed first,
    then oldest failures; a sample of what goes kept in the station's
    Kept folder.
  - ~~**F20d. Failed and one pass in N**~~ - dropped 22 November: lines
    keep all pictures or the failures; a good one is kept by hand, from a
    strip of recent pictures on the running screen if one comes.
  - ~~**F20f. Copy and Move to a folder in Sirius IV**~~ - done 22
    November 2026 (decision 89): To Folder on the toolbar and on
    right-click, each picture's `.sann` going with it, the folder dialog
    opening in the last folder used. The viewers' arrangement remembered, and
    measuring in mm, only if asked.

### Parts, shots and throughput

Designed 17 November 2026 in `runtime-design.md` section 14; nothing
built. Agreed order: **a single picture per part, through the PLC, solid
first**, then several pictures per part, then speed; the hardware trigger
at a later stage. Its questions (section 14.6) - line speeds, shots per
part, how the PLC tracks parts - size F26-F28.

- ~~**F25. The part, with the single picture**~~ - done 20 November
  (decision 83), but for which version of the line settings judged a
  part, which comes with F27's settings versioned. As designed: a part record - part ID, its shots, the
  combined verdict, timing, the settings that judged it - and part counts
  from the controller (total, passed, failed, late, not inspected), on
  the screen and kept with F4. A frame and part ID on every result,
  `.sann` and PLC write. Processing time on screen, average and worst. A
  deadline per station: no result in time is a reject, counted as late
  (section 5). The handshake (Ready, Busy) and error codes to the PLC.
  A single-picture part is a part of one shot, so F26 adds shots, not a
  redesign.
- **F26. Several shots per part** (L) - redesigned 24 November 2026
  from four real cases (`runtime-design.md` 14.2): the PLC triggers each
  shot with the item ID and the shot number; the station runs its recipe
  on each shot as it comes and a **Collect** node holds them until the
  set is complete, then what follows it judges the part once. In slices:
  - ~~**F26a. A set of shots at one station**~~ - done 24 November 2026
    (decision 95): Shots per Part on the station, Inspect's Shot and
    Last; Shot, Collect Images, Collect Verdicts and Collect Numbers; a
    shot answered "received" until the set's last; a part kept open
    across passes by its ID, given up on - not inspected - when another
    part begins at the station or after a minute; a set's early pictures
    held until the part is judged; Studio's Run Folder over sets.
  - **F26b. Two cameras on one trigger** (M) - case 4's beam splitter:
    a station with a camera per Camera node, a shot grabbing from each.
  - **F26c. A product across stations** (M) - case 3: the controller's
    record per item ID, each station's result added, the product's
    decided when every expected station has answered or at a deadline.
  - With **F33** - REVIEW, NoLens and the like - as these cases need it,
    and **F20e**, an item's pictures side by side in Sirius IV.
- **F27. Taking the next picture before the last is judged** (L) - after
  F26, when a line needs it. First **pipelining**: acquisition out of the
  recipe into the station's own grab loop and a bounded frame queue, with
  one worker. Then **several workers** - the recipe loaded N times -
  with results tagged by part ID, nodes marked safe or not for parallel
  instances, logs and records moved to one collector per station,
  settings versioned across instances, OpenCV's threads capped, and a
  Workers setting per station. Run Folder with N workers in Studio, to
  check a recipe against a line's numbers. Separately, independent
  branches of one inspection run in parallel, for latency (M).
- **F28. The hardware trigger** (M) - a later stage, agreed 17 November.
  The Camera node's Hardware trigger, and workflows started by Station
  Result, already work. To build: matching a picture to its part (a frame
  number the PLC matches, or a part number read at the trigger - to
  settle with the PLC team), counts of triggers, frames and results kept
  in step with a mismatch raised as an alarm; on top of F27's pipelining.
  Until then, the PLC Monitor's Poll Interval (20 ms by default) is the
  knob for timing.
- **F33. Result codes beyond PASS/FAIL** (M) - agreed 23 November 2026;
  how the earlier software worked. Most lines stay PASS/FAIL; others have
  three results, or four, or more. The PLC decides what each one means for
  the part, so the lists and the logic are the user's, not ours. Recipes
  already grade (`Grade`, the Grade and Worst Grade nodes), and the worst
  grade is already saved in the `.sann` and shown in IV, the CSV and the
  verdict widget. The line side is still yes/no: Inspect, `PartVerdict`,
  counts, yield, file names, saving policy, housekeeping and IV's filter.
  To build:
  - **A result list per station** in Builder, as long as the line needs:
    name, colour, **PLC code**, and **counts as** - **Pass**, **Fail**,
    or **Not counted** (out of the yield, counted on its own). PASS/FAIL/
    REVIEW/OTHER is one example, not a built-in set. The recipe's grades
    are matched to it by name. No list is PASS/FAIL as today, so existing
    projects are unchanged; `Passed` stays the "accepted?" answer
    everywhere.
  - **Inspect** gains a **Code** output, and its Verdict gives the
    result's name, so one PLC Write sends the code. Maybe also a trigger
    per result, grown from the list as Gather grows its inputs.
  - **No rule of ours for what a result leads to.** REVIEW on the earlier
    lines: neither pass nor fail at first. The machine brings the part back
    and inspects it again. A pass then is a pass; REVIEW again is a FAIL,
    or it would loop. That is the PLC's logic, or the controller
    workflow's, and may differ by line. We give it the pieces: the
    part's inspection number (its part ID seen before), branching by
    result, and a way for the workflow to give the part its **final
    result**. The part is **counted once**, at that final result, with
    re-inspections counted on their own.
  - **Yield from what counts**: passes out of passes plus fails. **No
    product** - a pick-up problem on the machine, or vision not seeing a
    part - is a result that is **Not counted**, so it does not make the
    yield wrong. A **timeout** (F25's Late: no answer before the
    deadline) is not that: it is a result of the list, **FAIL** or
    **REVIEW** as the line chooses, sent to the PLC with that result's
    code. A rejected part today (decision 83), so FAIL by default. **Not
    inspected** (a station busy or faulted) the same: FAIL or REVIEW,
    each chosen on its own.
  - **Counts per result** in Edge, and the yield.
  - **Pictures** named by the result (`_REVIEW`); Image Saving picks
    which results to keep. Housekeeping deletes passing results first,
    then the rest. IV filters by result.
  - **Deciding a result in a recipe**: a measurement in a borderline band
    is already the Grade node's job. Classifications, such as small
    defect blobs found, need a small node that gives a grade from rules
    or a class name, e.g. any blob under N px² → REVIEW, larger → FAIL.

### Cameras

- **F29. Cameras in the project** (M-L) - designed 17 November 2026,
  `runtime-design.md` section 15. A camera unit in Builder, beside
  stations and controllers, and a station naming its camera, so a camera
  is set up - and replaced - without opening the recipe in Studio. Studio
  keeps its Camera node for capturing and testing at the desk; in a
  station, the unit's camera and settings are used. In order:
  - ~~**the unit**~~ - done 18 November 2026 (decision 80): cameras in
    the project and the Project panel, a station's Camera menu, the
    camera window - Find, Use, exposure, gain, trigger and timeout each
    own or as the recipe, Grab, Live, Save Image - a camera offered when
    a station is added from a recipe, Test Run and Edge using it, and a
    camera held by another program said plainly;
  - ~~**Studio and the line agreeing**~~ - done 23 November 2026
    (decision 90): Open in Studio grabbing from the station's camera with
    the line's settings, saving the recipe's own; Take from Recipe and
    Give to Recipe in the camera window;
  - **commissioning** (M): a GigE camera's IP, and the network card
    checked; Replace…; identity by the camera's user-defined name;
    Simulated and Folder camera units;
  - **later** (S-M each): a maker's feature file (`.pfs`, `.mfs`); more
    settings; the window in Edge behind a user level.

  Questions in section 15.6: whether integrators use pylon Viewer or MVS
  today, how cameras are networked, where exposure belongs, how cameras
  are identified.

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
- ~~**F30. Find Rectangle**~~ - done 19 November 2026 (decision 82).
  Asked 19 November 2026: a board's
  outline, a chip's body. Find Circle's calipers along the four sides of
  a turned rectangle: **Region** the rectangle expected (drawn and turned
  like a Region of Interest, connectable so a match can carry it),
  **Search Width** either side of each side, **Direction** inward or
  outward, calipers per side. **Fit**: Rectangle - all the points at
  once, sides parallel and square - or Four Lines, each side alone and
  the corners where they meet, for a part not truly square or a camera
  at an angle; a **Square** checkbox, for a chip. Out: the rectangle
  (centre, length, width, angle, fit error - Measure reads it as it is),
  its sides, corners and edge points, and Length, Width and Angle; a
  missed side named.

### Image viewers

Every viewer is one control - Studio's, Sirius IV's, the camera
window's - so a tool added once is in all of them.

- ~~**F31. A rectangle measurement**~~ - done 19 November 2026
  (decision 81). Asked 19 November 2026. Dragged
  corner to corner, then turned by its knob: **L x W**, and its
  angle when turned. Its corners resize it, a knob turns it, and the knob
  marks which side is its top. Saved with the others: in the recipe, in
  a picture's `.sann`.
- ~~**F32. Rectangles as ROIs: their crops saved**~~ - done 19 November
  2026 (decision 81). **Save ROI As...** for the selected rectangle and **Save All ROIs
  As...** for every one, as `<name>-1.png`, `-2`, numbered as drawn, the
  way artboards are exported. A turned rectangle is cut upright, its top
  the knob's side; the picture's own values are kept, 12 bits as 12 in a
  PNG. In **Sirius IV**, the same rectangles cut from every picture in
  the list, `<picture>-<n>.png` - a day's pictures into a training set,
  for a deep-learning tool later.

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
- ~~**R3. Keep NodeGraphKit in step**~~ - NodeGraphKit removed 24
  November 2026: the other team only tried it. It is in the history
  (last at a69933f).
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
