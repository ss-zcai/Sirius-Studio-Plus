using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Sirius.Core.Graph;
using Sirius.Core.Runtime;

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

        // A number published as a setting can be limited for the line.
        if (kind == PublishedKind.Setting &&
            Tunable.For(node, current ?? new PublishedName(kind, member, string.Empty)) is { IsNumeric: true } setting)
        {
            LimitsPanel.Visibility = Visibility.Visible;
            LowestBox.Text = LimitText(current?.Lowest);
            HighestBox.Text = LimitText(current?.Highest);
            NodeRangeText.Text = $"The node itself allows {NodeRange(setting)}.";
        }

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

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Check();

    private void OnLimitsChanged(object sender, TextChangedEventArgs e) => Check();

    private bool HasLimits => LimitsPanel.Visibility == Visibility.Visible;

    private void Check()
    {
        // Null while the window is still being built.
        if (ProblemText is null || OkButton is null || LimitsPanel is null || LowestBox is null || HighestBox is null)
            return;

        var problem = Publishing.Problem(_graph, NameBox.Text, _node, _kind, _member);

        if (problem is null && HasLimits)
            problem = LimitsProblem(out _, out _);

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private string? LimitsProblem(out double? lowest, out double? highest)
    {
        var low = ReadLimit(LowestBox.Text, out lowest);
        var high = ReadLimit(HighestBox.Text, out highest);

        return !low || !high
            ? "A limit must be a number, or blank."
            : Publishing.LimitProblem(lowest, highest);
    }

    private static bool ReadLimit(string text, out double? limit)
    {
        limit = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
            return false;

        limit = value;
        return true;
    }

    private static string LimitText(double? limit) =>
        limit is { } value ? value.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;

    private static string NodeRange(Tunable setting) =>
        (double.IsFinite(setting.NodeMinimum), double.IsFinite(setting.NodeMaximum)) switch
        {
            (true, true) => $"{setting.Format(setting.NodeMinimum)} – {setting.Format(setting.NodeMaximum)}",
            (true, false) => $"{setting.Format(setting.NodeMinimum)} or more",
            (false, true) => $"{setting.Format(setting.NodeMaximum)} or less",
            _ => "any value",
        };

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var current = Publishing.NameOf(_node, _kind, _member);
        double? lowest = current?.Lowest, highest = current?.Highest;

        if (HasLimits && LimitsProblem(out lowest, out highest) is not null)
            return;

        if (current is not null && current.Name == NameBox.Text.Trim() &&
            current.Lowest == lowest && current.Highest == highest)
        {
            DialogResult = false;
            return;
        }

        // One change to the node, so one undo step.
        Publishing.Publish(_graph, _node, _kind, _member, NameBox.Text, lowest, highest);
        DialogResult = true;
    }

    private void OnUnpublish(object sender, RoutedEventArgs e)
    {
        Publishing.Unpublish(_node, _kind, _member);
        DialogResult = true;
    }
}
