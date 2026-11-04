# Integration guide

How to build an application on the kit. `src/NodeGraphKit.Demo` does every
step below. When something here is unclear, read the demo's code.

## 1. The pieces

```
NodeCatalog ──► GraphEditorViewModel ──► GraphEditorView   (the canvas)
     │               │   ├ NodeGraph        PropertiesView   (selected node)
     │               │   ├ UndoHistory
     │               │   └ GraphChanged ──► your code runs the graph
     └─────────► PaletteViewModel ──────► ToolboxView      (AddRequested ──► GraphEditorView.AddAtCentre)
```

| Type | Namespace | Role |
| --- | --- | --- |
| `PortType`, `PortTypes` | `NodeGraphKit.Model` | A kind of data: socket colour, socket shape, and what may be wired to what. |
| `NodeDefinition`, `PortDefinition`, `ParameterDefinition` | `NodeGraphKit.Model` | A kind of node: what the toolbox lists. |
| `NodeCatalog` | `NodeGraphKit.Model` | Every node type you offer, in toolbox order. |
| `NodeGraph`, `GraphNode`, `GraphPort`, `GraphConnection` | `NodeGraphKit.Model` | The graph being edited. No WPF in these. |
| `GraphSerializer` | `NodeGraphKit.Serialization` | JSON, for files, the clipboard and undo. |
| `GraphEditorViewModel` | `NodeGraphKit.ViewModels` | One open graph: its view models, selection, clipboard and undo. |
| `PaletteViewModel` | `NodeGraphKit.ViewModels` | The toolbox's contents and search. |
| `GraphEditorView`, `ToolboxView`, `PropertiesView` | `NodeGraphKit.Controls` | The three views. |

## 2. Port types

Register each type once, at start-up, with a colour and a shape:

```csharp
public static class MyTypes
{
    public static readonly PortType Image  = PortTypes.Register("Image", "#60A5FA", PortShape.Square);
    public static readonly PortType Number = PortTypes.Register("Number", "#FBBF24");
    public static readonly PortType Text   = PortTypes.Register("Text", "#34D399");
}
```

- **Shape.** Square is for the one or two "heavy" types a graph is built
  around (images, in the vision application). Everything else is round.
- **Colour.** Take colours from the socket palette in the
  [style guide](style-guide.md#socket-colours), so they read well on the
  canvas and stay distinct from each other.
- **`PortTypes.Any`** (grey) accepts, or feeds, any type.
- **Compatibility.** By default only a type may go into itself, plus `Any`
  either way. For conversions, replace the rule:

  ```csharp
  PortTypes.Compatibility = (output, input) =>
      PortTypes.DefaultCompatibility(output, input) ||
      (output == MyTypes.Integer && input == MyTypes.Number);
  ```

## 3. Node definitions

```csharp
public static readonly NodeDefinition Threshold = new("vision.threshold", "Threshold")
{
    Category = "Basic",                 // toolbox group, in order of first appearance
    Icon = "ph_threshold",              // resource key Icon.ph_threshold
    Description = "Splits an image into black and white at a level.",
    Inputs =  [new("In", MyTypes.Image)],
    Outputs = [new("Out", MyTypes.Image), new("Level", MyTypes.Number, "The level used")],
    Parameters =
    [
        ParameterDefinition.Integer("Level", 128, minimum: 0, maximum: 255),
        ParameterDefinition.Choice("Mode", ["Binary", "Inverse", "Otsu"]),
        ParameterDefinition.Boolean("Preview"),
    ],
    Tag = new ThresholdEvaluator(),     // optional: anything your runner wants
};

var catalog = new NodeCatalog([Threshold, /* ... */]);
```

- **`TypeId`** is what saved files store. Never change it once graphs have
  been saved. Rename `DisplayName` instead.
- **Port names** are labels on the node and keys in saved files. Keep them
  short and about the *role* ("Source", "Mask", "Result"). The socket's
  colour already says the type.
- **Parameters** pick their editor by kind:

  | Helper | Value type | Editor |
  | --- | --- | --- |
  | `Number(name, default, min, max, step)` | `double` | Slider and box with both `min` and `max`; box alone otherwise |
  | `Integer(name, default, min, max)` | `int` | The same, in whole steps |
  | `Text(name, default)` | `string` | Text box |
  | `Boolean(name, default)` | `bool` | Check box |
  | `Choice(name, options, default)` | `string` | Drop-down |

  Values are coerced to the kind and clamped to the range on the way in,
  so code reading them can trust them.
- **Icons.** See [the style guide](style-guide.md#icons). A key with no icon
  shows the puzzle piece (`ph_custom_node`) rather than a gap.

## 4. The window

`App.xaml`:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="pack://application:,,,/NodeGraphKit;component/Themes/NodeGraphKit.xaml" />
            <!-- your own icons after it, generated the same way -->
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

The views use `StaticResource` keys from the theme. A window that loads
without the theme merged fails with "Cannot find resource named
'Surface.Canvas'".

The window:

```xml
<Window xmlns:kit="clr-namespace:NodeGraphKit.Controls;assembly=NodeGraphKit"
        Background="{StaticResource Surface.Background}">
    <Grid TextElement.Foreground="{StaticResource Text.Primary}">
        <kit:ToolboxView x:Name="Toolbox" />
        <kit:GraphEditorView x:Name="Graph" />
        <kit:PropertiesView x:Name="Properties" />
    </Grid>
</Window>
```

and its code:

```csharp
var editor = new GraphEditorViewModel(catalog);
var palette = new PaletteViewModel(catalog);

Graph.DataContext = editor;
Properties.DataContext = editor;
Toolbox.DataContext = palette;

palette.AddRequested += (_, definition) => Graph.AddAtCentre(definition);
Activated += (_, _) => editor.RefreshClipboardState();
DarkTitleBar.Apply(this);
```

Set `TextElement.Foreground` on the window's root, as above, rather than
through a TextBlock style. A style would also repaint the text in popups,
which have their own colours.

The demo puts the three views in AvalonDock panels with the VS2013 dark
theme, as Sirius Studio Plus does. Any layout works: a `Grid` with
`GridSplitter`s is fine.

### Edit commands

`GraphEditorViewModel` has `UndoCommand`, `RedoCommand`, `CutCommand`,
`CopyCommand`, `PasteCommand`, `DuplicateCommand`, `SelectAllCommand`,
`DeleteSelectionCommand` and `SetAccentCommand` (takes a `NodeAccent`). Bind
your menu and toolbar to these. `UndoText` and `RedoText` name the step, for
the menu header and the tooltip.

The canvas handles the keyboard shortcuts itself while it has focus. These
are WPF's routed `ApplicationCommands`, so a focused text box still handles
its own Ctrl+C. To make them work anywhere in the window, add the same
`CommandBinding`s to the window, as `MainWindow.xaml` does in the demo.

## 5. Running the graph

The kit never runs anything. The pattern the demo uses:

```csharp
editor.GraphChanged += (_, _) => Run(editor.Graph);   // after every edit, undo and load

void Run(NodeGraph graph)
{
    var values = new Dictionary<GraphPort, object?>();

    foreach (var node in graph.TopologicalOrder())    // each node after what feeds it
    {
        if (!node.Enabled) { node.Status = NodeStatus.Idle; continue; }

        // An input's value is whatever its wire's output produced.
        var inputs = node.Inputs.ToDictionary(
            port => port.Name,
            port => graph.ConnectionInto(port) is { } wire ? values.GetValueOrDefault(wire.Source) : null);

        try
        {
            var outputs = Evaluate(node.Definition.TypeId, inputs, node);   // yours
            foreach (var port in node.Outputs)
                values[port] = outputs.GetValueOrDefault(port.Name);

            node.Status = NodeStatus.Ready;
            node.StatusMessage = null;
        }
        catch (Exception ex)
        {
            node.Status = NodeStatus.Error;
            node.StatusMessage = ex.Message;          // shown on the dot's tooltip and the properties panel
        }
    }
}
```

- Read parameters with `node.GetParameter<double>("Level")`. Integers come
  back as `int`, choices and text as `string`, and booleans as `bool`.
- `Status` and `StatusMessage` are not saved and not part of undo. Set them
  freely.
- `GraphChanged` fires once per step. Dragging a slider fires it as the
  value moves. If running is slow, run on a background thread with a
  cancellation token, and set the statuses back on the UI thread.

## 6. Files

```csharp
File.WriteAllText(path, editor.Save());
editor.History.MarkSaved();            // IsDirty goes false; the demo puts * in the title

var warnings = editor.Load(File.ReadAllText(path));   // replaces the graph, resets undo
```

`Load` never fails on content. A node type that no longer exists, a renamed
port or a wire the rules now refuse is left out and named in `warnings`, so
an old file still opens. It throws `JsonException` only for text that is not
a graph. The format:

```json
{
  "format": "NodeGraphKit",
  "version": 1,
  "nodes": [
    { "id": "8f3c…", "type": "sample.add", "name": "Add", "x": 300, "y": 230,
      "accent": "Default", "enabled": true, "parameters": { "Scale": 1 } }
  ],
  "connections": [
    { "from": "1a2b…", "output": "Value", "to": "8f3c…", "input": "A" }
  ]
}
```

## 7. Undo

Every step stores a snapshot of the whole graph (JSON). Undo puts the
previous one back. Snapshots can't drift from the graph the way hand-written
reverse operations can, and a few hundred nodes is a few hundred kilobytes.

Steps are recorded for:

- adding, deleting, wiring, pasting and duplicating;
- renaming, colouring and enabling;
- moving (one step per drag, however many nodes);
- parameter changes (one step per parameter while it keeps changing, so a
  slider drag is one step).

What this means for your code: **undo rebuilds the graph's nodes as new
objects with the same ids.** If you keep anything per node (cached results,
open windows), key it by `GraphNode.Id`, not by the object.

## 8. Extension points

| Want to | Use |
| --- | --- |
| Open something on a node's double-click | `GraphEditorView.NodeDoubleClick` (a double-click on the caption renames instead) |
| Add to a node's right-click menu | `GraphEditorView.NodeMenuOpening`: add to `e.Menu.Items` |
| Add controls to the properties panel | `PropertiesView.HeaderContent` / `FooterContent`; their DataContext is the selected `NodeViewModel` |
| Allow loops | `editor.Graph.AllowCycles = true` |
| Zoom, fit, scroll | `GraphEditorView.ZoomIn()`, `ZoomOut()`, `FitToScreen()`, `ViewportCentre()`, and `Canvas` for the Nodify editor itself |
| Add a node from code | `editor.AddNode(definition, point)` (graph coordinates) |
| Debug a binding | Run the demo with `NODEGRAPH_BINDING_LOG=C:\temp\bindings.log` |

## 9. What Sirius Studio Plus has that the kit leaves out

These are specific to the vision application, or depend on its engine:

- Sub-graphs opened in tabs of their own, and ports that grow as they are
  wired.
- Parameters driven by a wire, and region, ring and path editors drawn on
  an image.
- Image viewers, histograms, profiles and preview panels docked under the
  graph.
- Saving the docking layout between runs.
- The dark message box. The demo uses the system one.

Each was built on top of the same canvas, the same way you would build
yours.
