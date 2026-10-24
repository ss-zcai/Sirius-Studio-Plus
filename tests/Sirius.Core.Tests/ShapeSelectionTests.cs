using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The selection shared between a viewer and a table showing the same shapes.
/// </summary>
/// <remarks>
/// The rules are the whole design: what counts as "the same shapes" is the
/// port that produced them, not the panel reading them.
/// </remarks>
public class ShapeSelectionTests
{
    private static OutputPort Port(string name = "Shapes") =>
        new(name, name, typeof(object));

    [Fact]
    public void Nothing_is_selected_to_begin_with()
    {
        Assert.Null(new ShapeSelectionService().For(Port()));
    }

    /// <summary>
    /// The point of the whole thing: two panels reading one wire agree,
    /// because they key on the producer rather than on themselves.
    /// </summary>
    [Fact]
    public void Panels_reading_the_same_producer_see_the_same_selection()
    {
        var service = new ShapeSelectionService();
        var producer = Port();

        service.Select(producer, 4);

        Assert.Equal(4, service.For(producer));
    }

    [Fact]
    public void A_different_producer_keeps_its_own_selection()
    {
        var service = new ShapeSelectionService();
        var first = Port("A");
        var second = Port("B");

        service.Select(first, 2);
        service.Select(second, 7);

        Assert.Equal(2, service.For(first));
        Assert.Equal(7, service.For(second));
    }

    [Fact]
    public void Selecting_announces_which_producer_changed()
    {
        var service = new ShapeSelectionService();
        var producer = Port();
        var announced = new List<OutputPort>();

        service.Changed += (_, port) => announced.Add(port);

        service.Select(producer, 1);

        Assert.Single(announced);
        Assert.Same(producer, announced[0]);
    }

    /// <summary>
    /// Re-announcing an unchanged selection would bounce between the table and
    /// the viewer, each writing back what the other just set.
    /// </summary>
    [Fact]
    public void Selecting_what_is_already_selected_announces_nothing()
    {
        var service = new ShapeSelectionService();
        var producer = Port();
        service.Select(producer, 3);

        var announced = 0;
        service.Changed += (_, _) => announced++;

        service.Select(producer, 3);

        Assert.Equal(0, announced);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void Clearing_the_selection_leaves_nothing_selected(int? cleared)
    {
        var service = new ShapeSelectionService();
        var producer = Port();
        service.Select(producer, 5);

        service.Select(producer, cleared);

        Assert.Null(service.For(producer));
    }

    [Fact]
    public void Clearing_an_empty_selection_announces_nothing()
    {
        var service = new ShapeSelectionService();
        var announced = 0;
        service.Changed += (_, _) => announced++;

        service.Select(Port(), null);

        Assert.Equal(0, announced);
    }

    /// <summary>
    /// An unconnected input has no producer to key on, so there is nothing to
    /// share and nothing to crash on.
    /// </summary>
    [Fact]
    public void An_unconnected_input_selects_nothing()
    {
        var service = new ShapeSelectionService();

        service.Select(null, 3);

        Assert.Null(service.For(null));
    }
}
