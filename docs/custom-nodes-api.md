# Custom node API reference

Every public type a custom node uses, with exact signatures. Interface
version **1.0.0**. For why things are the way they are, see
`custom-nodes.md`; this page is the lookup.

Namespaces: `Sirius.Plugins` (the interface), `Sirius.Plugins.Testing`
(`TestRun`), `Sirius.Core.Shapes` (points, bounds, shapes),
`Sirius.Core.Nodes` (calibration, matrix, rectangle, ring, range).

Coordinates are image pixels: x to the right, **y downward**, origin at the
top-left of the top-left pixel. Angles are **degrees, positive clockwise**
as seen on the image.

---

## Sirius.Plugins

### `ICustomNode`

```csharp
public interface ICustomNode
{
    void Declare(INodeShape shape);   // once, before any Run; keep the handles
    void Run(IRunContext context);    // worker thread; may throw to fail the node
}
```

The class must be `public`, non-abstract, have a public parameterless
constructor, and carry `[CustomNode]`.

### `[CustomNode]`

```csharp
[CustomNode(string displayName,
    TypeKey = "vendor.name",      // required; never change once used
    Group = NodeGroup.Custom,     // toolbox group
    Icon = "ph_brightness",       // an application icon key; optional
    Description = "...")]         // tooltip and node reference
```

`TypeKey`: letters, digits, `-`, `_`, at least one dot. Saved as
`custom/<TypeKey>`.

### `NodeGroup`

`Source`, `Basic`, `Combine`, `Filtering`, `Morphology`, `Blob`, `Fitting`,
`Transformation`, `Analysis`, `Calibration`, `TextAndCodeReading`,
`Output`, `Custom` (default).

### `[Setting]`

On a public read/write instance property.

```csharp
[Setting(string? displayName = null,
    Description = "...", Group = "...",
    Min = double.NaN, Max = double.NaN, Step = double.NaN,
    Editor = SettingEditor.Auto,
    OddOnly = false, Logarithmic = false,
    Filter = null,            // FilePath only: "Label|*.ext;*.ext2"
    AllowsNoLimit = false,    // Range only
    Connectable = false)]     // also an input; read via context.Resolve
```

Allowed property types: `double`, `float`, `int`, `long`, `bool`, `string`,
any `enum`, `Point2`, `OrientedRect`, `ValueRange`, `Ring`. Not nullable
value types. `Connectable` is not allowed on an `enum`.

### `SettingEditor`

| Value | For |
|---|---|
| `Auto` | chosen from the type (below) |
| `Slider` | numbers; needs `Min` and `Max` |
| `TextBox` | numbers, `string`, `Point2` (typed `"x, y"`) |
| `ComboBox` | `enum` |
| `CheckBox` | `bool` |
| `FilePath` | `string`, with a browse button |
| `Colour` | `string`, `"#RRGGBB"` |
| `Rect` | `OrientedRect`, drawable on the image |
| `Ring` | `Ring`, drawable on the image |
| `Range` | `ValueRange`, two handles |

`Auto` gives: `bool` → CheckBox, `enum` → ComboBox, number with both `Min`
and `Max` → Slider, `OrientedRect` → Rect, `Ring` → Ring, `ValueRange` →
Range, anything else → TextBox.

### `INodeShape`

```csharp
InPort<T>  Input<T>(string name, string? label = null, bool required = true);
OutPort<T> Output<T>(string name, string? label = null);
```

`T` must be one of `CustomNodeTypes.PortTypes`: `Picture`, `ShapeSet`,
`double`, `int`, `bool`, `string`, `Calibration`, `Point2`,
`NumberMatrix`, `Bounds`, `OrientedRect`, `ValueRange`, `Ring`.

Names are unique per direction; an input and an output may share one. A
connectable setting adds an input named after the setting, so no declared
input may use that name.

### `InPort<T>`, `OutPort<T>`

Handles returned by `INodeShape`. Read-only: `string Name`,
`string Label`; `InPort<T>` also `bool IsRequired`. Not constructible by a
plugin.

### `IRunContext`

```csharp
T? Read<T>(InPort<T> port) where T : class;        // Picture, ShapeSet, NumberMatrix, string
T? ReadValue<T>(InPort<T> port) where T : struct;  // double, int, bool, Point2, Calibration, ...
bool IsWired<T>(InPort<T> port);
T Resolve<T>(string settingName, T current);       // connectable settings
void Publish<T>(OutPort<T> port, T value);
void Warn(string message);
CancellationToken Cancellation { get; }
bool IsCancelled { get; }
void ThrowIfCancelled();
```

- `Read`/`ReadValue` return null when nothing is wired or the upstream
  produced nothing. Numbers convert between numeric types; a 2-value
  `NumberMatrix` converts to `Point2`.
- Values read are borrowed: do not keep or modify them.
- `Resolve(nameof(X), X)` returns the wired value if `X` is wired, else
  `X`. Throws for a name that is not a connectable setting.
- `Publish` of an input `Picture` passes it through uncopied; of an
  allocated one, copies it into the application.
- Several `Warn` calls are joined into one message.

### `Picture`

```csharp
public sealed class Picture
{
    int Width { get; }
    int Height { get; }
    int Channels { get; }            // 1..4, interleaved
    PixelType Pixels { get; }
    ColourLayout Layout { get; }
    int Stride { get; }              // bytes per row = Width * Channels * sample size
    int RowLength { get; }           // samples per row = Width * Channels
    bool IsReadOnly { get; }         // true for inputs

    static Picture Allocate(int width, int height, int channels = 1,
                            PixelType pixels = PixelType.Byte8,
                            ColourLayout layout = ColourLayout.Unknown);
    static Picture Allocate(Picture like);
    Picture Copy();                  // writable, owned

    ReadOnlySpan<T> Row<T>(int y) where T : unmanaged;
    Span<T> WritableRow<T>(int y) where T : unmanaged;      // throws on an input
    ReadOnlySpan<T> Samples<T>() where T : unmanaged;       // all rows, contiguous
    Span<T> WritableSamples<T>() where T : unmanaged;       // throws on an input
}
```

`T` must match `Pixels`: `Byte8` → `byte`, `UInt16` → `ushort`, `Float32`
→ `float`; anything else throws `InvalidOperationException`. Sample `c` of
pixel `x` in row `y`: `Row<T>(y)[x * Channels + c]`.

An input picture throws `ObjectDisposedException` if touched after `Run`
returns. `Allocate` throws `ArgumentException` for a non-positive size,
channels outside 1–4, or a layout that disagrees with the channel count.

### `PixelType`

`Byte8` (0–255), `UInt16` (0–65535; 10/12-bit cameras stop at 1023/4095),
`Float32` (no fixed range).

### `ColourLayout`

`Unknown` (as the channel count suggests), `Gray` (1), `Bgr` (3), `Bgra`
(4), `Hsv` (3, hue 0–179 in 8-bit), `Hls` (3, hue 0–179 in 8-bit), `Lab`,
`Luv`, `YCrCb`, `Xyz` (3 each).

### `CustomNodeTypes`

```csharp
static IReadOnlyList<Type> PortTypes { get; }
static IReadOnlyList<Type> SettingTypes { get; }   // plus any enum
static bool IsPortType(Type type);
static bool IsSettingType(Type type);
```

---

## Sirius.Plugins.Testing

### `TestRun`

```csharp
static TestRun For(ICustomNode node);             // declares ports, checks settings
TestRun Supply(string input, object? value);      // null = unwired; pictures become read-only
TestRun Wire(string setting, object? value);      // drive a connectable setting
CancellationToken Cancellation { get; set; }
TestRun Execute();                                 // throws if a required input is missing
T Published<T>(string output);                     // throws if not published
bool WasPublished(string output);
IReadOnlyList<string> Warnings { get; }
```

`TestRun` implements `IRunContext` and applies the application's rules.

---

## Sirius.Core.Shapes

### `Point2`

```csharp
readonly record struct Point2(double X, double Y)
    double DistanceTo(Point2 other);
    static Point2 operator +(Point2 point, Point2 offset);
    static bool TryParse(string? text, out Point2 point);   // "100, 200"
```

### `Bounds`

```csharp
readonly record struct Bounds(double X, double Y, double Width, double Height)
    double Right { get; }     // X + Width
    double Bottom { get; }    // Y + Height
    double Area { get; }
    Point2 Centre { get; }
```

### `Shape` and its kinds

Immutable records. Create with a constructor, style with `init` properties:

```csharp
new CircleShape(new Point2(120, 80), 14)
{
    Label = "Ø 28.0",
    Colour = 0xFF22C55E,       // ARGB; null = viewer default
    StrokeWidth = 2,           // image pixels; null = viewer default
    Metrics = new Dictionary<string, double> { [ShapeMetric.Radius] = 14 },
}
```

| Type | Constructor |
|---|---|
| `PointShape` | `(Point2 At)` |
| `LineShape` | `(Point2 Start, Point2 End)` — also `double Length` |
| `RectShape` | `(Bounds Rect)` — axis-aligned |
| `OrientedRectShape` | `(Point2 Centre, double Width, double Height, double AngleDegrees)` — also `Point2[] Corners()` |
| `CircleShape` | `(Point2 Centre, double Radius)` |
| `EllipseShape` | `(Point2 Centre, double MajorAxis, double MinorAxis, double Angle)` — full axis lengths; angle of the major axis |
| `PolygonShape` | `(IReadOnlyList<Point2> Points, bool Closed = true)` |

Common `init` properties on every shape:

| Property | |
|---|---|
| `string? Label` | drawn beside the shape |
| `double? LabelSize` | label height in image pixels; null = fixed size on screen |
| `string? LabelFont` | font family name; null = viewer default |
| `uint? Colour` | ARGB, e.g. `0xFFEF4444` for red |
| `double? StrokeWidth` | in image pixels |
| `IReadOnlyDictionary<string, double> Metrics` | named measurements, keys from `ShapeMetric` |

Also: `Bounds Bounds { get; }` and `double? Metric(string name)`.

### `ShapeSet`

```csharp
ShapeSet(IReadOnlyList<Shape> shapes, int frameWidth, int frameHeight)
static ShapeSet Empty { get; }
IReadOnlyList<Shape> Shapes { get; }
int FrameWidth { get; }
int FrameHeight { get; }
int Count { get; }
```

Pass the size of the image the shapes were found in as the frame size.

### `ShapeMetric`

String keys for `Shape.Metrics`, so a downstream Shape Table or Select
Shape can read them: `Area`, `Perimeter`, `CentroidX`, `CentroidY`,
`BoundsX`, `BoundsY`, `BoundsWidth`, `BoundsHeight`, `Extent`,
`Circularity`, `Solidity`, `AspectRatio`, `Width`, `Length`, `Angle`,
`HoleCount`, `TouchesBorder`, `Radius`, `MajorAxis`, `MinorAxis`,
`FitError`, `FitPoints`.

---

## Sirius.Core.Nodes

### `Calibration`

```csharp
readonly record struct Calibration(double unitsPerPixel, string? unit)
    static readonly Calibration None;             // 1 px per px
    double UnitsPerPixel { get; }
    string Unit { get; }                          // "mm", "µm", "px"
    bool IsCalibrated { get; }
    static Calibration FromKnownLength(double pixels, double length, string? unit);
    double ToUnits(double pixels);
    double ToPixels(double units);
    double AreaToUnits(double squarePixels);
```

### `NumberMatrix`

```csharp
NumberMatrix(int rows, int columns, IReadOnlyList<double> values)   // row-major
static NumberMatrix Row(params double[] values);
static NumberMatrix Empty { get; }
int Rows { get; }
int Columns { get; }
int Count { get; }
IReadOnlyList<double> Values { get; }
double this[int row, int column] { get; }
double At(int index);                       // 0 when out of range
bool IsVector { get; }
bool IsSquare { get; }
static bool TryParse(string? text, out NumberMatrix matrix);   // "1 0 -1; 2 0 -2"
```

### `OrientedRect`

A setting or port value, not a drawn shape (see `OrientedRectShape`).

```csharp
OrientedRect(double centreX, double centreY, double width, double height, double angle)
double CentreX, CentreY, Width, Height { get; init; }
double Angle { get; init; }                  // degrees, clockwise
double Left { get; }
double Top { get; }
```

### `Ring`

```csharp
Ring(double centreX, double centreY, double innerRadius, double outerRadius)
double CentreX, CentreY, InnerRadius, OuterRadius { get; init; }
double MeanRadius { get; }
double HalfWidth { get; }
double Width { get; }
bool Contains(double x, double y);
static Ring FromMean(double centreX, double centreY, double meanRadius, double halfWidth);
```

### `ValueRange`

```csharp
ValueRange(double? low, double? high)       // null end = no limit
static ValueRange Unbounded { get; }
double? Low, High { get; init; }
bool HasLow, HasHigh, IsUnbounded { get; }
double LowOr(double fallback);
double HighOr(double fallback);
bool Contains(double value);
```
