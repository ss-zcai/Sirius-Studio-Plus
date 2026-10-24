using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

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
    private ImreadModes _mode = ImreadModes.AnyColor;

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

    [Param("Read Mode", Description = "How the file is decoded (colour, grayscale, unchanged).")]
    public ImreadModes Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
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

        context.Set(ImageOut, ImageData.Adopt(mat));
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
