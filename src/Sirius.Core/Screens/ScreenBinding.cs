using Sirius.Core.Graph;

namespace Sirius.Core.Screens;

/// <summary>
/// Something a widget can show from a station's recipe: a name already
/// published, or an output, setting or viewer that would be published when
/// chosen.
/// </summary>
/// <param name="Node">The node it is on.</param>
/// <param name="Kind">What it would be published as.</param>
/// <param name="Member">The output's or setting's name; empty for a view.</param>
/// <param name="Name">Its published name, or null when it is not published yet.</param>
public sealed record BindingChoice(VisionNode Node, PublishedKind Kind, string Member, string? Name)
{
    public bool IsPublished => Name is not null;

    /// <summary>"Diameter", or "Measure 1 › Distance" for one not yet published.</summary>
    public string Display => Name ?? Source;

    /// <summary>Where it comes from: "Measure 1 › Distance", "Image Viewer 1".</summary>
    public string Source => Kind switch
    {
        PublishedKind.View => Node.Name,
        PublishedKind.Result => $"{Node.Name} › {Node.FindOutput(Member)?.DisplayName ?? Member}",
        _ => $"{Node.Name} › {Node.Descriptor.Parameters.FirstOrDefault(p => p.Name == Member)?.DisplayName ?? Member}",
    };

    public override string ToString() => Display;
}

/// <summary>
/// What a widget can be bound to in a recipe, and binding it - publishing
/// on the way if need be (runtime design, section 0: binding labels the
/// output; nothing is wired).
/// </summary>
public static class ScreenBinding
{
    /// <summary>
    /// What a widget of <paramref name="kind"/> can show from
    /// <paramref name="recipe"/>: published names first, by name, then the
    /// rest by node.
    /// </summary>
    /// <remarks>
    /// A picture is a viewer, shown with what is drawn on it, or an image
    /// output; a value is any other output; a setting is a setting shown in
    /// the properties panel. Only the recipe's own graph publishes, so a
    /// sub-graph's insides are not offered.
    /// </remarks>
    public static IReadOnlyList<BindingChoice> Choices(VisionGraph recipe, WidgetKind kind)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var choices = new List<BindingChoice>();

        foreach (var node in recipe.Nodes)
        {
            switch (kind)
            {
                case WidgetKind.Image:
                    if (IsViewer(node))
                        choices.Add(Choice(node, PublishedKind.View, string.Empty));

                    foreach (var output in node.Outputs.Where(output => IsImage(output.DataType)))
                        choices.Add(Choice(node, PublishedKind.Result, output.Name));

                    break;

                case WidgetKind.Value:
                    foreach (var output in node.Outputs.Where(output => !IsImage(output.DataType)))
                        choices.Add(Choice(node, PublishedKind.Result, output.Name));

                    break;

                case WidgetKind.Setting:
                    foreach (var setting in node.Descriptor.Parameters.Where(p => !p.Attribute.Hidden))
                        choices.Add(Choice(node, PublishedKind.Setting, setting.Name));

                    break;
            }
        }

        return choices
            .OrderBy(choice => choice.IsPublished ? 0 : 1)
            .ThenBy(choice => choice.IsPublished ? choice.Name : null, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The name to bind to <paramref name="choice"/>: its published name, or
    /// one it is published under now - its own label, made unique.
    /// </summary>
    /// <returns>The name, and whether the recipe was changed to give it one.</returns>
    public static (string Name, bool Published) Bind(VisionGraph recipe, BindingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(choice);

        if (Publishing.NameOf(choice.Node, choice.Kind, choice.Member) is { } existing)
            return (existing.Name, false);

        var label = choice.Kind switch
        {
            PublishedKind.View => choice.Node.Name,
            PublishedKind.Result => choice.Node.FindOutput(choice.Member)?.DisplayName ?? choice.Member,
            _ => choice.Node.Descriptor.Parameters.FirstOrDefault(p => p.Name == choice.Member)?.DisplayName ?? choice.Member,
        };

        var name = Publishing.Suggest(recipe, label);
        Publishing.Publish(recipe, choice.Node, choice.Kind, choice.Member, name);
        return (name, true);
    }

    private static BindingChoice Choice(VisionNode node, PublishedKind kind, string member) =>
        new(node, kind, member, Publishing.NameOf(node, kind, member)?.Name);

    /// <summary>A node a picture is shown in, by its type key - Core does not know the viewer's type.</summary>
    private static bool IsViewer(VisionNode node) => node.Descriptor.TypeKey == "output.viewer";

    private static bool IsImage(Type type) => type.Name == "ImageData";
}
