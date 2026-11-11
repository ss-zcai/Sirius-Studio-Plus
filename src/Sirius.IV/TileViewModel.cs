using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Imaging;
using Sirius.Vision;

namespace Sirius.IV;

/// <summary>
/// One viewer: a picture from the list, read with its annotation file, and
/// the shapes from it drawn over the picture.
/// </summary>
/// <remarks>
/// The file is read on a worker - a large picture takes a moment to decode -
/// and only the latest asked for is shown: stepping quickly down the list
/// never shows a picture after the one it has moved on to.
/// </remarks>
public sealed class TileViewModel : ObservableObject
{
    private readonly BrowserViewModel? _owner;
    private ImageFileItem? _file;
    private IReadOnlyList<MeasurementRecord> _measurements = [];
    private IReadOnlyList<MeasurementRecord> _saved = [];
    private bool _applying;
    private BitmapSource? _picture;
    private ShapeSet? _shapes;
    private AnnotationFile? _annotations;
    private string? _problem;
    private bool _loading;
    private int _showing;

    /// <param name="owner">The window it is in, which keeps every picture's measurements; null for a viewer on its own.</param>
    public TileViewModel(BrowserViewModel? owner = null)
    {
        _owner = owner;
    }

    /// <summary>
    /// The measurements on the picture shown: what its annotation file has,
    /// or what was drawn since and not yet saved. Bound both ways to the
    /// viewer, which writes back what is drawn.
    /// </summary>
    public IReadOnlyList<MeasurementRecord> Measurements
    {
        get => _measurements;
        set
        {
            value ??= [];

            if (ReferenceEquals(value, _measurements))
                return;

            _measurements = value;
            OnPropertyChanged();

            // The viewer clears its measurements when a picture of another
            // size arrives: that is the picture changing, not a hand.
            if (_applying || _file is null || _picture is null)
                return;

            _owner?.Measured(_file, value, _saved, _picture.PixelWidth, _picture.PixelHeight);
        }
    }

    /// <summary>After a save: what is shown is what the file now says.</summary>
    internal void Saved() => _saved = _measurements;

    /// <summary>The picture this viewer is showing, or null for an empty place.</summary>
    public ImageFileItem? File
    {
        get => _file;
        private set
        {
            if (SetProperty(ref _file, value))
            {
                OnPropertyChanged(nameof(Caption));
                OnPropertyChanged(nameof(Verdict));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => _file is null;

    public BitmapSource? Picture
    {
        get => _picture;
        private set => SetProperty(ref _picture, value);
    }

    /// <summary>Every layer of its annotation file, drawn as one.</summary>
    public ShapeSet? Shapes
    {
        get => _shapes;
        private set => SetProperty(ref _shapes, value);
    }

    /// <summary>Its annotation file, when it has one that could be read.</summary>
    public AnnotationFile? Annotations
    {
        get => _annotations;
        private set
        {
            if (SetProperty(ref _annotations, value))
            {
                OnPropertyChanged(nameof(Verdict));
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    /// <summary>The file's name, over the viewer.</summary>
    public string Caption => _file?.RelativePath ?? string.Empty;

    /// <summary>PASS, FAIL, ERROR - from the annotations, or the file's name.</summary>
    public string? Verdict => _annotations?.VerdictWord ?? _file?.Verdict;

    /// <summary>Station, time, results and layers, for the caption's tooltip.</summary>
    public string? Details
    {
        get
        {
            if (_annotations is not { } annotations)
                return _file?.Path;

            var text = new StringBuilder();
            text.AppendLine(_file?.Path);

            if (annotations.Station is not null)
                text.AppendLine(CultureInfo.CurrentCulture, $"Station {annotations.Station}, inspection {annotations.Serial}");

            if (annotations.Time is { } time)
                text.AppendLine(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture));

            if (annotations.Recipe is not null)
                text.AppendLine(CultureInfo.CurrentCulture, $"Recipe {annotations.Recipe}");

            text.AppendLine(annotations.Grade is null ? annotations.VerdictWord : $"{annotations.VerdictWord} ({annotations.Grade})");

            foreach (var (name, value) in annotations.Results)
                text.AppendLine(CultureInfo.CurrentCulture, $"{name}: {Format(value)}");

            foreach (var layer in annotations.Layers)
                text.AppendLine(CultureInfo.CurrentCulture, $"Layer {layer.Name}: {layer.Shapes.Count} shape{(layer.Shapes.Count == 1 ? string.Empty : "s")}");

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>Why the picture or its annotations could not be read, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    /// <summary>Shows a picture, or nothing; returns when it is shown, or was overtaken.</summary>
    public Task Show(ImageFileItem? file)
    {
        if (ReferenceEquals(file, _file) && (_picture is not null || _loading))
            return Task.CompletedTask;

        var showing = ++_showing;
        File = file;
        Problem = null;

        if (file is null)
        {
            Apply(() =>
            {
                Picture = null;
                Shapes = null;
                Annotations = null;
                _saved = [];
                Measurements = [];
            });

            IsLoading = false;
            return Task.CompletedTask;
        }

        return LoadAsync(file, showing);
    }

    private async Task LoadAsync(ImageFileItem file, int showing)
    {
        IsLoading = true;

        Loaded loaded;

        try
        {
            loaded = await Task.Run(() => Load(file)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Emgu.CV.Util.CvException or ArgumentException)
        {
            loaded = new Loaded(null, null, $"{file.Name} could not be read: {ex.Message}");
        }

        // Moved on meanwhile.
        if (showing != _showing)
            return;

        IsLoading = false;

        Apply(() =>
        {
            Picture = loaded.Picture;
            Annotations = loaded.Annotations;
            Shapes = loaded.Annotations is { } annotations && loaded.Picture is { } picture
                ? Frame(annotations, picture.PixelWidth, picture.PixelHeight)
                : null;

            // The picture's own measurements: drawn and not saved, or saved.
            _saved = loaded.Annotations?.Measurements ?? [];
            Measurements = _owner?.UnsavedFor(file) ?? _saved;
        });

        Problem = loaded.Problem;
    }

    /// <summary>Changes what is shown without it counting as anything drawn by hand.</summary>
    private void Apply(Action change)
    {
        _applying = true;

        try
        {
            change();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>The picture, decoded as it was saved - 12 bits as 12 - and its annotation file.</summary>
    internal static Loaded Load(ImageFileItem file)
    {
        AnnotationFile? annotations = null;
        string? problem = null;

        if (System.IO.File.Exists(file.AnnotationPath))
            annotations = AnnotationFile.TryLoad(file.AnnotationPath, out problem);

        using var mat = CvInvoke.Imread(file.Path, ImreadModes.Unchanged);

        if (mat.IsEmpty)
            return new Loaded(null, annotations, $"{file.Name} is not a picture that can be read.");

        var bits = annotations?.Bits is > 0 and var said ? said : (int?)null;
        using var image = ImageData.CopyOf(mat, bits: bits);

        return new Loaded(MatImageConverter.ToBitmapSource(image), annotations, problem);
    }

    /// <summary>
    /// The shapes, in the frame of the picture shown - which is the frame they
    /// were saved in unless the picture has been changed since.
    /// </summary>
    private static ShapeSet Frame(AnnotationFile annotations, int width, int height)
    {
        var shapes = annotations.AllShapes();
        return new ShapeSet(shapes.Shapes, annotations.Width > 0 ? annotations.Width : width, annotations.Height > 0 ? annotations.Height : height);
    }

    private static string Format(object? value) => value switch
    {
        double number => number.ToString("0.###", CultureInfo.CurrentCulture),
        bool flag => flag ? "Yes" : "No",
        null => "—",
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
    };

    internal sealed record Loaded(BitmapSource? Picture, AnnotationFile? Annotations, string? Problem);
}
