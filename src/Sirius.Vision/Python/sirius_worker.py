"""
Runs one custom node library for Sirius Studio Plus.

Started by the application as

    <python> sirius_worker.py <library.py>

with the library's own interpreter. It imports the library, says which nodes
it declares, then runs them on request until the application closes its
input. Not for running by hand.

Every message, both ways, is one frame:

    uint32 header length, uint32 blob count     (little-endian)
    header                                      (UTF-8 JSON)
    per blob: uint64 length, then the bytes     (pixels)

stdout carries nothing but frames, so anything the library prints - and
anything a C extension writes to stdout - is sent to stderr, which the
application keeps the end of for its error messages.
"""

import importlib.util
import json
import os
import struct
import sys
import traceback

_HEADER = struct.Struct("<II")
_BLOB = struct.Struct("<Q")


def _take_stdio():
    """The protocol's streams; print() and stray output go to stderr instead."""
    if sys.platform == "win32":
        import msvcrt
        msvcrt.setmode(0, os.O_BINARY)
        msvcrt.setmode(1, os.O_BINARY)

    incoming = os.fdopen(os.dup(0), "rb", buffering=1 << 16)
    outgoing = os.fdopen(os.dup(1), "wb", buffering=1 << 16)

    os.dup2(2, 1)
    devnull = os.open(os.devnull, os.O_RDONLY)
    os.dup2(devnull, 0)
    os.close(devnull)

    sys.stdout = sys.stderr
    sys.stdin = open(os.devnull, "r")
    return incoming, outgoing


class Channel:
    def __init__(self, incoming, outgoing):
        self.incoming = incoming
        self.outgoing = outgoing

    def _exactly(self, count):
        buffer = bytearray(count)
        view = memoryview(buffer)
        got = 0
        while got < count:
            read = self.incoming.readinto(view[got:])
            if not read:
                raise EOFError
            got += read
        return buffer

    def receive(self):
        header_length, blob_count = _HEADER.unpack(self._exactly(_HEADER.size))
        header = json.loads(self._exactly(header_length).decode("utf-8"))
        blobs = []
        for _ in range(blob_count):
            (length,) = _BLOB.unpack(self._exactly(_BLOB.size))
            blobs.append(self._exactly(length))
        return header, blobs

    def send(self, header, blobs=()):
        text = json.dumps(header, allow_nan=False).encode("utf-8")
        self.outgoing.write(_HEADER.pack(len(text), len(blobs)))
        self.outgoing.write(text)
        for blob in blobs:
            self.outgoing.write(_BLOB.pack(len(blob)))
            self.outgoing.write(blob)
        self.outgoing.flush()


def _failure(error, where):
    return {
        "ok": False,
        "error": "%s: %s" % (type(error).__name__, error) if str(error) else type(error).__name__,
        "where": where,
        "trace": traceback.format_exc(),
    }


def _import_library(path):
    folder = os.path.dirname(os.path.abspath(path))
    if folder not in sys.path:
        sys.path.insert(0, folder)

    name = os.path.splitext(os.path.basename(path))[0]
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def _versions(sirius_nodes):
    numpy = getattr(sirius_nodes._np, "__version__", None) if sirius_nodes._np is not None else None
    return {
        "python": "%d.%d.%d" % sys.version_info[:3],
        "executable": sys.executable,
        "numpy": numpy,
        "sdk": sirius_nodes.__version__,
    }


def _inputs(sirius_nodes, spec, values, blobs):
    """Inputs as the node sees them: images as arrays, shapes as Shape objects."""
    result = {}
    for port in spec.inputs:
        value = values.get(port.name)
        if value is None:
            continue
        if port.kind == "image":
            value = sirius_nodes._image_input(
                value["width"], value["height"], value["channels"], value["dtype"],
                blobs[value["blob"]], value.get("layout"))
        elif port.kind == "shapes":
            value = sirius_nodes.Shapes(
                [sirius_nodes._shape_from_dict(shape) for shape in value.get("shapes", [])],
                value.get("width", 0), value.get("height", 0))
        result[port.name] = value
    return result


def _outputs(sirius_nodes, spec, values):
    """Outputs for the wire: images as blobs, shapes as dicts."""
    header = {}
    blobs = []
    kinds = {port.name: port.kind for port in spec.outputs}
    for name, value in values.items():
        if kinds[name] == "image":
            header[name] = {
                "width": value.width, "height": value.height, "channels": value.channels,
                "dtype": value.dtype, "layout": value.layout, "blob": len(blobs),
            }
            blobs.append(value.data)
        elif kinds[name] == "shapes":
            header[name] = [shape._to_dict() for shape in value]
        else:
            header[name] = value
    return header, blobs


def main():
    channel = Channel(*_take_stdio())

    if len(sys.argv) != 2:
        channel.send({"ok": False, "error": "usage: sirius_worker.py <library.py>", "where": "start"})
        return 2

    try:
        import sirius_nodes
        _import_library(sys.argv[1])
    except BaseException as error:  # SystemExit from a library is a failure to load, too
        hello = _failure(error, "import")
        try:
            hello.update(_versions(sirius_nodes))
        except Exception:
            hello["python"] = "%d.%d.%d" % sys.version_info[:3]
            hello["executable"] = sys.executable
        channel.send(hello)
        return 1

    classes = {}
    described = []
    for cls in sirius_nodes.nodes():
        spec = cls._sirius
        if spec.key in classes:
            spec.problems.append("uses the key %r, which an earlier node in this library already uses." % spec.key)
        described.append(spec._describe())
        if not spec.problems:
            classes[spec.key] = cls

    hello = {"ok": True, "nodes": described}
    hello.update(_versions(sirius_nodes))
    channel.send(hello)

    runners = {}

    while True:
        try:
            request, blobs = channel.receive()
        except EOFError:
            return 0

        op = request.get("op")

        if op == "quit":
            channel.send({"ok": True})
            return 0

        if op == "forget":
            runners.pop(request.get("instance"), None)
            channel.send({"ok": True})
            continue

        if op != "run":
            channel.send({"ok": False, "error": "unknown request %r" % (op,), "where": "protocol"})
            continue

        key = request.get("node")
        instance = request.get("instance")
        where = "run"
        try:
            runner = runners.get(instance)
            if runner is None or runner.spec.key != key:
                where = "create"
                cls = classes.get(key)
                if cls is None:
                    raise LookupError("this library has no node %r" % key)
                runner = sirius_nodes.NodeRunner(cls)
                runners[instance] = runner

            spec = runner.spec
            inputs = _inputs(sirius_nodes, spec, request.get("inputs") or {}, blobs)
            outputs = runner.run(request.get("settings") or {}, **inputs)
            header, out_blobs = _outputs(sirius_nodes, spec, outputs)
            channel.send({"ok": True, "outputs": header, "warnings": runner.warnings}, out_blobs)
        except Exception as error:
            if where != "create":
                where = runner.stage
            # A load() that failed is tried again next run, not left half done.
            if where == "create" or (instance in runners and runners[instance].loaded_with is None):
                runners.pop(instance, None)
            channel.send(_failure(error, where))
        except BaseException as error:  # KeyboardInterrupt, SystemExit: say why, then stop
            channel.send(_failure(error, where))
            return 1


if __name__ == "__main__":
    sys.exit(main())
