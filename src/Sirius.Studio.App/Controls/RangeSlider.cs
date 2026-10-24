using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// One track with a low and a high handle.
/// </summary>
/// <remarks>
/// <para>
/// WPF has no range slider, and two ordinary sliders are not one: a band is
/// a single decision, the pairing has to be inferred from the labels, and
/// nothing stops the low being dragged above the high. A colour range read
/// as six unrelated sliders for that reason.
/// </para>
/// <para>
/// Drawn rather than templated. A template would need a Track, two Thumbs
/// and the arithmetic to place them, all of which is this file's one method
/// anyway - and the painting is three rectangles and two circles.
/// </para>
/// </remarks>
public sealed class RangeSlider : FrameworkElement
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnBoundsChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            255.0, FrameworkPropertyMetadataOptions.AffectsRender, OnBoundsChanged));

    public static readonly DependencyProperty LowProperty = DependencyProperty.Register(
        nameof(Low),
        typeof(double),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLowChanged,
            CoerceLow));

    public static readonly DependencyProperty HighProperty = DependencyProperty.Register(
        nameof(High),
        typeof(double),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            255.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnHighChanged,
            CoerceHigh));

    /// <summary>Rounds every change to a multiple of this. Zero leaves it alone.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(double),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(1.0));

    /// <summary>
    /// Spreads the track over orders of magnitude rather than a flat scale.
    /// </summary>
    /// <remarks>
    /// Blob area runs from a few pixels to a few million. Flat, every useful
    /// setting falls in the first one percent of the travel; a decade per
    /// equal step is usable.
    /// </remarks>
    public static readonly DependencyProperty IsLogarithmicProperty = DependencyProperty.Register(
        nameof(IsLogarithmic),
        typeof(bool),
        typeof(RangeSlider),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double HandleRadius = 7;
    private const double TrackThickness = 4;

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x3A, 0x3D, 0x44));
    private static readonly Brush SpanBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush HandleEdgeBrush = Frozen(Color.FromRgb(0x17, 0x18, 0x1A));

    /// <summary>Which handle the mouse took hold of, if any.</summary>
    private bool? _draggingLow;

    public RangeSlider()
    {
        Height = 20;
        Focusable = true;
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Low
    {
        get => (double)GetValue(LowProperty);
        set => SetValue(LowProperty, value);
    }

    public double High
    {
        get => (double)GetValue(HighProperty);
        set => SetValue(HighProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public bool IsLogarithmic
    {
        get => (bool)GetValue(IsLogarithmicProperty);
        set => SetValue(IsLogarithmicProperty, value);
    }

    /// <summary>
    /// Holds the ends inside the bounds and the right way round.
    /// </summary>
    /// <remarks>
    /// Coerced rather than checked where the drag happens, because a value
    /// arrives from four directions: a drag, a key, a typed box and a
    /// binding carrying whatever a saved recipe held. Enforcing the invariant
    /// in one of those places leaves the other three able to draw handles
    /// off the end of the track, or crossed over.
    /// </remarks>
    private static object CoerceLow(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        return Math.Clamp((double)value, slider.Minimum, Math.Max(slider.Minimum, slider.High));
    }

    private static object CoerceHigh(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        return Math.Clamp((double)value, Math.Min(slider.Low, slider.Maximum), slider.Maximum);
    }

    // Each end bounds the other, so moving one may free or pinch the other.
    private static void OnLowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.CoerceValue(HighProperty);

    private static void OnHighChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.CoerceValue(LowProperty);

    private static void OnBoundsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        d.CoerceValue(LowProperty);
        d.CoerceValue(HighProperty);
    }

    protected override void OnRender(DrawingContext context)
    {
        var track = TrackRect();

        // Transparent fill over the whole control, so the gap above and below
        // the track still takes the mouse - a 4px target is not a target.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        context.DrawRoundedRectangle(TrackBrush, null, track, TrackThickness / 2, TrackThickness / 2);

        var low = PositionOf(Low);
        var high = PositionOf(High);

        context.DrawRoundedRectangle(
            SpanBrush,
            null,
            new Rect(low, track.Top, Math.Max(0, high - low), track.Height),
            TrackThickness / 2,
            TrackThickness / 2);

        DrawHandle(context, low, track);
        DrawHandle(context, high, track);
    }

    private static void DrawHandle(DrawingContext context, double x, Rect track)
    {
        var centre = new Point(x, track.Top + (track.Height / 2));

        // A dark ring, so two handles sitting on top of each other at the end
        // of the track still read as two.
        context.DrawEllipse(HandleBrush, new Pen(HandleEdgeBrush, 1.5), centre, HandleRadius, HandleRadius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;

        // Whichever handle is nearer, and the low one when they coincide -
        // otherwise a range collapsed to a point can only ever be widened
        // upwards.
        _draggingLow = PrefersLowHandle(x);

        CaptureMouse();
        Focus();
        MoveTo(x);

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingLow is not null && e.LeftButton == MouseButtonState.Pressed)
            MoveTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _draggingLow = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = Step > 0 ? Step : 1;

        var delta = e.Key switch
        {
            Key.Left or Key.Down => -step,
            Key.Right or Key.Up => step,
            _ => 0.0,
        };

        if (delta == 0)
            return;

        // Shift moves the upper end, so both are reachable from the keyboard
        // without a focus concept for each handle.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            High += delta;
        else
            Low += delta;

        e.Handled = true;
    }

    /// <summary>
    /// Which handle a press at <paramref name="x"/> takes hold of.
    /// </summary>
    /// <remarks>
    /// The nearer one, and the low one when they coincide - otherwise a range
    /// collapsed to a point could only ever be widened upwards.
    /// </remarks>
    internal bool PrefersLowHandle(double x) =>
        Math.Abs(x - PositionOf(Low)) <= Math.Abs(x - PositionOf(High));

    /// <summary>Moves the handle being dragged to a position on the control.</summary>
    private void MoveTo(double x)
    {
        var value = ValueAt(x);

        // Pushed past its partner, a handle stops rather than swapping -
        // swapping loses track of which one is under the cursor and the range
        // jitters as the mouse crosses over. Coercion is what stops it.
        if (_draggingLow == true)
            Low = Snap(value);
        else
            High = Snap(value);
    }

    /// <summary>
    /// Rounds to the step. Granularity of input only - the bounds are the
    /// coercion's business.
    /// </summary>
    private double Snap(double value)
    {
        // Not on a log track: a step of one would be the whole of the first
        // decade and nothing at all at the far end.
        if (IsLogarithmic)
            return Math.Round(value);

        return Step > 0 ? Math.Round(value / Step) * Step : value;
    }

    private Rect TrackRect()
    {
        var top = (ActualHeight - TrackThickness) / 2;

        return new Rect(
            HandleRadius,
            Math.Max(0, top),
            Math.Max(0, ActualWidth - (2 * HandleRadius)),
            TrackThickness);
    }

    /// <summary>Where a value sits along the control, in pixels.</summary>
    internal double PositionOf(double value)
    {
        var track = TrackRect();
        return track.Left + (FractionOf(value) * track.Width);
    }

    /// <summary>The value at a position along the control.</summary>
    internal double ValueAt(double x)
    {
        var track = TrackRect();

        if (track.Width <= 0)
            return Minimum;

        return ValueOf(Math.Clamp((x - track.Left) / track.Width, 0, 1));
    }

    /// <summary>How far along the track a value sits, from zero to one.</summary>
    internal double FractionOf(double value)
    {
        if (IsLogarithmic)
        {
            var low = LogOf(Minimum);
            var high = LogOf(Maximum);

            return high <= low ? 0 : Math.Clamp((LogOf(value) - low) / (high - low), 0, 1);
        }

        var span = Maximum - Minimum;
        return span <= 0 ? 0 : Math.Clamp((value - Minimum) / span, 0, 1);
    }

    /// <summary>The value a fraction along the track.</summary>
    internal double ValueOf(double fraction)
    {
        if (!IsLogarithmic)
            return Minimum + (fraction * (Maximum - Minimum));

        var low = LogOf(Minimum);
        var high = LogOf(Maximum);

        return Math.Pow(10, low + (fraction * (high - low)));
    }

    /// <summary>
    /// A logarithm that tolerates zero, which is where a size filter starts.
    /// </summary>
    private static double LogOf(double value) => Math.Log10(Math.Max(value, 1));

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
