using Sample.Drivers;
using Sirius.Plc;
using Xunit;

namespace Sample.Drivers.Tests;

/// <summary>
/// What every driver must do, checked: the conformance check, run here
/// against the sample's in-memory device. For a real driver, point it at a
/// PLC on the bench - addresses nothing else uses, since they are written.
/// </summary>
public sealed class ConformanceTests
{
    private static PlcDriverCheckPlan Plan() => new()
    {
        WriteAndReadBack =
        {
            new(new PlcVariable("R0.0", PlcType.Bool), true),
            new(new PlcVariable("R1", PlcType.Byte), (byte)200),
            new(new PlcVariable("R2", PlcType.Int16), (short)-1234),
            new(new PlcVariable("R3", PlcType.UInt16), (ushort)54321),
            new(new PlcVariable("R4", PlcType.Int32), 123456789),
            new(new PlcVariable("R6", PlcType.UInt32), 4000000000u),
            new(new PlcVariable("R8", PlcType.Real), 3.25f),
            new(new PlcVariable("R10", PlcType.LReal), 1.0 / 3),
            new(new PlcVariable("R20", PlcType.String, 10), "PART-42"),
        },
        ReadOnly = { new PlcVariable("R100", PlcType.Int16) },
        Missing = new PlcVariable("R999", PlcType.Int32),   // runs past the end of the device
    };

    private static PlcConnection Connection(string device, string wordOrder = "big") =>
        new("sample-registers", device, new Dictionary<string, string> { ["wordOrder"] = wordOrder, ["answerMs"] = "1" });

    [Theory]
    [InlineData("big")]
    [InlineData("little")]
    public async Task The_driver_passes_the_conformance_check(string wordOrder)
    {
        var results = await PlcDriverCheck.RunAsync(
            () => new RegisterDriverFactory().Create(Connection($"check-{wordOrder}", wordOrder)),
            Plan());

        Assert.All(results, result => Assert.True(result.Passed, result.ToString()));
    }

    [Fact]
    public async Task Word_order_decides_where_a_32_bit_value_high_word_goes()
    {
        var device = RegisterDevice.Named("order");
        await using var driver = new RegisterDriverFactory().Create(Connection("order", "little"));
        await driver.ConnectAsync(default);

        await driver.WriteAsync(new PlcVariable("R0", PlcType.Int32), 0x00010002, default);

        Assert.Equal(new ushort[] { 0x0002, 0x0001 }, device.Read(0, 2));
    }

    [Theory]
    [InlineData("R10", PlcType.Bool, "R10 is a whole register: a Bool needs a bit of one, as R10.0.")]
    [InlineData("R10.3", PlcType.Int16, "R10.3 is a bit: a Int16 needs a whole register, as R10.")]
    [InlineData("R10.16", PlcType.Bool, "R10.16: a register has bits 0 to 15.")]
    [InlineData("D100", PlcType.Int16, "'D100' is not a register: write R10 for a register, R10.3 for a bit of one.")]
    public void A_wrong_address_is_said_in_words(string address, PlcType type, string problem)
    {
        var driver = new RegisterDriverFactory().Create(Connection("words"));
        Assert.Equal(problem, driver.AddressProblem(new PlcVariable(address, type)));
    }

    [Fact]
    public async Task A_pulled_cable_drops_the_connection_and_it_comes_back()
    {
        var device = RegisterDevice.Named("cable");
        await using var driver = new RegisterDriverFactory().Create(Connection("cable"));
        await driver.ConnectAsync(default);

        device.IsUnplugged = true;
        await Assert.ThrowsAsync<PlcException>(() => driver.ReadAsync(new PlcVariable("R0", PlcType.Int16), default));
        Assert.False(driver.IsConnected);

        device.IsUnplugged = false;
        await driver.ConnectAsync(default);
        Assert.True(driver.IsConnected);
    }

    [Fact]
    public void Settings_that_cannot_work_are_refused_in_words()
    {
        var connection = new PlcConnection("sample-registers", "x", new Dictionary<string, string> { ["wordOrder"] = "middle", ["answerMs"] = "1" });
        var refused = Assert.Throws<PlcException>(() => new RegisterDriverFactory().Create(connection));
        Assert.Equal("The word order 'middle' is not big or little.", refused.Message);
    }
}
