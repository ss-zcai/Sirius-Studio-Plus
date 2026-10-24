using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using IoPath = System.IO.Path;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Steps through the images in a folder, one per evaluation.
/// </summary>
/// <remarks>
/// <para>
/// The same job as <see cref="ImageFileSourceNode"/>, but over a folder: a
/// recipe is rarely tuned against one image, and swapping the path by hand to
/// check the next one loses the settings being compared.
/// </para>
/// <para>
/// The frame is a parameter like any other, so stepping through the folder
/// re-runs the graph exactly as moving a slider does. Nothing special is
/// needed to make the viewers and charts follow.
/// </para>
/// </remarks>
[VisionNode(
    "Image Sequence",
    TypeKey = "source.imageSequence",
    Category = NodeCategory.Source,
    Icon = "ph_image_sequence",
    Description = "Steps through the images in a folder, one per evaluation.")]
public sealed class ImageSequenceNode : VisionNode, ISequenceNode
{
    private static readonly string[] Extensions =
        [".tif", ".tiff", ".png", ".jpg", ".jpeg", ".bmp"];

    private string _sourcePath = string.Empty;
    private ImreadModes _mode = ImreadModes.AnyColor;
    private int _index = 1;
    private string[] _files = [];

    public ImageSequenceNode()
    {
        ImageOut = AddOutput<ImageData>("Out");
    }

    public OutputPort ImageOut { get; }

    /// <summary>
    /// A folder, or any image inside one.
    /// </summary>
    /// <remarks>
    /// Accepting either means the ordinary file dialog can be used to pick the
    /// first image, which is how an operator thinks of a sequence, while a
    /// folder pasted in still works.
    /// </remarks>
    [Param("Folder or First Image",
        Description = "A folder of images, or any image in it.",
        Editor = ParamEditor.FilePath,
        Filter = "Image Files (*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp)|*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*")]
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetParam(ref _sourcePath, value))
                Rescan();
        }
    }

    [Param("Frame",
        Editor = ParamEditor.Sequence,
        Description = "Which image in the folder, counting from one.")]
    public int Index
    {
        get => _index;
        set => SetParam(ref _index, Clamp(value));
    }

    [Param("Read Mode", Description = "How each file is decoded (colour, grayscale, unchanged).")]
    public ImreadModes Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
    }

    /// <summary>How many images the folder holds.</summary>
    public int Count => _files.Length;

    /// <summary>The file the current frame will read, for the inspector.</summary>
    public string? CurrentName =>
        Count == 0 ? null : IoPath.GetFileName(_files[Clamp(_index) - 1]);

    /// <summary>
    /// Re-reads the folder listing.
    /// </summary>
    /// <remarks>
    /// Done when the path changes rather than on every evaluation: a listing is
    /// cheap but not free, and re-reading it mid-run would let the sequence
    /// shift underneath a comparison that is halfway through.
    /// </remarks>
    public void Rescan()
    {
        _files = ListImages(_sourcePath);

        // Picking a file rather than a folder starts the sequence on it, which
        // is what choosing "the first image" is meant to do.
        if (File.Exists(_sourcePath))
        {
            var at = Array.FindIndex(
                _files,
                file => string.Equals(file, _sourcePath, StringComparison.OrdinalIgnoreCase));

            if (at >= 0)
                _index = at + 1;
        }

        _index = Clamp(_index);

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(CurrentName));
    }

    private static string[] ListImages(string path)
    {
        try
        {
            var folder = Directory.Exists(path) ? path : IoPath.GetDirectoryName(path);

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return [];

            return Directory.EnumerateFiles(folder)
                .Where(file => Extensions.Contains(IoPath.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => IoPath.GetFileName(file) ?? file, NaturalOrder.Comparer)
                .ToArray();
        }
        catch (Exception)
        {
            // A path that cannot be listed - gone, renamed, or not permitted -
            // is an empty sequence. Evaluation reports it; the inspector should
            // not throw while someone is still typing a path.
            return [];
        }
    }

    private int Clamp(int value) => Count == 0 ? 1 : Math.Clamp(value, 1, Count);

    protected override void OnEvaluate(EvalContext context)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath))
            throw new InvalidOperationException("No folder or image selected.");

        // A folder that was empty when it was chosen may have been filled
        // since, and a path typed before the drive was ready is common enough.
        if (Count == 0)
            Rescan();

        if (Count == 0)
            throw new InvalidOperationException($"No images found in '{_sourcePath}'.");

        var file = _files[Clamp(_index) - 1];
        var mat = CvInvoke.Imread(file, _mode);

        // Imread reports failure by returning an empty Mat rather than throwing.
        if (mat.IsEmpty)
        {
            mat.Dispose();
            throw new InvalidOperationException(
                $"'{IoPath.GetFileName(file)}' could not be decoded as an image.");
        }

        context.Set(ImageOut, ImageData.Adopt(mat));
    }
}

/// <summary>
/// Orders file names the way a person reads them.
/// </summary>
/// <remarks>
/// Ordinal sorting puts "img10" before "img2", which scrambles exactly the
/// numbered sequences this node exists to walk through. Comparing runs of
/// digits as numbers fixes that without a dependency on shlwapi.
/// </remarks>
internal static class NaturalOrder
{
    public static IComparer<string> Comparer { get; } = new NaturalComparer();

    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
                return string.CompareOrdinal(x, y);

            int i = 0, j = 0;

            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var startX = i;
                    var startY = j;

                    while (i < x.Length && char.IsDigit(x[i]))
                        i++;

                    while (j < y.Length && char.IsDigit(y[j]))
                        j++;

                    // Leading zeros are padding, not value: "007" and "7" are
                    // the same frame, and must not sort apart.
                    var numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                    var numberY = y.AsSpan(startY, j - startY).TrimStart('0');

                    if (numberX.Length != numberY.Length)
                        return numberX.Length - numberY.Length;

                    var digits = numberX.SequenceCompareTo(numberY);
                    if (digits != 0)
                        return digits;

                    continue;
                }

                var letters = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (letters != 0)
                    return letters;

                i++;
                j++;
            }

            return (x.Length - i) - (y.Length - j);
        }
    }
}
