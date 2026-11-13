using System.Globalization;

namespace Sirius.Plc;

/// <summary>An address to write in the PLC under test and read back - one the PLC's program does not use.</summary>
public sealed record PlcCheckValue(PlcVariable Variable, object Value);

/// <summary>What a driver is checked against: addresses in the PLC on the bench, safe to read and write.</summary>
public sealed class PlcDriverCheckPlan
{
    /// <summary>Written, then read back: one of each type the driver supports, at addresses nothing else uses.</summary>
    public IList<PlcCheckValue> WriteAndReadBack { get; } = [];

    /// <summary>Only read: inputs, and anything the PLC's program owns.</summary>
    public IList<PlcVariable> ReadOnly { get; } = [];

    /// <summary>An address written correctly that this PLC has not got - DB999.0 on an S7 with no DB999 - which must fail with a <see cref="PlcException"/>.</summary>
    public PlcVariable? Missing { get; set; }

    /// <summary>Text that is no address of this brand at all, which <see cref="IPlcDriver.AddressProblem"/> must say is wrong.</summary>
    public string? Nonsense { get; set; } = "not an address";

    /// <summary>How long each step may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>One check, and how it went.</summary>
public sealed record PlcCheckResult(string Check, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")}  {Check}{(Detail.Length > 0 ? $" - {Detail}" : string.Empty)}";
}

/// <summary>
/// What every driver must do, checked against a real PLC (or the simulated
/// one): connect; know its own addresses; read and write each type and get
/// back what was written, as the type it was asked for; read many at once;
/// refuse a bad address with a <see cref="PlcException"/>; stop when
/// cancelled; take several requests at once; connect again after
/// disconnecting.
/// </summary>
/// <remarks>
/// Run from a test, or a console program, with the PLC on the bench:
/// <code>
/// var results = await PlcDriverCheck.RunAsync(() => new SiemensS7Factory().Create(connection), plan);
/// Assert.All(results, result => Assert.True(result.Passed, result.ToString()));
/// </code>
/// What cannot be checked by a program - a cable pulled out, a PLC switched
/// off - is in the guide's checklist (<c>docs/plc-drivers.md</c>).
/// </remarks>
public static class PlcDriverCheck
{
    public static async Task<IReadOnlyList<PlcCheckResult>> RunAsync(Func<IPlcDriver> make, PlcDriverCheckPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(make);
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<PlcCheckResult>();
        IPlcDriver driver;

        try
        {
            driver = make();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            results.Add(new("Is made", false, Threw(ex)));
            return results;
        }

        var variables = plan.WriteAndReadBack.Select(value => value.Variable).Concat(plan.ReadOnly).ToArray();

        try
        {
            // ---- connecting ----

            var connected = await Check(results, "Connects", async () =>
            {
                using var limit = Limit(plan, cancellationToken);
                await driver.ConnectAsync(limit.Token).ConfigureAwait(false);

                if (!driver.IsConnected)
                    return "IsConnected is false after ConnectAsync returned.";

                return string.IsNullOrWhiteSpace(driver.Description) ? "Description is empty: the log would say nothing about which PLC." : null;
            }).ConfigureAwait(false);

            if (!connected)
                return results;

            // ---- addresses ----

            await Check(results, "Knows its own addresses", () =>
            {
                var refused = variables.Select(variable => (variable, problem: driver.AddressProblem(variable)))
                    .Where(pair => pair.problem is not null)
                    .ToArray();

                if (refused.Length > 0)
                    return Task.FromResult<string?>($"refuses {refused[0].variable}: {refused[0].problem}");

                if (plan.Nonsense is { } nonsense && driver.AddressProblem(new PlcVariable(nonsense, PlcType.Bool)) is null)
                    return Task.FromResult<string?>($"takes '{nonsense}' as an address; AddressProblem should say what is wrong with it.");

                return Task.FromResult<string?>(null);
            }).ConfigureAwait(false);

            // ---- reading and writing ----

            foreach (var (variable, value) in plan.WriteAndReadBack)
            {
                await Check(results, $"Writes and reads back {variable}", async () =>
                {
                    var written = PlcValues.Convert(value, variable.Type);

                    using var limit = Limit(plan, cancellationToken);
                    await driver.WriteAsync(variable, written, limit.Token).ConfigureAwait(false);
                    var read = await driver.ReadAsync(variable, limit.Token).ConfigureAwait(false);

                    if (read?.GetType() != PlcValues.ClrType(variable.Type))
                        return $"read back a {read?.GetType().Name ?? "null"}, not the {PlcValues.ClrType(variable.Type).Name} a {variable.Type} is carried as.";

                    return Same(written, read, variable.Type) ? null : $"wrote {PlcValues.Text(written)}, read back {PlcValues.Text(read)}.";
                }).ConfigureAwait(false);
            }

            foreach (var variable in plan.ReadOnly)
            {
                await Check(results, $"Reads {variable}", async () =>
                {
                    using var limit = Limit(plan, cancellationToken);
                    var read = await driver.ReadAsync(variable, limit.Token).ConfigureAwait(false);

                    return read?.GetType() == PlcValues.ClrType(variable.Type)
                        ? null
                        : $"read a {read?.GetType().Name ?? "null"}, not the {PlcValues.ClrType(variable.Type).Name} a {variable.Type} is carried as.";
                }).ConfigureAwait(false);
            }

            if (variables.Length > 0)
            {
                await Check(results, "Reads many at once", async () =>
                {
                    using var limit = Limit(plan, cancellationToken);
                    var read = await driver.ReadManyAsync(variables, limit.Token).ConfigureAwait(false);

                    if (read.Count != variables.Length)
                        return $"asked for {variables.Length}, got {read.Count}.";

                    for (var i = 0; i < variables.Length; i++)
                    {
                        if (read[i]?.GetType() != PlcValues.ClrType(variables[i].Type))
                            return $"{variables[i]} came back as a {read[i]?.GetType().Name ?? "null"}.";
                    }

                    return null;
                }).ConfigureAwait(false);

                await Check(results, "Takes several requests at once", async () =>
                {
                    using var limit = Limit(plan, cancellationToken);
                    var reads = Enumerable.Range(0, 8).Select(_ => driver.ReadAsync(variables[0], limit.Token));
                    await Task.WhenAll(reads).ConfigureAwait(false);
                    return null;
                }).ConfigureAwait(false);

                await Check(results, "Stops when cancelled", async () =>
                {
                    using var cancelled = new CancellationTokenSource();
                    await cancelled.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await driver.ReadAsync(variables[0], cancelled.Token).ConfigureAwait(false);
                        return "read with a cancelled token instead of stopping.";
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }).ConfigureAwait(false);
            }

            // ---- what goes wrong ----

            if (plan.Missing is { } missing)
            {
                await Check(results, $"Refuses {missing} with a PlcException", async () =>
                {
                    using var limit = Limit(plan, cancellationToken);

                    try
                    {
                        await driver.ReadAsync(missing, limit.Token).ConfigureAwait(false);
                        return "read it without complaint.";
                    }
                    catch (PlcException ex)
                    {
                        return string.IsNullOrWhiteSpace(ex.Message) ? "the PlcException says nothing." : null;
                    }
                }).ConfigureAwait(false);
            }

            await Check(results, "Disconnects, and connects again", async () =>
            {
                await driver.DisconnectAsync().ConfigureAwait(false);

                if (driver.IsConnected)
                    return "IsConnected is still true after DisconnectAsync.";

                if (variables.Length > 0)
                {
                    try
                    {
                        using var quick = Limit(plan, cancellationToken);
                        await driver.ReadAsync(variables[0], quick.Token).ConfigureAwait(false);
                        return "read while disconnected; it should throw a PlcException saying it is not connected.";
                    }
                    catch (PlcException)
                    {
                        // As it should.
                    }
                }

                using var limit = Limit(plan, cancellationToken);
                await driver.ConnectAsync(limit.Token).ConfigureAwait(false);

                if (!driver.IsConnected)
                    return "IsConnected is false after connecting again.";

                if (variables.Length > 0)
                    await driver.ReadAsync(variables[0], limit.Token).ConfigureAwait(false);

                return null;
            }).ConfigureAwait(false);
        }
        finally
        {
            await Check(results, "Lets go", async () =>
            {
                await driver.DisposeAsync().ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Runs one check; an exception is its failure, said in words.</summary>
    private static async Task<bool> Check(List<PlcCheckResult> results, string name, Func<Task<string?>> check)
    {
        string? failure;

        try
        {
            failure = await check().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failure = "took longer than the plan's timeout.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = Threw(ex);
        }

        results.Add(new PlcCheckResult(name, failure is null, failure ?? string.Empty));
        return failure is null;
    }

    private static string Threw(Exception ex) =>
        ex is PlcException ? ex.Message : $"threw {ex.GetType().Name}: {ex.Message} - a driver says what went wrong with a PlcException.";

    private static CancellationTokenSource Limit(PlcDriverCheckPlan plan, CancellationToken cancellationToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(plan.Timeout);
        return limit;
    }

    private static bool Same(object written, object? read, PlcType type) => type switch
    {
        PlcType.Real => read is float number && Math.Abs(number - (float)written) <= 1e-5f * Math.Max(1, Math.Abs((float)written)),
        PlcType.LReal => read is double number && Math.Abs(number - (double)written) <= 1e-12 * Math.Max(1, Math.Abs((double)written)),
        _ => string.Equals(PlcValues.Text(written), PlcValues.Text(read), StringComparison.Ordinal),
    };
}
