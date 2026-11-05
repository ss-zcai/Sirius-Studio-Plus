using System.IO;
using Sirius.Cameras.Basler;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// What a PC without the right pylon is told: every case a tester has met,
/// set up in a temporary folder rather than by installing pylons.
/// </summary>
public sealed class PylonRuntimeTests : IDisposable
{
    private static readonly Version Shipped = new(1, 3, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pylon-{Guid.NewGuid():N}");
    private readonly string _app;
    private readonly string _install;
    private readonly string _runtime;

    public PylonRuntimeTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        _install = Path.Combine(_root, "Basler", "pylon");
        _runtime = Path.Combine(_install, "Runtime", "x64");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void No_pylon_at_all_says_to_install_it()
    {
        var problem = Check(path: [], install: null);

        Assert.StartsWith("pylon is not installed on this PC.", problem);
        Assert.Contains("pylon 26.10 or later", problem);
    }

    /// <summary>pylon 7.3: installed, on the PATH, and with no .NET 8 helper.</summary>
    [Fact]
    public void An_older_pylon_without_the_helper_is_named_as_too_old()
    {
        Directory.CreateDirectory(_runtime);
        File.WriteAllText(Path.Combine(_runtime, "PylonBase_v7_3.dll"), string.Empty);

        var problem = Check(path: [_runtime], install: _install);

        Assert.Contains("older than this build needs", problem);
        Assert.Contains("PylonBase_v7_3.dll", problem);
        Assert.Contains("pylon 26.10 or later", problem);
    }

    [Fact]
    public void An_older_helper_is_named_with_both_versions()
    {
        Helper();

        var problem = Check(path: [_runtime], install: _install, version: new Version(1, 2, 0, 0));

        Assert.Contains("its .NET helper is 1.2", problem);
        Assert.Contains("made with 1.3 (pylon 26.10)", problem);
    }

    [Fact]
    public void A_pylon_not_on_the_path_says_so()
    {
        Helper();

        var problem = Check(path: [], install: _install);

        Assert.Contains("not on the PATH", problem);
    }

    [Fact]
    public void A_helper_that_will_not_load_says_so()
    {
        Helper();

        var problem = Check(path: [_runtime], install: _install, loads: false);

        Assert.Contains("would not load", problem);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(1, 4)]
    public void The_same_or_a_newer_pylon_is_fine(int major, int minor)
    {
        Helper();

        Assert.Null(Check(path: [_runtime], install: _install, version: new Version(major, minor, 0, 0)));
    }

    [Fact]
    public void A_path_entry_no_path_can_hold_is_skipped()
    {
        Helper();

        Assert.Null(Check(path: ["bad|folder\0", _runtime], install: _install));
    }

    private void Helper()
    {
        Directory.CreateDirectory(_runtime);
        File.WriteAllText(Path.Combine(_runtime, PylonRuntime.Helper), string.Empty);
    }

    private string? Check(string[] path, string? install, Version? version = null, bool loads = true) =>
        PylonRuntime.Check(_app, path, install, Shipped, "26.10", _ => version ?? Shipped, _ => loads);
}
