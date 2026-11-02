using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Sirius.Core.Nodes;
using Sirius.Studio.App;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The help window itself, laid out - its markup is only parsed when it
/// opens, so a mistake there would otherwise first show on somebody's
/// screen.
/// </summary>
[Collection(WpfCollection.Name)]
public class NodeHelpWindowTests
{
    public NodeHelpWindowTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    [Fact]
    public void It_opens_for_every_tool_in_the_library() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            VisionNodeLibrary.Register();

            var shipped = typeof(VisionNodeLibrary).Assembly;

            foreach (var descriptor in NodeCatalog.All.Where(d => d.NodeType.Assembly == shipped))
            {
                var content = Laid(new NodeHelpWindow(descriptor.CreateInstance()));
                Assert.Contains(Runs(content), run => run.Text == descriptor.TypeKey);
            }
        });

    /// <summary>
    /// A type is written in its socket's colour, so the help and the canvas
    /// say the same thing: an image input reads blue in both.
    /// </summary>
    [Fact]
    public void A_type_is_written_in_its_sockets_colour() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var content = Laid(new NodeHelpWindow(new BlobFilterNode()));

            var image = Runs(content).First(run => run.Text == "Image");
            var colour = ((SolidColorBrush)((TextBlock)image.Parent).Foreground).Color;

            Assert.Equal((Color)ColorConverter.ConvertFromString("#60A5FA")!, colour);
        });

    /// <summary>
    /// A name and its type sit on one baseline. They are separate blocks
    /// aligned by their tops, so that holds only while both lines are the
    /// same height above the baseline - which the space in the other font
    /// each one carries is there to make so.
    /// </summary>
    [Fact]
    public void A_name_and_its_type_are_lines_of_one_height() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var content = Laid(new NodeHelpWindow(new BlobFilterNode()));

            var type = (TextBlock)Runs(content).First(run => run.Text == "Image").Parent;
            var row = (Grid)type.Parent;
            var name = row.Children.OfType<TextBlock>().First(block => !ReferenceEquals(block, type));

            Assert.Equal(name.ActualHeight, type.ActualHeight, 3);
        });

    /// <summary>
    /// The window's content, taken out and laid out on its own - a window
    /// builds its visual tree only when shown, and a test has no business
    /// putting one on the screen.
    /// </summary>
    private static FrameworkElement Laid(NodeHelpWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var context = window.DataContext;

        window.Content = null;
        window.Close();

        content.DataContext = context;
        content.Measure(new Size(520, 620));
        content.Arrange(new Rect(0, 0, 520, 620));
        content.UpdateLayout();

        return content;
    }

    private static List<Run> Runs(DependencyObject root)
    {
        var runs = new List<Run>();
        Walk(root);
        return runs;

        void Walk(DependencyObject node)
        {
            if (node is TextBlock block)
                runs.AddRange(block.Inlines.OfType<Run>());

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
    }
}
