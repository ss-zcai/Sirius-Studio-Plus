using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Vision;
using Sirius.Vision.Runtime;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Housekeeping of a station's saved pictures (F20c): days past those
/// kept, a most size and a free space left - oldest passed first, then
/// oldest failures - and a sample of what goes kept for records.
/// </summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-hk-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 11, 22, 15, 0, 0);

    public HousekeepingTests() => Directory.CreateDirectory(_folder);

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

    private string Station => Path.Combine(_folder, "Top");

    /// <summary>A station's picture of <paramref name="bytes"/>, with a 100-byte .sann, written an hour after its day began.</summary>
    private string Picture(DateTime day, long serial, string verdict, int bytes = 1000)
    {
        var folder = Path.Combine(Station, ImageSaving.DayFolder(day));
        Directory.CreateDirectory(folder);

        var stem = ImageSaving.FileStem(day.AddHours(1).AddSeconds(serial), serial, verdict);
        var picture = Path.Combine(folder, stem + ".tif");

        File.WriteAllBytes(picture, new byte[bytes]);
        File.WriteAllBytes(Path.ChangeExtension(picture, ".sann"), new byte[100]);

        foreach (var file in new[] { picture, Path.ChangeExtension(picture, ".sann") })
            File.SetLastWriteTime(file, day.AddHours(1));

        return picture;
    }

    private ImageHousekeeper Keeper(ImageSaving saving, Func<string, long>? free = null) =>
        new(saving, Station, free ?? (_ => long.MaxValue)) { Now = () => _now };

    private static double Gigabytes(long bytes) => bytes / (double)(1L << 30);

    [Fact]
    public void The_limits_are_kept_in_the_project_and_checked()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        var saving = new ImageSaving(ImageSavePolicy.All, KeepDays: 30, MaxGigabytes: 200, LeaveFreeGigabytes: 20, SamplePercent: 1);

        project.SetSaving(project.Stations[0], saving);

        var text = File.ReadAllText(project.FilePath);
        Assert.Contains("\"maxGB\": 200", text, StringComparison.Ordinal);
        Assert.Contains("\"leaveFreeGB\": 20", text, StringComparison.Ordinal);
        Assert.Contains("\"samplePercent\": 1", text, StringComparison.Ordinal);
        Assert.Equal(saving, Project.Load(project.FilePath).Project.Stations[0].Saving);

        Assert.Equal(100, saving.SampleEvery);
        Assert.True(saving.Samples(300));
        Assert.False(saving.Samples(301));
        Assert.Equal(0, new ImageSaving().SampleEvery);
        Assert.Equal(4, new ImageSaving(SamplePercent: 25).SampleEvery);
        Assert.Equal("Saves every image, TIF, 30 days, 200 GB at most", saving.Summary);

        Assert.NotNull((saving with { MaxGigabytes = -1 }).Problem());
        Assert.NotNull((saving with { SamplePercent = 101 }).Problem());
        Assert.NotNull((saving with { LeaveFreeGigabytes = double.NaN }).Problem());
    }

    /// <summary>
    /// A day past those kept goes whole - but for its sample, moved to Kept
    /// with its .sann - and a folder that is not a day is left alone.
    /// </summary>
    [Fact]
    public void An_old_day_goes_but_for_its_sample_moved_to_kept()
    {
        var old = _now.Date.AddDays(-5);

        foreach (var serial in new long[] { 99, 100, 101, 200 })
            Picture(old, serial, serial == 200 ? "FAIL" : "PASS");

        var today = Picture(_now.Date, 300, "PASS");
        Directory.CreateDirectory(Path.Combine(Station, "notes"));

        var result = Keeper(new ImageSaving(ImageSavePolicy.All, KeepDays: 2, SamplePercent: 1)).Run();

        Assert.False(Directory.Exists(Path.Combine(Station, ImageSaving.DayFolder(old))));
        Assert.True(File.Exists(today));
        Assert.True(Directory.Exists(Path.Combine(Station, "notes")));
        Assert.Equal((2, 2), (result.Deleted, result.Sampled));
        Assert.Empty(result.Problems);

        // 1 in 100 by number: 100 and 200, passed and failed alike, each with its .sann.
        var kept = Path.Combine(Station, ImageSaving.KeptFolder, ImageSaving.DayFolder(old));
        Assert.Equal(
            ["_000100_PASS.sann", "_000100_PASS.tif", "_000200_FAIL.sann", "_000200_FAIL.tif"],
            Directory.GetFiles(kept).Select(path => Path.GetFileName(path)[19..]).Order(StringComparer.Ordinal));

        // Kept is never a day: a second round leaves it.
        Keeper(new ImageSaving(ImageSavePolicy.All, KeepDays: 1, SamplePercent: 1)).Run();
        Assert.Equal(4, Directory.GetFiles(kept).Length);
    }

    /// <summary>
    /// Over the most size: the oldest passed pictures go first, then - only
    /// when no passed are left - the oldest failures, down to 90%.
    /// </summary>
    [Fact]
    public void Over_the_most_size_the_oldest_passed_go_first_then_failures()
    {
        var first = _now.Date.AddDays(-2);
        var second = _now.Date.AddDays(-1);

        // 1100 bytes each with its .sann: 8 pictures, 8800 bytes.
        var failedOld = Picture(first, 1, "FAIL");
        var passedOld = Picture(first, 2, "PASS");
        var failedNew = Picture(second, 3, "FAIL");
        var passed = new[] { Picture(second, 4, "PASS"), Picture(second, 5, "PASS"), Picture(second, 6, "PASS") };
        var errored = Picture(second, 7, "ERROR");
        var newest = Picture(second, 8, "PASS");

        // At most 7000 bytes: down to 6300, so 2500 must go - three pictures, all passed.
        var keeper = Keeper(new ImageSaving(ImageSavePolicy.All, MaxGigabytes: Gigabytes(7000)));
        Assert.Equal(8800, keeper.Measure());
        Assert.True(keeper.IsDue(long.MaxValue));

        var result = keeper.Run();

        Assert.Equal(3, result.Deleted);
        Assert.Equal(3300, result.Freed);
        Assert.False(File.Exists(passedOld));
        Assert.False(File.Exists(passed[0]));
        Assert.False(File.Exists(passed[1]));
        Assert.False(File.Exists(Path.ChangeExtension(passedOld, ".sann")));
        Assert.True(File.Exists(passed[2]));
        Assert.True(File.Exists(failedOld));
        Assert.Equal(5500, keeper.Size);
        Assert.False(keeper.IsDue(long.MaxValue));

        // Smaller still: the passed go before any failure, then the oldest failures.
        var tighter = Keeper(new ImageSaving(ImageSavePolicy.All, MaxGigabytes: Gigabytes(3000)));
        tighter.Run();

        Assert.False(File.Exists(passed[2]));
        Assert.False(File.Exists(newest));
        Assert.False(File.Exists(failedOld));
        Assert.True(File.Exists(failedNew));
        Assert.True(File.Exists(errored));

        // The first day, emptied, gone.
        Assert.False(Directory.Exists(Path.GetDirectoryName(failedOld)));
    }

    /// <summary>
    /// Under the free space left: pictures go until it and 1 GB more are
    /// free. The sample is moved, not deleted - it frees nothing on the
    /// drive, so more go to make the room.
    /// </summary>
    [Fact]
    public void Under_the_free_space_left_pictures_go_and_a_sample_is_moved()
    {
        var day = _now.Date.AddDays(-1);

        // Oldest first: 100 - in a 1% sample - then 101, 102, 103.
        var sample = Picture(day, 100, "PASS");
        var next = Picture(day, 101, "PASS");
        var after = Picture(day, 102, "PASS");
        var last = Picture(day, 103, "PASS");

        // 200 bytes under the 2 GB left free; cleared to 2000 bytes more: two pictures' worth.
        var floor = ImageSaving.Bytes(2);
        var keeper = new ImageHousekeeper(
            new ImageSaving(ImageSavePolicy.All, LeaveFreeGigabytes: 2, SamplePercent: 1),
            Station,
            _ => floor - 200)
        {
            Now = () => _now,
            FreeMargin = 2000,
        };

        Assert.True(keeper.IsDue(floor - 1));
        Assert.False(keeper.IsDue(floor));

        var result = keeper.Run();

        Assert.Equal((2, 1, 2200L), (result.Deleted, result.Sampled, result.Freed));
        Assert.False(File.Exists(sample));
        Assert.True(File.Exists(Path.Combine(Station, ImageSaving.KeptFolder, ImageSaving.DayFolder(day), Path.GetFileName(sample))));
        Assert.False(File.Exists(next));
        Assert.False(File.Exists(after));
        Assert.True(File.Exists(last));
    }

    /// <summary>A picture written in the last minute is left: its .sann may not be written yet.</summary>
    [Fact]
    public void A_picture_just_written_is_left()
    {
        var fresh = Picture(_now.Date, 1, "PASS");
        File.SetLastWriteTime(fresh, _now.AddSeconds(-10));

        Keeper(new ImageSaving(ImageSavePolicy.All, MaxGigabytes: Gigabytes(10))).Run();

        Assert.True(File.Exists(fresh));
    }

    /// <summary>
    /// Stopped by a full disk, a station looks again now and then, and saves
    /// once there is room - made by hand, say - rather than staying stopped
    /// until it is started again.
    /// </summary>
    [Fact]
    public void A_station_stopped_by_a_full_disk_saves_again_once_there_is_room()
    {
        long free = 10_000_000;
        using var recorder = new ImageRecorder(new ImageSaving(ImageSavePolicy.All), Path.Combine(_folder, "full"), "Top", freeSpace: _ => free);

        using var mat = new Mat(10, 10, DepthType.Cv8U, 1);
        using var image = ImageData.Adopt(mat.Clone());

        InspectionRecord Record(long serial) => new(
            new InspectionSnapshot("Top", serial, DateTime.Now, new Verdict(true, null, 1, 0), TimeSpan.Zero, 0, new Dictionary<string, object?>()),
            null,
            image,
            []);

        recorder.Record(Record(1));
        Assert.True(SpinWait.SpinUntil(() => recorder.Skipped == 1, TimeSpan.FromSeconds(10)));
        Assert.Contains("less than 1 GB free", recorder.Problem);

        // Still full: skipped without a look until the time comes.
        recorder.Record(Record(2));
        Assert.Equal(2, recorder.Skipped);

        free = 50L << 30;
        recorder.FullRecheck = TimeSpan.Zero;
        recorder.Record(Record(3));
        recorder.Dispose();

        Assert.Equal(1, recorder.Saved);
        Assert.Null(recorder.Problem);
    }

    /// <summary>A recorder with limits keeps them as it saves: a station over its size is brought under it.</summary>
    [Fact]
    public void A_recorder_with_a_most_size_brings_the_station_under_it()
    {
        var old = _now.Date.AddDays(-3);

        for (var serial = 1; serial <= 6; serial++)
            Picture(old, serial, "PASS", 1000);

        var saving = new ImageSaving(ImageSavePolicy.All, MaxGigabytes: Gigabytes(4000));
        using var recorder = new ImageRecorder(saving, Station, "Top", freeSpace: _ => long.MaxValue);

        recorder.WaitForHousekeeping();
        recorder.Dispose();

        Assert.NotNull(recorder.LastHousekeeping);
        Assert.True(Directory.GetFiles(Station, "*.tif", SearchOption.AllDirectories).Length <= 3);
    }
}
