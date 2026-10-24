using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Core.Tests;

/// <summary>
/// A disposable payload that tracks whether it was released, so tests can
/// assert on the engine's buffer ownership rules without involving OpenCV.
/// </summary>
public sealed class TrackedPayload : IDisposable
{
    private static int _liveCount;

    public TrackedPayload(int value)
    {
        Value = value;
        Interlocked.Increment(ref _liveCount);
    }

    public static int LiveCount => Volatile.Read(ref _liveCount);

    public int Value { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        Interlocked.Decrement(ref _liveCount);
    }

    public override string ToString() => $"Payload({Value}){(IsDisposed ? " disposed" : string.Empty)}";
}

/// <summary>Counts how many times it was evaluated, for dirty-tracking tests.</summary>
public abstract class CountingNode : VisionNode
{
    private int _evaluationCount;

    public int EvaluationCount => Volatile.Read(ref _evaluationCount);

    protected void RecordEvaluation() => Interlocked.Increment(ref _evaluationCount);

    public void ResetCount() => Volatile.Write(ref _evaluationCount, 0);
}

[VisionNode("Test Source", TypeKey = "test.source", Category = NodeCategory.Source)]
public sealed class TestSourceNode : CountingNode
{
    private int _seed = 1;

    public TestSourceNode()
    {
        ValueOut = AddOutput<TrackedPayload>("Value");
    }

    public OutputPort ValueOut { get; }

    [Param("Seed", Min = 0, Max = 100)]
    public int Seed
    {
        get => _seed;
        set => SetParam(ref _seed, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        RecordEvaluation();
        context.Set(ValueOut, new TrackedPayload(_seed));
    }
}

[VisionNode("Test Add", TypeKey = "test.add", Category = NodeCategory.Basic)]
public sealed class TestAddNode : CountingNode
{
    private int _addend = 1;

    public TestAddNode()
    {
        ValueIn = AddInput<TrackedPayload>("Value");
        ValueOut = AddOutput<TrackedPayload>("Value");
    }

    public InputPort ValueIn { get; }

    public OutputPort ValueOut { get; }

    [Param("Addend", Min = -100, Max = 100)]
    public int Addend
    {
        get => _addend;
        set => SetParam(ref _addend, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        RecordEvaluation();
        var input = context.Get<TrackedPayload>(ValueIn)!;
        context.Set(ValueOut, new TrackedPayload(input.Value + _addend));
    }
}

/// <summary>Always throws, to exercise the error path.</summary>
[VisionNode("Test Failing", TypeKey = "test.failing", Category = NodeCategory.Basic)]
public sealed class TestFailingNode : CountingNode
{
    public TestFailingNode()
    {
        ValueIn = AddInput<TrackedPayload>("Value");
        ValueOut = AddOutput<TrackedPayload>("Value");
    }

    public InputPort ValueIn { get; }

    public OutputPort ValueOut { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        RecordEvaluation();

        // Publish a value before throwing, so the test can confirm the
        // scheduler disposes output that never reached the cache.
        context.Set(ValueOut, new TrackedPayload(-1));
        throw new InvalidOperationException("Intentional test failure.");
    }
}

/// <summary>Emits a warning but still produces a result.</summary>
[VisionNode("Test Warning", TypeKey = "test.warning", Category = NodeCategory.Basic)]
public sealed class TestWarningNode : CountingNode
{
    public TestWarningNode()
    {
        ValueIn = AddInput<TrackedPayload>("Value");
        ValueOut = AddOutput<TrackedPayload>("Value");
    }

    public InputPort ValueIn { get; }

    public OutputPort ValueOut { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        RecordEvaluation();
        var input = context.Get<TrackedPayload>(ValueIn)!;
        context.Warn("Something was slightly off.");
        context.Set(ValueOut, new TrackedPayload(input.Value));
    }
}

/// <summary>A port carrying an unrelated type, for connection-validity tests.</summary>
[VisionNode("Test Text", TypeKey = "test.text", Category = NodeCategory.Source)]
public sealed class TestTextNode : CountingNode
{
    public TestTextNode()
    {
        TextOut = AddOutput<string>("Text");
        TextIn = AddInput<string>("Text", isRequired: false);
    }

    public OutputPort TextOut { get; }

    public InputPort TextIn { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        RecordEvaluation();
        context.Set(TextOut, "hello");
    }
}
