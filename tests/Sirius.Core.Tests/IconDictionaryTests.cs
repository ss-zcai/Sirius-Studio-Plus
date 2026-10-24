using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Sirius.Core.Nodes;
using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The generated icon dictionary, checked as a dictionary rather than by eye.
/// </summary>
/// <remarks>
/// <c>Icons.xaml</c> is written by <c>tools/svg2xaml.py</c> from the SVG
/// sets, so nobody reads it. An icon that fails to convert, or a node naming
/// one that is not there, shows up as a blank square in a node header - which
/// looks like a drawing somebody has not got round to rather than a fault.
/// </remarks>
public class IconDictionaryTests
{
    private const string DictionaryPath = "src/Sirius.Studio.App/Themes/Icons.xaml";

    /// <summary>
    /// Loads the dictionary from source, as the application would.
    /// </summary>
    /// <remarks>
    /// Parsed from the file rather than looked up through a pack URI, so the
    /// markup on disk is what is checked - a stale compiled copy would hide
    /// exactly the regeneration mistakes this is here to catch.
    /// </remarks>
    private static ResourceDictionary LoadDictionary()
    {
        using var stream = File.OpenRead(Path.Combine(Repository.Root, DictionaryPath));

        return (ResourceDictionary)XamlReader.Load(stream);
    }

    [Fact]
    public void Every_icon_in_the_dictionary_can_be_realised()
    {
        var dictionary = LoadDictionary();

        Assert.NotEmpty(dictionary.Keys);

        var failures = new List<string>();
        foreach (var key in dictionary.Keys)
        {
            try
            {
                // Indexing forces the deferred conversion that would otherwise
                // only happen the first time the icon is displayed.
                Assert.IsAssignableFrom<ImageSource>(dictionary[key]);
            }
            catch (Exception ex)
            {
                failures.Add($"{key}: {ex.GetBaseException().Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Every node names an icon, and that icon exists. A typo would otherwise
    /// show up as a silently blank node header.
    /// </summary>
    [Fact]
    public void Every_node_icon_key_resolves_to_a_drawing()
    {
        TestCatalog.Ensure();
        VisionNodeLibrary.Register();

        var dictionary = LoadDictionary();
        var missing = new List<string>();

        // Only the shipped library: the fixture nodes defined in this test
        // assembly have no icon by design.
        var shipped = typeof(VisionNodeLibrary).Assembly;

        foreach (var descriptor in NodeCatalog.All.Where(d => d.NodeType.Assembly == shipped))
        {
            if (string.IsNullOrWhiteSpace(descriptor.Icon))
            {
                missing.Add($"{descriptor.TypeKey}: no icon assigned");
                continue;
            }

            if (!dictionary.Contains($"Icon.{descriptor.Icon}"))
                missing.Add($"{descriptor.TypeKey}: '{descriptor.Icon}' is not in the dictionary");
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Every dockable panel view-model needs a DataTemplate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing one is not an error: AvalonDock falls back to rendering the
    /// view-model as text, so the panel opens looking empty rather than
    /// failing. That is hard to attribute, so it is asserted here.
    /// </para>
    /// <para>
    /// A template on a base type counts, because WPF's implicit lookup walks
    /// up the hierarchy - a preview is drawn by the template declared for
    /// <c>ImagePanelViewModel</c>. That it genuinely resolves that way is
    /// pinned separately, by building one and looking at what comes out;
    /// this is only a check that some template could apply.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_panel_view_model_has_a_data_template()
    {
        var theme = File.ReadAllText(Path.Combine(Repository.Root, "src/Sirius.Studio.App/Themes/Dark.xaml"));

        var panelTypes = typeof(Sirius.Studio.App.ViewModels.NodePanelViewModel).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(Sirius.Studio.App.ViewModels.NodePanelViewModel).IsAssignableFrom(t))
            .ToArray();

        Assert.NotEmpty(panelTypes);

        bool HasTemplate(Type type)
        {
            for (var current = type;
                 current is not null && typeof(Sirius.Studio.App.ViewModels.PanelViewModel).IsAssignableFrom(current);
                 current = current.BaseType)
            {
                if (theme.Contains($"{{x:Type vm:{current.Name}}}", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        var missing = panelTypes
            .Where(t => !HasTemplate(t))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(missing.Length == 0, "No DataTemplate in Dark.xaml for: " + string.Join(", ", missing));
    }
}
