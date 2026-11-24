using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Sirius.IV;

/// <summary>
/// ROIs from All Pictures: each picture's own rectangles, or one picture's
/// from every picture; and the folder the crops go in.
/// </summary>
public partial class RoisFromAllWindow : Window
{
    private RoisFromAllWindow(
        IReadOnlyList<RoiBatch.Cut> own,
        (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source,
        int listed,
        string folder)
    {
        InitializeComponent();
        Sirius.Studio.App.DarkTitleBar.Apply(this);

        var (ownText, sameText, sameForAll) = Describe(own, source, listed);

        OwnText.Text = ownText;
        SameText.Text = sameText;
        OwnOption.IsEnabled = own.Count > 0;
        SameOption.IsEnabled = source is not null;
        OwnOption.IsChecked = !sameForAll;
        SameOption.IsChecked = sameForAll;
        FolderBox.Text = folder;
    }

    /// <summary>
    /// The two choices in words, and which is offered first: each picture's
    /// own - unless only the picture shown has any, which is how a set of
    /// training images is begun.
    /// </summary>
    internal static (string Own, string Same, bool SameForAll) Describe(
        IReadOnlyList<RoiBatch.Cut> own, (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source, int listed)
    {
        var rois = own.Sum(cut => cut.Rois.Count);

        var ownText = own.Count == 0
            ? "Each picture's own ROIs - none of the pictures listed has any"
            : $"Each picture's own ROIs - {Count(rois, "ROI")} drawn on {Count(own.Count, "picture")}";

        var sameText = source is { } shown
            ? $"The {Count(shown.Rois.Count, "ROI")} on {shown.Picture.Name}, cut from each of the {Count(listed, "picture")} listed"
            : "One picture's ROIs from every picture - draw them on the picture shown first";

        var onlyShown = source is { } on && own.Count == 1 && ReferenceEquals(own[0].Picture, on.Picture);
        return (ownText, sameText, own.Count == 0 || onlyShown);
    }

    /// <summary>
    /// Where the crops are offered to go: a folder beside the one open, named
    /// after it - "Circularity Check ROIs" beside "Circularity Check" - not
    /// inside it, where the list would show them among the pictures.
    /// </summary>
    internal static string SuggestedFolder(string? open)
    {
        if (string.IsNullOrWhiteSpace(open))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ROIs");

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(open));
        var parent = Path.GetDirectoryName(folder);
        var name = Path.GetFileName(folder);

        // A drive's root has no parent to sit beside it in.
        return parent is null || string.IsNullOrEmpty(name) ? Path.Combine(folder, "ROIs") : Path.Combine(parent, $"{name} ROIs");
    }

    /// <summary>The window, made and not shown - for a test.</summary>
    internal static RoisFromAllWindow Unshown(
        IReadOnlyList<RoiBatch.Cut> own, (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source, int listed, string? open) =>
        new(own, source, listed, SuggestedFolder(open));

    /// <summary>Asks; null when cancelled, else which ROIs and where they go.</summary>
    internal static (bool SameForAll, string Folder)? Ask(
        Window owner,
        IReadOnlyList<RoiBatch.Cut> own,
        (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source,
        int listed,
        string? open)
    {
        var window = new RoisFromAllWindow(own, source, listed, SuggestedFolder(open)) { Owner = owner };

        return window.ShowDialog() == true
            ? (window.SameOption.IsChecked == true, window.Folder!)
            : null;
    }

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count:N0} {what}s";

    /// <summary>The folder typed, in full; null when it is not one Windows takes.</summary>
    private string? Folder
    {
        get
        {
            var typed = Environment.ExpandEnvironmentVariables(FolderBox.Text.Trim());

            if (typed.Length == 0 || !Path.IsPathFullyQualified(typed))
                return null;

            try
            {
                return Path.GetFullPath(typed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // The folder suggested may not be made yet: start from where it would be.
        var start = Folder is { } typed && Directory.Exists(typed) ? typed : Path.GetDirectoryName(Folder ?? string.Empty);

        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder for the ROI images",
            InitialDirectory = start ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnCut(object sender, RoutedEventArgs e)
    {
        if (Folder is null)
        {
            ProblemText.Text = "Type a whole folder path, such as D:\\Pictures\\ROIs, or choose one with Browse.";
            ProblemText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
