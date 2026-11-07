"""
Custom nodes for Sirius Studio Plus, written in Python.

A node is a class with the @node decorator. Its inputs, outputs and settings
are class attributes; run() is called once per image with the inputs as
keyword arguments, and returns the outputs:

    from sirius_nodes import node, Input, Output, Setting, Rect

    @node("Dark Spots", key="acme.dark-spots", group="Analysis",
          description="Finds spots darker than the level.")
    class DarkSpots:
        image = Input.image("Image")
        spots = Output.shapes("Spots")
        count = Output.integer("Count")

        level = Setting.integer("Level", 60, min=0, max=255)

        def load(self):
            # Once, before the first run - load models here, not in run().
            ...

        def run(self, image):
            found = [Rect(10, 20, 5, 5, label="spot", score=0.9)]
            return {"spots": found, "count": len(found)}

The application runs each library file in its own Python process, with the
interpreter that library names, so a library can use whatever packages its
environment has. Settings are attributes of the node object (self.level)
when load() and run() are called.

Images arrive as numpy arrays when numpy is installed - height x width, or
height x width x channels, in BGR order like OpenCV - and as Image objects
otherwise. Either can be returned.

An image says how many bits of its values carry the picture: image.bits is
12 for a 12-bit camera's frame, whose values stop at 4095 inside uint16,
and image.max_value is 4095. A returned image of the same type as the first
image in keeps its bits; say otherwise with with_bits(result, 16).
"""

import math
import re

__version__ = "1.1"

try:
    import numpy as _np
except Exception:  # not installed, or installed and broken
    _np = None

GROUPS = (
    "Source", "Basic", "Combine", "Filtering", "Morphology", "Blob", "Fitting",
    "Transformation", "Analysis", "Calibration", "TextAndCodeReading", "Judging",
    "Output", "Custom",
)

_KEY = re.compile(r"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)+$")
_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")

# Node classes, in the order their modules declared them.
_registry = []


# ---- images -------------------------------------------------------------------

_SAMPLE_SIZES = {"uint8": 1, "uint16": 2, "float32": 4}
_LAYOUTS = ("unknown", "gray", "bgr", "bgra", "hsv", "hls", "lab", "luv", "ycrcb", "xyz")


def _default_layout(channels):
    return {1: "gray", 3: "bgr", 4: "bgra"}.get(channels, "unknown")


def _container_bits(dtype):
    """The bits a sample type holds: 8, 16, or None for floating point."""
    return {"uint8": 8, "uint16": 16}.get(str(dtype))


def bits_of(image):
    """
    How many bits of an image's values carry the picture: 12 for a 12-bit
    camera's frame in uint16, otherwise all its type holds. None for float.
    """
    container = _container_bits(image.dtype)
    bits = getattr(image, "bits", None)
    return container if bits is None or container is None else min(bits, container)


def max_value(image):
    """The largest value a pixel can hold: 255, 4095 for 12-bit, 1.0 for float."""
    bits = bits_of(image)
    return 1.0 if bits is None else (1 << bits) - 1


def with_bits(image, bits):
    """
    The same image, saying that `bits` of its values carry the picture -
    for a result whose bits differ from the image it was made from.
    """
    if bits is not None and not 1 <= int(bits) <= 16:
        raise ValueError("bits must be 1 to 16, not %r" % (bits,))
    if isinstance(image, Image):
        image.bits = None if bits is None else int(bits)
        return image
    if _np is not None and isinstance(image, _np.ndarray):
        tagged = image.view(_Picture)
        tagged.bits = None if bits is None else int(bits)
        return tagged
    raise TypeError("with_bits takes a numpy array or an Image, not %s" % type(image).__name__)


if _np is not None:
    class _Picture(_np.ndarray):
        """
        A numpy array that knows how many bits of its values carry the
        picture - image.bits, image.max_value. Otherwise a numpy array.
        """

        def __array_finalize__(self, obj):
            # Slices and arithmetic keep it; a different dtype makes it
            # meaningless, which bits_of() allows for.
            self.bits = getattr(obj, "bits", None)

        def __array_wrap__(self, array, *args, **kwargs):
            result = super().__array_wrap__(array, *args, **kwargs)
            # A reduction - image.mean() - is a plain number, not a 0-d picture.
            if isinstance(result, _np.ndarray) and result.ndim == 0:
                return result[()]
            return result

        @property
        def max_value(self):
            return max_value(self)


class Image:
    """
    A picture, for when numpy is not installed: rows of samples, top to
    bottom, channels interleaved (BGR for colour).
    """

    def __init__(self, width, height, channels=1, dtype="uint8", data=None, layout=None, bits=None):
        if dtype not in _SAMPLE_SIZES:
            raise ValueError("dtype must be uint8, uint16 or float32, not %r" % (dtype,))
        if width < 1 or height < 1 or not 1 <= channels <= 4:
            raise ValueError("an image needs a width and height of at least 1 and 1 to 4 channels")

        size = width * height * channels * _SAMPLE_SIZES[dtype]
        if data is None:
            data = bytearray(size)
        elif len(data) != size:
            raise ValueError("a %dx%dx%d %s image needs %d bytes, not %d"
                             % (width, height, channels, dtype, size, len(data)))

        self.width = width
        self.height = height
        self.channels = channels
        self.dtype = dtype
        self.data = data
        self.layout = layout or _default_layout(channels)
        self.bits = bits

    @property
    def max_value(self):
        """The largest value a pixel can hold: 255, 4095 for 12-bit, 1.0 for float."""
        return max_value(self)

    @property
    def shape(self):
        """(height, width) or (height, width, channels), as numpy would say it."""
        return (self.height, self.width) if self.channels == 1 else (self.height, self.width, self.channels)

    def to_numpy(self):
        if _np is None:
            raise RuntimeError("numpy is not installed in this Python environment")
        return _np.frombuffer(self.data, dtype=self.dtype).reshape(self.shape)

    def __repr__(self):
        bits = bits_of(self)
        depth = "" if bits in (None, _container_bits(self.dtype)) else ", %d-bit" % bits
        return "Image(%dx%dx%d %s%s, %s)" % (self.width, self.height, self.channels, self.dtype, depth, self.layout)


def _as_image(value):
    """An output image as an Image, whatever the node returned."""
    if isinstance(value, Image):
        return value

    if _np is not None and isinstance(value, _np.ndarray):
        array = value
        if array.ndim == 3 and array.shape[2] == 1:
            array = array[:, :, 0]
        if array.ndim not in (2, 3) or (array.ndim == 3 and not 1 <= array.shape[2] <= 4):
            raise ValueError("an image must be height x width or height x width x channels (1-4), "
                             "not %s" % (array.shape,))
        if array.dtype == _np.bool_:
            array = array.astype(_np.uint8) * 255
        elif array.dtype.name not in _SAMPLE_SIZES:
            array = array.astype(_np.float32)

        bits = getattr(value, "bits", None)
        array = _np.ascontiguousarray(_np.asarray(array))
        channels = 1 if array.ndim == 2 else array.shape[2]
        return Image(array.shape[1], array.shape[0], channels, array.dtype.name,
                     memoryview(array).cast("B"), bits=bits)

    raise TypeError("an image output must be a numpy array or an Image, not %s" % type(value).__name__)


def _image_input(width, height, channels, dtype, data, layout, bits=None):
    """An input image as the node will see it, saying its bits."""
    if _np is not None:
        array = _np.frombuffer(data, dtype=dtype)
        array = array.reshape((height, width) if channels == 1 else (height, width, channels))
        return with_bits(array, bits)
    return Image(width, height, channels, dtype, data, layout, bits)


# ---- shapes -------------------------------------------------------------------

class Shape:
    """
    Something found on the image. Keyword arguments other than label and
    colour are measurements - score=0.93, class_id=2 - that Filter Shapes,
    Shape Table and the other shape nodes can use.
    """

    kind = None

    def __init__(self, label=None, colour=None, **metrics):
        self.label = None if label is None else str(label)
        self.colour = colour
        self.metrics = {}
        for name, value in metrics.items():
            value = float(value)
            if not (math.isnan(value) or math.isinf(value)):
                self.metrics[name] = value

    def _geometry(self):
        raise NotImplementedError

    def _to_dict(self):
        result = {"kind": self.kind}
        result.update(self._geometry())
        if self.label is not None:
            result["label"] = self.label
        if self.colour is not None:
            result["colour"] = _argb(self.colour)
        if self.metrics:
            result["metrics"] = self.metrics
        return result

    def __repr__(self):
        parts = ["%s=%r" % item for item in self._geometry().items()]
        if self.label is not None:
            parts.append("label=%r" % self.label)
        parts += ["%s=%r" % item for item in self.metrics.items()]
        return "%s(%s)" % (type(self).__name__, ", ".join(parts))


class Point(Shape):
    kind = "point"

    def __init__(self, x, y, **kwargs):
        Shape.__init__(self, **kwargs)
        self.x, self.y = float(x), float(y)

    def _geometry(self):
        return {"x": self.x, "y": self.y}


class Line(Shape):
    kind = "line"

    def __init__(self, x1, y1, x2, y2, **kwargs):
        Shape.__init__(self, **kwargs)
        self.x1, self.y1, self.x2, self.y2 = float(x1), float(y1), float(x2), float(y2)

    def _geometry(self):
        return {"x1": self.x1, "y1": self.y1, "x2": self.x2, "y2": self.y2}


class Rect(Shape):
    """An upright rectangle from its top-left corner - what most detectors report."""
    kind = "rect"

    def __init__(self, x, y, width, height, **kwargs):
        Shape.__init__(self, **kwargs)
        self.x, self.y, self.width, self.height = float(x), float(y), float(width), float(height)

    def _geometry(self):
        return {"x": self.x, "y": self.y, "width": self.width, "height": self.height}


class Box(Shape):
    """A rectangle turned about its centre, angle in degrees clockwise."""
    kind = "box"

    def __init__(self, cx, cy, width, height, angle=0.0, **kwargs):
        Shape.__init__(self, **kwargs)
        self.cx, self.cy = float(cx), float(cy)
        self.width, self.height, self.angle = float(width), float(height), float(angle)

    def _geometry(self):
        return {"cx": self.cx, "cy": self.cy, "width": self.width, "height": self.height, "angle": self.angle}


class Circle(Shape):
    kind = "circle"

    def __init__(self, cx, cy, radius, **kwargs):
        Shape.__init__(self, **kwargs)
        self.cx, self.cy, self.radius = float(cx), float(cy), float(radius)

    def _geometry(self):
        return {"cx": self.cx, "cy": self.cy, "radius": self.radius}


class Ellipse(Shape):
    """Axes end to end, angle in degrees clockwise."""
    kind = "ellipse"

    def __init__(self, cx, cy, major, minor, angle=0.0, **kwargs):
        Shape.__init__(self, **kwargs)
        self.cx, self.cy = float(cx), float(cy)
        self.major, self.minor, self.angle = float(major), float(minor), float(angle)

    def _geometry(self):
        return {"cx": self.cx, "cy": self.cy, "major": self.major, "minor": self.minor, "angle": self.angle}


class Polygon(Shape):
    kind = "polygon"

    def __init__(self, points, closed=True, **kwargs):
        Shape.__init__(self, **kwargs)
        self.points = [(float(x), float(y)) for x, y in points]
        self.closed = bool(closed)

    def _geometry(self):
        return {"points": [list(point) for point in self.points], "closed": self.closed}


class Shapes(list):
    """Shapes arriving on an input, with the size of the image they were found in (0 if unknown)."""

    def __init__(self, shapes=(), width=0, height=0):
        list.__init__(self, shapes)
        self.width = width
        self.height = height


_SHAPE_KINDS = {cls.kind: cls for cls in (Point, Line, Rect, Box, Circle, Ellipse, Polygon)}


def _shape_from_dict(data):
    kind = data.get("kind")
    cls = _SHAPE_KINDS.get(kind)
    if cls is None:
        raise ValueError("unknown shape kind %r" % (kind,))

    geometry = {key: value for key, value in data.items() if key not in ("kind", "label", "colour", "metrics")}
    shape = cls(**geometry)
    shape.label = data.get("label")
    shape.colour = data.get("colour")
    shape.metrics = dict(data.get("metrics") or {})
    return shape


def _as_shape(value):
    if isinstance(value, Shape):
        return value
    if isinstance(value, dict):
        return _shape_from_dict(value)
    raise TypeError("a shapes output must hold Point, Line, Rect, Box, Circle, Ellipse or Polygon, "
                    "not %s" % type(value).__name__)


_NAMED_COLOURS = {
    "red": 0xFFFF0000, "green": 0xFF00C000, "blue": 0xFF0000FF, "yellow": 0xFFFFD700,
    "orange": 0xFFFF8C00, "cyan": 0xFF00FFFF, "magenta": 0xFFFF00FF, "white": 0xFFFFFFFF,
    "black": 0xFF000000,
}


def _argb(colour):
    """A colour as 0xAARRGGBB, from '#RRGGBB', '#AARRGGBB', a name, (r, g, b) or an int."""
    if isinstance(colour, int):
        return colour & 0xFFFFFFFF
    if isinstance(colour, str):
        text = colour.strip().lower()
        if text in _NAMED_COLOURS:
            return _NAMED_COLOURS[text]
        if text.startswith("#") and len(text) in (7, 9):
            value = int(text[1:], 16)
            return value | 0xFF000000 if len(text) == 7 else value
    if isinstance(colour, (tuple, list)) and len(colour) in (3, 4):
        r, g, b = (int(part) & 0xFF for part in colour[:3])
        a = int(colour[3]) & 0xFF if len(colour) == 4 else 0xFF
        return (a << 24) | (r << 16) | (g << 8) | b
    raise ValueError("%r is not a colour - use '#RRGGBB', a name, or (r, g, b)" % (colour,))


# ---- declarations -------------------------------------------------------------

_PORT_KINDS = ("image", "number", "integer", "bool", "text", "shapes")


class _Port:
    def __init__(self, kind, label, required, description, is_input):
        self.kind = kind
        self.label = label
        self.required = required
        self.description = description
        self.is_input = is_input
        self.name = None

    def _describe(self):
        result = {"name": self.name, "label": self.label or self.name, "type": self.kind}
        if self.is_input:
            result["required"] = self.required
        if self.description:
            result["description"] = self.description
        return result


class Input:
    """Inputs - wired from other nodes. An input that is not required may arrive as None."""

    @staticmethod
    def image(label=None, required=True, description=None):
        return _Port("image", label, required, description, True)

    @staticmethod
    def number(label=None, required=True, description=None):
        return _Port("number", label, required, description, True)

    @staticmethod
    def integer(label=None, required=True, description=None):
        return _Port("integer", label, required, description, True)

    @staticmethod
    def bool(label=None, required=True, description=None):
        return _Port("bool", label, required, description, True)

    @staticmethod
    def text(label=None, required=True, description=None):
        return _Port("text", label, required, description, True)

    @staticmethod
    def shapes(label=None, required=True, description=None):
        return _Port("shapes", label, required, description, True)


class Output:
    """Outputs - what run() returns, by name."""

    @staticmethod
    def image(label=None, description=None):
        return _Port("image", label, False, description, False)

    @staticmethod
    def number(label=None, description=None):
        return _Port("number", label, False, description, False)

    @staticmethod
    def integer(label=None, description=None):
        return _Port("integer", label, False, description, False)

    @staticmethod
    def bool(label=None, description=None):
        return _Port("bool", label, False, description, False)

    @staticmethod
    def text(label=None, description=None):
        return _Port("text", label, False, description, False)

    @staticmethod
    def shapes(label=None, description=None):
        return _Port("shapes", label, False, description, False)


class _Setting:
    def __init__(self, kind, label, default, description, group, connectable, reload, **extra):
        self.kind = kind
        self.label = label
        self.default = default
        self.description = description
        self.group = group
        self.connectable = connectable
        self.reload = reload
        self.extra = {key: value for key, value in extra.items() if value is not None}
        self.name = None

    def _coerce(self, value):
        """A value from the application, as the node expects it."""
        if value is None:
            return self.default
        if self.kind == "number":
            return float(value)
        if self.kind == "integer":
            return int(round(float(value)))
        if self.kind == "bool":
            return bool(value)
        return str(value)

    def _describe(self):
        result = {"name": self.name, "label": self.label or self.name, "type": self.kind,
                  "default": self.default}
        for key in ("description", "group"):
            if getattr(self, key):
                result[key] = getattr(self, key)
        if self.connectable:
            result["connectable"] = True
        if self.reload:
            result["reload"] = True
        result.update(self.extra)
        return result


class Setting:
    """
    Settings - shown in the properties panel and saved in the recipe.

    reload=True calls load() again when the setting changes: for a model
    file, say. connectable=True gives the setting an input as well, so
    another node can drive it.
    """

    @staticmethod
    def number(label=None, default=0.0, min=None, max=None, step=None, description=None,
               group=None, connectable=False, reload=False, logarithmic=False):
        return _Setting("number", label, float(default), description, group, connectable, reload,
                        min=min, max=max, step=step, logarithmic=logarithmic or None)

    @staticmethod
    def integer(label=None, default=0, min=None, max=None, step=None, description=None,
                group=None, connectable=False, reload=False):
        return _Setting("integer", label, int(default), description, group, connectable, reload,
                        min=min, max=max, step=step)

    @staticmethod
    def bool(label=None, default=False, description=None, group=None, connectable=False, reload=False):
        return _Setting("bool", label, default is True, description, group, connectable, reload)

    @staticmethod
    def text(label=None, default="", description=None, group=None, connectable=False, reload=False):
        return _Setting("text", label, str(default), description, group, connectable, reload)

    @staticmethod
    def choice(label=None, default=None, choices=(), description=None, group=None, reload=False):
        choices = [str(choice) for choice in choices]
        if default is None and choices:
            default = choices[0]
        return _Setting("choice", label, str(default or ""), description, group, False, reload,
                        choices=choices)

    @staticmethod
    def file(label=None, default="", filter=None, description=None, group=None, reload=False):
        """filter as a file dialog writes it: 'ONNX model (*.onnx)|*.onnx'."""
        return _Setting("file", label, str(default), description, group, False, reload, filter=filter)


class _Spec:
    """What the decorator found on a class."""

    def __init__(self, cls, name, key, group, description, icon):
        self.cls = cls
        self.name = name
        self.key = key
        self.group = group
        self.description = description
        self.icon = icon
        self.inputs = []
        self.outputs = []
        self.settings = []
        self.problems = []

    def _describe(self):
        result = {
            "class": self.cls.__name__,
            "name": self.name,
            "key": self.key,
            "group": self.group,
            "inputs": [port._describe() for port in self.inputs],
            "outputs": [port._describe() for port in self.outputs],
            "settings": [setting._describe() for setting in self.settings],
        }
        if self.description:
            result["description"] = self.description
        if self.icon:
            result["icon"] = self.icon
        if self.problems:
            result["problems"] = self.problems
        return result


def node(name, key, group="Custom", description=None, icon=None):
    """
    Makes a class a node.

    key is what saved recipes find the node by - vendor.name, such as
    "acme.ocr" - so set it once and never change it. group is the toolbox
    section: one of GROUPS.
    """

    def declare(cls):
        spec = _Spec(cls, name, key, group, description, icon)

        if not name or not str(name).strip():
            spec.problems.append("needs a name.")
        if not isinstance(key, str) or not _KEY.match(key):
            spec.problems.append("has the key %r, which should be written vendor.name - letters, digits, "
                                 "'-' and '_', with at least one dot, like \"acme.ocr\"." % (key,))
        if group not in GROUPS:
            spec.problems.append("has the group %r; it should be one of %s." % (group, ", ".join(GROUPS)))
        if not callable(getattr(cls, "run", None)):
            spec.problems.append("needs a run() method.")

        # Base classes first, so a shared base can declare common settings.
        seen = {}
        for owner in reversed(cls.__mro__):
            for attribute, value in vars(owner).items():
                if isinstance(value, (_Port, _Setting)):
                    seen[attribute] = value

        for attribute, value in seen.items():
            if not _NAME.match(attribute):
                spec.problems.append("has a port or setting called %r, which is not a plain name." % attribute)
                continue
            value.name = attribute
            if isinstance(value, _Setting):
                spec.settings.append(value)
            elif value.is_input:
                spec.inputs.append(value)
            else:
                spec.outputs.append(value)

        inputs = {port.name for port in spec.inputs}
        for setting in spec.settings:
            if setting.connectable and setting.name in inputs:
                spec.problems.append("has an input and a connectable setting both called %r." % setting.name)
        if not spec.outputs:
            spec.problems.append("has no outputs, so nothing it does can be used.")

        cls._sirius = spec
        if not hasattr(cls, "warn"):
            cls.warn = _warn
        _registry.append(cls)
        return cls

    return declare


def _warn(self, message):
    """Shows a warning on the node: it still produced a result, but something is worth knowing."""
    warnings = self.__dict__.setdefault("_sirius_warnings", [])
    if message and str(message) not in warnings:
        warnings.append(str(message))


def nodes():
    """The node classes declared so far."""
    return list(_registry)


# ---- running ------------------------------------------------------------------

class NodeRunner:
    """
    One node on a canvas: its object, loaded once, run many times.

    This is what the application does with a node, so a test can do the
    same without the application:

        runner = NodeRunner(DarkSpots)
        out = runner.run({"level": 40}, image=numpy_image)
        assert out["count"] == 1
    """

    def __init__(self, cls):
        spec = getattr(cls, "_sirius", None)
        if spec is None:
            raise TypeError("%s has no @node decorator" % cls.__name__)
        if spec.problems:
            raise TypeError("%s: %s" % (spec.name, " ".join(spec.problems)))
        self.spec = spec
        self.obj = cls()
        self.loaded_with = None
        self.stage = "run"
        self.warnings = []

    def run(self, settings=None, **inputs):
        """Runs once; returns the outputs by name. Warnings are in self.warnings."""
        settings = settings or {}
        spec = self.spec

        for setting in spec.settings:
            setattr(self.obj, setting.name, setting._coerce(settings.get(setting.name)))

        reload_key = tuple(getattr(self.obj, setting.name) for setting in spec.settings if setting.reload)
        if self.loaded_with != reload_key:
            self.loaded_with = None
            self.stage = "load"
            load = getattr(self.obj, "load", None)
            if callable(load):
                load()
            self.loaded_with = reload_key

        self.stage = "run"
        self.obj.__dict__["_sirius_warnings"] = []
        result = self.obj.run(**{port.name: inputs.get(port.name) for port in spec.inputs})
        self.warnings = list(self.obj.__dict__.get("_sirius_warnings") or [])
        return self._outputs(result)

    def _outputs(self, result):
        spec = self.spec
        names = [port.name for port in spec.outputs]

        # A dict by name always works. With one output the value alone does,
        # and with several a tuple in the order they were declared.
        if result is None:
            values = {}
        elif len(names) == 1:
            values = dict(result) if isinstance(result, dict) and set(result) == {names[0]} else {names[0]: result}
        elif isinstance(result, dict):
            values = dict(result)
        elif isinstance(result, tuple) and len(result) == len(names):
            values = dict(zip(names, result))
        else:
            raise TypeError("run() should return a dict of outputs by name (%s)" % ", ".join(names))

        for unknown in [name for name in values if name not in names]:
            self.warnings.append("run() returned %r, which is not one of the outputs." % unknown)
            del values[unknown]

        outputs = {}
        for port in spec.outputs:
            if port.name not in values or values[port.name] is None:
                continue
            try:
                outputs[port.name] = _convert_output(port.kind, values[port.name])
            except (TypeError, ValueError) as error:
                raise type(error)("output %r: %s" % (port.name, error))
        return outputs


def _convert_output(kind, value):
    if kind == "image":
        return _as_image(value)
    if kind == "number":
        number = float(value)
        if math.isnan(number) or math.isinf(number):
            return None
        return number
    if kind == "integer":
        return int(value)
    if kind == "bool":
        return bool(value)
    if kind == "text":
        return str(value)
    if kind == "shapes":
        if isinstance(value, (Shape, dict)):
            value = [value]
        return [_as_shape(shape) for shape in value]
    raise ValueError("unknown output type %r" % kind)
