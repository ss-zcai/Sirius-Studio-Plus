using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Each parameter draws exactly one editor row.
/// </summary>
/// <remarks>
/// <para>
/// The properties panel shows every editor kind at once and hides all but
/// one, each behind its own <c>Is…</c> flag. The text box is the fallback
/// for whatever has no row of its own, so it and the dedicated kinds have to
/// agree exactly: miss a kind and the parameter has no editor at all, double
/// up and it gets two.
/// </para>
/// <para>
/// Written as a chain of negations it doubled up. A colour range shipped
/// with a read-only "53 to 164" text box under every slider, because
/// <c>Range</c> was added and the chain was not told to exclude it. There is
/// one list now - <c>ParameterViewModel.Dedicated</c> - and these tests hold
/// it against the markup so the pair cannot drift again.
/// </para>
/// </remarks>
public class ParameterRowTests
{
    private static readonly string Markup =
        File.ReadAllText(Path.Combine(Repository.AppSource, "MainWindow.xaml"));

    /// <summary>
    /// The <c>Is…</c> flags the properties panel actually binds a row to.
    /// </summary>
    private static IReadOnlySet<string> BoundFlags { get; } =
        Regex.Matches(Markup, @"Visibility=""\{Binding (Is[A-Za-z]+), Converter=\{StaticResource BoolToVisibility\}\}""")
            .Select(match => match.Groups[1].Value)
            .Where(name => typeof(ParameterViewModel).GetProperty(name) is not null)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A parameter of each editor kind, built against a real node so the
    /// view-model is the one the panel would be given.
    /// </summary>
    public static TheoryData<ParamEditor> Kinds
    {
        get
        {
            var data = new TheoryData<ParamEditor>();

            foreach (var kind in Enum.GetValues<ParamEditor>())
                data.Add(kind);

            return data;
        }
    }

    /// <summary>
    /// The markup was found and really does bind rows - otherwise every
    /// theory below passes by asserting over an empty set.
    /// </summary>
    [Fact]
    public void The_panel_binds_editor_rows()
    {
        Assert.NotEmpty(BoundFlags);
        Assert.Contains("IsTextBox", BoundFlags);
    }

    /// <summary>
    /// Every kind is either in the dedicated list or falls back to the text
    /// box, and never both. This is the invariant the chain of negations
    /// broke.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_draws_exactly_one_row(ParamEditor kind)
    {
        var dedicated = Dedicated.Contains(kind);
        var text = ParameterViewModel.FallsBackToText(kind);

        Assert.True(
            dedicated ^ text,
            dedicated
                ? $"{kind} has a row of its own and a text box under it"
                : $"{kind} has no editor row at all");
    }

    /// <summary>
    /// Every kind with a row of its own has that row in the markup. A kind
    /// added to the list without the matching row leaves the parameter with
    /// no editor, and nothing says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_dedicated_kind_has_its_row_in_the_markup(ParamEditor kind)
    {
        if (!Dedicated.Contains(kind))
            return;

        Assert.Contains($"Is{kind}", BoundFlags);
    }

    /// <summary>
    /// And nothing in the markup binds a row the view-model does not offer -
    /// a leftover from a kind that was renamed or removed would be a row
    /// that can never show.
    /// </summary>
    [Fact]
    public void No_row_is_bound_to_a_kind_that_no_longer_exists()
    {
        var known = Enum.GetValues<ParamEditor>()
            .Select(kind => $"Is{kind}")
            .Append("IsTextBox")

            // Not an editor row: the "(driven by connection)" label, which
            // shows beside whichever row is up. Listed rather than filtered
            // out by shape, so a genuinely stale row still fails.
            .Append("IsDriven")
            .ToHashSet(StringComparer.Ordinal);

        var unknown = BoundFlags.Where(flag => !known.Contains(flag)).ToArray();

        Assert.True(unknown.Length == 0, "Rows bound to nothing: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// The fallback is the one that has no kind of its own, which is what
    /// makes "everything else" well defined.
    /// </summary>
    [Fact]
    public void The_text_box_is_the_fallback()
    {
        Assert.True(ParameterViewModel.FallsBackToText(ParamEditor.Auto));
        Assert.True(ParameterViewModel.FallsBackToText(ParamEditor.TextBox));
    }

    /// <summary>
    /// Read off the view-model rather than listed again here. A second copy
    /// of the list is the thing this test exists to prevent.
    /// </summary>
    private static IReadOnlyCollection<ParamEditor> Dedicated { get; } =
        (HashSet<ParamEditor>)typeof(ParameterViewModel)
            .GetField("Dedicated", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
}
