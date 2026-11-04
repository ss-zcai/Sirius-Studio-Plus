# Node Graph UI Kit

The node graph editor from **Sirius Studio Plus**, taken out on its own: the
canvas, the toolbox, the properties panel, and the dark theme they are drawn
in, with no vision algorithms and no evaluation. A starting point for any
team building a node-based tool that should look and behave like the
Sirius Vision family.

What you get:

- **A canvas.** Typed sockets: the colour and shape say what flows. Wiring
  rules stop a type mismatch or a loop before the mouse is released. You
  can rename a node on its caption, colour nodes to mark up a big graph, and
  each node has a status dot. Right-click menus, and drag from the toolbox
  to drop a node where you want it.
- **Editing.** Undo and redo name the step ("Undo Delete Add"). Cut, copy,
  paste and duplicate keep the wires inside the selection. Delete, and
  select all.
- **A toolbox** with search: Enter adds the first match, Esc clears.
- **A properties panel** with editors for numbers (a slider when there is a
  range), integers, text, check boxes and drop-downs.
- **Graphs saved as plain JSON**, readable and diffable.
- **The whole dark theme.** Every standard WPF control is restyled (menus,
  combo boxes, scroll bars, sliders, check boxes and more). The icon set
  comes with the tools to add your own icons in the same style.

What it does not do is run anything. A node is a description. What it
*does* is yours to write, and the [integration guide](docs/integration-guide.md)
shows where it goes.

## Running the demo

You need Windows and the **.NET 8 SDK**.

```bash
dotnet run --project src/NodeGraphKit.Demo
```

The demo opens with a small graph built from five sample tools: Image File,
Number, Add, Compare and Viewer. Between them they show every socket shape
and every kind of parameter editor. Press F5 or the Run button for the
stand-in "run": it sets each node's status from its wiring alone.

```bash
dotnet test
```

runs the tests: the wiring rules, the file format, undo, and a check that
the theme, every icon and the three views load.

## What is in the folder

| Path | What it holds |
| --- | --- |
| `src/NodeGraphKit` | The library: graph model, undo, JSON files, view models, theme, icons, and the three views. Depends on Nodify and CommunityToolkit.Mvvm only. |
| `src/NodeGraphKit.Demo` | A shell laid out like Sirius Studio Plus (menu, toolbar, docking panels, status bar) with the five sample tools. Copy it to start an application. |
| `tests/NodeGraphKit.Tests` | xUnit tests. |
| `res/icons` | The SVG masters of the icons the kit uses. Add yours here. |
| `tools/restyle_icon.py` | Turns a Phosphor icon (or any icon drawn on its grid) into the house style. |
| `tools/svg2xaml.py` | Turns `res/icons` into `Themes/Icons.xaml`. |
| `docs/integration-guide.md` | How to build your application on the kit. |
| `docs/style-guide.md` | The palette, sockets, colours and icon rules, for screens of your own. |
| `pack.ps1` | Zips the kit (and optionally a built demo) for sending on. |

## Using it in five steps

1. Reference `src/NodeGraphKit/NodeGraphKit.csproj`.
2. Merge the theme in `App.xaml`:
   ```xml
   <ResourceDictionary Source="pack://application:,,,/NodeGraphKit;component/Themes/NodeGraphKit.xaml" />
   ```
3. Register your port types and describe your nodes as `NodeDefinition`s in a
   `NodeCatalog`.
4. Create a `GraphEditorViewModel(catalog)` and a `PaletteViewModel(catalog)`.
   Give them to `GraphEditorView`, `PropertiesView` and `ToolboxView`.
5. Handle `GraphChanged` to run the graph your way, and set each node's
   `Status`.

The [integration guide](docs/integration-guide.md) walks through each step
with code.

## Mouse and keyboard

| To | Do |
| --- | --- |
| Add a node | Drag it from the toolbox; double-click it there; type in the search and press Enter; or right-click the canvas and choose **Add Node** |
| Wire | Drag from an output socket to an input (or the other way) |
| Remove a wire | Click it and press Del; right-click it; Alt+click it; or Alt+click a socket to clear all of its wires |
| Select | Click, or drag a box on empty canvas. Shift adds to the selection, Ctrl toggles. |
| Pan / zoom | Right-drag / mouse wheel |
| Rename | Double-click the caption, or F2 |
| Colour nodes | Toolbar colour box, **Edit > Node Style**, or right-click > **Style** |
| Undo / redo | Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z |
| Cut / copy / paste / duplicate | Ctrl+X / Ctrl+C / Ctrl+V / Ctrl+D |
| Search the toolbox | Ctrl+F |

## Licences

The kit's own code belongs to Sigma Squares (Beijing) Tech Co Ltd. The
library uses Nodify and CommunityToolkit.Mvvm, both MIT. The demo also uses
AvalonDock, which is **Ms-PL**; its licence is in
`LICENSE-avalondock-mspl.txt`. The icons are restyled from Phosphor (MIT),
whose licence must travel with any copy: `res/icons/LICENSE-phosphor.txt`.
See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
None of Sirius Studio Plus's vision dependencies (Emgu CV, camera SDKs) is
included or needed.
