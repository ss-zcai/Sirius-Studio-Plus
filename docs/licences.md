# Licences

Every licence question for the Sirius suite (Studio, Builder, Edge,
Sirius IV, the SDKs and NodeGraphKit) in one place: what we use, on what
terms, what is settled and what is not. Updated 13 November 2026.

`THIRD-PARTY-NOTICES.txt` is what ships to customers. This page is for
us: the reasoning, the open questions and what to do about them. The
backlog's L items point here.

**Internal testing is fine as things stand. A build going to a customer
is not, until section 1 is settled.**

---

## 1. Before any build goes to a customer

| # | What | Status | What has to happen |
|---|---|---|---|
| L1 | **Emgu CV** (OpenCV for .NET) | **Blocker** | Buy a commercial licence, and get Emgu's written word on §4 (below). |
| L2 | **Basler pylon**: our side of its conditions | Open | Our own licence agreement (EULA) with a liability exclusion; legal review. |
| L3 | **Hikrobot MVS**: may its DLL ship? | Open, unknown | Ask Hikrobot whether it may ship. (Now in the notices.) |
| L5 | **HslCommunication** (PLC), once the PLC work ships | **Licence held** (Enterprise) | Check the contract allows shipping it in our product; keep the activation code out of git; add to the notices. |
| L6 | **Our own EULA** | Missing | Write one. pylon requires it (L2), and every customer build needs one anyway. |

### L1. Emgu CV - the one real blocker

**What:** Emgu.CV 4.13 is the OpenCV wrapper behind nearly every vision
node. It is **dual licensed: GPLv3, or a paid commercial licence.**
Shipping a closed product under the GPLv3 option would mean releasing the
whole product's source under GPLv3. Nobody knows whether the original
Sirius Studio held a commercial licence ("no / not sure"), so assume not.

**The commercial terms**, from Emgu's published agreements (read 27
October 2026): $149 a year Windows-only or $299 multi-platform, per
developer.
- **Stay on the v4 rev 2 agreement, not v5.** v4 rev 2 has no expiry
  clause: the grant attaches to the version downloaded and ends only on
  breach. v5 adds §6: stop renewing and you must *immediately stop all
  new distribution*. That turns a yearly fee into a fee for being allowed
  to keep shipping. v5 also drops the Workgroup and Enterprise tiers, and
  adds limits on build machines, auto-renewal, no refunds and Ontario
  jurisdiction.
- **§4, in both versions, needs Emgu's written confirmation before
  paying.** It forbids redistribution inside "a development toolkit or
  library ... intended for use by software developers", and building a
  product of "substantially the same functionality". Studio is a node
  graph tool exposing OpenCV operations, and we ship an SDK, so ask
  plainly whether that is caught.

**Recommendation:** buy the Windows tier, stay on Emgu 4.13 under v4
rev 2, with §4 confirmed in writing. It has a procurement lead time, so
start early. Moving to Emgu 5 buys nothing we use.

**Fallback:** OpenCvSharp (Apache 2.0). Emgu types appear only in the
vision nodes and one image converter, so it is a mechanical port: 139
`CvInvoke` calls to 55 functions, plus Read Text's Tesseract wrapper. A
few days. See `decisions.md` §8.

### L2. Basler pylon

**What:** `Basler.Pylon.dll` (pylon 26.10 .NET API) ships in the package
since 31 October 2026, for Basler cameras. The pylon Runtime itself is
installed separately on each PC; we do not ship it.

**Terms:** the pylon Software License Agreement, Acquisition Version, is
free and needs no licence file. It allows distributing applications
built on it, **excluding vTools** (which need a paid Runtime licence:
never use them). Its conditions:

1. don't use Basler's name, logo or trademarks to market the product;
2. show a valid copyright notice for Basler - **done**, in the notices;
3. don't remove Basler's notices - **done**;
4. **ship our own licence agreement, at least as restrictive as
   Basler's** - open, needs L6;
5. **exclude Basler from liability** - open, in our EULA;
6. **indemnify Basler** - open, legal review.

### L3. Hikrobot MVS

**What:** `MvCameraControl.Net.dll` from MVS 5.1.1 ships in the package,
copied by `tools/pack.ps1`, for Hikrobot cameras. MVS itself is installed
separately on each PC.

**Problem:** MVS's licence notice lists only its open-source parts.
Appendix I, which should hold Hikrobot's own terms for the SDK, is empty.
So we do not know whether the DLL may ship beside our application.

**To do:**
- Ask Hikrobot (or a distributor) in writing whether the .NET wrapper may
  be redistributed.
- If it may not, stop shipping it: load it from the MVS install on the
  PC, as pylon's runtime is.
- ~~Add Hikrobot to `THIRD-PARTY-NOTICES.txt`~~ - done 13 November 2026:
  its copyright, and that its redistribution terms are being confirmed.

### L5. PLC communication libraries (with backlog F23)

**HslCommunication: the Enterprise licence is held** (confirmed 13
November 2026). It is commercial, not open source. Enterprise is the
edition for companies, under a contract, with any number of activations
and PCs, all the source code (C#, Java, Python), and source updates for
life. The other editions, for the record: Personal (¥240, not for
companies) and Professional (¥7,800, no source); Enterprise is ¥11,000.

So it is the library under our PLC drivers (F23d) and the PLC team's:
one library for Siemens, Mitsubishi, Omron, Allen-Bradley, Modbus,
Keyence and Panasonic. Its virtual PLCs can serve the simulator and the
driver test kit.

What is left:
- **Check the contract's words**, as only the price page has been read.
  It should say that HslCommunication may be built into a product we
  sell and installed on customers' machines, and that the PLC start kit
  may carry it to the PLC team. Inside the company, the start kit should
  be fine; a contractor or partner writing drivers would need checking.
- **Whether the activation code is tied to a version**, so an update of
  the library does not stop the line.
- **The activation code is a secret.** Without one, a program stops
  after a trial (24 hours by the price page, 8 by the README), so every
  build must carry it. Keep it **out of the repository and the start
  kit's source**: put it into the build from an environment variable or
  a file outside git, as the camera SDKs' paths are. A build without it
  says so loudly, at start and in the Output panel, rather than stopping
  hours later on a line.
- **Add it to `THIRD-PARTY-NOTICES.txt`** when the first driver using it
  ships: "HslCommunication, Copyright (c) Richard Hu, used under a
  commercial licence".
- Kept behind `IPlcDriver`, so a brand could still move to another
  library if ever needed.

**Open alternatives**, per brand, should one be wanted:
- S7.Net Plus (MIT) for Siemens;
- FluentModbus or NModbus (MIT) for Modbus TCP;
- Snap7 and Sharp7 (LGPL): workable as a separate DLL, not merged in;
- libplctag (MPL 2.0 / LGPL) for EtherNet/IP.

### L6. Our own licence agreement

The package has third-party notices but **no licence agreement of our
own**. One is needed for any customer build, and pylon requires one (L2,
conditions 4-6). It should cover at least:
- the licence grant: per machine, as runtime design §12 proposes;
- no reverse engineering;
- a warranty and liability exclusion that also covers Basler;
- the third-party components, by reference to the notices.

A lawyer's job; the facts are on this page.

---

## 2. Settled: what ships, and what it asks of us

All permissive. What each asks is met by `THIRD-PARTY-NOTICES.txt` and the
licence files beside it; `tools/pack.ps1` refuses to pack without them.

| Component | Version | Licence | What it asks | How it is met |
|---|---|---|---|---|
| OpenCV (inside Emgu) | 4.13 | Apache 2.0 | Notice and licence text | `LICENSE-apache-2.0.txt` |
| FFmpeg (inside Emgu's `opencv_videoio_ffmpeg`) | - | LGPL 2.1+ | A separate DLL, replaceable | Shipped as Emgu builds it, unmodified |
| Tesseract OCR, and its English data (`eng.traineddata`, fast) | 5.3.4 | Apache 2.0 | Notice | Notices; Apache text |
| ZXing.Net | 0.16.11 | Apache 2.0 | Notice | Notices; Apache text |
| Nodify | 7.3.0 | MIT | Copyright notice | MIT text in the notices |
| AvalonDock and its VS2013 theme | 4.74.1 | **Ms-PL** (not MIT) | Keep its notices; source only under Ms-PL | `LICENSE-avalondock-mspl.txt` (fixed d5052b6) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Copyright notice | MIT text |
| Clipper2 | 1.5.4 | Boost 1.0 | Nothing for compiled code | Text in the notices anyway |
| Phosphor Icons (restyled) | - | MIT | Copyright notice | `LICENSE-phosphor.txt` |
| .NET and Windows Desktop Runtime | 8 | MIT | - | Notices |

**Tools only, not shipped:** xUnit, Microsoft.NET.Test.Sdk (tests); the
Python icon scripts.

**Chosen to avoid licences:** DevExpress was considered for the UI and
dropped. The stack is license-free WPF (`decisions.md` §2).

---

## 3. Installed by the customer, not shipped by us

Each PC needs these installed separately. Their own licences are
between the customer and the maker, but the customer should be told.

| What | For | Licence |
|---|---|---|
| Basler **pylon Runtime** 26.10 or later | Basler cameras | Free, Basler's agreement (no vTools) |
| Hikrobot **MVS** | Hikrobot cameras | Hikrobot's (see L3) |
| **Python** and its libraries | Python nodes | Each library its own (see section 4) |
| **HALCON** runtime | C++ nodes built on HALCON | MVTec runtime licence per PC |
| **OpenCV** (native) | C++ nodes built on it | Apache 2.0 |

---

## 4. What we hand to others

- **The custom-node SDK** (`Sirius.Plugins-SDK-*.zip`): our headers,
  samples and guide. It carries no third-party code, so nothing to add,
  but Emgu's §4 (L1) is about exactly this kind of "development toolkit":
  include the SDK in the question to Emgu.
  - **Its users' libraries are their own concern.** A custom node
    developer chooses what their nodes use, and its licence is theirs to
    meet. The guides say so (`python-nodes.md` §4, `cpp-nodes.md` §7),
    naming the case most likely to bite: **Ultralytics YOLO is
    AGPL-3.0**, so a closed product using it needs Ultralytics'
    commercial licence. *We do not use YOLO* (13 November 2026). If we
    ever ship a node library of our own that does, it comes back to
    section 1.
- **NodeGraphKit**, given to the PLC team: the editor and theme, with
  their own `THIRD-PARTY-NOTICES.txt` (Nodify MIT, AvalonDock Ms-PL,
  Phosphor MIT). Inside the company, so no issue; the notices travel with
  it.
- **The PLC start kit** (F23e), when it exists: our code, plus whatever
  driver library it carries: HslCommunication (Enterprise, held), as
  the contract allows, with the activation code kept out of it (L5).

---

## 5. Adding a dependency

Before a new package, SDK or data file goes into the product:

1. **Read its licence** - the package's own, not the repository's front
   page; AvalonDock's turned out to be Ms-PL, not MIT.
2. **Strong copyleft (GPL, AGPL) or commercial**: stop and decide here
   first, with an entry in section 1.
3. **LGPL**: only as a separate DLL that could be swapped, never merged.
4. **Permissive**: add it to `THIRD-PARTY-NOTICES.txt`, with its text if
   it asks for it, and the file to `pack.ps1`'s required list.
5. **A maker's SDK** (a camera, a PLC): find its redistribution terms in
   writing, or load it from the maker's install instead of shipping it.
