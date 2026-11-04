"""Restyle an SVG icon into the kit's house style.

Usage, from the NodeGraphKit folder:

    python tools/restyle_icon.py <source.svg> <key>
    python tools/svg2xaml.py src/NodeGraphKit/Themes/Icons.xaml res/icons

The source is any icon drawn in Phosphor's geometry - a 256 canvas, strokes
16 wide, round caps and joins. Phosphor Regular (https://phosphoricons.com,
MIT) is the quickest place to start: download the "regular" SVG of the
glyph you want and pass it here.

The house style: the glyph repainted white, and every shape drawn again
behind it in #333 at 50% opacity with a stroke 32 units wider - a dark
keyline that keeps the icon readable over light backgrounds as well as the
dark theme. The result is written to res/icons/<key>.svg, and the second
command turns the folder into the WPF dictionary the application loads; a
node then names the icon by its key ("ph_my_tool").

An Illustrator export is fine too: CSS classes in a <style> block are
resolved to inline paint, and a leftover <image> tracing layer is dropped.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "res" / "icons"

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

        # An element that says nothing about fill is filled: black is SVG's
        # default. Phosphor's dots - and the colon in zoom-1 - are written
        # that way, as bare circles, and were dropped while a missing fill
        # was read as none.
        painted = fill != "none"

        # The transparent bounds rect carries no paint and needs no keyline;
        # svg2xaml pins the bounds from the viewBox itself.
        if not painted and stroke in (None, "none"):
            continue

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
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)

    source, key = pathlib.Path(sys.argv[1]), sys.argv[2]
    if not re.fullmatch(r"[A-Za-z0-9_]+", key):
        raise SystemExit("the key may hold letters, digits and underscores only")

    markup = inline(source.read_text(encoding="utf-8"))
    target = OUT / f"{key}.svg"
    target.write_text(restyle(markup, key), encoding="utf-8")
    print(f"{source.name} -> {target.relative_to(ROOT)}; now run tools/svg2xaml.py")


if __name__ == "__main__":
    sys.exit(main())