# Sirius PLC Kit

For the team writing PLC drivers and workflows: Sirius Builder to work in,
the driver SDK to build against, a sample driver, and the guides.

**For use inside the company.** This build includes libraries whose
licences are not yet settled for customers (`docs/licences.md` in the
main repository); do not give it to anyone outside.

```
Builder\              Sirius Builder - run SiriusBuilder.exe; nothing to install
  plc-drivers\          drivers it loads at start; the sample is in Sample\
sdk\
  lib\                  Sirius.Plc.dll and Sirius.Core.dll - what a driver builds against
  SampleDriver\         a working driver library: copy it to start yours
  SampleDriver.Tests\   its tests, the conformance check among them
  SampleDriver.sln
docs\
  plc-drivers.md        writing a driver - Siemens S7 on HslCommunication step by step
  plc-workflows.md      controllers, workflows, every node, patterns
```

---

## Ten minutes in Builder, with no PLC

1. Run `Builder\SiriusBuilder.exe`. **File ▸ New Project**.
2. In the **Project** panel: **+ ▸ Add Station** - its recipe is empty,
   which is enough here: it inspects and says NONE.
3. **+ ▸ Add Controller**. It starts with a simulated PLC and a workflow
   that opens in a tab: when M0.0 rises, read a part number at MW10, have
   the station inspect, write the result to M0.1 and pulse M0.2.
4. Click the nodes: their settings are in **Properties**. The **Nodes**
   panel on the left holds the rest - drag them in.
5. **Test Run** (F5). The running screen opens, and beside it the
   simulated PLC's window with the workflow's log.
6. In that window: Address `M0.0`, Type *Bool (bit)*, Value `true`,
   **Set in Simulator**. The log shows the pass; M0.1 and M0.2 change in
   the memory list. Click M0.0 there to flip it off, then on again.

`docs\plc-workflows.md` has the rest.

## The sample driver

Builder already lists **Sample (registers)** - right-click the controller
▸ **PLC…** ▸ Driver. It is `sdk\SampleDriver`, built and placed in
`Builder\plc-drivers\Sample\`, which is all installing a driver is. It
talks to an in-memory device of 16-bit registers (`R10`, `R10.3`), with a
setting of its own (word order) - the shape of a Modbus-style driver, with
everything a real one must do.

Build and test it - this needs the .NET 8 SDK:

```
dotnet test sdk\SampleDriver.sln
```

## Your driver - Siemens S7 first

1. Copy `sdk\SampleDriver` and `sdk\SampleDriver.Tests`; rename them,
   and the namespace, assembly name, factory and **key** (`siemens-s7`).
   The key and each setting's name are saved in projects: settle them
   before anyone uses the driver.
2. Add the HslCommunication package to the driver project.
3. Follow `docs\plc-drivers.md` §4: the base class over HSL's clients,
   the S7 address translation, the CPU, rack and slot settings.
4. **The HslCommunication activation code** comes from the machine -
   the `SIRIUS_HSL_CODE` environment variable in the guide - never from
   source. Without it HSL stops after a trial period.
5. Point the conformance test at a PLC on the bench (§6.1) - addresses
   nothing else uses, since it writes them - and make it pass.
6. Build in Release, copy `bin\Release\net8.0\` into
   `Builder\plc-drivers\Siemens\`, restart Builder, choose **Siemens S7**
   in the PLC window, **Test Connection**, **Read** a few addresses.
7. Do the bench checklist (§6.2) - the cable pull above all - and the
   hand-over checklist (§7).

The TIA Portal side, which catches everyone once: the CPU must allow
**PUT/GET communication from remote partners**, and a DB read by address
must have **optimised block access switched off**.

## When something does not load

Builder's PLC window lists, under *What was done*, every driver library
it could not use and why: built against another version of Sirius.Plc, a
key already taken, no public factory with a constructor taking nothing.

## What to send back

The driver's `bin\Release\net8.0\` folder, its source, and the conformance
test's results against a real PLC.
