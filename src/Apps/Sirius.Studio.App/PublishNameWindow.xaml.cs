using System.Windows;
using System.Windows.Controls;
using Sirius.Core.Graph;

namespace Sirius.Studio.App;

/// <summary>
/// Asks for the name an output, setting or viewer is published under.
/// </summary>
/// <remarks>
/// Publishing is one step on the node, so it is undone as one: the dialog
/// changes nothing until OK, and then makes a single call.
/// </remarks>
public partial class PublishNameWindow : Window
{
    private readonly VisionGraph _graph;
    private readonly VisionNode _node;
    private readonly PublishedKind _kind;
    private readonly string _member;

    private PublishNameWindow(VisionGraph graph, VisionNode node, PublishedKind kind, string member, string what)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _graph = graph;
        _node = node;
        _kind = kind;
        _member = member;

        var current = Publishing.NameOf(node, kind, member);

        Title = current is null ? "Publish" : "Published Name";
        WhatText.Text = what;
        NameBox.Text = current?.Name ?? Publishing.Suggest(graph, DefaultName(node, kind, member));
        UnpublishButton.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>
    /// Asks for a name and publishes, renames or unpublishes accordingly.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public static bool Ask(Window? owner, VisionGraph graph, VisionNode node, PublishedKind kind, string member)
    {
        var window = new PublishNameWindow(graph, node, kind, member, Describe(node, kind, member))
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
    }

    /// <summary>What the name is offered as before anything is typed: the output's or setting's own label.</summary>
    internal static string DefaultName(VisionNode node, PublishedKind kind, string member) => kind switch
    {
        PublishedKind.Result => node.FindOutput(member)?.DisplayName ?? member,
        PublishedKind.Setting => node.Descriptor.Parameters.FirstOrDefault(p => p.Name == member)?.DisplayName ?? member,
        _ => node.Name,
    };

    private static string Describe(VisionNode node, PublishedKind kind, string member) => kind switch
    {
        PublishedKind.Result => $"Publish the output {node.Name} › {DefaultName(node, kind, member)} as a result a screen can show.",
        PublishedKind.Setting => $"Publish the setting {node.Name} › {DefaultName(node, kind, member)} so it can be changed from a screen.",
        _ => $"Publish {node.Name} as a view a screen can show, with everything drawn on it.",
    };

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        var problem = Publishing.Problem(_graph, NameBox.Text, _node, _kind, _member);

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var current = Publishing.NameOf(_node, _kind, _member);

        if (current is not null && current.Name == NameBox.Text.Trim())
        {
            DialogResult = false;
            return;
        }

        Publishing.Publish(_graph, _node, _kind, _member, NameBox.Text);
        DialogResult = true;
    }

    private void OnUnpublish(object sender, RoutedEventArgs e)
    {
        Publishing.Unpublish(_node, _kind, _member);
        DialogResult = true;
    }
}
