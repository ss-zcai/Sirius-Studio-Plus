using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>How many bits of a loaded image's values carry the picture.</summary>
/// <remarks>
/// A 12-bit camera's picture saved as a 16-bit file stops at 4095, and
/// nothing in the file says so. Auto takes the file's own depth - never a
/// guess from the pixels, which would take a dark 16-bit picture for a
/// 12-bit one. Settings are in the image's own values, so Auto on a 12-bit
/// file is never wrong, only longer sliders than needed.
/// </remarks>
public enum ImageBits
{
    [System.ComponentModel.Description("Auto")]
    Auto = 0,

    [System.ComponentModel.Description("8-bit")]
    Bits8 = 8,

    [System.ComponentModel.Description("10-bit")]
    Bits10 = 10,

    [System.ComponentModel.Description("12-bit")]
    Bits12 = 12,

    [System.ComponentModel.Description("14-bit")]
    Bits14 = 14,

    [System.ComponentModel.Description("16-bit")]
    Bits16 = 16,
}

/// <summary>Loads an image from disk. The usual entry point of a graph.</summary>
[VisionNode(
    "Image File",
    TypeKey = "source.imageFile",
    Category = NodeCategory.Source,
    Icon = "ph_image_file",
    Description = "Reads an image from disk and feeds it into the graph.")]
public sealed class ImageFileSourceNode : VisionNode
{
    private string _filePath = string.Empty;
    private ImreadModes _mode = ImreadModes.Unchanged;
    private ImageBits _bits = ImageBits.Auto;

    public ImageFileSourceNode()
    {
        ImageOut = AddOutput<ImageData>("Out");
    }

    public OutputPort ImageOut { get; }

    [Param("File Path",
        Description = "Path to the source image.",
        Editor = ParamEditor.FilePath,
        Filter = "Image Files (*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp)|*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*")]
    public string FilePath
    {
        get => _filePath;
        set => SetParam(ref _filePath, value);
    }

    /// <remarks>
    /// Unchanged for a new node, so a 12- or 16-bit file keeps its levels;
    /// a recipe saved with Any Color keeps it.
    /// </remarks>
    [Param("Read Mode", Description = "How the file is decoded. Unchanged keeps its own depth - 0 to 4095 for a 12-bit camera. Any Color and Grayscale give 8 bits: a 12- or 16-bit file is squeezed into them and loses its fine steps.")]
    public ImreadModes Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
    }

    [Param("Bits", Description = "How many bits carry the picture. Auto takes the file's own: 16 for a 16-bit file. A 12-bit camera's saved frames stop at 4095 - choose 12-bit, and tools' settings range to 4095.")]
    public ImageBits Bits
    {
        get => _bits;
        set => SetParam(ref _bits, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (string.IsNullOrWhiteSpace(_filePath))
            throw new InvalidOperationException("No image file selected.");

        if (!File.Exists(_filePath))
            throw new FileNotFoundException($"Image file not found: {_filePath}");

        var mat = CvInvoke.Imread(_filePath, _mode);

        // Imread reports failure by returning an empty Mat rather than throwing.
        if (mat.IsEmpty)
        {
            mat.Dispose();
            throw new InvalidOperationException(
                $"'{Path.GetFileName(_filePath)}' could not be decoded as an image.");
        }

        context.Set(ImageOut, ImageData.Adopt(mat, bits: _bits == ImageBits.Auto ? null : (int)_bits));
    }
}

/// <summary>
/// Generates a blank image. Useful for testing a graph without a camera or
/// file, and as a canvas for generated content.
/// </summary>
[VisionNode(
    "Blank Image",
    TypeKey = "source.constant",
    Category = NodeCategory.Source,
    Icon = "ph_blank_image",
    Description = "Produces a blank grayscale image of a fixed size.")]
public sealed class ConstantImageNode : VisionNode
{
    private int _width = 640;
    private int _height = 480;
    private int _value = 128;

    public ConstantImageNode()
    {
        ImageOut = AddOutput<ImageData>("Out");
    }

    public OutputPort ImageOut { get; }

    [Param(Min = 1, Max = 8192)]
    public int Width
    {
        get => _width;
        set => SetParam(ref _width, value);
    }

    [Param(Min = 1, Max = 8192)]
    public int Height
    {
        get => _height;
        set => SetParam(ref _height, value);
    }

    [Param("Value", Min = 0, Max = 255, Editor = ParamEditor.Slider)]
    public int Value
    {
        get => _value;
        set => SetParam(ref _value, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var mat = new Mat(_height, _width, DepthType.Cv8U, 1);
        try
        {
            mat.SetTo(new Emgu.CV.Structure.MCvScalar(_value));
        }
        catch
        {
            mat.Dispose();
            throw;
        }

        context.Set(ImageOut, ImageData.Adopt(mat));
    }
}
