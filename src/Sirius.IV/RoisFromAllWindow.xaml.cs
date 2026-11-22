using System.Windows;

namespace Sirius.IV;

/// <summary>ROIs from All Pictures: each picture's own rectangles, or one picture's from every picture.</summary>
public partial class RoisFromAllWindow : Window
{
    private RoisFromAllWindow(IReadOnlyList<RoiBatch.Cut> own, (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source, int listed)
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

    /// <summary>Asks; null when cancelled, else whether the shown picture's ROIs are cut from every picture.</summary>
    internal static bool? Ask(Window owner, IReadOnlyList<RoiBatch.Cut> own, (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? source, int listed)
    {
        var window = new RoisFromAllWindow(own, source, listed) { Owner = owner };
        return window.ShowDialog() == true ? window.SameOption.IsChecked == true : null;
    }

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count:N0} {what}s";

    private void OnCut(object sender, RoutedEventArgs e) => DialogResult = true;
}
