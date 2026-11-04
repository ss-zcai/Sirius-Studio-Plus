# Style guide

What makes a screen look like part of the Sirius Vision family, for anything
you build next to the kit. The theme already applies most of this to every
standard control. This page is for the parts you draw yourself.

The one rule: **use the theme's keys, not new hex values.** If something
needs a colour that is not here, add a key to `Themes/Dark.xaml`, so the
application stays one palette.

## Palette

| Key | Colour | For |
| --- | --- | --- |
| `Surface.Background` | `#1E1F22` | Window background |
| `Surface.Panel` | `#2B2D30` | Menu, toolbar, status bar, panels |
| `Surface.Canvas` | `#17181A` | The node canvas: darkest, so nodes stand off it |
| `Surface.Node` | `#32343A` | Node body; button face |
| `Surface.NodeHeader` | `#3C3F45` | Hover on buttons, menu items and groups |
| `Surface.Border` | `#4A4D55` | Borders, separators, the node outline at rest |
| `Accent` | `#60A5FA` | Wires; focus and hover borders; checked states; slider fill |
| `Selection` | `#FBBF24` | Selected nodes and wires: amber, so it reads against blue wires |
| `Text.Primary` | `#E6E6E6` | Text |
| `Text.Secondary` | `#9AA0A6` | Labels, port names, hints, placeholder text |

Two unkeyed values recur in the templates: `#1A1B1E` for input fields, popups
and drop-downs, and `#10131A` for text on an `Accent` fill (a highlighted
menu item, a selected list row).

## Type

Segoe UI 12 throughout, set on `TextBlock` by the theme. Section headers use
the `SectionHeader` style: 11, SemiBold, `Text.Secondary`, upper case
("PROPERTIES"). Port names and parameter labels are 11 in `Text.Secondary`.
Node captions are SemiBold.

## The canvas

### Nodes

- A rounded body in the node's accent colour, with a 1px border, a 15px
  icon, the caption and a status dot in the header.
- When selected, a 2px `Selection` outline, drawn behind the sockets so it
  does not cut across them.
- A bypassed node (`Enabled` off) is drawn at 40% opacity. That is the only
  sign of it on the canvas, so it has to be obvious.

### Sockets

An 11px shape centred on the node's edge, inside an 18px hit area. **The
shape and colour carry the type**, which leaves the label free to say what
the port is for.

- **Square**: the main data type of the application (images, in the
  vision app). **Round**: everything else.
- **Hollow**: nothing is wired in, so an unconnected required input is
  visible without reading anything.

### Socket colours

The colours the vision application uses. Pick from these first; they are
distinct from each other and from the accents at canvas zoom.

| Colour | Used for |
| --- | --- |
| `#60A5FA` blue | Image (square) |
| `#FBBF24` amber | Numbers |
| `#34D399` green | Text |
| `#A78BFA` violet | Shapes, histograms |
| `#F472B6` pink | Profiles; booleans in the demo |
| `#9CA3AF` grey | `Any` |

### Wires

`Accent` blue, 2px. On hover: pale blue (`#BFDBFE`), 2.5px, with a soft
glow. Selected: `Selection` amber with an amber glow. The glow is also the
click target, so a wire is easy to hit.

### Status dot

| Status | Colour | Meaning |
| --- | --- | --- |
| Idle | `#6B7280` grey | Not run, or bypassed |
| Ready | `#22C55E` green | Ran without trouble |
| Warning | `#F59E0B` amber | Ran, with something to check |
| Error | `#EF4444` red | Failed |

The message goes on the dot's tooltip and in the properties panel's Status
box, never on the node, so the canvas stays readable.

### Node colours (accents)

For marking up a big graph. **The background stays dark and the border
carries the colour.** A saturated fill fights the white caption, and two
loud nodes are worse than none. Each text-on-fill pair clears 7:1 contrast,
and each border clears 3:1 against its fill.

| Accent | Fill | Border | Meaning |
| --- | --- | --- | --- |
| Default | `#32343A` | `#4A4D55` | The ordinary node |
| Muted | `#26282C` | `#3A3D44` | Settled, or kept only for comparison |
| Blue | `#2A3546` | `#5B8DD6` | Grouping |
| Teal | `#1E3A40` | `#2BB3C9` | Grouping |
| Green | `#2A3A24` | `#6DBF4A` | Known good |
| Amber | `#3E3420` | `#D9A33A` | Needs attention |
| Red | `#42292A` | `#DB5C5C` | The problem, or the one not to touch |
| Purple | `#352D45` | `#A378D6` | Grouping |

## Controls

The theme restyles these controls everywhere: Button, ToggleButton, TextBox,
ComboBox, CheckBox, RadioButton, Slider, ScrollBar, ProgressBar, Expander,
GroupBox, TabControl, DataGrid, Menu, ContextMenu, ToolTip, ToolBar and
Separator. Use them as they are.

Keyed styles to reach for:

| Key | For |
| --- | --- |
| `ToolbarButton` | Icon-only toolbar button, 34×28, with an 18px icon inside |
| `ToolbarToggleButton` | The same, staying lit while checked: for a mode |
| `SectionHeader` | A panel heading |
| `AccentItemTemplate` | A node colour as a swatch and a name, in a menu or drop-down |

Other habits:

- Text boxes commit on Enter or lost focus, and Esc puts the old value back.
  The theme does this for every `TextBox`. Bind with
  `UpdateSourceTrigger=LostFocus` so a half-typed number never reaches the
  graph.
- Call `DarkTitleBar.Apply(window)` in every window's constructor. Otherwise
  Windows draws a light caption over a dark window.
- Put the toolbar in a plain bordered `StackPanel` (see the demo), not a
  `ToolBar` with its gripper. Group buttons with `Separator`s.
- Popups (tooltips, menus, drop-downs) are separate visual trees and inherit
  nothing from the window, which is why the theme styles each one. A popup
  of your own needs its colours set the same way.

## Icons

Restyled [Phosphor Regular](https://phosphoricons.com) (MIT): a white glyph
over a dark keyline, on a 256 grid with 16-unit round-capped strokes. The
keyline is `#333` at 50% opacity, 32 units wider than the glyph. It is
nearly invisible on the dark theme, but it keeps an icon readable over a
light image or a printed report.

**Adding one:**

1. Find the glyph on phosphoricons.com and download its *Regular* SVG.
2. Restyle it, giving it a key:
   ```bash
   python tools/restyle_icon.py ~/Downloads/funnel.svg ph_filter
   ```
   This writes `res/icons/ph_filter.svg`.
3. Regenerate the dictionary:
   ```bash
   python tools/svg2xaml.py src/NodeGraphKit/Themes/Icons.xaml res/icons
   ```
4. Use it: `Icon = "ph_filter"` on a node definition, or
   `{StaticResource Icon.ph_filter}` in XAML.
5. Run `dotnet test`. It loads every icon, so a bad one fails there rather
   than showing up as a blank square.

**Drawing one yourself:** copy the nearest `res/icons/ph_*.svg` and edit the
geometry. The two layers and the paint are already right. Keep to the
256-unit canvas and 16-unit strokes, with round caps and joins, and most
geometry on multiples of 8. Check it at 16px on the dark panel, ignoring the
keyline: if it doesn't read there, it doesn't read.

Keep icons monochrome. One coloured icon in a white toolbar reads as a
mistake. Let the button carry any state colour.

The icons in the kit:

- **Files:** `ph_new`, `ph_open`, `ph_save`, `ph_save_as`
- **Editing:** `ph_undo`, `ph_redo`, `ph_cut`, `ph_copy`, `ph_paste`,
  `ph_delete`, `ph_select_all`, `ph_style`
- **Running:** `ph_run`
- **Search:** `ph_search`, `ph_clear`
- **View:** `ph_zoom_in`, `ph_zoom_out`, `ph_zoom_fit`
- **The sample nodes:** `ph_image_file`, `ph_number`, `ph_counter`,
  `ph_compare`, `ph_image_viewer`
- **Fallback:** `ph_custom_node`

The Sirius Studio Plus repository has about 80 more in `res/ui icons`, in
the same style. Copy any of them into `res/icons` and regenerate.
