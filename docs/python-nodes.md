# Custom nodes in Python

A Python file can add nodes to Sirius Studio Plus. Each node takes our
inputs (images, numbers, shapes, text), shows its settings in the
properties panel, and gives back outputs the rest of the recipe uses -
Filter Shapes, Shape Table, Counter, Result Log - like any built-in node.

The library runs in **its own Python process, with its own Python**: an OCR
engine on one CUDA stack and a YOLO model on another can both be in one
recipe. The application does not install or manage Python. If the Python
will not start, or a package is missing, the Custom Nodes window and the
node say exactly what failed.

`samples/PythonNodes/dark_spots.py` is a complete example with its tests.

---

## 1. A node

```python
from sirius_nodes import node, Input, Output, Setting, Rect

@node("Label Reader", key="acme.label-reader", group="TextAndCodeReading",
      description="Reads the printed label with our OCR engine.")
class LabelReader:
    image = Input.image("Image")

    words = Output.shapes("Words")
    text = Output.text("Text")
    confidence = Output.number("Confidence")

    model = Setting.file("Model", "models/ocr-v3.onnx", filter="ONNX (*.onnx)|*.onnx", reload=True)
    min_score = Setting.number("Min Score", 0.5, min=0, max=1, step=0.01)
    use_gpu = Setting.bool("Use GPU", True, reload=True)

    def load(self):
        # Once, before the first run, with the settings already set.
        # Again only when a reload=True setting changes.
        self.engine = acme_ocr.Engine(self.model, gpu=self.use_gpu)

    def run(self, image):
        found = [w for w in self.engine.read(image) if w.score >= self.min_score]
        return {
            "words": [Rect(w.x, w.y, w.w, w.h, label=w.text, score=w.score) for w in found],
            "text": " ".join(w.text for w in found),
            "confidence": min((w.score for w in found), default=0.0),
        }
```

- **`key`** is what a saved recipe finds the node by: `vendor.name`. Set it
  once and never change it. It is saved as `custom/acme.label-reader`.
- **`group`** is the toolbox section: `Source`, `Basic`, `Combine`,
  `Filtering`, `Morphology`, `Blob`, `Fitting`, `Transformation`,
  `Analysis`, `Calibration`, `TextAndCodeReading`, `Judging`, `Output` or
  `Custom` (the default).
- **`run()`** is called once per image with the inputs as keyword
  arguments, named as the class attributes are. Settings are attributes:
  `self.min_score`.
- **Return** a dict of outputs by name. With one output, the value alone
  will do; with several, a tuple in declaration order also works. An output
  left out (or `None`) is not set.
- **`self.warn("...")`** shows a warning on the node - it still produced a
  result, but something is worth knowing.
- **An exception** puts the node in error, with the message, file and line:
  `ValueError: no label found (label_reader.py, line 31, in run)`.

### Types

| Declared as | Python sees / returns | In the application |
|---|---|---|
| `Input.image` / `Output.image` | numpy array, `H x W` or `H x W x C`, **BGR** like OpenCV; `uint8`, `uint16` or `float32` | Image |
| `Input.number` / `Output.number` | `float` | Number |
| `Input.integer` / `Output.integer` | `int` | Integer |
| `Input.bool` / `Output.bool` | `bool` | Pass/fail, yes/no |
| `Input.text` / `Output.text` | `str` | Text |
| `Input.shapes` / `Output.shapes` | list of `Point`, `Line`, `Rect`, `Box`, `Circle`, `Ellipse`, `Polygon` | Shapes |

An output image of another dtype is converted to `float32`; a bool mask to
0/255. Without numpy installed, images arrive as `sirius_nodes.Image`
(`width`, `height`, `channels`, `dtype`, `data` bytes) and may be returned
as one.

An input declared `required=False` arrives as `None` when nothing is wired.

### Shapes

Detections, OCR words and found features are shapes, so every shape node
works on them:

```python
Rect(x, y, width, height, label="scratch", score=0.91, class_id=3)   # top-left corner
Box(cx, cy, width, height, angle, label="chip")                       # turned about its centre
Circle(cx, cy, radius)
Ellipse(cx, cy, major, minor, angle)
Point(x, y)
Line(x1, y1, x2, y2)
Polygon([(x, y), ...], closed=True)
```

`label` is drawn beside the shape. Every other keyword - `score`,
`class_id`, `area` - is a measurement that Filter Shapes and Shape Table can
use. `colour="red"`, `"#FF8800"` or `(r, g, b)` sets its colour.

Shapes arriving on an input are the same classes, in a list with `.width`
and `.height` of the image they were found in.

### Settings

```python
Setting.number(label, default, min=, max=, step=, logarithmic=False)
Setting.integer(label, default, min=, max=, step=)
Setting.bool(label, default)
Setting.text(label, default)
Setting.choice(label, default, choices=["fast", "accurate"])
Setting.file(label, default, filter="ONNX (*.onnx)|*.onnx")
```

All take `description=` (the tooltip) and `group=` (a heading in the
properties panel). `reload=True` calls `load()` again when it changes - for
a model path, a device. `connectable=True` (number, integer, bool, text)
gives the setting an input as well, so another node can drive it.

They are saved in the recipe by name: renaming one loses saved values.

---

## 2. Where it runs

A library is a `.py` file that imports `sirius_nodes` and declares at least
one `@node`. It is loaded from:

- the application's `plugins` folder, or `%AppData%\SiriusStudioPlus\plugins`,
  at any depth - so a library can sit in a folder with its models, helper
  modules and environment; or
- **Tools ▸ Custom Nodes ▸ Add**, which remembers it.

Files called `test_*.py`, `*_test.py` or `conftest.py`, helper modules that
declare no node, and anything inside `.venv`, `venv`, `site-packages` or
`__pycache__` are passed over.

**Which Python** runs it, first found:

1. `sirius.json` beside it, naming an interpreter or an environment folder:
   ```json
   { "python": "C:\\envs\\ocr\\python.exe" }
   ```
   A relative path is from the library's folder; `%VARIABLES%` are expanded.
   A venv folder or a conda environment folder works too.
2. A `.venv` or `venv` folder beside it.
3. `python.exe` on PATH, then the `py` launcher. A real install is
   preferred to the Microsoft Store alias in `WindowsApps`: Python started
   through the alias runs inside a package and cannot see files under
   `%AppData%`, so it cannot load a library from the per-user plugins
   folder.

For anything with GPU or pinned packages, use `sirius.json`: PATH differs
from machine to machine.

The library's folder is on `sys.path`, and is the working directory, so it
can import its own modules and open files relative to itself.

### The process

- Started when the library loads, and **kept running**: a model loaded in
  `load()` stays loaded across Run Continuously and Run Folder.
- Each node on the canvas has its own object; `load()` runs once for it. Two
  nodes of the same kind load the model twice - keep a module-level cache if
  that matters.
- One run at a time per library. Two libraries run in parallel.
- `print()` goes to the process's stderr. Its last lines are included in the
  message when the process stops unexpectedly.
- **A crash** (a CUDA fault, a segfault in an extension, `os._exit`) ends
  only that process. The node reports it; the next run starts a new one.
- **Stopping** a run gives Python 5 seconds to finish the current image
  before the process is ended.
- **Restart Python**, a button on each Python node, starts the process
  again and re-reads the file - after editing `run()`. A change to the
  node's inputs, outputs or settings needs the application restarted.

Images go to and from the process through a pipe. A 5 MP colour frame
(15 MB) sent and returned unchanged takes about 13 ms on the development
machine; a node that returns only shapes and numbers pays half that. Shared
memory, if that matters, can come later without changing a library.

### Errors

| What happened | Where it shows |
|---|---|
| No Python found, or `sirius.json` names one that does not exist | Custom Nodes window |
| Python would not start | Custom Nodes window, with the reason Windows gave |
| The file would not import - `ModuleNotFoundError: No module named 'torch'` | Custom Nodes window, with the file and line, and which Python it was |
| A node is declared wrongly - bad key, unknown group, no outputs | Custom Nodes window; the library's other nodes still load |
| Importing took more than 2 minutes | Custom Nodes window |
| `load()` or `run()` raised | On the node, with the file and line |
| The process died | On the node, with its exit code and last output |

A library that failed to load is tried again when it is added again in
**Tools ▸ Custom Nodes ▸ Add Library** - install the missing package, or
fix `sirius.json`, and add the same file; no restart needed.

---

## 3. Testing without the application

`NodeRunner` does what the application does - settings, `load()`,
`run()`, converting outputs - so pytest can check a node:

```python
from sirius_nodes import NodeRunner
from label_reader import LabelReader

def test_reads_the_sample_label():
    runner = NodeRunner(LabelReader)
    out = runner.run({"min_score": 0.8}, image=cv2.imread("samples/label.png"))
    assert out["text"] == "LOT 4471"
    assert runner.warnings == []
```

`sirius_nodes.py` is in the application's `python` folder. Put that folder
on `PYTHONPATH`, or copy the file next to the library for testing - when
the application runs the library it always uses the copy in its own
`python` folder.

---

## 4. Licences

The application does not ship Python or any package; each library brings
its own. Check what they are licensed under before a library goes to a
customer. In particular, **Ultralytics YOLO is AGPL-3.0**: using it in a
closed product needs Ultralytics' commercial licence.
