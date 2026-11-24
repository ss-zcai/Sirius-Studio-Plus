# Runtime, HMI and PLC: design

Draft for review, 8 November 2026. Backlog F2. **No code yet**: this
gathers what was agreed on 1 November, turns each point into a concrete
proposal built on what the engine already does, and lists what is still
open. Change anything; strike what is wrong.

Marked **Agreed** where it was settled in discussion, **Proposed** where it
is mine to argue for, **Open** where it needs an answer from you.

**Start with section 0**, the MVP agreed on 8 November. Sections 1-13 are
the longer view it must not close doors on; section 14 (17 November) is
parts of several pictures, and speed; section 15, cameras as a unit of
the project.

**Three programs (decision 71, 9 November 2026).** What section 0 first
put in Studio is now apart from it:

- **Sirius Studio** builds recipes - the vision logic - and publishes the
  names a screen shows. Nothing else.
- **Sirius Builder** (`SiriusBuilder.exe`) builds the project: its
  stations, each running a recipe made in Studio; the screen; how far the
  line may change each setting; later the PLC and I/O. It test-runs the
  project, and opens a station's recipe in Studio.
- **Sirius Edge** (`SiriusEdge.exe`, formerly the runtime) runs the project
  on the machine, full screen, and edits nothing.

Where section 0 below says Studio for projects, screens or Run as
Runtime, read Builder.

---

## 0. The MVP (agreed 8 November 2026)

Three parts only: **acquire, inspect, publish**. Get the process flowing
first; PLC, I/O, user levels, records and upload come after.

- **Acquire** is the recipe's own source - its Camera node, a virtual
  camera, or an Image Sequence for the desk. No station-level trigger
  yet: a **Trigger** button on the screen runs one inspection, exactly as
  Run does in Studio, and a **Run / Stop** button runs continuously.
- **Inspect** is the recipe, unchanged.
- **Publish** is what the screen shows, plus counts. Counts start from
  zero each start; saving them when stopped comes later. Result Log and
  image saving work as they do now, as nodes in the recipe.

**A project, built in Studio.** Studio opens either a lone recipe, as
today, or a project: a folder holding the project file, its recipes and
its screens, copied to the machine as it is.

- A project has **stations**. In the MVP a station is a named recipe -
  the camera is inside it. Several stations run side by side, each with
  its own camera and inspection, e.g. two cameras with two recipes.
- One screen may show any station's values and images: Camera 1's image
  and verdict beside Camera 2's.

**Published names.** A screen finds what it shows by name, never by node.

- Binding a widget to an output - *Measure 1 › Distance* - labels that
  output `Distance` in the recipe, automatically and editably. Nothing in
  the graph changes: no wire, no node. The port shows a small tag with the
  name.
- Viewers ("Main View") and settings are labelled the same way.
- A screen stores *station › name*. Replacing a node means giving the new
  output the same name; a name no longer found is reported when the
  project opens.
- A **Published** list shows every name, its node, and what uses it.

**Settings changed on the line, chosen at design time.** In Builder you
pick which settings appear on the screen, each with a range at most the
node's own, kept in the project for the station. On the line only those change, only within that range. The
changed values are kept in a small file beside the recipe and applied when
it loads; the recipe itself is not rewritten.

**The screen designer** is a free-form canvas - drag, resize, snap to a
grid - with these widgets: image view with its overlays, PASS/FAIL, value
readout, counter with reset, button (Trigger, Run/Stop, Reset counts; per
station or all), setting control, text and logo. Screens are saved as our
own JSON layout (section 7), not XAML.

**Running it.**

- **Run as Runtime** in Studio opens the screen in a window exactly as the
  operator will see it, for testing while designing.
- **The runtime** is a separate program that opens a project, goes full
  screen and runs it, without Studio. It edits nothing; closing it needs at
  least a simple lock.

**Order of work**, each step usable and tested before the next:

1. **Done 8 November 2026** (decision 65). Published names in the recipe: the label on outputs, settings and
   viewers, saved in the `.sgraph`; the port tag; the Published list.
2. **Done 8 November 2026** (decision 66). The project: file, stations, New / Open Project in Studio, a project
   tree, opening a station's recipe in the graph tab.
3. **Done 8 November 2026** (decision 67). The station runner, without any UI: load a recipe, Trigger, Run / Stop,
   counts, a snapshot of its published values after each inspection.
   Proved with two simulated or virtual cameras grabbing at once.
4. **Done 9 November 2026** (decision 68). Screens: the JSON model, the widgets, the runtime program, Run as
   Runtime.
5. **Done 9 November 2026** (decision 69). The screen designer canvas in Studio.
6. **Done 9 November 2026** (decision 70). Settings on the line, with the file of changed values.
7. **Done 9 November 2026** (decision 71). Projects, screens and line settings moved out of Studio
   into Sirius Builder; the runtime renamed Sirius Edge.

---

## 1. What it is for

Studio is where an engineer builds and tunes a recipe. Nothing today runs
that recipe on a machine for weeks with an operator in front of it and a
PLC beside it: a verdict reaches the status line and a CSV, nothing
outside the PC (roadmap, Known limits).

The runtime is that missing half:

- an **operator application** that starts at boot straight into running,
  full screen, with no graph in sight;
- **screens** an SI (system integrator) designs, showing what the recipe
  publishes;
- **signals** to and from the machine's PLC;
- **records** that survive a restart: counts, results, saved images, who
  changed what.

**Agreed:** decide at the edge, analyse in the cloud. The station inspects
and answers the PLC locally, with the Studio Plus engine. Results and
images go to the Sirius suite afterwards, through a local queue, for
Image Cloud (IC), Data Insight (DI) and Sirius IV. A station keeps
working when the Server cannot be reached.

**Agreed:** one architecture, two sizes. A small project runs everything
in one process on one PC; a large one spreads the same parts over
machines, as the suite does. The boundaries are drawn now (section 10) so
the second does not mean a rewrite.

Tiers a customer moves up through without redoing recipes: standalone
Studio Plus, then with the Server, then with GPU inspection on the Server.

---

## 2. What is already there to build on

The runtime is mostly assembly of parts that exist:

| Need | Already in the engine |
|---|---|
| Run a recipe once per part | `GraphScheduler.InspectAsync` - an inspection, as opposed to a re-run after an edit |
| Things that keep state across parts | `IInspectionNode` (Counter, Result Log act only on inspections) |
| A pass or fail for the part | `IJudgingNode` / `IGradingNode`, summed into a `Verdict` (passed, grade, checks, failed) |
| A camera and its trigger | `ICameraDriver` with Free Run, Software and Hardware trigger; Basler and Hikrobot; one SDK gate (decision 60) |
| Showing a fast run without slowing it | `LiveDisplay` (decision 55): every frame inspected, at most 30 shown a second |
| Running without the editor | `FolderRun` and `sirius-run.exe`: load a recipe, inspect, report, exit code |
| Custom algorithms | C# nodes in-process; Python nodes in their own processes, a crash ending only that process |
| A named way into and out of a graph | Sub-graphs' Graph Input / Graph Output |

What does **not** exist: a way for a recipe to say which of its values are
for the outside world (section 4), a station loop that runs for weeks
(section 5), anything persistent beyond Result Log's CSV, screens, and
any I/O.

---

## 3. Recipe and project

**Agreed.** Two files, two authors:

- **Recipe** (`.sgraph`, as today) - the vision logic. Written by the
  engineer in Studio.
- **Project** (new) - the deployment: stations, cameras, triggers, PLC
  signals, screens, users. Written by the SI. A project *uses* recipes and
  does not edit them.

**Proposed:** a project is a folder, not one file, because two kinds of
thing live in it and must not overwrite each other:

```
Line 3 Cap Check/
  project.json          stations, cameras, I/O map, users, settings   - SI writes, versioned
  screens/*.json        screen layouts                                 - SI writes, versioned
  recipes/*.sgraph      copies of the recipes it runs                  - engineer's, copied in
  state/                tuned values, counts, records, audit trail     - the runtime writes
  images/               saved images and their annotation files        - the runtime writes
```

Deploying a new version of the project replaces the first three and never
touches `state/` or `images/`, so counts and tuned values survive an
update. `images/` may be moved to another disk in `project.json`.

**Proposed:** recipes are copied into the project rather than referenced
where they lie, so a project folder moved to the machine is complete. The
copy carries the recipe's name and a version (section 4) so the runtime can
say which one it runs.

**Proposed:** a project is edited in Studio, as a new workspace beside the
graph (stations, I/O, screens), rather than in a separate designer
program. One installer, one place to test a recipe against its screens.

---

## 4. What a recipe publishes

**Agreed:** screens and PLC signals bind only to names the recipe
publishes, never to node IDs. A recipe can then be reworked freely as long
as its published names stay; a name missing after an update is reported
when the project loads, not discovered on the line.

Three kinds:

- **Results** - an output the outside world reads: a count, a diameter,
  a code read, a pass/fail of one check. Name, type, unit.
- **Tunables** - a setting the operator or technician may change on the
  line: a threshold, a tolerance. Name, type, limits (inside the node's
  own), default, and the user level needed to change it.
- **Views** - an image with its overlays, for a screen to show: what a
  viewer panel shows today.

The overall verdict and grade are always published, as `Verdict` and
`Grade`, without anyone marking them.

**Proposed: how they are marked.** In Studio, right-click an output port
(*Publish as Result...*), a setting in the properties panel (*Publish as
Tunable...*), or a viewer panel (*Publish as View...*), give a name, done.
A Published panel lists them all, renamable, with a warning on any whose
node is gone. Saved in the `.sgraph` as a list of
`{ name, kind, node, port or setting, type, unit, limits, level }`.
Top-level graph only at first; publishing from inside a sub-graph comes
with promoted parameters (F5).

**Proposed: recipe version.** A number in the recipe, raised on every save
that changes its published list or any setting - the runtime logs it with
every result, so a record says exactly which logic judged the part.

**Proposed: tuned values live in `state/`, not in the recipe.** A tuned
threshold is an override by published name. When a recipe is updated:

- a tunable whose name and type are unchanged keeps its tuned value,
  clamped into new limits if they moved, with an audit entry when clamped;
- a tunable that is gone is dropped, logged;
- a new one starts at its default.

The engineer's recipe is never rewritten on the line.

---

## 5. Stations and the run loop

**Agreed:** a station is a camera, a recipe, a trigger and its outputs.
The project holds a *list* of stations from day one; the MVP shows one.

```
 trigger --> acquire --> inspect --> publish --> outputs
 (camera,     (frame      (recipe,    (named       (PLC signals, records,
  PLC, timer,  set)        one at a    results,     saved images, screens,
  folder)                  time)       views)       upload queue)
```

**Proposed: one inspection at a time per station**, as the scheduler works
today. Stations run in parallel with each other. (For speed, section 14.3:
acquisition out of the recipe, and several recipe instances per station.)

**Proposed: frames are a set from day one.** Acquisition hands the engine
a *frame set* - one frame today - so several images per trigger (several
lights, a burst) and later several cameras per station extend the set
rather than change the loop. The graph side, a node that takes the set
(like Image Sequence, but per trigger), is phase 4 and touches the core
graph; designing the set now keeps that door open.

**Proposed: missed parts are never silent.** "Drop frames rather than fall
behind" (agreed) is right for a free-running display, but on a line a
dropped frame is a part that went past uninspected. So:

- PLC-handshaken triggers cannot overrun: the PLC waits for Ready.
- A hardware-triggered camera's frames are queued up to a small depth
  (default 2). Past that, the oldest is dropped, counted as **Missed**,
  shown on screen as an alarm, and - when a PLC is connected - signalled
  as not inspected, which the PLC treats as a reject.
- Free Run (setup, demonstration) drops silently, as today.

**Proposed: a result deadline per station.** If an inspection has not
finished by it, the PLC gets a timeout result (a reject by default)
on time; the inspection is cancelled, and logged as late. Guards against
a slow Python node or a pathological image holding the line.

**Proposed: station states** - Stopped, Starting, Ready, Busy, Faulted -
shown on screen and mirrored to the PLC. Faulted covers a camera gone, a
recipe that will not load, the disk full; each has a plain message, as the
Camera node's Device note does today.

---

## 6. The runtime application

**Agreed** features, with what I propose for each:

- **Starts at boot into running, full screen.** Proposed: a Windows
  scheduled task at logon (auto-logon set by the SI), full-screen window,
  Alt+F4 and the title bar closed off below engineer level. Windows'
  kiosk mode (Assigned Access) as an option, not a requirement.
- **Watchdog.** Proposed: a small second process that starts the
  runtime, expects a heartbeat every few seconds, and restarts it if the
  heartbeat stops or it exits; restarts are recorded in the audit trail
  and counted on screen.
- **Camera reconnect.** Proposed: a camera that drops out puts the
  station in Faulted ("Camera lost: retrying") and is reopened every few
  seconds through the existing driver gate, with no operator action.
- **Memory steady over days.** Proposed: frame buffers pooled per
  station, saved images written by a separate queue that drops saves -
  never inspections - when the disk falls behind, and a soak test (a
  simulated camera for 72 hours, memory and handle counts logged) as part
  of the release checks.
- **User levels**: operator, technician, engineer. Proposed: a PIN or
  password per user, set in the project; screens and tunables say which
  level they need; automatic log-out back to operator after a few idle
  minutes. Windows accounts later if a customer asks.
- **Records that survive a restart** - section 8.
- **Tuned values kept apart from the recipe** - section 4.

**Proposed:** a new project `Sirius.Runtime` (WPF), sharing Core, Vision
and the camera drivers with Studio, so a recipe judges identically in
both. `sirius-run.exe` stays for scripts.

**Proposed: all operator-facing text translatable from day one** -
English and Simplified Chinese at least - including text the SI types
into screens, entered per language.

---

## 7. Screens (HMI)

**Agreed:** the SI builds the screens, so a real designer is needed:
templates with slots first, a free-form canvas later, as a workspace in
Studio.

**Proposed: screens are our own layout model**, saved as JSON (widgets,
positions, bindings to published names), drawn by the same WPF controls in
Studio's designer and in the runtime. Not raw XAML: XAML from an SI could
do anything, breaks between versions, and cannot be checked against the
published names.

**Agreed widget list:** image view with a history strip, PASS/FAIL,
value readout, counter with reset, tunable control, recipe selector,
results table, buttons, text and logos.

**Proposed for the MVP:** one standard template - a large image view,
PASS/FAIL, counters, a results table, a tunables page behind a level -
whose slots the SI fills from the published lists. Enough to put a station
in front of an operator before the designer exists.

**Built (17 November 2026): several screens** (decision 79). Main, the
start screen, and any pages and popups beside it, each a file in
`screens\`. A button opens one (Open Screen) or closes it (Close, shown
as Back on a page). A popup opens over the page, which is dimmed and goes
on running; one popup at a time. The tunables page behind a level, above,
becomes a popup behind a login once user levels exist.

**Built (10 November 2026): the setting widget's controls.** For a number
the SI chooses one of four controls in Builder's property grid:
- **− value +**, the default.
- **Slider**, which needs a lowest and a highest. Until both are set, the
  line shows − and + and says why.
- **Box**, a box to type in and nothing else.
- **Range**, which pairs this setting (the low end) with a second published
  setting (the high end) on one slider with two handles. The track runs from
  the low setting's lowest to the high setting's highest.

The label goes beside the control or above it. Above makes the widget two
lines tall, on the grid. A slider changes the setting when it is let go,
not on every pixel, so a running station takes one change per drag. An
inspection that changes nothing leaves a box being typed in, or a handle
being dragged, alone.

**Proposed:** the image view holds the last failed part on screen until
the next failure or a tap (decision 55 left this to the runtime), with
the history strip showing the last N parts, failures marked.

---

## 8. Records

**Proposed: a local SQLite database in `state/`** for counts, results and
the audit trail. Transactions make counts survive a power cut mid-write;
the screens' results table and the uploader query it; it is one file to
copy. (SQLite is public domain; Microsoft.Data.Sqlite is MIT - licences
to confirm with L1-L4.) Result Log's CSV stays as a node for whoever
wants a spreadsheet.

- **Counts** - total, passed, failed, per grade, Missed, Late - per station
  and per recipe, reset by a technician (audited) or by the PLC.
- **Results** - one row per inspection: station, recipe and version,
  product code, time, serial, verdict, grade, every published result,
  duration, node errors, saved image names.
- **Saved images** - policy per station: none, failures, all, failures and
  one pass in N. A disk quota; when it is reached, oldest passes go first,
  then oldest failures. File names carry station, time, serial and
  verdict, so Sirius IV can match them (section 11). Annotations beside
  them as vectors when F20 is on. **Built (11 November 2026):** none,
  failed or all, in day folders, with Keep for in days and a stop below
  1 GB free; `annotation-format.md`. The quota and one pass in N are
  backlog F20c and F20d.
- **Audit trail** - log-ins, tunable changes (from, to, who), recipe
  switches, count resets, restarts, faults.

**Open:** the Server's result and image format (section 12), so the same
rows upload without translation.

---

## 9. PLC

**Update, 13 November 2026 - the plan has moved: see backlog F23 and
decision 74.** A project holds **controllers** beside its stations: each
is one PLC (its connection) and a workflow - a graph of PLC nodes with
trigger and value wires - edited in a tab in Builder and run by Edge.
Two nodes, Inspect and Station Result, join a workflow to the stations
over a station bus. We build the framework (engine, editor, nodes,
`IPlcDriver`, a simulated PLC, a driver test kit, the S7 and Modbus TCP
drivers) and give it to the PLC team as a start kit for other brands;
it replaces Sirius Interface, which was rebuilt for each customer. This
section's rules still hold: named values, never addresses, outside the
workflow; the handshake; hardware triggers where timing matters; a
licence check per library (L5).

**Agreed:** a signal layer with one driver per brand; the PLC talks in
named signals, never addresses inside the engine. Driver order: Modbus
TCP, then Siemens S7, Rockwell (EtherNet/IP), Omron (FINS, or EtherNet/IP
on NJ/NX), then digital I/O cards. **Reuse Sirius Interface's drivers or
protocol before writing new ones.**

**Proposed signals**, mapped to addresses in the project:

| From the PLC | To the PLC |
|---|---|
| Trigger | Ready, Busy |
| Product code (selects the recipe) | Done, Result Valid |
| Reset counts | Pass, Fail, Grade code, Not Inspected |
| Enable | Published results (numbers, codes) |
| Heartbeat | Heartbeat, Alarm code |

**Proposed handshake** (each step with a timeout, each timeout an alarm):

1. Runtime holds **Ready** while it can take a part.
2. PLC raises **Trigger**. Runtime drops Ready and Done, raises **Busy**,
   triggers the camera by software.
3. Inspection ends (or the deadline passes). Runtime writes the results,
   then raises **Result Valid** and **Done**, drops Busy.
4. PLC reads the results and drops Trigger. Runtime drops Done and Result
   Valid, raises Ready.

Where the part's position must be caught to the millisecond, the camera is
triggered by hardware from a sensor and the PLC only exchanges the product
code and results; the handshake is the same with step 2's camera trigger
left out.

**Proposed: recipe change by product code.** The project maps codes to
recipes; a change arrives while Ready, the runtime loads the recipe (kept
warm if the project says so), and Ready returns only when it is loaded.

**Proposed: libraries** to evaluate, each with a licence check before use:
NModbus (Modbus TCP), S7.Net Plus or Snap7 (S7), libplctag (EtherNet/IP),
our own FINS (a small protocol). Digital I/O through the card maker's SDK.
Polling PLCs every few milliseconds is fine for results; for timing, the
hardware trigger above.

**Proposed: a fake PLC** - a panel in Studio and the runtime that shows
every signal and lets you drive the inputs by hand - for desk testing and
for commissioning.

---

## 10. Boundaries: one architecture, two sizes

```
   +-------------+      frame sets      +----------+     results     +-----------+
   | Acquisition | -------------------> |  Engine  | --------------> |  Records  |---> Uplink (IC, DI)
   | cameras     |                      |  recipe  |                 |  SQLite,  |
   +-------------+                      +----------+                 |  images   |
          ^                                  |                        +-----------+
          | trigger                          | results, state
   +-------------+                      +----------+
   |  Signals    | <------------------- |  Station | ----> Screens (read published values,
   |  PLC, I/O   |      outputs         |  loop    |       write tunables)
   +-------------+                      +----------+
```

**Proposed:** each box is an interface in-process for now - a frame
source, a signal map, a result sink, a station event stream - so that any
of them can later move behind a pipe or a network connection: an engine
on a GPU server for deep-learning nodes, records on the Server, screens on
a second PC. Nothing in the small version pays for that beyond keeping the
interfaces honest.

---

## 11. Sirius IV

**Agreed:** Sirius IV (Image Viewer; it was to be "Sirius ICB") is the
station's image browser - the saved images and their annotations (F20),
grouped by matching file names and viewed in step. Comes in with the
runtime.

**Built (11 November 2026), decision 73:** `SiriusIV.exe`, on Studio's
viewer. A folder's pictures in a Files panel, its day folders with it;
one, two, four or nine viewers; the shapes from each picture's `.sann`
drawn over it. The annotation format is ours (`annotation-format.md`).
Next: the annotations as a table, layers on and off, and matching
pictures across folders.

---

## 12. Open questions

### About the suite - needed before the records and PLC design firm up

1. What are Sirius Client, Interface and Server/MS written in, and what
   protocols do they speak (to each other, to the PLC)? Can I read the
   code?
2. How does the Server store results and images - a database, files, an
   API to post to?
3. What do Client and Interface do today when the Server cannot be
   reached?
4. ~~Does Sirius Interface have PLC drivers we could reuse?~~ No: it
   was written again for each customer. The framework is new (decision
   74).
5. Sirius Client: does the new runtime replace it for small projects, or
   does Client get a local-engine mode? (Agreed to decide, not yet decided.)

### About the lines

6. Typical line speed (parts a second) and image size - and the fastest
   you expect to meet. Decides the queue depth, whether to save every
   image, and how much a Python node can cost.
7. Which PLC brand do the first customers have? That driver goes after
   Modbus TCP.

### About Sirius IV

8. ~~ICB's annotation format~~ - ours, `.sann` (decision 73). Still
   open: the rule that matches two stations' pictures of one part.

### Decisions for you

9. **Name** of the runtime application - "Sirius Studio Plus Runtime",
   "Sirius Station", something else?
10. **Remote access** - none at first, a read-only status page in a
    browser, or full remote screens?
11. **Project editing in Studio** (my proposal) or a separate designer?
12. **Licensing per machine**: agreed in principle - how is it enforced
    (a licence file bound to the PC, a dongle, the Server)? And L1 (Emgu)
    and L2 (pylon) must be settled before any runtime ships.

---

## 13. Phases

A proposal; each phase usable on its own.

1. **A station that runs.** Publishing results, tunables and views in
   Studio; the project folder with one station; `Sirius.Runtime` with
   the standard screen template; camera trigger (hardware, software, free
   run) and a folder as a pretend camera; counts, results and saved
   images in SQLite and on disk, with a quota; user levels; tuned values
   kept apart; boot start, watchdog, camera reconnect; English and
   Chinese. Ends with a 72-hour soak test.
2. **The machine.** Signals, the handshake, Modbus TCP, digital I/O, the
   fake PLC panel; recipe change by product code; audit trail; F4
   persistent counts made visible everywhere.
3. **The suite.** Upload queue to IC and DI in the Server's format; F20
   annotations and Sirius IV; the first-customer PLC brand; several stations on
   one PC; the screen designer with templates and slots.
4. **Larger lines.** The free-form screen canvas; several images per
   trigger into one inspection; several cameras per station; engines
   behind a network boundary (GPU nodes on the Server); the remaining PLC
   drivers.

---

## 14. Parts, shots and throughput

Discussed 17 November 2026. **No code; nothing to change yet.** Agreed
order: **a single picture per part, through the PLC, solid first**, then
several pictures per part, then speed. Backlog F25-F28.

This section builds on section 5 (frame sets, missed parts never silent,
a result deadline) and section 9 (the handshake, hardware triggers). Where
it says otherwise, it supersedes them.

### 14.1 The part

Today the unit is the **inspection**: a station's counts, its PASS / FAIL
widget and each saved picture are per picture. On a line the unit is the
**part**, and it can take several pictures - and, at speed, a part's
result arrives after the next part's picture has been taken. Both cases
need the part as a thing of its own.

**Proposed: a part record.**

| Field | |
|---|---|
| Part ID | from the PLC (a value Gather read), or the frame number when nothing gives one |
| Shots | each inspection of it: station, shot number, frame number, verdict, published values, picture |
| Verdict | combined from the shots: passed if every shot passed, else the worst grade |
| Timing | triggered, taken, judged, returned to the PLC; late or not |
| Settings | which version of the line settings judged it (14.3) |

- A **single-picture part is a part with one shot**, so defining the
  record now costs nothing extra and makes several shots a count, not a
  redesign.
- **Part counts** (total, passed, failed, late, not inspected) come from
  the controller and belong on the screen beside, or instead of, the
  station's picture counts. They are what F4 should keep over a restart.
- Each picture's `.sann` already carries `part` (the values Gather read);
  it gains the **shot** and **frame** numbers, so Sirius IV can put a
  part's pictures side by side (F20e).

### 14.2 Several shots per part

**Built at one station, 24 November 2026** (F26a, decision 95); two
cameras on one trigger (F26b) and a product across stations (F26c) to
come.

**Redesigned 24 November 2026** from four real cases and the earlier
system's files (Jianfan, CLT). The first design here - judge each picture
on its own, combine the verdicts in the workflow - turned out to be the
exception: the lines collect a part's pictures and judge them together.

#### The cases

| | Pictures per part | Judged | Result |
|---|---|---|---|
| **1. OCR on a box** - lot number, expiry | 1 per camera | that picture | PASS / FAIL |
| **2. Pad printing on a mould** | 4-7, one camera, a light setup each | **once, on all of them blended**: colour and pattern on the combined picture | PASS / FAIL / REVIEW (shading in a grey area) |
| **3. A medical device**, 6-8 stations on a conveyor | one or a few per station, at different places and times | each station its own defects; **the product's result combines every station's** | PASS / reject |
| **4. A lens** - cameras behind a beam splitter, focal planes apart | up to 8: 2 cameras x up to 4 light setups (or 1 x 3) | **each picture as it is taken, combined at the end** | PASS / FAIL / REVIEW / NoLens / ... |

A 4-6 picture part takes 5-6 s on these lines, light changes, settling
delays and communication included; work on a picture while the next
light is set is what makes that time.

#### Agreed (24 November)

- **The PLC drives the sequence**: it switches the lights, triggers each
  shot, and says which shot it is. No light control of ours yet - later,
  with digital I/O in Builder.
- **Software triggers.** Case 4's two cameras are triggered at about the
  same time, sometimes a little apart. The hardware trigger is later
  (14.4).
- **The item ID** (the earlier system's `ItemID`) comes with every
  trigger and names the product.
- **Shots are not counted by us**: the PLC gives the shot number. A
  set missing a picture - a network fault, 3 of 4 arrived - ends by its
  timeout and is failed.
- **Something wrong mid-set**: the line moves on; whether the part is
  FAIL or REVIEW - a second chance - is the machine's design, so it is a
  setting (F33).
- **Several PCs**, each with a few cameras and stations; each PC
  answers for its own stations.

#### The model

**A set of shots at one station (cases 1, 2, 4).** A trigger carries the
item ID and the shot number, and whether it is the last - or the
station's shot count says so. The station runs **its recipe on each shot
as it comes**, as today, and keeps what each shot produced under the item
ID until the set is complete:

```
PLC: trigger, item 260511155932987, shot 2 of 4
  --> Inspect (station Lens, item, shot)
        recipe runs on this shot's picture(s) ------------------------+
          per-shot nodes: find defects, measure ...                  |
          Collect (by shot) <-- holds shot 1, 2 ... of this item      |
             not complete: answer "received" (the earlier 202)        |
             complete:     what follows Collect runs - Blend all,     |
                           combine the defects - one part verdict     |
  <-- shot answer: received / the part's result and code -------------+
```

- **Collect** is a recipe node: what a shot gives it is kept per shot
  number; when the set is complete it gives them all - pictures for a
  Blend (case 2), lists of defects and values to combine (case 4).
  Everything after it runs once per part, everything before it once per
  shot. One recipe file, tested in Studio by running a folder of one
  part's pictures through it in shot order.
- **Before the set is complete** a shot answers "received"; nodes after
  Collect do not run, and that is not an error. The part's verdict - and
  its result code (F33) - come with the last shot.
- **Two cameras on one trigger** (case 4): the station has a camera per
  Camera node (by name, as F29 maps them), and a shot grabs from each;
  a pair a little apart in time is still one shot.
- **The set's timeout**: a set not complete within it - the next item's
  first shot arriving, or the time running out - is ended as **a part
  not inspected**: FAIL, or REVIEW where the line chooses (F33). Shots of
  an item already ended are refused.
- **Saved pictures**: each shot's picture with its own `.sann` - its own
  shapes - and the item ID, shot number and the part's result; Sirius IV
  puts an item's pictures side by side (F20e).

**A product across stations (case 3).** Each station judges its pictures
as above; the **controller keeps a record per item ID**: each station's
result added as it comes, the product's result decided once every
expected station has answered - or at a deadline, a station that never
answered counting as not inspected. Combined by the F33 rules: any FAIL
rejects; a REVIEW where nothing failed is REVIEW. A product across PCs
is the PLC's to combine: each PC answers for its own stations.

**Later, only if a job needs it:** two stations sharing one camera;
lights switched by us; per-shot verdicts sent to the PLC before the set
is complete (today a shot before the last answers only "received").

### 14.3 Throughput: taking the next picture before the last is judged

At speed, the camera takes a picture, the part moves on, the next picture
is taken - and the first part's result only has to reach the PLC before
the part reaches the reject station. Two numbers decide what is needed:
the **cycle** (time between parts) and the **deadline** (time from the
picture to the reject station).

| | Needed |
|---|---|
| processing < cycle | **Pipelining**: the next picture is taken while this one is judged. One worker. |
| cycle < processing < deadline | **Several workers**: N instances of the recipe judging parts side by side, N at least processing / cycle, with a margin |
| processing > deadline | No threading helps: a faster recipe or PC, a GPU, or the reject station further away |

Judged on the **worst** processing time (the 99th percentile), not the
average: a part late once an hour is a fault.

**Today:** the Camera node is inside the recipe and grabs when it runs, so
a station's cycle is grab plus processing; a station judges one picture at
a time with one recipe instance; its pictures are valid until its next
inspection starts; and within an inspection the nodes run one after
another.

**Proposed:**

```
camera --> acquisition --> frame queue --> worker 1 (recipe instance) --+
           frame no., time,  (bounded)  --> worker 2 (recipe instance) --+--> results by part ID --> PLC, on time or reject
           part ID, deadline            --> worker N                   --+                       --> screen, pictures, records
```

1. **Acquisition out of the recipe.** The station owns the camera and runs
   its own grab loop, stamping each frame with a frame number, time, part
   ID and deadline. In a station the recipe's Camera node becomes "this
   frame"; in Studio it grabs as now. Everything else rests on this.
2. **Workers are separate recipe instances**, the recipe loaded N times,
   each with its own scheduler: a graph holds state (cached outputs,
   loaded models, counters) and cannot be shared between threads.
3. **Every result carries its part ID**, since results finish out of
   order. The PLC's own tracking (shift register, encoder) matches it to
   the part; the PLC owns the reject timing.
4. **Fail-safe deadlines**, as section 5 proposed: no result by the
   deadline is a reject, counted as late. A full queue drops the frame
   and rejects the part - it never blocks the camera, which would lose
   parts silently. Late, dropped and not inspected are counts on the
   screen.

**What makes several instances hard:**

- **Nodes with state** - Result Log, counters, Image Sequence's position,
  teaching. Logs and records move out of the recipe to one collector per
  station, written in order. Nodes are marked safe or not to run in
  parallel instances; a recipe with one that is not runs with one worker,
  and says why.
- **Custom nodes.** C#: each instance has its own node objects - fine
  unless the author kept something static. Python: a worker process per
  instance, so N times the memory. C++: one DLL, node objects per
  instance. The SDKs gain a "may run in parallel instances" declaration.
- **Settings changed on the line** reach every instance between frames,
  so a part is judged with one consistent set; the set's version goes in
  the part record.
- **Memory**: models, templates and image buffers N times; the camera's
  buffers at least N plus the queue.
- **CPU**: OpenCV already threads within a call; N workers on top of that
  oversubscribe. The worker count follows the cores, with OpenCV's
  threads per worker capped.
- **The screen** already draws at its own pace; with N workers it shows
  the latest result, never every one.

**Also, separately:** running a recipe's **independent branches in
parallel** within one inspection shortens the time to a result (latency)
but not the parts per second (throughput) - useful when the deadline is
tight. An engine change of its own.

**Proposed, for Studio:** Run Folder with N workers, reporting parts per
second and the worst processing time, so a recipe can be checked against
a line's numbers before it goes on the machine.

**To learn from:** Sirius Client and MS already separate taking pictures
(Client) from judging them (MS). How they handle queues, late results
and part IDs is worth asking before this is built.

### 14.4 The hardware trigger - a later stage

Agreed 17 November 2026: **later**. Until then the PLC triggers by a bit.

**Already there:**

- The **Camera node's Hardware trigger** waits for a pulse on the camera's
  own input. A station running continuously with it set inspects each
  part as the sensor fires, with no PLC bit in between.
- A workflow can start from **Station Result** rather than a PLC bit:
  the station finishes, the workflow writes the result.

**Missing:**

- **Matching a picture to its part.** With a bit, the workflow reads the
  part number before it triggers; with a hardware trigger nothing tells
  vision which part it was. The result carries a frame number the PLC
  can match, or a part number read as close to the trigger as possible -
  a choice to make with the PLC team.
- **A lost trigger noticed.** Counts of triggers, frames and results kept
  in step, a mismatch raised as an alarm.
- **Pipelining** (14.3): a hardware-triggered camera fires at line speed
  whether or not the last picture has been judged.

**Meanwhile**, triggering by bit: the PLC Monitor reads the bit every
**Poll Interval** (20 ms by default), so a part waits up to one interval,
plus the PLC's scan, before its picture is taken. On an indexing machine,
where the part stops under the camera, that does not matter. In
continuous motion it varies from part to part, so the part is not in the
same place in every picture; the poll interval is the first thing to
shorten, and the hardware trigger the cure.

### 14.5 Order

1. **With the single picture (F25)** - the part record and part counts; a
   frame and part ID on every result, `.sann` and PLC write; the
   processing time shown, average and worst; a deadline per station, no
   result a reject, a count of late ones; the handshake (Ready, Busy)
   and error codes to the PLC.
2. **Several shots (F26)** - Combine, Calculate, Part Value, the two-shot
   starter workflow and its sequence rules, the part's widgets.
3. **Pipelining, then workers (F27)** - acquisition out of the recipe
   with one worker first, which has none of the parallel-state problems;
   then N workers, the parallel-safe marking, logs moved to the station,
   settings versioned per instance, a Workers setting per station (1 by
   default), and Run Folder with N workers in Studio.
4. **The hardware trigger (F28)** - part matching, trigger and frame
   counts, on top of 3.

### 14.6 Questions, to size it

1. Parts per minute, recipe processing time, and the time from the camera
   to the reject station, for the fastest line expected. (Question 6, in
   these terms.)
2. Several pictures per part: usually one camera at several positions
   (robot, axis), or several cameras? How many shots?
3. What does the PLC want between shots - only "picture taken, move on",
   or a pass or fail per shot?
4. How does the PLC track parts to the rejector - shift register, encoder
   counts, something else - and is there an encoder?
5. How did Sirius Interface do multi-shot sequences for customers? Is
   there a project whose signal list we could start from?

---

## 15. Cameras in the project

Discussed 17 November 2026. **No code yet.** Backlog F29.

### 15.1 Why

The camera is chosen inside the recipe today: the Camera node's Device
holds the camera's label with its serial number (`Basler acA1920-40gm
[23456789]`), beside exposure, gain, trigger and timeout. On a machine
that means:

- a camera replaced - a new serial - is a recipe opened in Studio and
  changed, although the vision logic has not;
- one recipe on two machines is two copies differing only in their
  camera;
- the integrator commissions a camera in Studio, or the maker's tools;
- there is nowhere to set a GigE camera's address, or check its
  connection, apart from a recipe.

The camera belongs to the machine; the recipe, to the judgement.

### 15.2 Proposed: a camera unit in Builder

A project holds **cameras** beside its stations and controllers, listed in
the Project panel. A **station names its camera**, as it names its recipe.
Stations get an icon of their own; the camera's goes to cameras.

**The camera window** (as the PLC window):

- **Find**: every camera the drivers see - Basler, Hikrobot - with model,
  serial and IP; choose one.
- **Live view and Grab**, with exposure, gain and trigger mode; frame rate
  and lost frames shown.
- **Save Image**: sample pictures taken on the machine, for the vision
  engineer to build or check a recipe on.
- **Take from recipe / Give to recipe** (15.3).

**What goes where:**

| The camera unit (Builder) | The recipe (Studio) |
|---|---|
| Driver and identity (serial, or the camera's user-defined name); IP | The vision logic, its thresholds and checks |
| Trigger mode, timeout; the hardware trigger later (F28) | The Camera node: where pictures come in |
| Exposure and gain - or "As the recipe" | Its own camera settings, for the desk |
| Later: pixel format, area of the image, packet size, a maker's feature file | |

**In a station**, the recipe's Camera node takes its pictures from the
station's camera unit, with the unit's settings. It is the first half of
section 14.3's "acquisition out of the recipe" - the station owns the
camera - without the queue and workers.

**Exposure and gain** go both ways: lighting differs from machine to
machine, but a recipe's thresholds were tuned at a brightness. Proposed:
in the camera unit, with **As the recipe** as a choice - and the default
for a camera unit made from an existing recipe. Exposure published as a
setting, and changed on the line, keeps working.

### 15.3 Studio keeps its Camera node

Agreed 17 November: **the Camera node stays in Studio, as it is.** An
engineer captures from a camera and tests the vision logic in one place -
grab, tune, run - rather than saving pictures to disk, loading them in
Studio, and going round again when something is wrong. The camera unit
only takes over when the recipe runs in a station: Test Run, Edge.

Keeping both needs three things.

**One camera, two programs.** A GigE camera is controlled by one program
at a time. With Edge or a Test Run holding it, Studio cannot grab from it,
and the other way round. Proposed: say so plainly - "Basler acA1920
[23456789] is in use by another program, likely Sirius Edge: stop it
there, or test on saved pictures" - rather than the SDK's error. Studio
does not stop a running line from its side.

**Open in Studio carries the station's camera.** When Builder opens a
station's recipe in Studio, it tells Studio the station's camera unit and
its settings; the Camera node grabs from that camera, with the line's
exposure and gain, and says so on the node. An engineer at the machine
tests with what the station will use, not whatever the recipe was last
saved with. Opened on its own, Studio uses the node's own Device, as now.

**A rule and two buttons for the settings.** In a station the camera
unit's values win, unless set to As the recipe. Builder's camera window
has **Take from recipe** - what the engineer tuned in Studio, into the
unit - and **Give to recipe** - the line's values back into the recipe's
Camera node, so the desk matches the machine. Without them the two drift
apart quietly.

### 15.4 Details

- **Identity.** By serial - exact - with **Replace…**: the cameras found,
  one chosen, the unit moved to it with all its settings. Or by the
  camera's **user-defined name**, written into the camera, so a
  replacement given the same name is found as the old one was. Both
  offered; serial by default.
- **IP setup.** Both SDKs set a GigE camera's IP, or give it one for now
  when it is on the wrong subnet. The PC's network card - jumbo frames,
  receive buffers - is Windows' and needs an administrator; the window
  checks it and says what is wrong, which is most of a GigE camera's
  commissioning trouble.
- **The rare features.** Both makers' cameras are GenICam. Rather than a
  page for every feature, a camera unit can load a feature file the
  maker's own viewer saved (pylon `.pfs`, MVS `.mfs`), kept in the
  project folder and applied as the camera opens. Our window covers what
  is common; the maker's tools, the rare.
- **At the desk.** A **Simulated** or **Folder of pictures** camera unit,
  so a project is tried without hardware - and a station needs no Image
  Sequence in its recipe standing in for a camera.
- **Existing recipes and projects.** Adding a station from a recipe with
  a Camera node offers a camera unit made from its Device, exposure and
  gain. A station with no camera unit runs as today. A recipe with two
  Camera nodes maps each to a camera unit, by node name.
- **Edge.** The camera window is for maintenance on the machine, behind
  a user level, as the PLC window (F23c).
- **Shared.** A camera as a unit of its own is what lets two stations
  take turns on one camera - one of the ways to several shots (14.2).

### 15.5 Order

**Step 1 built, 18 November 2026** (decision 80); **step 2, 23 November**
(decision 90).

1. Camera units in the project, stations naming theirs; the camera
   window - find, choose, live view, Grab, Save Image, exposure, gain,
   trigger; a unit offered from a recipe's Camera node; Test Run and Edge
   using it; "in use by another program" said plainly in Studio and the
   runtime.
2. Open in Studio carrying the station's camera; Take from and Give to
   recipe.
3. IP setup and the network card check; Replace…; identity by
   user-defined name; Simulated and Folder camera units.
4. A maker's feature file; more settings; the window in Edge.

### 15.6 Questions

1. Do integrators commission cameras in pylon Viewer or MVS today? Then
   a feature file builds on what they already do.
2. On your machines, is each camera on its own network port with a fixed
   IP, or several on a switch?
3. Exposure: the machine's, with As the recipe to fall back on - agreed?
4. Identity: serial with Replace…, or do service engineers already name
   cameras?
