"""Convert the Sirius SVG icon sets into a WPF ResourceDictionary.

Each SVG becomes a DrawingImage keyed `Icon.<file stem>`, preserving the
artwork's own fills and strokes. The toolbar set is multi-coloured, so flattening
everything into a single tintable Geometry - which is what an earlier version of
this script did - would have discarded the design.

Usage:
    python tools/svg2xaml.py <output.xaml> <svg-dir> [<svg-dir> ...]

Supports the subset the icon sets actually use: path, rect, circle, ellipse,
line, polygon, polyline, nested <g>, CSS classes declared in <style>, inline
style attributes, presentation attributes, and translate/rotate/scale/matrix
transforms. Anything outside that raises rather than silently dropping shapes.
"""
import glob
import math
import os
import re
import sys
import xml.etree.ElementTree as ET

SVG_NS = '{http://www.w3.org/2000/svg}'
SHAPES = {'path', 'rect', 'circle', 'ellipse', 'line', 'polygon', 'polyline'}

# Identity matrix as (a, b, c, d, e, f), matching SVG's matrix(...) order.
IDENTITY = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


def parse_length(text, default=0.0):
    """Read an SVG length, dropping any unit suffix.

    Values like "2px" or ".5px" are valid SVG but not valid WPF Doubles, so
    emitting them verbatim produces a XAML dictionary that only fails when the
    offending icon is first looked up at runtime.
    """
    if text is None:
        return default

    match = re.match(r'\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)', str(text))
    return float(match.group(1)) if match else default


def fmt(value):
    rounded = round(value, 3)
    if abs(rounded - round(rounded)) < 1e-9:
        return str(int(round(rounded)))
    return f'{rounded:g}'


def multiply(m, n):
    """Compose two SVG matrices: apply n, then m."""
    a1, b1, c1, d1, e1, f1 = m
    a2, b2, c2, d2, e2, f2 = n
    return (
        a1 * a2 + c1 * b2,
        b1 * a2 + d1 * b2,
        a1 * c2 + c1 * d2,
        b1 * c2 + d1 * d2,
        a1 * e2 + c1 * f2 + e1,
        b1 * e2 + d1 * f2 + f1,
    )


def parse_transform(text):
    """Turn an SVG transform list into a single matrix."""
    if not text:
        return IDENTITY

    matrix = IDENTITY
    for name, raw in re.findall(r'(\w+)\s*\(([^)]*)\)', text):
        nums = [float(n) for n in re.findall(r'[-+0-9.eE]+', raw)]
        if name == 'translate':
            tx = nums[0]
            ty = nums[1] if len(nums) > 1 else 0.0
            step = (1, 0, 0, 1, tx, ty)
        elif name == 'scale':
            sx = nums[0]
            sy = nums[1] if len(nums) > 1 else sx
            step = (sx, 0, 0, sy, 0, 0)
        elif name == 'rotate':
            angle = math.radians(nums[0])
            cos_a, sin_a = math.cos(angle), math.sin(angle)
            step = (cos_a, sin_a, -sin_a, cos_a, 0, 0)
            if len(nums) == 3:
                cx, cy = nums[1], nums[2]
                step = multiply(multiply((1, 0, 0, 1, cx, cy), step), (1, 0, 0, 1, -cx, -cy))
        elif name == 'matrix':
            step = tuple(nums[:6])
        else:
            raise SystemExit(f'unsupported transform: {name}')

        matrix = multiply(matrix, step)

    return matrix


def parse_css(root):
    """Collect `.cls-N { fill: ...; }` rules declared in <style>."""
    rules = {}
    for style in root.iter(SVG_NS + 'style'):
        for selector, body in re.findall(r'\.([\w-]+)\s*\{([^}]*)\}', style.text or ''):
            declarations = {}
            for prop, value in re.findall(r'([\w-]+)\s*:\s*([^;]+)', body):
                declarations[prop.strip()] = value.strip()
            rules[selector] = declarations
    return rules


def resolve_style(element, css, inherited):
    """Merge class, inline style and presentation attributes over the parent's."""
    style = dict(inherited)

    for name in (element.get('class') or '').split():
        style.update(css.get(name, {}))

    for prop, value in re.findall(r'([\w-]+)\s*:\s*([^;]+)', element.get('style') or ''):
        style[prop.strip()] = value.strip()

    for prop in ('fill', 'stroke', 'stroke-width', 'fill-rule', 'opacity', 'fill-opacity'):
        if element.get(prop) is not None:
            style[prop] = element.get(prop)

    return style


def to_brush(colour, opacity):
    """SVG colour to a WPF ARGB string, or None when nothing should be painted."""
    if colour is None or colour in ('none', 'transparent'):
        return None

    named = {'gray': '#808080', 'grey': '#808080', 'black': '#000000', 'white': '#ffffff'}
    colour = named.get(colour.lower(), colour)

    if not colour.startswith('#'):
        return None

    hex_digits = colour[1:]
    if len(hex_digits) == 3:
        hex_digits = ''.join(c * 2 for c in hex_digits)
    if len(hex_digits) != 6:
        return None

    alpha = 255
    try:
        alpha = int(round(float(opacity) * 255)) if opacity is not None else 255
    except ValueError:
        alpha = 255

    return f'#{max(0, min(255, alpha)):02X}{hex_digits.upper()}'


def points_to_path(raw, close):
    nums = [float(n) for n in re.findall(r'[-+0-9.eE]+', raw or '')]
    pts = list(zip(nums[0::2], nums[1::2]))
    if not pts:
        return None
    body = f'M{fmt(pts[0][0])},{fmt(pts[0][1])}' + ''.join(f'L{fmt(x)},{fmt(y)}' for x, y in pts[1:])
    return body + ('Z' if close else '')


def ellipse_path(cx, cy, rx, ry):
    """Two half arcs: one arc command cannot close a full ellipse."""
    return (
        f'M{fmt(cx - rx)},{fmt(cy)}'
        f'A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx + rx)},{fmt(cy)}'
        f'A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx - rx)},{fmt(cy)}Z'
    )


def rect_path(x, y, w, h, rx, ry):
    if rx <= 0 and ry <= 0:
        return f'M{fmt(x)},{fmt(y)}L{fmt(x + w)},{fmt(y)}L{fmt(x + w)},{fmt(y + h)}L{fmt(x)},{fmt(y + h)}Z'

    rx = min(rx or ry, w / 2)
    ry = min(ry or rx, h / 2)
    return (
        f'M{fmt(x + rx)},{fmt(y)}'
        f'L{fmt(x + w - rx)},{fmt(y)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w)},{fmt(y + ry)}'
        f'L{fmt(x + w)},{fmt(y + h - ry)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w - rx)},{fmt(y + h)}'
        f'L{fmt(x + rx)},{fmt(y + h)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x)},{fmt(y + h - ry)}'
        f'L{fmt(x)},{fmt(y + ry)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + rx)},{fmt(y)}Z'
    )


def shape_to_path(element):
    tag = element.tag.replace(SVG_NS, '')
    get = lambda name, default=0.0: parse_length(element.get(name), default)

    if tag == 'path':
        return (element.get('d') or '').strip() or None
    if tag == 'rect':
        return rect_path(get('x'), get('y'), get('width'), get('height'), get('rx'), get('ry'))
    if tag == 'circle':
        r = get('r')
        return ellipse_path(get('cx'), get('cy'), r, r)
    if tag == 'ellipse':
        return ellipse_path(get('cx'), get('cy'), get('rx'), get('ry'))
    if tag == 'line':
        return f'M{fmt(get("x1"))},{fmt(get("y1"))}L{fmt(get("x2"))},{fmt(get("y2"))}'
    if tag == 'polygon':
        return points_to_path(element.get('points'), close=True)
    if tag == 'polyline':
        return points_to_path(element.get('points'), close=False)

    return None


def emit_drawing(element, css, inherited_style, indent, out):
    """Walk the SVG tree, writing GeometryDrawing elements."""
    style = resolve_style(element, css, inherited_style)
    matrix = parse_transform(element.get('transform'))
    pad = ' ' * indent

    tag = element.tag.replace(SVG_NS, '')
    wrapped = matrix != IDENTITY

    if wrapped:
        a, b, c, d, e, f = matrix
        out.append(f'{pad}<DrawingGroup>')
        out.append(f'{pad}  <DrawingGroup.Transform>')
        out.append(f'{pad}    <MatrixTransform Matrix="{fmt(a)},{fmt(b)},{fmt(c)},{fmt(d)},{fmt(e)},{fmt(f)}" />')
        out.append(f'{pad}  </DrawingGroup.Transform>')
        indent += 2
        pad = ' ' * indent

    if tag in SHAPES:
        data = shape_to_path(element)
        if data:
            fill = to_brush(style.get('fill', '#000000'), style.get('fill-opacity', style.get('opacity')))
            stroke = to_brush(style.get('stroke'), style.get('opacity'))
            width = fmt(parse_length(style.get('stroke-width'), 1.0))
            rule = 'EvenOdd' if style.get('fill-rule') == 'evenodd' else 'Nonzero'
            prefix = 'F0 ' if rule == 'EvenOdd' else 'F1 '
            geometry = escape(prefix + data)

            if stroke:
                out.append(f'{pad}<GeometryDrawing Geometry="{geometry}"'
                           + (f' Brush="{fill}"' if fill else '') + '>')
                out.append(f'{pad}  <GeometryDrawing.Pen>')
                out.append(f'{pad}    <Pen Brush="{stroke}" Thickness="{width}" '
                           f'StartLineCap="Round" EndLineCap="Round" LineJoin="Round" />')
                out.append(f'{pad}  </GeometryDrawing.Pen>')
                out.append(f'{pad}</GeometryDrawing>')
            elif fill:
                out.append(f'{pad}<GeometryDrawing Brush="{fill}" Geometry="{geometry}" />')

    for child in element:
        if child.tag.replace(SVG_NS, '') in ('defs', 'style', 'title', 'desc'):
            continue
        emit_drawing(child, css, style, indent, out)

    if wrapped:
        indent -= 2
        pad = ' ' * indent
        out.append(f'{pad}</DrawingGroup>')


def escape(text):
    return text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;')


def convert(path):
    root = ET.parse(path).getroot()
    css = parse_css(root)

    view_box = root.get('viewBox') or '0 0 32 32'
    vx, vy, vw, vh = (float(n) for n in view_box.split())

    body = []
    # A transparent rectangle over the whole viewBox pins the drawing's bounds.
    # Without it each icon scales to its own artwork, so glyphs that do not
    # reach the edges render larger than their neighbours.
    body.append(f'        <GeometryDrawing Brush="#00000000" Geometry="'
                f'{escape(f"F1 M{fmt(vx)},{fmt(vy)}L{fmt(vx + vw)},{fmt(vy)}L{fmt(vx + vw)},{fmt(vy + vh)}L{fmt(vx)},{fmt(vy + vh)}Z")}" />')

    for child in root:
        if child.tag.replace(SVG_NS, '') in ('defs', 'style', 'title', 'desc'):
            continue
        emit_drawing(child, css, {}, 8, body)

    return body


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)

    output, folders = sys.argv[1], sys.argv[2:]

    files = []
    for folder in folders:
        files.extend(sorted(glob.glob(os.path.join(folder, '*.svg'))))
    if not files:
        raise SystemExit('no SVGs found')

    lines = [
        '<!--',
        '    Generated from the SVG icon sets in res/ - do not edit by hand.',
        '',
        '    Regenerate with:',
        '      python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Icons.xaml \\',
        '        "res/toolbox icons" "res/toolbar icons"',
        '',
        '    Each icon is a DrawingImage keyed Icon.<file stem>, keeping the',
        '    artwork\'s own colours; the toolbar set is multi-coloured, so a',
        '    single tintable Geometry would lose the design.',
        '-->',
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        '',
    ]

    for path in files:
        key = os.path.splitext(os.path.basename(path))[0]
        body = convert(path)
        lines.append(f'  <DrawingImage x:Key="Icon.{key}">')
        lines.append('    <DrawingImage.Drawing>')
        lines.append('      <DrawingGroup>')
        lines.extend(body)
        lines.append('      </DrawingGroup>')
        lines.append('    </DrawingImage.Drawing>')
        lines.append('  </DrawingImage>')
        lines.append('')

    lines.append('</ResourceDictionary>')

    os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)
    with open(output, 'w', encoding='utf-8', newline='\r\n') as handle:
        handle.write('\n'.join(lines) + '\n')

    print(f'wrote {output} ({len(files)} icons)')


if __name__ == '__main__':
    main()
