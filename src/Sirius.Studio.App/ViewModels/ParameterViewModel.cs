using System.Drawing;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// One editable parameter in the inspector, built from
/// <see cref="ParamDescriptor"/>.
/// </summary>
/// <remarks>
/// This is the replacement for the DevExpress <c>PropertyGridControl</c>. The
/// ranges and editor kinds come from the <see cref="ParamAttribute"/> on the
/// property itself, so a node declares its own UI rather than relying on the
/// shell recognising a property name.
/// </remarks>
public sealed class ParameterViewModel : ObservableObject
{
    private readonly VisionNode _node;
    private readonly ParamDescriptor _descriptor;
    private readonly InputPort? _port;

    public ParameterViewModel(VisionNode node, ParamDescriptor descriptor)
    {
        _node = node;
        _descriptor = descriptor;

        BrowseCommand = new RelayCommand(Browse);

        NextCommand = new RelayCommand(
            () => StepSequence(1),
            () => Sequence is { } sequence && sequence.Index < sequence.Count);

        PreviousCommand = new RelayCommand(
            () => StepSequence(-1),
            () => Sequence is { } sequence && sequence.Index > 1);

        // The folder listing changes the length and can move the position, so
        // the editor follows the node rather than only driving it.
        node.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ISequenceNode.Count)
                or nameof(ISequenceNode.Index)
                or nameof(ISequenceNode.CurrentName))
            {
                RefreshSequence();
            }
        };

        // A connectable parameter gets an input port; watch it so the editor
        // can show that a wire is overriding the typed value.
        _port = node.FindInput(descriptor.Name);
        if (_port is not null)
        {
            _port.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(InputPort.IsConnected))
                {
                    OnPropertyChanged(nameof(IsDriven));
                    OnPropertyChanged(nameof(IsEditable));
                }
            };
        }

        // Node setters may coerce (odd-only sizes, clamping), so the displayed
        // value has to follow what the node actually accepted rather than what
        // was typed.
        _node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == descriptor.Name)
            {
                RaiseValueChanged();
                return;
            }

            // A bound living on another parameter has moved, so the slider's
            // range changed even though this value did not.
            if (e.PropertyName == descriptor.Attribute.NameSource)
                OnPropertyChanged(nameof(DisplayName));

            if (e.PropertyName == descriptor.Attribute.MinSource)
                OnPropertyChanged(nameof(Minimum));

            if (e.PropertyName == descriptor.Attribute.MaxSource)
                OnPropertyChanged(nameof(Maximum));

            if (e.PropertyName == descriptor.Attribute.StepSource)
                OnPropertyChanged(nameof(Step));
        };
    }

    /// <summary>The node this parameter belongs to.</summary>
    public VisionNode Node => _node;

    /// <summary>
    /// The label. Taken from the node when the parameter names a source for
    /// it, so a band can say what it is actually banding.
    /// </summary>
    public string DisplayName =>
        ReadLabel(_descriptor.Attribute.NameSource) ?? _descriptor.DisplayName;

    private string? ReadLabel(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return null;

        return _node.GetType().GetProperty(propertyName)?.GetValue(_node) as string;
    }

    public string? Description => _descriptor.Description;

    public ParamEditor Kind => _descriptor.Editor;

    public bool IsSlider => Kind == ParamEditor.Slider;

    public bool IsCheckBox => Kind == ParamEditor.CheckBox;

    public bool IsComboBox => Kind == ParamEditor.ComboBox;

    /// <summary>
    /// Whether the list is long enough that typing beats scrolling.
    /// </summary>
    /// <remarks>
    /// Not every dropdown: a searchable one is editable, which means a
    /// caret, a text box look, and the chance to type something that is
    /// not on the list. That is a fair trade for a hundred colour
    /// conversions and a poor one for three typefaces.
    /// </remarks>
    public bool IsSearchable => IsComboBox && Options.Count >= SearchableFrom;

    /// <summary>
    /// Where a list stops being readable at a glance. A dropdown shows
    /// about this many without scrolling, so it is the point at which
    /// finding an entry starts to mean hunting for it.
    /// </summary>
    internal const int SearchableFrom = 12;

    public bool IsRegion => Kind == ParamEditor.Region;

    public bool IsRange => Kind == ParamEditor.Range;

    public bool IsRect => Kind == ParamEditor.Rect;

    public bool IsRing => Kind == ParamEditor.Ring;

    public bool IsFilePath => Kind == ParamEditor.FilePath;

    public bool IsSequence => Kind == ParamEditor.Sequence;

    public bool IsColour => Kind == ParamEditor.Colour;

    /// <summary>The swatches offered for a colour parameter.</summary>
    public IReadOnlyList<ColourOption> ColourOptions => StandardColours.All;

    /// <summary>
    /// The swatch matching the stored value, or null when it is a custom one.
    /// </summary>
    /// <remarks>
    /// Null rather than a nearest match: a hex typed by hand should stay
    /// exactly what was typed, and showing a swatch that is merely close
    /// would suggest the value had been changed to it.
    /// </remarks>
    public ColourOption? SelectedColour
    {
        get => StandardColours.Find(TextValue);
        set
        {
            if (value is not null)
                TextValue = value.Hex;
        }
    }

    /// <summary>
    /// True when this parameter's input port is wired, meaning the upstream
    /// value is in use and whatever is typed here is ignored.
    /// </summary>
    public bool IsDriven => _port?.IsConnected ?? false;

    /// <summary>False while a connection is overriding this parameter.</summary>
    public bool IsEditable => !IsDriven;

    /// <summary>
    /// The kinds that draw a row of their own.
    /// </summary>
    /// <remarks>
    /// One list rather than a chain of negations, because the text box is the
    /// fallback for everything else and the two have to agree. Written as a
    /// chain, adding a kind and forgetting to exclude it drew both rows - a
    /// colour range shipped with a read-only "53 to 164" box under every
    /// slider for exactly that reason.
    /// </remarks>
    private static readonly HashSet<ParamEditor> Dedicated =
    [
        ParamEditor.Slider,
        ParamEditor.CheckBox,
        ParamEditor.ComboBox,
        ParamEditor.Region,
        ParamEditor.Range,
        ParamEditor.Rect,
        ParamEditor.Ring,
        ParamEditor.FilePath,
        ParamEditor.Sequence,
        ParamEditor.Colour,
    ];

    /// <summary>Whether a kind is left to the plain text box.</summary>
    internal static bool FallsBackToText(ParamEditor kind) => !Dedicated.Contains(kind);

    public bool IsTextBox => FallsBackToText(Kind);

    private ISequenceNode? Sequence => _node as ISequenceNode;

    /// <summary>Position within the sequence, as "3 / 47".</summary>
    public string SequenceText => Sequence is not { Count: > 0 } sequence
        ? "no images"
        : $"{sequence.Index} / {sequence.Count}";

    /// <summary>The current item's name, shown under the controls.</summary>
    public string? SequenceItem => Sequence?.CurrentName;

    public IRelayCommand NextCommand { get; }

    public IRelayCommand PreviousCommand { get; }

    private void StepSequence(int delta)
    {
        if (Sequence is not { } sequence)
            return;

        sequence.Index += delta;
        RefreshSequence();
    }

    /// <summary>
    /// Pushes the node's sequence state back into the editor.
    /// </summary>
    /// <remarks>
    /// The ends of the sequence enable and disable the buttons, so the commands
    /// have to be re-queried whenever the position or the folder changes - not
    /// only when this editor moved it.
    /// </remarks>
    private void RefreshSequence()
    {
        OnPropertyChanged(nameof(SequenceText));
        OnPropertyChanged(nameof(SequenceItem));
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(TextValue));

        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens a file dialog and writes the chosen path into the parameter.</summary>
    public IRelayCommand BrowseCommand { get; }

    /// <summary>The label of the node action this parameter carries beside its editor, if any.</summary>
    public string? ActionLabel => _descriptor.Attribute.Action;

    /// <summary>The node's button beside this editor - Teach beside the region it teaches from.</summary>
    public NodeActionViewModel? InlineAction { get; internal set; }

    public bool HasInlineAction => InlineAction is not null;

    private void Browse()
    {
        // A log is a file to write, which need not exist yet - and an
        // open dialog refuses one that does not.
        FileDialog dialog = _descriptor.Attribute.SaveFile
            ? new SaveFileDialog { OverwritePrompt = false }
            : new OpenFileDialog();

        dialog.Title = $"Select {DisplayName}";
        dialog.Filter = _descriptor.Attribute.Filter ?? "All files (*.*)|*.*";

        // Reopen where the current value points, so re-picking a file in the
        // same folder does not start from scratch.
        var current = TextValue;
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var directory = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    dialog.InitialDirectory = directory;

                dialog.FileName = Path.GetFileName(current);
            }
            catch (ArgumentException)
            {
                // A malformed path is not worth failing the dialog over.
            }
        }

        if (dialog.ShowDialog() == true)
            TextValue = dialog.FileName;
    }

    /// <summary>
    /// Slider lower bound: another parameter's value when the node names one,
    /// otherwise the fixed value from the attribute.
    /// </summary>
    public double Minimum => ReadBound(_descriptor.Attribute.MinSource)
                             ?? (_descriptor.Attribute.HasMin ? _descriptor.Attribute.Min : 0);

    public double Maximum => ReadBound(_descriptor.Attribute.MaxSource)
                             ?? (_descriptor.Attribute.HasMax ? _descriptor.Attribute.Max : 100);

    /// <summary>
    /// Reads a bound from the node, by parameter name or plain property.
    /// </summary>
    /// <remarks>
    /// Another parameter first, which is what this was written for - a
    /// maximum that tracks a minimum the operator also sets. Falling back
    /// to a plain property lets a bound be derived rather than edited: a
    /// colour range's hue stops at 179, which is a fact about the frame
    /// and not something to offer anybody a box for.
    /// </remarks>
    private double? ReadBound(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return null;

        var bound = _node.Descriptor.Parameters.FirstOrDefault(p => p.Name == propertyName);
        var value = bound is not null
            ? bound.GetValue(_node)
            : _node.GetType().GetProperty(propertyName)?.GetValue(_node);

        return value is null ? null : System.Convert.ToDouble(value);
    }

    public double Step =>
        ReadBound(_descriptor.Attribute.StepSource)
        ?? (_descriptor.Attribute.HasStep
            ? _descriptor.Attribute.Step
            : IsIntegral ? 1 : 0.01);

    /// <summary>
    /// How this parameter's number is written.
    /// </summary>
    /// <remarks>
    /// A whole-numbered parameter showed as "9.00", which reads as a value
    /// that could be 9.25 - the editor describing a precision the parameter
    /// does not have. A step of one or more means the same thing even when
    /// the underlying type is floating point: a grey level is a grey level.
    /// </remarks>
    public string ValueFormat =>
        IsIntegral || (Step >= 1 && Math.Abs(Step - Math.Round(Step)) < 1e-9)
            ? "0"
            : "0.##";

    /// <summary>
    /// The number as shown beside the slider.
    /// </summary>
    /// <remarks>
    /// A string rather than binding the number with a StringFormat, because
    /// a StringFormat cannot itself be bound - it is not a dependency
    /// property - and the format depends on the parameter.
    /// </remarks>
    public string NumericText
    {
        get => NumericValue.ToString(ValueFormat, CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out var parsed))
            {
                NumericValue = parsed;
            }

            // Either way the display is refreshed: a rejected entry snaps
            // back, and an accepted one is re-rendered in this parameter's
            // own format rather than as the operator happened to type it.
            OnPropertyChanged(nameof(NumericText));
        }
    }

    private bool IsIntegral =>
        _descriptor.ValueType == typeof(int) ||
        _descriptor.ValueType == typeof(short) ||
        _descriptor.ValueType == typeof(long) ||
        _descriptor.ValueType == typeof(byte);

    /// <summary>Available values for an enum parameter.</summary>
    /// <remarks>
    /// Distinct, because an enum may name the same value twice and
    /// <c>Enum.GetValues</c> returns one entry per name. OpenCV's colour
    /// conversions do it throughout - Bgra2Rgba and Rgba2Bgra are the same
    /// number - so the list showed "Bgra2Rgba" twice, one of which could
    /// never be the selected item and so could never appear chosen.
    /// </remarks>
    /// <remarks>
    /// Each value is offered under its name in words - "Centre To Centre"
    /// for CentreToCentre - as an <see cref="EnumOption"/>; the parameter
    /// still holds the value itself, so nothing saved changes.
    /// </remarks>
    public IReadOnlyList<object> Options => _descriptor.ValueType.IsEnum
        ? _options ??= Enum.GetValues(_descriptor.ValueType)
            .Cast<object>()
            .Distinct()
            .Select(value => (object)new EnumOption(value, TypeNames.Words(value.ToString()!)))
            .ToArray()
        : Choices();

    /// <summary>
    /// A text parameter's list, from its node - with the value it holds
    /// first when that is not among them, so a camera that is unplugged
    /// still shows which one the recipe wants.
    /// </summary>
    private IReadOnlyList<object> Choices()
    {
        if (_node is not IParameterChoices source)
            return Array.Empty<object>();

        var choices = source.ChoicesFor(_descriptor.Name).Cast<object>().ToList();

        if (Raw is string { Length: > 0 } held && !choices.Contains(held))
            choices.Insert(0, held);

        return choices;
    }

    private IReadOnlyList<object>? _options;

    private object? Raw
    {
        get => _descriptor.GetValue(_node);
        set
        {
            _descriptor.SetValue(_node, value);
            RaiseValueChanged();
        }
    }

    /// <summary>Numeric view, used by sliders and spin boxes.</summary>
    public double NumericValue
    {
        get => Raw is null ? 0 : System.Convert.ToDouble(Raw);
        set
        {
            var coerced = IsIntegral ? Math.Round(value) : value;
            Raw = System.Convert.ChangeType(coerced, _descriptor.ValueType);
        }
    }

    public bool BoolValue
    {
        get => Raw is bool b && b;
        set => Raw = value;
    }

    public object? SelectedOption
    {
        get => Raw is { } raw && _descriptor.ValueType.IsEnum
            ? Options.OfType<EnumOption>().FirstOrDefault(option => Equals(option.Value, raw))
            : Raw;
        set
        {
            if (value is EnumOption option)
                Raw = option.Value;
            else if (value is not null)
                Raw = value;
        }
    }

    public string TextValue
    {
        get => Raw?.ToString() ?? string.Empty;
        set
        {
            try
            {
                // A compound value reads itself; Convert.ChangeType cannot,
                // and would reject every keystroke in the box.
                if (_descriptor.ValueType == typeof(Point2))
                {
                    if (Point2.TryParse(value, out var point))
                        Raw = point;
                    else
                        RaiseValueChanged();

                    return;
                }

                Raw = _descriptor.ValueType == typeof(string)
                    ? value
                    : System.Convert.ChangeType(value, _descriptor.ValueType);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Reject the keystroke and snap back to the stored value rather
                // than throwing out of a binding.
                RaiseValueChanged();
            }

            // The swatches follow the text, so typing a hex that happens to be
            // one of them selects it.
            if (IsColour)
                OnPropertyChanged(nameof(SelectedColour));
        }
    }

    public int RegionX
    {
        get => RegionValue.X;
        set => RegionValue = RegionValue with { X = value };
    }

    public int RegionY
    {
        get => RegionValue.Y;
        set => RegionValue = RegionValue with { Y = value };
    }

    public int RegionWidth
    {
        get => RegionValue.Width;
        set => RegionValue = RegionValue with { Width = Math.Max(0, value) };
    }

    public int RegionHeight
    {
        get => RegionValue.Height;
        set => RegionValue = RegionValue with { Height = Math.Max(0, value) };
    }

    /// <summary>
    /// The region as a whole. Bound two-way by the image viewer so an ROI drawn
    /// on the image writes straight back into the node parameter.
    /// </summary>
    /// <summary>
    /// The region as an upright rectangle, which is what the viewer can
    /// edit by dragging.
    /// </summary>
    /// <remarks>
    /// A rect parameter answers here too, in its centre and size with the
    /// angle left alone - dragging says where the region is and how big,
    /// and rotating it is a separate question. Deliberately not the
    /// bounding box of the rotated rectangle, which would grow a little
    /// every time it was read back and written out.
    /// </remarks>
    public Rectangle RegionValue
    {
        get
        {
            if (Raw is Rectangle rectangle)
                return rectangle;

            if (Raw is OrientedRect rect)
            {
                var (x, y, width, height) = rect.Upright;
                return new Rectangle(
                    (int)Math.Round(x), (int)Math.Round(y),
                    (int)Math.Round(width), (int)Math.Round(height));
            }

            return Rectangle.Empty;
        }

        set
        {
            if (Raw is OrientedRect rect)
            {
                RectValue = rect.WithUpright(value.X, value.Y, value.Width, value.Height);
                return;
            }

            if (RegionValue != value)
                Raw = value;
        }
    }

    public OrientedRect RectValue
    {
        get => Raw is OrientedRect rect ? rect : default;
        set
        {
            if (RectValue != value)
                Raw = value;
        }
    }

    /// <summary>
    /// The region as "x, y, w, h, angle". Rejected text leaves the value
    /// alone rather than guessing, and the box reverts to what is there.
    /// </summary>
    /// <summary>
    /// How far a region is turned. Zero, and read-only, for a parameter
    /// that has no angle to turn.
    /// </summary>
    public double RegionAngle
    {
        get => Raw is OrientedRect rect ? rect.Angle : 0;
        set
        {
            if (Raw is OrientedRect rect && Math.Abs(rect.Angle - value) > 1e-9)
                RectValue = rect with { Angle = value };
        }
    }

    public string RectText
    {
        get => RectValue.ToString();
        set
        {
            if (OrientedRect.TryParse(value, out var parsed))
                RectValue = parsed;

            OnPropertyChanged();
        }
    }

    public Ring RingValue
    {
        get => Raw is Ring ring ? ring : default;
        set
        {
            if (RingValue != value)
                Raw = value;
        }
    }

    /// <summary>
    /// The band as "x, y, inner, outer". Rejected text leaves the value
    /// alone rather than guessing, and the box reverts to what is there.
    /// </summary>
    public string RingText
    {
        get => RingValue.ToString();
        set
        {
            if (Ring.TryParse(value, out var parsed))
                RingValue = parsed;

            OnPropertyChanged();
        }
    }

    public ValueRange RangeValue
    {
        get => Raw is ValueRange range ? range : new ValueRange(Minimum, Maximum);
        set
        {
            if (RangeValue != value)
                Raw = value;
        }
    }

    /// <summary>
    /// Whether this parameter lets an end be left off entirely.
    /// </summary>
    /// <remarks>
    /// A colour channel is always bounded - every value is between 0 and 255
    /// and a handle parked at an end is a real limit. A blob filter is not:
    /// "any area" has to be expressible, and it cannot be the end of a track
    /// because the useful end depends on the image.
    /// </remarks>
    public bool AllowsNoLimit => _descriptor.Attribute.AllowsNoLimit;

    /// <summary>The slider spans orders of magnitude rather than a flat scale.</summary>
    public bool IsLogarithmic => _descriptor.Attribute.Logarithmic;

    /// <summary>
    /// The ends, separately, for the two handles and the two boxes.
    /// </summary>
    /// <remarks>
    /// Each end is held against the other rather than against the
    /// parameter's own bounds, so a low typed above the high stops at it
    /// instead of crossing over. An absent end reads as the track's extreme,
    /// which is where its handle is drawn.
    /// </remarks>
    public double RangeLow
    {
        get => RangeValue.LowOr(Minimum);
        set => RangeValue = RangeValue with
        {
            Low = Math.Clamp(value, Minimum, RangeValue.HighOr(Maximum)),
        };
    }

    public double RangeHigh
    {
        get => RangeValue.HighOr(Maximum);
        set => RangeValue = RangeValue with
        {
            High = Math.Clamp(value, RangeValue.LowOr(Minimum), Maximum),
        };
    }

    /// <summary>
    /// The ends as text. Empty means no limit, where the parameter allows
    /// one, so clearing the box is how a filter is switched off.
    /// </summary>
    public string RangeLowText
    {
        get => Format(RangeValue.Low);
        set
        {
            RangeValue = RangeValue with { Low = ParseEnd(value, RangeValue.Low) };
            OnPropertyChanged();
        }
    }

    public string RangeHighText
    {
        get => Format(RangeValue.High);
        set
        {
            RangeValue = RangeValue with { High = ParseEnd(value, RangeValue.High) };
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Reads a typed end. Blank clears it where that is allowed, and an
    /// unreadable one leaves the old value rather than guessing at zero.
    /// </summary>
    private double? ParseEnd(string? text, double? current)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AllowsNoLimit ? null : current;

        return TryParse(text, out var parsed)
            ? Math.Clamp(parsed, Minimum, Maximum)
            : current;
    }

    private string Format(double? value) => value is not { } number
        ? string.Empty
        : IsIntegral || Step >= 1
            ? ((long)Math.Round(number)).ToString(CultureInfo.CurrentCulture)
            : number.ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void RaiseValueChanged()
    {
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(NumericText));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedOption));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(RegionValue));
        OnPropertyChanged(nameof(RectValue));
        OnPropertyChanged(nameof(RectText));
        OnPropertyChanged(nameof(RegionAngle));
        OnPropertyChanged(nameof(RangeValue));
        OnPropertyChanged(nameof(AllowsNoLimit));
        OnPropertyChanged(nameof(RangeLow));
        OnPropertyChanged(nameof(RangeHigh));
        OnPropertyChanged(nameof(RangeLowText));
        OnPropertyChanged(nameof(RangeHighText));
        OnPropertyChanged(nameof(RegionX));
        OnPropertyChanged(nameof(RegionY));
        OnPropertyChanged(nameof(RegionWidth));
        OnPropertyChanged(nameof(RegionHeight));
    }
}

/// <summary>
/// An enum value in a dropdown, shown by its name in words.
/// </summary>
/// <remarks>
/// The text is what the list shows, what the editable box shows once one
/// is picked and what a search matches; the value is what the parameter
/// holds and the recipe saves.
/// </remarks>
public sealed record EnumOption(object Value, string Label)
{
    public override string ToString() => Label;
}
