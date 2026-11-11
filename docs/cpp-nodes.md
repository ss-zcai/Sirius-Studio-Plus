# Custom nodes in C++

A C++ DLL can add nodes to Sirius Studio Plus. Each node takes our inputs
(images, numbers, shapes, text), shows its settings in the properties
panel, and gives back outputs the rest of the recipe uses - Filter Shapes,
Shape Table, Counter, Result Log - like any built-in node.

The library runs **inside the application**. An image reaches it as a
pointer to the memory it is already in - no copy, however large - so a C++
node is as fast as a built-in tool. The price of that is that the library
shares the application's process: what it gets wrong can, at worst, take
the application with it. Most of what goes wrong is caught and becomes the
node's error instead; section 4 says exactly what is and is not.

It is an ordinary native DLL. Nothing in it is .NET, and it links nothing
of ours: two headers, `sirius_node.h` and `sirius_node.hpp`, are the whole
interface. So it can be built with any OpenCV, with HALCON, with ONNX
Runtime, TensorRT or libtorch - whatever the code already uses.

`samples/CppNodes` is a complete example with a `build.cmd` and a
`CMakeLists.txt`. The headers are in the application's `cpp` folder.

For when to choose C++, C# or Python:

| | Runs | An image costs | A crash |
|---|---|---|---|
| **C++** | in the application | nothing in, one copy out | caught where it can be; otherwise ends the application |
| **C#** | in the application | nothing in, one copy out | an exception is the node's error |
| **Python** | in its own process | a copy each way (about 13 ms for 5 MP colour) | ends only that process |

---

## 1. A node

```cpp
#include <opencv2/imgproc.hpp>      // before sirius_node.hpp, for to_mat / from_mat
#include "sirius_node.hpp"

class LabelReader : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Label Reader")
            .key("acme.label-reader")
            .group("TextAndCodeReading")
            .description("Reads the printed label with our OCR engine.");

        d.input_image("image", "Image");

        d.output_shapes("words", "Words");
        d.output_text("text", "Text");
        d.output_number("confidence", "Confidence");

        d.setting_file("model", "Model", "ONNX (*.onnx)|*.onnx", "models/ocr-v3.onnx").reload();
        d.setting_number("min_score", "Min Score", 0.5).range(0, 1).step(0.01);
        d.setting_bool("use_gpu", "Use GPU", true).reload();
    }

    // Once, before the first run. Again only when a reload() setting changes.
    void load(sirius::Run& run) override
    {
        engine_ = acme::Engine(run.text("model"), run.boolean("use_gpu"));
    }

    void run(sirius::Run& run) override
    {
        cv::Mat image = sirius::to_mat(run.image("image"));   // no copy
        double lowest = 1.0;
        std::string text;

        for (const acme::Word& word : engine_.read(image))
        {
            if (word.score < run.number("min_score"))
                continue;

            run.add_shape("words", sirius::Shape::rect(word.x, word.y, word.w, word.h)
                                       .with_label(word.text)
                                       .with_metric("score", word.score));
            text += (text.empty() ? "" : " ") + word.text;
            lowest = std::min(lowest, word.score);
        }

        run.set_text("text", text);
        run.set_number("confidence", text.empty() ? 0.0 : lowest);
    }

private:
    acme::Engine engine_;
};

SIRIUS_NODES(LabelReader)      // once, in one source file: every node class in the library
```

- **`key`** is what a saved recipe finds the node by: `vendor.name`. Set it
  once and never change it. It is saved as `custom/acme.label-reader`.
- **`group`** is the toolbox section: `Source`, `Basic`, `Combine`,
  `Filtering`, `Morphology`, `Blob`, `Fitting`, `Transformation`,
  `Analysis`, `Calibration`, `TextAndCodeReading`, `Judging`, `Output` or
  `Custom` (the default). The node's tooltip always adds which library it
  came from, and searching the toolbox for the library's name finds its
  nodes.
- **A name** - `"image"`, `"min_score"` - is what `run()` asks for a port
  or a setting by, and what a saved recipe knows it by. Names are unique
  across a node's inputs, outputs and settings, and should not change once
  recipes use them. The **label** beside it is what the screen shows, and
  may change freely.
- **`run()`** is called once for each inspection. Asking for a name the
  node did not declare, or as the wrong type, throws a `sirius::error`
  saying which names it does have.
- **One object for each node on the canvas**, kept for as long as the node
  is there. What `load()` builds stays built across Run Continuously and
  Run Folder. Two nodes of the same kind load the model twice - share it
  through a static cache if that matters.

### Reading and writing

| | Declare | In `run()` |
|---|---|---|
| Image in | `input_image` | `run.image("name")` - a `sirius::Image` |
| Number / integer / bool in | `input_number` ... | `run.number("name")`, `run.integer`, `run.boolean` |
| Text in | `input_text` | `run.text("name")` |
| Shapes in | `input_shapes` | `run.shapes("name")` - a `std::vector<sirius::Shape>` |
| A setting | `setting_number` ... | the same `run.number`, `run.integer`, `run.boolean`, `run.text` |
| Image out | `output_image` | `run.set_image("name", image)` |
| Number / integer / bool / text out | `output_number` ... | `run.set_number`, `set_integer`, `set_bool`, `set_text` |
| Shapes out | `output_shapes` | `run.add_shape("name", shape)`, once for each |

An input is required unless declared `.optional()`. For an optional one,
`run.has("name")` says whether anything is wired, `run.number("name",
fallback)` reads it with a fallback, and `run.image("name").empty()` is
true when nothing is.

`run.warn("...")` shows on the node in amber; the run still succeeds.
`run.cancelled()` turns true when the operator stops the run - check it in
a long loop and return. `run.library_folder()` is the folder the DLL is in,
for a model file kept beside it.

### Images

`sirius::Image` is a width, a height, a channel count, a sample type, a
stride and a pointer - the fields of `sirius_image` in `sirius_node.h`.

- **An input is the application's own memory.** It is read-only - every
  other node wired to the same output is reading it too - and it is valid
  only until `run()` returns. Copy it (`cv::Mat::clone()`) to change it or
  to keep it.
- **Use the stride.** A region cut from a larger image is rows of that
  image, so the bytes from one row to the next are more than a row holds.
  `image.row<uint8_t>(y)` and `cv::Mat` both honour it.
- **Sample types** are OpenCV's depths under other names: `SIRIUS_U8`,
  `SIRIUS_S8`, `SIRIUS_U16`, `SIRIUS_S16`, `SIRIUS_S32`, `SIRIUS_F32`,
  `SIRIUS_F64`.
- **`layout`** says what the channels mean - `SIRIUS_LAYOUT_GRAY`, `BGR`,
  `BGRA`, `HSV`, ... Colour is blue, green, red, as OpenCV has it. A model
  that wants RGB needs the conversion.
- **`bits`** is how many bits carry the picture: 12 for a 12-bit camera's
  frame in 16-bit samples, where the values stop at 4095.
  `image.max_value()` is the largest value a pixel can hold. A result of
  the same sample type as the first image in keeps its bits unless the node
  says otherwise.
- **An output is copied** when `set_image` is called, so the pixels behind
  it may be freed straight after. That one copy is the only one.

With OpenCV, include it before `sirius_node.hpp`:

```cpp
cv::Mat in = sirius::to_mat(run.image("image"));          // wraps the input; no copy
cv::Mat out;
cv::GaussianBlur(in, out, cv::Size(5, 5), 0);
run.set_image("result", sirius::from_mat(out, run.image("image").layout));
```

The application's own OpenCV is built into one of its DLLs, under no name
a library's OpenCV uses, so a library may use any OpenCV version.

Without OpenCV, describe pixels the node owns:

```cpp
std::vector<uint8_t> mask(width * height);
...
run.set_image("mask", sirius::Image(mask.data(), width, height, 1, SIRIUS_U8));
```

### Shapes

`sirius::Shape::point`, `line`, `rect`, `box` (a turned rectangle),
`circle`, `ellipse` and `polygon`, in pixels of the image they were found
in. Each may carry a label, a colour (`0xAARRGGBB`) and measurements:

```cpp
run.add_shape("defects", sirius::Shape::box(cx, cy, width, height, angle)
                             .with_label("scratch")
                             .with_metric("score", 0.93)
                             .with_metric("area", 1520));
```

A measurement is what Filter Shapes, Select Shape and Shape Table work on,
whatever produced the shape. A shapes output nothing was added to is an
empty set.

### Settings

```cpp
d.setting_number("gain", "Gain", 1.5).range(0, 10).step(0.1).connectable();
d.setting_integer("level", "Level", 60).range(0, 255);
d.setting_bool("use_gpu", "Use GPU", true).reload();
d.setting_text("prefix", "Prefix", "LOT");
d.setting_choice("mode", "Mode", { "fast", "accurate" });
d.setting_file("model", "Model", "ONNX (*.onnx)|*.onnx").reload();
```

`.description(...)` is the tooltip; `.group(...)` the heading it is listed
under; `.connectable()` gives the setting an input as well, so another node
can drive it; `.logarithmic()` a slider that gives each power of ten the
same travel; `.reload()` calls `load()` again when it changes.

They are saved in the recipe by name: renaming one loses saved values.

---

## 2. Building

A 64-bit DLL, with any compiler. With Visual C++:

```
cl /LD /EHsc /MD /O2 /I "C:\Program Files\Sirius Studio Plus\cpp" my_nodes.cpp /Fe:MyNodes.dll
```

or the sample's `CMakeLists.txt`, or a Visual Studio DLL project with the
`cpp` folder added to its include directories. C++14 or later.

- **x64.** A 32-bit DLL is refused, by name.
- **`/MD`** - the shared C++ runtime - in Release. The application already
  carries it, so nothing has to be installed on the line PC. A Debug build
  (`/MDd`) needs the debug runtime, which only a PC with Visual Studio has:
  fine on a developer's PC, not on the line.
- **Tools no newer than the application's runtime.** A process has one C++
  runtime - the first one loaded - and code built with newer Visual C++
  tools than that runtime can crash on it. The application carries the
  runtime of Visual Studio 2022 17.14 (tools 14.44). A library built with
  something later still loads, with a caution in the Custom Nodes window
  saying which versions are involved.
- **`/Zi` and `/DEBUG`** write a `.pdb`. Keep it. When a node crashes the
  message says where - `MyNodes.dll+0x1A2B` - and the `.pdb` turns that
  into a file and a line.
- **Close the application before rebuilding.** It holds the DLL open while
  it runs, so the linker cannot replace it. (A C++ library cannot be
  unloaded: it may hold threads and GPU memory nothing can ask it to give
  back.)
- **To debug**, start Sirius Studio Plus, then in Visual Studio choose
  Debug ▸ Attach to Process ▸ `SiriusStudioPlus.exe`, with the code type
  set to Native. Breakpoints in `run()` then stop on the next inspection.

---

## 3. Where it runs

A library is a DLL that exports `sirius_node_library` - which
`SIRIUS_NODES(...)` does. It is found by that export, without being loaded,
in:

- the application's `plugins` folder, or `%AppData%\SiriusStudioPlus\plugins`,
  at any depth - so a library can sit in a folder with its models and the
  DLLs it uses; or
- **Tools ▸ Custom Nodes ▸ Add**, which remembers it.

Other DLLs in those folders - OpenCV's, a runtime's - are passed over.

### The DLLs it needs

Windows looks for what a library needs **beside the library first**, then
in Windows' own folders, then along `PATH`. So either copy the DLLs beside
it - `opencv_world4100.dll`, `onnxruntime.dll` - or have their folder on
`PATH`, as HALCON's installer does.

For a folder that is on neither, name it in a `sirius.json` beside the
library:

```json
{ "dllFolders": ["C:\\opencv\\build\\x64\\vc16\\bin", "%CUDA_PATH%\\bin"] }
```

A relative path is from the library's folder; `%VARIABLES%` are expanded.

When something is missing, Windows says only that "the specified module
could not be found". The Custom Nodes window names it - `It needs
opencv_world4100.dll, which Windows could not find` - looking through the
DLLs that were found for what they need in turn.

**One process holds one DLL of each name.** Two libraries that each bring
an `opencv_world4100.dll` share whichever loaded first. Two that need
*different* builds under the *same* name cannot both be right; give one a
different name, or link it statically.

### Threads

- The application never calls into one library from two threads at once -
  two stations running the same node take turns - so a node needs no locks
  of its own. Two different libraries do run in parallel.
- Successive calls may come from different threads. Do not keep anything
  in thread-local storage between runs.
- A library may start threads of its own (OpenCV and every deep learning
  runtime do). They must not touch a `sirius::Run` or an input image after
  `run()` has returned.

---

## 4. When something goes wrong

Everything in this table is one node's error, or one line in the Custom
Nodes window. The application carries on.

| What happened | Where it shows |
|---|---|
| A DLL the library needs is missing | Custom Nodes window, naming the DLL |
| The DLL is 32-bit, or built for another interface version | Custom Nodes window |
| `sirius.json` cannot be read, or names a folder that is not there | Custom Nodes window |
| A node is declared wrongly - bad key, a name used twice, an unknown type | Custom Nodes window; the library's other nodes still load |
| `load()` or `run()` threw - `sirius::error`, `std::exception`, `cv::Exception`, anything | On the node, with its `what()` |
| `run()` asked for a name the node does not have, or as the wrong type | On the node, listing the names it does have |
| An output image whose size, type or stride makes no sense | On the node |
| An output image whose pixels cannot be read - freed already, or smaller than it says | On the node |
| **A hardware fault** in `load()` or `run()`: a null or stale pointer, an integer divided by zero, the stack run out, an instruction the processor does not have | On the node, saying what and where - then see below |

A library that failed to load is tried again when it is added again in
**Tools ▸ Custom Nodes ▸ Add Library** - put the missing DLL beside it, and
add the same file; no restart needed.

### After a hardware fault

The fault is caught, and the node says what happened:

> crashed while running: it read through a null pointer, in its own code
> (MyNodes.dll+0x1A2B). MyNodes.dll has been stopped, because its memory
> can no longer be trusted - save your work and restart Sirius Studio Plus.

"In its own code" or "inside opencv_world4100.dll, which it uses" says
whose code faulted; the offset is what a `.pdb` or a map file turns into a
line.

**The library is then stopped.** Every node from it fails, with a message
saying so, until the application is restarted. Catching the fault kept the
application alive; it did not undo whatever the code wrote before it
faulted, and an inspection that carries on with damaged memory and gives a
wrong answer is worse than one that stops. The rest of the application -
the recipe, the other libraries, saving - carries on.

This guard is in `sirius_node.hpp`, compiled into the library, and needs
Microsoft's compiler or clang-cl. A library built with another compiler, or
written in C straight on `sirius_node.h`, has no such guard unless it
provides one.

### What cannot be caught

These end the application, as they end any program:

- a fault on **a thread the library started itself**;
- **`abort()`**, `std::terminate()`, a failed `assert` in a Debug build, an
  exception thrown out of a destructor or a `noexcept` function;
- **memory overwritten without a fault** - writing past the end of a buffer
  into something else that is valid. Nothing notices until later, somewhere
  else;
- a fault while the DLL is being loaded - in the constructor of a global
  object.

If a library cannot be trusted not to do these - third-party code, a model
runtime with a history - and the application must stay up regardless, run
it as a Python node instead, or ask us for C++ nodes in a process of their
own. That costs a copy of each image in exchange for the isolation.

### Exceptions that are not `std::exception`

Some libraries throw types of their own - HALCON's `HException` is one.
They are caught, but with nothing to read the message is only "It threw an
exception that says nothing about itself." Define `SIRIUS_EXTRA_CATCH`
before including the header to say how to read it:

```cpp
#include <HalconCpp.h>

#define SIRIUS_EXTRA_CATCH(error, size) \
    catch (const HalconCpp::HException& ex) { copy_text(error, size, ex.ErrorMessage().Text()); }

#include "sirius_node.hpp"
```

---

## 5. HALCON, and other libraries

Nothing here is particular to OpenCV. An image is a pointer, a size and a
stride, which every imaging library can wrap or copy from.

With HALCON, a one-channel 8-bit image whose rows are adjacent
(`stride == width`) can be used where it is:

```cpp
const sirius::Image image = run.image("image");
HalconCpp::HImage wrapped;
wrapped.GenImage1Extern("byte", image.width, image.height, image.data, nullptr);
```

Anything else - colour, which HALCON keeps as separate planes and we keep
interleaved, or a slice of a larger image - needs HALCON's own copy:
`GenImageInterleaved`. A region or an XLD contour goes back as a
`sirius::Shape::polygon` of its points.

*The HALCON lines above were written from HALCON's documentation and have
not been built here - there is no HALCON on the PC this was developed on.
Check them against your version.*

For a deep learning runtime:

- Create the session in **`load()`**, from settings declared `.reload()`,
  so it is built once and rebuilt only when the model or the device
  changes.
- Keep the model beside the DLL and find it with `run.library_folder()`,
  or take its path from a `setting_file`.
- Check **`run.cancelled()`** between batches.
- The runtime's DLLs go beside the library, or in `dllFolders`.

---

## 6. Plain C

`sirius_node.h` alone is the interface; `sirius_node.hpp` is a convenience
over it. A library in C - or in Rust, or anything that can export one C
function - fills in a `sirius_library` structure: five functions and a
description of its nodes as JSON. The header documents each. Such a library
has no guard against hardware faults unless it brings its own.

---

## 7. Licences

The application ships neither OpenCV's C++ libraries nor any runtime a
library uses; each library brings its own. Check what they are licensed
under before a library goes to a customer - HALCON needs its own runtime
licence on each PC, and **Ultralytics YOLO is AGPL-3.0**: using it in a
closed product needs Ultralytics' commercial licence.
