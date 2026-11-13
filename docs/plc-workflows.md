# PLC workflows: the guide

How a Sirius project talks to a PLC: controllers, their workflows, every
node, and the patterns a line needs. For writing a driver for a new brand
of PLC, see `plc-drivers.md`.

---

## 1. Controllers

A project holds **stations** - a camera and its recipe each - and
**controllers**: a PLC each, and the **workflow** that watches it, reads
and writes it, and has stations inspect. Both are listed in Builder's
Project panel, a camera's icon or a PLC's.

| | Where | Set in |
|---|---|---|
| Which driver, the PLC's address, the driver's settings (CPU, rack, slot) | the project file, `.sproj` | Builder ▸ right-click the controller ▸ **PLC…** |
| The workflow | `controllers\<name>.sflow` | Builder ▸ double-click the controller |

So a project moved to another line changes an address, not a workflow.

**Add Controller** (the Project panel's **+**) makes one with a simulated
PLC of its own name and a starter workflow:

```
Trigger (PLC Monitor, M0.0 rising)
  → Part Number (PLC Read, MW10 as Int16)
  → Part (Gather: PartNumber)
  → Inspect (the first station, told the part number)
      Done   → Result (PLC Write M0.1 ← Passed)  → Done (PLC Pulse M0.2)
      Failed → Error (PLC Write M0.3 ← true)
```

It runs as it is: Test Run, set M0.0 in the simulator, and the station
inspects.

**Where workflows run.** Builder's **Test Run** and **Sirius Edge** run every
controller beside the stations: connected when the screen opens,
reconnected by themselves when the PLC is lost, stopped before the
stations when it closes. Their state is along the bottom of the screen -
"Line 1 PLC: connected, 12 passes".

---

## 2. How a workflow runs

A workflow is a graph, as a recipe is, with two kinds of wire:

- **trigger wires** carry *now* - from a filled socket to a filled socket;
- **value wires** carry what was read, a verdict, a part number.

A recipe runs once per picture, start to end. A workflow **waits**: its
event nodes - **PLC Monitor** and **Station Result** - watch, and each
time something happens they start a **pass**.

### 2.1 A pass

A pass runs what is downstream of the node that started it, in wiring
order, each node once:

1. **A node with a trigger wired in runs if one of its triggers fired.**
   So a branch not taken does not run: wire Inspect's **Pass** to one
   Write and **Fail** to another, and only one runs.
2. **A node with no trigger wired in runs once everything feeding it has
   run.** Gather and Result Value work this way: Gather waits for its three
   Reads, whichever order they finish in.
3. **A node that fails** - the PLC did not answer, an address it has not
   got, longer than its time limit - gives nothing, fires its **Failed**
   trigger, and the log says why. What needed a value from it does not
   run: the failure is said once, where it happened, and nothing
   half-known is written to the PLC.

### 2.2 One pass at a time

A controller's passes run **one at a time, in the order raised**. A
second trigger while a pass runs waits for it; two inspections finishing
together are answered in order. A sequence never meets itself halfway.

Within a pass nodes run one after another, so two Inspects in one pass
inspect one after the other, not at once.

### 2.3 Time limits

Every step has one: 5 s unless the node says otherwise - Inspect has its
own **Timeout** (10 s), Wait For its own, Pulse its width plus 5 s. Past
it the step fails, and **Failed** fires.

### 2.4 Edges, and what a Monitor does not see

- A PLC Monitor starts from **what it first reads**. A bit already on when
  the controller starts does not fire; the PLC must drop it and raise it
  again. The same after a lost connection: an edge across it is no edge.
- A change while the pass it started is still running is not seen. A PLC
  waiting for an answer holds its bit until it has one - the handshake
  (§4.2) - so nothing is lost that matters.

---

## 3. The nodes

Sockets: **●** trigger, **○** value.

### PLC

**PLC Monitor** - *starts a pass.* Watches an address and starts the
workflow when it changes.

| | |
|---|---|
| Settings | **Address**, **Type** (Bool for a bit), **Length** (for a String), **Edge**: Rising (off to on), Falling, Change (any new value - for a number or text), **Poll Interval** (ms, 20) |
| Out | ● **Triggered** · ○ **Value** (as it now is) · ○ **Count** (times fired since the controller started) |

**PLC Read** - reads an address when triggered.

| | |
|---|---|
| In | ● **Trigger** |
| Settings | **Address**, **Type**, **Length** |
| Out | ○ **Value** · ● **Done** · ● **Failed** |

**PLC Write** - writes to an address when triggered.

| | |
|---|---|
| In | ● **Trigger** · ○ **Value** (unwired: the setting below) |
| Settings | **Address**, **Type**, **Length**, **Value** (true, 1, 3.5, text) |
| Out | ● **Done** · ● **Failed** |

A value is converted to the type: Inspect's **Passed** (yes/no) written
as an Int16 is 1 or 0. One that does not fit - 70000 into an Int16 - fails
the step, saying so.

**PLC Pulse** - sets a bit, waits, clears it: *done*, said to the PLC.

| | |
|---|---|
| In | ● **Trigger** |
| Settings | **Address** (a bit), **Width** (ms, 200 - long enough for the PLC's scan) |
| Out | ● **Done** · ● **Failed** |

The bit is cleared even if the controller stops part way.

**PLC Wait For** - waits until an address holds a value.

| | |
|---|---|
| In | ● **Trigger** |
| Settings | **Address**, **Type**, **Length**, **Value** (what it waits for), **Timeout** (ms, 5000), **Poll Interval** (ms, 20) |
| Out | ○ **Value** · ● **Done** · ● **Timed Out** · ● **Failed** |

Timing out is not failing: **Timed Out** fires, so a workflow can say what
happens then.

**Gather** - puts values together under names, as what an inspection is
told about its part.

| | |
|---|---|
| In | ● **Trigger** (optional) · ○ **Value 1** … **Value 8** |
| Settings | **Name 1** … **Name 8** - PalletId, PositionId, RunMode |
| Out | ○ **Part** · ● **Gathered** |

Without a trigger it runs once every value wired in has been read.

### Stations

**Inspect** - has a station inspect once.

| | |
|---|---|
| In | ● **Trigger** · ○ **Part** (from Gather) |
| Settings | **Station** (one of the project's), **Timeout** (ms, 10000) |
| Out | ● **Done** (whatever the verdict) · ● **Pass** · ● **Fail** · ● **Failed** (could not inspect: busy, faulted, too slow) · ○ **Verdict** (PASS, FAIL, ERROR, NONE) · ○ **Passed** · ○ **Result** |

The part's values go with the inspection: into its picture's `.sann` as
`"part": { "PalletId": 12, ... }` - the part ID pictures from two
stations are matched on - and the station's snapshot.

**Station Result** - *starts a pass* whenever a station has inspected,
however it was started: a PLC, a button, running.

| | |
|---|---|
| Settings | **Station** |
| Out | ● **Finished** · ● **Pass** · ● **Fail** · ○ **Verdict** · ○ **Passed** · ○ **Result** |

A workflow that both Inspects a station and hears its Station Result gets
two passes per part. Answer from Inspect's outputs, usually; Station
Result is for a station running by itself, or triggered by hardware.

**Result Value** - one value from an inspection, by name.

| | |
|---|---|
| In | ○ **Result** |
| Settings | **Name** - a published result (Diameter, Count, Code) or a part value (PalletId) |
| Out | ○ **Value** |

---

## 4. Patterns

Addresses are Siemens-style; write your PLC's.

### 4.1 Trigger, inspect, answer

The starter workflow (§1). The PLC sets M0.0; the station inspects; M0.1
is the result, M0.2 pulses *done*. The PLC reads M0.1 on the rising edge
of M0.2, then drops M0.0.

### 4.2 The handshake, with Busy

When the PLC must not trigger again until the answer is in:

```
Monitor (Trigger rising)
  → Write Busy ← true
  → Inspect
      Done → Write Result ← Passed → Write Busy ← false → Pulse Done
      Failed → Write Alarm ← true → Write Busy ← false
```

Chain them through each node's **Done**. A Write's **Failed** wired to an
alarm bit, or to nothing - it is logged either way.

### 4.3 The part's identity

```
Monitor (Trigger rising)
  → Read Pallet (DB34.DBW22, Int16), Read Position (DB34.DBW24, Int16)   both from Triggered
  → Gather (PalletId, PositionId)       runs once both Reads have
  → Inspect (Part ← Gather's Part)
```

Each picture's `.sann` then carries the pallet and position.

### 4.4 Measurements back to the PLC

```
Inspect
  Result → Result Value (Diameter) → Write DB40.DBD0 (Real) ← Value     triggered by Inspect's Done
```

One Result Value and one Write per value. The name is what the recipe
publishes (Studio ▸ Published).

### 4.5 Waiting for the PLC

```
… → Pulse Done → Wait For (Trigger, false, 2000 ms)
                    Done      → (ready for the next)
                    Timed Out → Write Alarm ← true
```

### 4.6 A station that runs by itself

A station started from the screen, or triggered by hardware, with the
PLC told each result:

```
Station Result (Top)
  Finished → Write Result ← Passed → Pulse Done
```

---

## 5. Trying it at the desk

1. Add a controller: simulated, with the starter workflow.
2. **Test Run** (F5). The screen opens, and beside it the simulated PLC's
   window, with the workflow's log.
3. In the PLC window: Address `MW10`, Type Int16, Value 42, **Set in
   Simulator** - the part number. Then `M0.0`, Bool, `true`, **Set in
   Simulator** - the trigger.
4. The station inspects; the log says so; M0.1 and M0.2 change in the
   memory list. Click M0.0 to flip it off, and again to trigger the next.

To go to the real PLC: right-click the controller ▸ **PLC…**, choose its
driver, type its address and settings, **Test Connection**, and **Read**
an address or two to be sure. The workflow is unchanged - if its
addresses were written as the real PLC's.

### The PLC window

| | |
|---|---|
| **Connection** | the driver, the PLC's address, the driver's own settings; **Test Connection** says how it went and how long it took |
| **Read and Write** | any address as Bool, Byte, Int16 (INT), UInt16 (WORD), Int32 (DINT), UInt32 (DWORD), Real, LReal or String - through a connection of its own, as the workflow will |
| **Simulated PLC memory** | every address set, its value and type; a bit flipped with a click; **Unplug** to see what the workflow does without a PLC |
| **Workflow** | during a Test Run, the workflow's log |

---

## 6. When something does not work

| Seen | Likely |
|---|---|
| The check along the bottom of the workflow: "Part Number: No address is given." | as it says - each problem names its node |
| "There is no station called 'Top'." | the station was renamed: choose it again in the node's **Station** |
| "Nothing starts this workflow" | it has no PLC Monitor or Station Result |
| A trigger that does nothing | the bit was already on when the run started (§2.4); drop it and raise it |
| "Inspect: Top did not inspect: it is busy or faulted." | the station is running continuously, or its recipe would not load - the screen says which |
| "Inspect: No answer within 10 s." | the inspection took longer than its **Timeout** |
| "Cannot connect to ...", once, then nothing | the PLC is unreachable; it keeps trying every 2 s and says when it connects |
| "No driver called 'siemens-s7' is installed." | the driver library is not in `plc-drivers\` on this PC (`plc-drivers.md` §3.2) |

---

## 7. The file

`controllers\<name>.sflow`, JSON:

```json
{ "format": "sirius-workflow", "version": 1, "name": "Line 1 PLC",
  "graph": { "version": 2, "nodes": [ ... ], "connections": [ ... ] } }
```

`graph` is written as a recipe's is. A reader ignores fields it does not
know, so later versions add rather than change. Saved by hand - Ctrl+S
with its tab in front, or Save All - and a Test Run offers to save a
changed one first, since it runs what is on disk.
