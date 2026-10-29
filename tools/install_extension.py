"""Install the hand-drawn extension icons into the live set.

Usage, from the repository root:

    python tools/install_extension.py
    python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Icons.xaml \
        "res/ui icons"

Source is ``res/phosphor-icons/Extension/``, which holds icons drawn in
Phosphor's geometry - 256 canvas, stroke 16, round caps - but exported from
Illustrator. An Illustrator export differs from the Phosphor source in two
ways this script has to deal with before the house restyle can be applied:

  * paint lives in a ``<style>`` block as CSS classes rather than inline
    attributes, so looking for a ``stroke`` attribute finds nothing and the
    shape would be skipped as if it were the transparent bounds rect - an
    empty icon rather than an error;
  * an export may carry a leftover ``<image>`` reference layer, a base64
    sketch the artwork was traced over, which the XAML converter rejects.

The restyle itself matches ``docs/icon-inventory.md``: repaint to ``#fff``
and redraw every shape behind it with a stroke 32 units wider at 50% opacity,
which puts the dark keyline edge 16 units - 1/16 of the canvas - outside the
white silhouette.

Adding an icon means dropping the SVG in the Extension folder, adding a line
to INSTALL below, and running the two commands above.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "res" / "phosphor-icons" / "Extension"
REGULAR = ROOT / "res" / "phosphor-icons" / "SVGs" / "regular"
OUT = ROOT / "res" / "ui icons"

# Phosphor Regular source -> the resource key it supplies. Restyled by the
# same path as the hand-drawn set, since the house style is the same; these
# simply did not need drawing because Phosphor already had them.
PHOSPHOR = {
    "intersect": "ph_logic",
    "stack-simple": "ph_blend",
    "mask-happy": "ph_mask",

    # The edit actions. Undo and redo are the U-turn arrows rather than the
    # circular ones, which in this set read as refresh - and a Run button is
    # already on the same toolbar, so "re-run" is exactly the wrong guess to
    # invite. Style is the palette rather than the paint bucket: it picks a
    # colour from a set, it does not flood-fill anything.
    "arrow-u-up-left": "ph_undo",
    "arrow-u-up-right": "ph_redo",
    "scissors": "ph_cut",
    "copy": "ph_copy",
    "clipboard-text": "ph_paste",
    "palette": "ph_style",
    "selection-all": "ph_select_all",

    # The value nodes. A matrix is a grid, and text is a letter - both
    # read at toolbox size in a way the measurement glyphs standing in
    # for them did not.
    "squares-four": "ph_matrix",
    "text-t": "ph_text",

    # A custom node that names no icon of its own. A puzzle piece is the
    # usual sign for something added from outside, and is unlike every
    # built-in tool, so a plugin's node is recognisable as one at a glance.
    "puzzle-piece": "ph_custom_node",

    # Sub-graphs: a network of nodes for the node that holds one, and a
    # way in and a way out for the boundaries inside it.
    "graph": "ph_sub_graph",
    "sign-in": "ph_graph_input",
    "sign-out": "ph_graph_output",

    # The toolbox search box. Its clear button reuses ph_clear, the
    # viewer's own cross.
    "magnifying-glass": "ph_search",

    # Edge points without a fit: a ring of them, a row of them, and points
    # joined into an outline.
    "target": "ph_ring_calipers",
    "ruler": "ph_line_calipers",
    "polygon": "ph_points_polygon",

    # A chart for the node that shows one: profiles, in its own panel.
    "chart-line": "ph_line_chart",

    # Judging: weighing two numbers, a list of checks combined, a graded
    # paper, a ranking for the worst of several, a tally, a CSV written,
    # and a funnel for keeping the shapes in range.
    "scales": "ph_compare",
    "list-checks": "ph_check_logic",
    "exam": "ph_grade",
    "ranking": "ph_worst_grade",
    "hash": "ph_counter",
    "file-csv": "ph_result_log",
    "funnel": "ph_filter_shapes",

    # tag-simple rather than tag: the plain outline keeps its shape under
    # the keyline at toolbox size, where tag's eyelet fills in.
    "tag-simple": "ph_label",

    # A limit check passes or fails a reading, so a tick in a ring - the
    # gauge glyph reads as a dial, which is the measuring, not the
    # judging.
    "check-circle": "ph_limit_check",

    # Reading what is printed on a part. The barcode glyph stands for all
    # the symbologies rather than only the 1D ones, because a node called
    # Read Code reads whichever is in front of it; text-aa is letters,
    # which is what the other one reads.
    "barcode": "ph_read_code",
    "text-aa": "ph_read_text",

    # Calibration is the crosshair: it fixes what a pixel is worth, as a
    # reticle fixes a scale. The ruler went to Line Calipers, which reads
    # along a straight edge.
    "crosshair": "ph_calibrate",
}

# source file -> the resource key it supplies
INSTALL = {
    "point-to-line.svg": "ph_measure_p2l",
    "parallel-line.svg": "ph_measure_parallel",
    "circle-centre-radius.svg": "ph_measure_circle",
    "circle-3-points.svg": "ph_measure_circle3",
    "highpass.svg": "ph_highpass",

    # Drawn to replace Phosphor stand-ins that read as something else:
    # circle-dashed was a selection marquee, orange-slice was fruit, and
    # chart-line-up was a trend rather than a fitted line.
    "fit-circle.svg": "ph_fit_circle",
    "fit-ellipse.svg": "ph_fit_ellipse",
    "fit-line.svg": "ph_fit_line",

    # 1:1 as a "1" in a frame: the magnifier with a square in it read as
    # cropping.
    "zoom-1.svg": "ph_zoom_actual",
    "numbers.svg": "ph_number",
}

KEYLINE_EXTRA = 32.0
SHAPES = ("path", "rect", "circle", "ellipse", "line", "polygon", "polyline")
PAINT = ("fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
         "opacity")

SHAPE_PATTERN = re.compile(r"<(" + "|".join(SHAPES) + r")\b([^>]*?)/?>")


def stylesheet(markup):
    """class name -> {property: value}, read from the <style> blocks."""
    rules = {}

    for block in re.findall(r"<style[^>]*>(.*?)</style>", markup, re.S):
        for selectors, body in re.findall(r"([^{}]+)\{([^{}]*)\}", block):
            properties = {}

            for declaration in body.split(";"):
                name, _, value = declaration.partition(":")
                name, value = name.strip(), value.strip()

                # "16px" is valid CSS but not a valid WPF Double, and the
                # keyline width is derived from this as a number.
                if name == "stroke-width":
                    value = value.removesuffix("px")

                if name in PAINT:
                    properties[name] = value

            for selector in selectors.split(","):
                selector = selector.strip()
                if selector.startswith("."):
                    # A later rule for the same class wins, as in CSS.
                    rules.setdefault(selector[1:], {}).update(properties)

    return rules


def inline(markup):
    """Resolve CSS classes to inline paint and drop unsupported elements."""
    rules = stylesheet(markup)
    markup = re.sub(r"<image\b.*?/>", "", markup, flags=re.S)

    def expand(match):
        tag, attributes = match.group(1), match.group(2)
        classes = re.search(r'\bclass="([^"]*)"', attributes)

        if not classes:
            return match.group(0)

        properties = {}
        for name in classes.group(1).split():
            properties.update(rules.get(name, {}))

        attributes = re.sub(r'\sclass="[^"]*"', "", attributes)
        painted = "".join(f' {k}="{v}"' for k, v in properties.items())

        return f"<{tag}{attributes.rstrip().rstrip('/')}{painted}/>"

    return SHAPE_PATTERN.sub(expand, markup)


def attr(text, name):
    found = re.search(rf'\b{name}="([^"]*)"', text)
    return found.group(1) if found else None


def repaint(text, fill, stroke, width):
    """Rewrite one element's paint, leaving its geometry alone."""
    # The cap and join are stripped as well: re-adding them without removing
    # them first duplicates the attribute and the XML no longer parses.
    text = re.sub(rf'\s({"|".join(PAINT)})="[^"]*"', "", text)
    paint = f' fill="{fill}"'

    if stroke:
        paint += (f' stroke="{stroke}" stroke-width="{width:g}"'
                  ' stroke-linecap="round" stroke-linejoin="round"')

    return text.rstrip().rstrip("/") + paint


def restyle(markup, key):
    """The house style: white glyph over a wider dark keyline."""
    body = re.search(r"<svg[^>]*>(.*)</svg>", markup, re.S).group(1)
    glyph, keyline = [], []

    for tag, raw in SHAPE_PATTERN.findall(body):
        fill = attr(raw, "fill")
        stroke = attr(raw, "stroke")
        width = float(attr(raw, "stroke-width") or 0)

        # The transparent bounds rect carries no paint and needs no keyline;
        # svg2xaml pins the bounds from the viewBox itself.
        if fill in (None, "none") and stroke in (None, "none"):
            continue

        painted = fill not in (None, "none")

        glyph.append(f"<{tag}" + repaint(
            raw, "#fff" if painted else "none",
            "#fff" if stroke else None, width) + "/>")

        keyline.append(f"<{tag}" + repaint(
            raw, "#333" if painted else "none",
            "#333", width + KEYLINE_EXTRA) + "/>")

    if not glyph:
        raise SystemExit(f"{key}: nothing painted - check the <style> block")

    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
        f'data-name="{key}">\n'
        f'  <g opacity=".5">{"".join(keyline)}</g>\n'
        f'  {"".join(glyph)}\n'
        '</svg>\n'
    )


def main():
    for name, key in PHOSPHOR.items():
        source = REGULAR / f"{name}.svg"

        if not source.exists():
            raise SystemExit(f"missing {source}")

        markup = inline(source.read_text(encoding="utf-8"))
        (OUT / f"{key}.svg").write_text(restyle(markup, key), encoding="utf-8")
        print(f"{name + '.svg':28} -> {key}.svg")

    for name, key in INSTALL.items():
        source = SRC / name

        if not source.exists():
            raise SystemExit(f"missing {source}")

        markup = inline(source.read_text(encoding="utf-8"))
        (OUT / f"{key}.svg").write_text(restyle(markup, key), encoding="utf-8")
        print(f"{name:28} -> {key}.svg")

    print(f"installed {len(PHOSPHOR) + len(INSTALL)} icons; now run tools/svg2xaml.py")


if __name__ == "__main__":
    sys.exit(main())
