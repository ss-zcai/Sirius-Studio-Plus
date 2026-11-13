using System.IO;
using System.Windows;
using System.Windows.Media;
using AvalonDock.Layout;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.IV;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius IV's matching: pictures that belong together - two cameras' views
/// of one part - made into sets, by groups of N down the list or by names
/// alike, and shown side by side with the viewers kept in step.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SiriusIvMatchingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-iv-match-" + Guid.NewGuid().ToString("N"));

    public SiriusIvMatchingTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    private static void Picture(string path, int width = 40, int height = 30)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(100));
        CvInvoke.Imwrite(path, mat);
    }

    /// <summary>
    /// Two cameras, three parts: part 1's second camera failed; part 3 has
    /// its first camera's picture only.
    /// </summary>
    private string CameraPairs()
    {
        foreach (var name in new[]
                 {
                     "p01_cam1_PASS.png", "p01_cam2_FAIL.png",
                     "p02_cam1_PASS.png", "p02_cam2_PASS.png",
                     "p03_cam1_PASS.png",
                 })
        {
            Picture(Path.Combine(_folder, name));
        }

        return _folder;
    }

    private static BrowserViewModel Open(string folder)
    {
        var model = new BrowserViewModel();
        WpfTestHost.Wait(model.OpenAsync(folder));
        return model;
    }

    private static string[][] Names(IEnumerable<IReadOnlyList<ImageFileItem>> sets) =>
        sets.Select(set => set.Select(item => item.Name).ToArray()).ToArray();

    // ---- the rules -------------------------------------------------------------------------

    [Fact]
    public void Groups_of_N_take_the_pictures_down_the_list_as_sorted()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            var rule = new MatchRule { Mode = MatchMode.Groups, GroupSize = 2 };

            Assert.Equal(
                [["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], ["p02_cam1_PASS.png", "p02_cam2_PASS.png"], ["p03_cam1_PASS.png"]],
                Names(model.SetsFor(rule)));

            // As sorted: Z to A, the groups are taken from the other end.
            model.SortDescending = true;
            Assert.Equal(
                [["p03_cam1_PASS.png", "p02_cam2_PASS.png"], ["p02_cam1_PASS.png", "p01_cam2_FAIL.png"], ["p01_cam1_PASS.png"]],
                Names(model.SetsFor(rule)));
        });
    }

    [Fact]
    public void Names_alike_in_their_first_characters_are_a_set()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());

            Assert.Equal(
                [["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], ["p02_cam1_PASS.png", "p02_cam2_PASS.png"], ["p03_cam1_PASS.png"]],
                Names(model.SetsFor(new MatchRule { Mode = MatchMode.NameStart, Characters = 3 })));

            // Longer than a name: the whole name, so each its own.
            Assert.All(model.SetsFor(new MatchRule { Mode = MatchMode.NameStart, Characters = 200 }), set => Assert.Single(set));
        });
    }

    [Theory]
    [InlineData(@"^(.+?)_cam\d")]
    [InlineData(@"^(?<key>p\d+)_")]
    [InlineData(@"^p\d+")]
    public void A_pattern_matches_names_alike_in_the_part_it_finds(string pattern)
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());

            Assert.Equal(
                [["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], ["p02_cam1_PASS.png", "p02_cam2_PASS.png"], ["p03_cam1_PASS.png"]],
                Names(model.SetsFor(new MatchRule { Mode = MatchMode.Pattern, Pattern = pattern })));
        });
    }

    [Fact]
    public void A_named_key_group_wins_and_names_the_pattern_does_not_find_are_on_their_own()
    {
        WpfTestHost.OnStaThread(() =>
        {
            Picture(Path.Combine(_folder, "lot7_A_left.png"));
            Picture(Path.Combine(_folder, "lot7_B_right.png"));
            Picture(Path.Combine(_folder, "reference.png"));
            var model = Open(_folder);

            // Group 1 is the lot and the letter; "key" - the lot - is what counts.
            var rule = new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^((?<key>lot\d+)_[AB])_" };

            Assert.Equal([["lot7_A_left.png", "lot7_B_right.png"], ["reference.png"]], Names(model.SetsFor(rule)));
        });
    }

    [Fact]
    public void The_same_name_in_two_cameras_folders_is_a_set_in_folder_order()
    {
        WpfTestHost.OnStaThread(() =>
        {
            Picture(Path.Combine(_folder, "cam2", "0001.png"));
            Picture(Path.Combine(_folder, "cam1", "0001.png"));
            Picture(Path.Combine(_folder, "cam1", "0002.png"));
            Picture(Path.Combine(_folder, "cam2", "0002.png"));
            var model = Open(_folder);

            // Z to A puts cam2's first; each set is still cam1 then cam2.
            model.Sort = FileSort.Name;
            model.SortDescending = true;

            var sets = model.SetsFor(new MatchRule { Mode = MatchMode.NameStart, Characters = 20 });

            Assert.Equal(
                [[@"cam1\0002.png", @"cam2\0002.png"], [@"cam1\0001.png", @"cam2\0001.png"]],
                sets.Select(set => set.Select(item => item.RelativePath).ToArray()).ToArray());
        });
    }

    [Theory]
    [InlineData(MatchMode.Groups, 1, 20, "", "A group is 2 to 9 pictures.")]
    [InlineData(MatchMode.Groups, 10, 20, "", "A group is 2 to 9 pictures.")]
    [InlineData(MatchMode.NameStart, 2, 0, "", "At least one character of the names must be alike.")]
    [InlineData(MatchMode.Pattern, 2, 20, " ", "Type a pattern: the part of the name that must be alike.")]
    public void A_rule_that_cannot_be_used_says_why(MatchMode mode, int size, int characters, string pattern, string problem) =>
        Assert.Equal(problem, new MatchRule { Mode = mode, GroupSize = size, Characters = characters, Pattern = pattern }.Problem());

    [Fact]
    public void A_pattern_that_cannot_be_read_says_so_and_is_not_used()
    {
        var rule = new MatchRule { Mode = MatchMode.Pattern, Pattern = "^(unclosed" };
        Assert.StartsWith("The pattern cannot be read:", rule.Problem());

        var model = new BrowserViewModel { Match = rule };
        Assert.False(model.IsMatching);
    }

    // ---- the list and the viewers -----------------------------------------------------------

    [Fact]
    public void Matched_the_list_has_a_line_for_each_set_and_the_viewers_show_the_set()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            Assert.Equal(ViewerLayout.Single, model.Layout);

            model.Match = new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^(.+?)_cam\d" };

            // A line for each set, by its first picture; the rest shown with it.
            Assert.Equal(["p01_cam1_PASS.png", "p02_cam1_PASS.png", "p03_cam1_PASS.png"], model.Files.Select(file => file.Name));
            Assert.Equal(["+1", "+1", null], model.Files.Select(file => file.SetCountText));
            Assert.StartsWith("3 sets of 5 pictures, 1 failed · ", model.CountText);

            // The worst of the set: part 1 failed, though its first picture passed.
            Assert.Equal(["FAIL", "PASS", "PASS"], model.Files.Select(file => file.SetVerdict));
            Assert.Equal("PASS", model.Files[0].Verdict);
            Assert.Contains("One of a set of 2:", model.Files[0].Description);

            // Enough viewers for a pair, and the pair shown in them.
            Assert.Equal(ViewerLayout.Two, model.Layout);
            Assert.Equal([model.Files[0]], model.Chosen);
            Assert.Equal(["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], model.Tiles.Select(tile => tile.File?.Name));

            // Stepping on is a set at a time.
            model.Choose([model.Files[1]]);
            Assert.Equal(["p02_cam1_PASS.png", "p02_cam2_PASS.png"], model.Tiles.Select(tile => tile.File?.Name));

            // Part 3 on its own: the second viewer is empty.
            model.Choose([model.Files[2]]);
            Assert.Equal(["p03_cam1_PASS.png", null], model.Tiles.Select(tile => tile.File?.Name));

            // Off again: a line for each picture.
            model.Match = MatchRule.Off;
            Assert.Equal(5, model.Files.Count);
            Assert.All(model.Files, file => Assert.Null(file.SetCountText));
        });
    }

    [Fact]
    public void Failed_shows_each_set_with_a_failure_in_it_whole()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            model.Match = new MatchRule { Mode = MatchMode.Groups, GroupSize = 2 };

            model.Filter = FileFilter.Failed;

            // The pair is made before the filter: the failure keeps its partner.
            var line = Assert.Single(model.Files);
            Assert.Equal(["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], line.Set.Select(item => item.Name));
            Assert.StartsWith("1 shown of 3 sets of 5 pictures", model.CountText);

            // Searching finds a set by any picture in it.
            model.Filter = FileFilter.All;
            model.SearchText = "p02_cam2";
            Assert.Equal(["p02_cam1_PASS.png"], model.Files.Select(file => file.Name));
        });
    }

    [Fact]
    public void Matching_keeps_the_picture_chosen_in_view_by_choosing_its_set()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            var second = model.Files.Single(file => file.Name == "p02_cam2_PASS.png");
            model.Choose([second]);

            model.Match = new MatchRule { Mode = MatchMode.Groups, GroupSize = 2 };

            Assert.Equal("p02_cam1_PASS.png", Assert.Single(model.Chosen).Name);
        });
    }

    [Fact]
    public void Triples_get_three_viewers_side_by_side()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            model.Match = new MatchRule { Mode = MatchMode.Groups, GroupSize = 3 };

            Assert.Equal(ViewerLayout.Three, model.Layout);
            Assert.Equal((1, 3), (model.Rows, model.Columns));
            Assert.Equal(3, model.Tiles.Count);
        });
    }

    [Theory]
    [InlineData(1, ViewerLayout.Single)]
    [InlineData(2, ViewerLayout.Two)]
    [InlineData(3, ViewerLayout.Three)]
    [InlineData(4, ViewerLayout.Four)]
    [InlineData(5, ViewerLayout.Nine)]
    [InlineData(9, ViewerLayout.Nine)]
    public void A_set_gets_a_layout_with_room_for_it(int size, ViewerLayout layout) =>
        Assert.Equal(layout, MatchRule.LayoutFor(size));

    [Fact]
    public void The_rule_and_sync_view_are_remembered_between_sessions()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var settings = Path.Combine(_folder, "sirius-iv.json");
            var rule = new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^(.+)_cam\d+$" };

            _ = new BrowserViewModel(settings) { Match = rule, SyncViews = false };

            var again = new BrowserViewModel(settings);
            Assert.Equal(rule, again.Match);
            Assert.False(again.SyncViews);
        });
    }

    // ---- the Match dialog ---------------------------------------------------------------------

    [Fact]
    public void The_dialog_shows_what_a_rule_makes_of_the_pictures_before_it_is_used()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(CameraPairs());
            var dialog = new MatchingViewModel(model.Match, model.SetsFor);

            Assert.True(dialog.IsOff);
            Assert.Equal("Each of the 5 pictures on its own.", dialog.PreviewText);

            dialog.IsGroups = true;
            Assert.True(dialog.IsGroups);
            Assert.Equal("5 pictures make 3 sets: 2 of 2, 1 on its own.", dialog.PreviewText);
            Assert.Equal("p01_cam1_PASS.png  +  p01_cam2_FAIL.png", dialog.Preview[0]);

            // Typing a pattern chooses it; one that cannot be read says why, and shows nothing.
            dialog.Pattern = "^(p0";
            Assert.True(dialog.IsPattern);
            Assert.StartsWith("The pattern cannot be read:", dialog.Problem);
            Assert.Empty(dialog.Preview);

            dialog.Pattern = @"^(p\d+)";
            Assert.Null(dialog.Problem);
            Assert.Equal(new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^(p\d+)" }, dialog.Rule);

            dialog.Characters = "x";
            Assert.True(dialog.IsNameStart);
            Assert.Equal("At least one character of the names must be alike.", dialog.Problem);
        });
    }

    [Fact]
    public void The_dialog_window_is_made_with_its_parts()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(CameraPairs());
            var window = new MatchingWindow(new MatchingViewModel(model.Match, model.SetsFor));

            try
            {
                Assert.NotNull(window.FindName("PatternBox"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- Sync View --------------------------------------------------------------------------

    private static ImageViewer Viewer(double width, double height)
    {
        var viewer = new ImageViewer
        {
            ImageSource = System.Windows.Media.Imaging.BitmapSource.Create(
                40, 30, 96, 96, PixelFormats.Gray8, null, new byte[40 * 30], 40),
        };

        viewer.Measure(new Size(width, height));
        viewer.Arrange(new Rect(0, 0, width, height));
        viewer.UpdateLayout();
        return viewer;
    }

    private static void PointAt(ImageViewer viewer, Point? at) =>
        typeof(ImageViewer)
            .GetMethod("UpdateReadout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(viewer, [at]);

    [Fact]
    public void In_step_the_viewers_share_one_view_and_show_where_the_pointer_is()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var (first, second) = (Viewer(900, 700), Viewer(400, 300));
            var sync = new ViewSync();
            sync.Add(first);
            sync.Add(second);

            // Each fitted to its own size: different views.
            Assert.NotEqual(first.View, second.View);

            // On: the first's view for both; then whichever moves, the other follows.
            sync.IsOn = true;
            Assert.Equal(first.View, second.View);

            second.FitToWindow();
            Assert.Equal(second.View, first.View);

            // The pointer on one is marked on the other.
            PointAt(first, new Point(12, 7));
            Assert.Equal(new Point(12, 7), second.LinkedCursor);
            Assert.Null(first.LinkedCursor);

            PointAt(first, null);
            Assert.Null(second.LinkedCursor);

            // Off: each on its own again, nothing marked.
            PointAt(first, new Point(3, 3));
            sync.IsOn = false;
            Assert.Null(second.LinkedCursor);

            first.FitToWindow();
            Assert.NotEqual(first.View, second.View);

            // Gone from the screen: no longer followed.
            sync.IsOn = true;
            sync.Remove(second);
            var before = second.View;
            first.FitToWindow();
            Assert.Equal(before, second.View);
        });
    }

    [Fact]
    public void A_viewer_set_to_a_view_does_not_announce_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var viewer = Viewer(500, 400);
            var told = 0;
            viewer.ViewChanged += (_, _) => told++;

            viewer.SetView(new Matrix(2, 0, 0, 2, 5, 5));
            Assert.Equal(new Matrix(2, 0, 0, 2, 5, 5), viewer.View);
            Assert.Equal(0, told);

            viewer.FitToWindow();
            Assert.Equal(1, told);
        });
    }

    // ---- a tab for each viewer ---------------------------------------------------------------

    /// <summary>
    /// Each viewer is a tab of its own, titled with its picture's name, put in
    /// the layout's grid - and put back there, after being dragged about,
    /// when the layout is chosen again.
    /// </summary>
    [Fact]
    public void Each_viewer_is_a_tab_titled_with_its_picture_in_the_layouts_grid()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(CameraPairs());
            var window = new ViewerWindow(model);

            try
            {
                var docking = (AvalonDock.DockingManager)window.FindName("Docking");
                AvalonDock.Layout.LayoutDocumentPane[] Panes() =>
                    docking.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocumentPane>().ToArray();

                Assert.Equal(["p01_cam1_PASS.png"], window.ViewerTabs.Select(tab => tab.Title));
                Assert.Single(Panes());

                // A pair: two tabs side by side, a pane each, named for their pictures.
                model.Match = new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^(.+?)_cam\d" };
                Assert.Equal(["p01_cam1_PASS.png", "p01_cam2_FAIL.png"], window.ViewerTabs.Select(tab => tab.Title));
                Assert.Equal(2, Panes().Length);
                Assert.All(window.ViewerTabs, tab => Assert.False(tab.CanClose));

                var row = Assert.IsType<AvalonDock.Layout.LayoutDocumentPaneGroup>(window.ViewerTabs[0].Parent.Parent);
                Assert.Equal(System.Windows.Controls.Orientation.Horizontal, row.Orientation);
                Assert.Same(row, window.ViewerTabs[1].Parent.Parent);

                // Its tab says how it went, with the picture's details for a tooltip.
                var header = (FrameworkElement)docking.DocumentHeaderTemplate.LoadContent();
                var host = new System.Windows.Controls.Border { DataContext = window.ViewerTabs[1], Child = header };
                host.Measure(new Size(300, 30));
                WpfTestHost.Settle(window.Dispatcher);
                Assert.Equal(["p01_cam2_FAIL.png", "FAIL"], Texts(header));
                Assert.Contains("p01_cam2_FAIL.png", (string)header.ToolTip);

                // Stepping on: the titles follow; an empty viewer is numbered.
                model.Choose([model.Files[2]]);
                Assert.Equal(["p03_cam1_PASS.png", "Viewer 2"], window.ViewerTabs.Select(tab => tab.Title));

                // Dragged into one pane as two tabs; the layout chosen again puts them back.
                var second = window.ViewerTabs[1];
                second.Parent.RemoveChild(second);
                ((AvalonDock.Layout.LayoutDocumentPane)window.ViewerTabs[0].Parent).Children.Add(second);
                docking.Layout.CollectGarbage();
                Assert.Single(Panes());

                model.SetLayoutCommand.Execute(ViewerLayout.Two);
                Assert.Equal(2, Panes().Length);
                Assert.All(Panes(), pane => Assert.Single(pane.Children));

                // 2 x 2: two rows of two, in order.
                model.Layout = ViewerLayout.Four;
                Assert.Equal(4, Panes().Length);
                var grid = Assert.IsType<AvalonDock.Layout.LayoutDocumentPaneGroup>(window.ViewerTabs[0].Parent.Parent.Parent);
                Assert.Equal(System.Windows.Controls.Orientation.Vertical, grid.Orientation);
                Assert.Same(window.ViewerTabs[0].Parent.Parent, window.ViewerTabs[1].Parent.Parent);
                Assert.Same(window.ViewerTabs[2].Parent.Parent, window.ViewerTabs[3].Parent.Parent);
                Assert.NotSame(window.ViewerTabs[0].Parent.Parent, window.ViewerTabs[2].Parent.Parent);

                // One again: one tab, one pane, and the files still beside it.
                model.Layout = ViewerLayout.Single;
                Assert.Single(window.ViewerTabs);
                Assert.Single(Panes());
                Assert.Single(docking.Layout.Descendents().OfType<AvalonDock.Layout.LayoutAnchorable>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Pictures_of_one_name_in_two_cameras_folders_have_their_folder_in_the_title()
    {
        WpfTestHost.OnStaThread(() =>
        {
            Picture(Path.Combine(_folder, "cam1", "0001.png"));
            Picture(Path.Combine(_folder, "cam2", "0001.png"));
            Picture(Path.Combine(_folder, "cam1", "0002.png"));
            var model = Open(_folder);
            model.Match = new MatchRule { Mode = MatchMode.NameStart, Characters = 20 };

            Assert.Equal([@"cam1\0001.png", @"cam2\0001.png"], model.Tiles.Select(tile => tile.Title));

            model.Choose([model.Files[1]]);
            Assert.Equal(["0002.png", "Viewer 2"], model.Tiles.Select(tile => tile.Title));
        });
    }

    /// <summary>A toggle's text takes the toggle's colour, as a button's does - not black.</summary>
    [Fact]
    public void A_toolbar_toggles_label_is_in_the_toggles_colour()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(CameraPairs());
            var window = new ViewerWindow(model);

            try
            {
                var label = new System.Windows.Controls.TextBlock { Text = "Sync", Style = (Style)window.FindResource("ToolbarLabel") };
                var toggle = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Style = (Style)window.FindResource("ToolbarToggle"),
                    Content = label,
                };

                toggle.Measure(new Size(100, 30));
                toggle.Arrange(new Rect(0, 0, 100, 30));
                WpfTestHost.Settle(window.Dispatcher);

                Assert.Same(toggle.Foreground, label.Foreground);
                Assert.NotEqual(Colors.Black, ((SolidColorBrush)label.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<string> Texts(DependencyObject root)
    {
        var found = new List<string>();

        void Walk(DependencyObject element)
        {
            if (element is System.Windows.Controls.TextBlock { Text: { Length: > 0 } text })
                found.Add(text);

            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                Walk(child);
        }

        Walk(root);
        return found;
    }
}
