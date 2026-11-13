# Backlog

What is left to do, in a proposed order, for review. `roadmap.md` says
where the product stands; this says what could come next. Updated
13 November 2026: the PLC is now planned as **controllers** in a project,
each a PLC and a workflow, the framework built by us (F23, decision 74).

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
- **L5. PLC communication libraries** (with F23d-e). Whatever speaks each
  brand's protocol under a driver - ours or the PLC team's - has its
  licence checked before it ships. Proposed for ours: **S7.Net Plus**
  (MIT) for Siemens, **FluentModbus** or NModbus (MIT) for Modbus TCP.
  Snap7 and Sharp7 are LGPL (a separate DLL, not linked in: workable);
  libplctag is MPL 2.0 / LGPL; HslCommunication is commercial.
- **L4. YOLO (Ultralytics) is AGPL-3.0.** Only if a Python node library
  we ship uses it: closed distribution needs Ultralytics' commercial
  licence.

Settled, for the record: AvalonDock (Ms-PL, notices fixed), Clipper2
(Boost), ZXing, OpenCV and Tesseract (Apache 2.0), Phosphor and the rest
(MIT).

---

## 2. Now: small, and worth doing next

- ~~**N1. Pack 1.0.10**~~ - done, 6 November 2026.
- ~~**N2. Test with real cameras**~~ - done, 7 November 2026.
- ~~**N6. Pack 1.0.11 and SDK 1.0.3**~~ - done, 11 November 2026.
- **N3. Icons** - mostly settled, 7 November 2026: you are happy with
  most, and will draw a few better ones later. Still true meanwhile:
  `ph_find_curve`, `ph_lens_calibration`, `ph_line_defects` and
  `ph_undistort` have no SVG in `res/ui icons`, so regenerating
  `Themes/Icons.xaml` deletes them; Offset Shapes and Transform Shapes
  borrow Dilate's and Resize's.
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

- ~~**F1. Real bit depth through the tools**~~ - done, 7 November 2026
  (decision 64), with the histogram (F1a) and Read Mode: Unchanged for
  new nodes.
- ~~**F1b. Bits for Python nodes**~~ - done, 7 November 2026: `image.bits`,
  `image.max_value`, `with_bits()`; SDK file 1.1, packed in SDK 1.0.3.

### The runtime, HMI and PLC

- **F2. Write the runtime design document** (M, design only) - **draft
  written 8 November 2026: `runtime-design.md`**, for review, with
  proposals, phases and open questions (its section 12). **The MVP of
  section 0 is built, 8-9 November 2026** (decisions 65-70): published
  names, projects and stations, the station runner, screens with the
  runtime program and Run as Runtime, the screen designer, and settings
  changed on the line; then split into Sirius Builder (projects, screens)
  and Sirius Edge (the runtime), Studio keeping recipes (decision 71).
  Next on it: counts kept over a stop, user levels, then the PLC as
  controllers (F23); later a shared UI library so the three can ship apart. The
  fourteen ideas agreed on 1 November - recipe vs project, published
  results and tunables, stations, the HMI designer, the runtime exe, PLC
  handshake and drivers, licensing per machine, edge vs cloud - into
  `docs/`, with the open questions, before any code. Needs from you:
  - what Sirius Client, Interface and Server are written in, and their
    protocols, and whether that code can be read;
  - how the Server stores results and images;
  - typical line speed and image size;
  - what happens today when the Server cannot be reached.

  Also in scope: **Sirius IV** (Image Viewer) as the runtime's image
  browser - the station's saved images and their annotations (F20). Its
  first version is built (decision 73).
- ~~**F3. Signal out**~~ - replaced by F23 below: the PLC through
  controllers, each a PLC and a workflow. Today a verdict
  still reaches the status line and a CSV, and nothing outside the PC.
- **F23. The PLC as controllers in a project** (L) - proposed 13 November
  2026, shaped the same day (decision 74).

  **The idea.** A Builder project holds **controllers** beside its
  stations, listed in a panel under Stations. A controller is one PLC -
  its brand, address and connection settings - and a **workflow**: a
  graph of PLC nodes (watch a bit, read, write, gather values) that
  decides when a station inspects and what goes back to the PLC.
  Double-clicking a controller opens its workflow in a tab in Builder.
  Edge runs the workflows beside the stations. Several PLCs on one
  machine are several controllers.

  **Who does what (agreed).** We build the whole framework here: the
  workflow engine, the editor, the nodes, the driver interface, a
  simulated PLC, a driver test kit and the first real drivers. Then the
  PLC team gets it as a **start kit** and writes further brands' drivers
  against it. Their prototype on NodeGraphKit - Connector, Monitor,
  Read, Write and Trigger nodes, an S7-1200 connected - is the reference
  for what the nodes must do. So a new brand is a driver: no new nodes,
  no change to Builder. This replaces Sirius Interface, which was
  written again for each customer.

  **Names, not addresses** (runtime design §9): only the workflow knows
  `DB34.20.0`. Stations, screens and records see named values - a
  pallet ID, a position, a verdict.

  **Design points, proposed:**
  - **Events and values.** A workflow is not a recipe. A recipe runs once,
    start to end, per picture; a workflow waits for things to happen. It
    has two kinds of wire:
    - **trigger** wires carry "now" from node to node;
    - **value** wires carry what was read, taken when the trigger
      arrives.

    A node that starts things - Monitor (a bit's rising edge), Station
    Result, a timer - starts a pass along the trigger wires. A
    controller's passes run one at a time, in order, so a sequence
    never meets itself halfway. Every node that waits has a timeout, and
    a timeout is an alarm. The editor, file handling and node catalogue
    are shared with recipes; only the running is new.
  - **The connection belongs to the controller**, not to a node: brand,
    address, rack/slot or unit ID, and poll interval are set in
    Builder's Properties, with **Test Connection**. The nodes use their
    controller's connection, so no Connector node, and each
    controller's status (connected, last event, last error) is one
    thing to show.
  - **The file is ours**: `controllers/<name>.sflow`, JSON, starting
    with `"format": "sirius-workflow", "version": 1`. As with `.sann`, a
    reader ignores fields it does not know. The connection settings live
    in the `.sproj`:
    `"controllers": [{ "name": "Line 1 PLC", "driver": "siemens-s7", "address": "192.168.0.10", "workflow": "controllers/Line 1 PLC.sflow" }]`.
  - **Addresses as text, typed.** Each driver parses its own brand's
    address (`DB34.20.0`, `40001`, `D100`), and each node says the
    type to read or write it as: bit, 16- or 32-bit integer, real or
    text.

  **The pieces, in order:**
  - **F23a. The engine and the nodes, with the simulated PLC** (M-L):
    - **`IPlcDriver`**: connect and disconnect, connection state, read
      and write a typed value at an address, read many in one call,
      watch an address by polling (or the brand's own subscription), and
      log lines;
    - **a simulated PLC driver**: a memory map, for tests and the desk;
    - **the workflow runner**: events, values, passes in order, timeouts;
    - **the PLC nodes**: Monitor, Read, Write, Pulse, Wait For, and
      Gather (named values into one record - their Trigger node);
    - **the station nodes**: **Inspect** (a station by name, with named
      values) and **Station Result** (verdict, PASS and FAIL, and
      published results by name). They meet the stations through a
      **station bus** in Edge: `Inspect(station, values)` returning the
      result, and Ready and Finished events.
    - **The part ID**: the values given to Inspect go with the picture,
      into the `.sann` (a `"plc"` object), the Result Log and the
      screen. This is what matching across two stations (F20e) waits
      for.

    All of it is tested without a window, against the simulator.
  - **F23b. Controllers in Builder** (M): a Controllers panel under
    Stations, with add, rename and remove; the connection in Properties
    with Test Connection; the workflow editor as a tab; a panel showing
    the simulated PLC's memory, changed by hand; and Test Run running
    the workflows with the stations. A workflow naming a station or
    value the project lacks is reported when the project opens.
  - **F23c. Workflows running in Edge** (M). Edge connects each
    controller and runs its workflow. A **Controller** widget for
    screens shows connected or not, the last event and the last error. A
    lost connection is an alarm, and Edge reconnects by itself.
  - **F23d. The first real drivers** (M each): **Siemens S7** first, as
    the PLC team's prototype talks to one; then **Modbus TCP**, which
    nearly every brand speaks. Each is built on an open library (L5),
    with a driver test kit run against a real PLC: types and byte order,
    timeouts, a cable pulled and put back.
  - **F23e. The start kit for the PLC team** (S-M), packed as the
    custom-node SDK is:
    - the driver interface;
    - a template driver project;
    - the test kit, and the guide;
    - the simulator, to test against.

    They write Mitsubishi MC, Omron FINS, Rockwell EtherNet/IP, Beckhoff
    ADS and OPC UA as customers need them. Drivers are DLLs in a
    `plc-drivers` folder, found as C# custom nodes are. Interface 1.0,
    kept stable as the node interface is.
  - **Later**: one workflow driving several PLCs, if a sequence must span
    two; the handshake's Ready and Busy, and recipe change by product
    code, as nodes (runtime design §9); digital I/O cards as a driver.
- **F4. Counts that survive a restart** (S-M). Counter starts from zero
  each session. Wanted by the runtime anyway.
- **F20. Saved images and their annotations** - the runtime's half done,
  11 November 2026 (decision 73, `annotation-format.md`): a station keeps
  none, failed or all of its pictures, as PNG, JPEG or BMP, in a day
  folder, with a `.sann` file beside each - shapes in layers, one per
  viewer, the verdict and the published results - chosen per station in
  Builder; and **Sirius IV** (`SiriusIV.exe`) browses them. Left:
  - **F20a. The batch half** (S-M): Run Folder and `sirius-run.exe`
    writing the same `.sann` beside each image they run.
  - **F20b. Sirius IV, next panels** (M each): the annotations as a
    table (shapes and their measurements, results), a row double-clicked
    zooming to its shape, and exported as one CSV across the pictures
    chosen (the old ICB's Export Features, one `.xlsx` each); layers
    switched on and off.
  - **F20e. Matching pictures** - done 12 November 2026: groups of N down
    the sorted list, names alike in their first N characters, or in the
    part a pattern finds; with Sync View. Left: matching on a part ID
    from the line (F23a), once stations save one - two stations' names
    share no time or count to match on.
  - **F20c. A disk quota** (S-M), beside Keep for: when a size is reached,
    oldest passes go first, then oldest failures (runtime design §8).
  - **F20d. Failed and one pass in N** (S), the policy the runtime design
    proposed beside none, failed and all.

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
