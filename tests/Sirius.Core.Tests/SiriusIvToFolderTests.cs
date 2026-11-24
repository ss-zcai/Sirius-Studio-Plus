using System.IO;
using System.Windows;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.IV;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius IV's To Folder (F20f): the pictures chosen - a matched set whole -
/// copied or moved into another folder, each with its .sann; names there
/// already asked about; the folders used offered again.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SiriusIvToFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-ivto-" + Guid.NewGuid().ToString("N"));

    public SiriusIvToFolderTests(WpfTestHost host)
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

    /// <summary>A station's folder: two days, a picture each with a .sann, and one without.</summary>
    private string Station()
    {
        var station = Path.Combine(_folder, "Top");

        Picture(Path.Combine(station, "2026-11-21", "0001_cam1_FAIL.png"), annotated: true);
        Picture(Path.Combine(station, "2026-11-21", "0001_cam2_FAIL.png"), annotated: true);
        Picture(Path.Combine(station, "2026-11-22", "0002_cam1_PASS.png"), annotated: false);

        return station;
    }

    private static void Picture(string path, bool annotated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var mat = new Mat(30, 40, DepthType.Cv8U, 1))
        {
            mat.SetTo(new MCvScalar(100));
            CvInvoke.Imwrite(path, mat);
        }

        if (annotated)
            new AnnotationFile { Image = Path.GetFileName(path), Width = 40, Height = 30, Station = "Top", Passed = false }.Save(AnnotationFile.PathFor(path));
    }

    private static BrowserViewModel Open(string folder, string? settings = null)
    {
        var model = new BrowserViewModel(settings);
        WpfTestHost.Wait(model.OpenAsync(folder));
        return model;
    }

    [Fact]
    public void Copied_pictures_take_their_sann_and_the_folder_is_offered_again()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var settings = Path.Combine(_folder, "iv.json");
            var model = Open(Station(), settings);
            var target = Path.Combine(_folder, "class A");

            var chosen = new[] { model.Files[0], model.Files[2] };
            var plan = BrowserViewModel.TransferPlan(chosen, target);

            // A picture with a .sann takes it; one without, only itself.
            Assert.Equal(2, plan[0].Files.Count);
            Assert.Single(plan[1].Files);

            var result = WpfTestHost.Wait(model.TransferAsync(plan, target, move: false, replace: true));

            Assert.Equal((2, 0), (result.Done, result.Skipped));
            Assert.Empty(result.Problems);
            Assert.Equal(
                ["0001_cam1_FAIL.png", "0001_cam1_FAIL.sann", "0002_cam1_PASS.png"],
                Directory.GetFiles(target).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal));

            // Copied, not moved: the originals and the list as they were.
            Assert.True(File.Exists(model.Files[0].Path));
            Assert.Equal(3, model.Files.Count);

            // Offered again, the latest first, and remembered.
            Assert.Equal([Path.GetFullPath(target)], model.RecentTargets);
            model.RememberTarget(Path.Combine(_folder, "class B"));
            model.RememberTarget(target);
            Assert.Equal([Path.GetFullPath(target), Path.GetFullPath(Path.Combine(_folder, "class B"))], model.RecentTargets);
            Assert.Equal(model.RecentTargets, new BrowserViewModel(settings).RecentTargets);
        });
    }

    [Fact]
    public void Names_there_already_are_replaced_or_left()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(Station());
            var target = Directory.CreateDirectory(Path.Combine(_folder, "class A")).FullName;
            File.WriteAllText(Path.Combine(target, "0001_cam1_FAIL.png"), "already here");

            var plan = BrowserViewModel.TransferPlan([model.Files[0], model.Files[1]], target);

            var left = BrowserViewModel.RunTransfer(plan, target, move: false, replace: false);
            Assert.Equal((1, 1), (left.Done, left.Skipped));
            Assert.Equal("already here", File.ReadAllText(Path.Combine(target, "0001_cam1_FAIL.png")));

            var replaced = BrowserViewModel.RunTransfer(plan, target, move: false, replace: true);
            Assert.Equal(2, replaced.Done);
            Assert.NotEqual("already here", File.ReadAllText(Path.Combine(target, "0001_cam1_FAIL.png")));
        });
    }

    /// <summary>
    /// Moved from the window, a matched set goes whole; measurements drawn
    /// and not saved are saved first and go with it; the move is asked
    /// about, and the list read again.
    /// </summary>
    [Fact]
    public void A_matched_set_moves_whole_with_its_unsaved_measurements()
    {
        var said = new List<string>();
        var previous = Sirius.UI.Dialog.Present;
        Sirius.UI.Dialog.Present = (_, message, _, _, _) =>
        {
            said.Add(message);
            return MessageBoxResult.OK;
        };

        try
        {
            WpfTestHost.OnStaThread(() =>
            {
                WpfTestHost.EnsureApplicationResources();
                var model = Open(Station());
                model.Match = new MatchRule { Mode = MatchMode.Pattern, Pattern = @"^(\d+)_cam\d+_" };
                var window = new ViewerWindow(model);
                var asked = new List<string>();
                window.ConfirmTransfer = (message, _) =>
                {
                    asked.Add(message);
                    return true;
                };

                try
                {
                    // The set of part 0001: two cameras' pictures, one line in the list.
                    var set = model.Files.Single(item => item.Name.StartsWith("0001", StringComparison.Ordinal));
                    Assert.Equal(2, set.Set.Count);
                    model.Choose([set]);
                    model.Measured(set.Set[1], [new MeasurementRecord("PointToPoint", [new Point2(1, 1), new Point2(20, 1)], "#F59E0B", null)], [], 40, 30);

                    var target = Path.Combine(_folder, "rejects");
                    WpfTestHost.Wait(window.ToFolderAsync(move: true, target));

                    Assert.Contains("Move 2 pictures, with their annotation files", Assert.Single(asked), StringComparison.Ordinal);
                    Assert.Contains("not saved", asked[0], StringComparison.Ordinal);

                    Assert.Equal(4, Directory.GetFiles(target).Length);
                    Assert.False(File.Exists(set.Set[0].Path));
                    Assert.Single(AnnotationFile.TryLoad(Path.Combine(target, "0001_cam2_FAIL.sann"), out _)!.Measurements);

                    // The list read again: only the third picture left.
                    Assert.Equal(["0002_cam1_PASS.png"], model.Files.Select(item => item.Name));
                    Assert.False(model.HasUnsavedChanges);
                    Assert.StartsWith("Moved 2 pictures to", said.Last(), StringComparison.Ordinal);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Sirius.UI.Dialog.Present = previous;
        }
    }

    [Fact]
    public void Copy_and_move_are_one_level_and_need_a_picture_chosen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(Station());
            var window = new ViewerWindow(model);

            try
            {
                model.RememberTarget(Path.Combine(_folder, "class A"));
                model.Choose([model.Files[0]]);

                // Straight to the folder dialog: no second level, even with folders used before.
                var move = new System.Windows.Controls.MenuItem { Tag = "move" };
                window.ReadyToFolder(move);

                Assert.True(move.IsEnabled);
                Assert.Empty(move.Items);

                model.Choose([]);
                window.ReadyToFolder(move);
                Assert.False(move.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
