using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Enter committing a text box, and Escape putting back what was there.
/// </summary>
/// <remarks>
/// Parameter bindings commit on lost focus so that a half-typed number never
/// re-runs the graph. That is the right trade, but it means a value typed and
/// left alone does nothing - which reads as the application ignoring you.
/// </remarks>
public class CommitOnEnterTests
{
    private sealed class Holder
    {
        public string Value { get; set; } = "before";
    }

    /// <summary>A text box bound the way a parameter editor is.</summary>
    private static (TextBox Box, Holder Source) Bound(bool acceptsReturn = false)
    {
        var source = new Holder();
        var box = new TextBox { DataContext = source, AcceptsReturn = acceptsReturn };

        box.SetBinding(
            TextBox.TextProperty,
            new Binding(nameof(Holder.Value))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            });

        return (box, source);
    }

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Without it, this is the behaviour being complained about.</summary>
    [Fact]
    public void Typing_alone_does_not_reach_the_source()
    {
        OnStaThread(() =>
        {
            var (box, source) = Bound();

            box.Text = "after";

            Assert.Equal("before", source.Value);
        });
    }

    [Fact]
    public void Enter_commits_the_value()
    {
        OnStaThread(() =>
        {
            var (box, source) = Bound();
            box.Text = "after";

            CommitOnEnter.Apply(box, Key.Enter);

            Assert.Equal("after", source.Value);
        });
    }

    [Fact]
    public void Escape_puts_back_what_was_there()
    {
        OnStaThread(() =>
        {
            var (box, source) = Bound();
            box.Text = "a mistake";

            CommitOnEnter.Apply(box, Key.Escape);

            Assert.Equal("before", source.Value);
            Assert.Equal("before", box.Text);
        });
    }

    /// <summary>
    /// The kernel of a 2D Filter is typed over several lines, so Enter there
    /// is a newline and must not be taken as "done".
    /// </summary>
    [Fact]
    public void Enter_is_left_alone_in_a_box_that_takes_newlines()
    {
        OnStaThread(() =>
        {
            var (box, source) = Bound(acceptsReturn: true);
            box.Text = "after";

            CommitOnEnter.Apply(box, Key.Enter);

            Assert.Equal("before", source.Value);
        });
    }

    [Fact]
    public void Any_other_key_commits_nothing()
    {
        OnStaThread(() =>
        {
            var (box, source) = Bound();
            box.Text = "after";

            CommitOnEnter.Apply(box, Key.A);

            Assert.Equal("before", source.Value);
        });
    }

    /// <summary>An unbound box has nothing to commit, and must not throw.</summary>
    [Fact]
    public void An_unbound_box_is_ignored()
    {
        OnStaThread(() =>
        {
            var box = new TextBox { Text = "loose" };

            CommitOnEnter.Apply(box, Key.Enter);
            CommitOnEnter.Apply(box, Key.Escape);

            Assert.Equal("loose", box.Text);
        });
    }
}
