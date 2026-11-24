using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.UI.Controls;

/// <summary>
/// A shape set as a sortable table.
/// </summary>
/// <remarks>
/// The columns are rebuilt whenever the data changes, because a shape set
/// carries whatever measurements its producer took - a blob finder with no
/// solidity filter has no solidity column. Declaring them in XAML would mean
/// a fixed set, with empty columns for everything a producer did not measure.
///
/// Rows are plain string arrays and cells bind by index. That keeps the
/// formatting decisions in <see cref="ShapeTable"/>, where they can be tested
/// without a window.
/// </remarks>
public sealed class ShapeTableControl : ContentControl
{
    private readonly DataGrid _grid;

    public ShapeTableControl()
    {
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };

        _grid.SelectionChanged += OnGridSelectionChanged;
        _grid.PreviewMouseLeftButtonDown += OnGridClicked;
        _grid.PreviewKeyDown += OnGridKeyDown;
        _grid.MouseDoubleClick += OnGridDoubleClicked;

        Content = _grid;
        Rebuild();
    }

    private bool _syncing;

    /// <summary>The rows shown, in the shapes' order.</summary>
    private IReadOnlyList<string[]> _rows = [];

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(ShapeSet),
        typeof(ShapeTableControl),
        new FrameworkPropertyMetadata(null, OnDataChanged));

    public ShapeSet? Data
    {
        get => (ShapeSet?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>What a pixel is worth, for sizes in a real unit; null lists them in pixels.</summary>
    public static readonly DependencyProperty CalibrationProperty = DependencyProperty.Register(
        nameof(Calibration),
        typeof(Calibration?),
        typeof(ShapeTableControl),
        new FrameworkPropertyMetadata(null, OnDataChanged));

    public Calibration? Calibration
    {
        get => (Calibration?)GetValue(CalibrationProperty);
        set => SetValue(CalibrationProperty, value);
    }

    /// <summary>
    /// A table made elsewhere, shown in place of <see cref="Data"/>'s: one
    /// row per shape, in the shapes' order, with columns of its own.
    /// </summary>
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table),
        typeof(ShapeTable),
        typeof(ShapeTableControl),
        new FrameworkPropertyMetadata(null, OnDataChanged));

    public ShapeTable? Table
    {
        get => (ShapeTable?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    /// <summary>A row double-clicked - or Enter pressed on it: the shape's index, counting from zero.</summary>
    public event EventHandler<int>? RowActivated;

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(ShapeTableControl),
        new FrameworkPropertyMetadata(
            -1,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedIndexChanged));

    /// <summary>
    /// The selected shape, counting from zero, or -1 for none: its place in
    /// the set, not the row it is on - which a sort moves.
    /// </summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ShapeTableControl)d).Rebuild();

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ShapeTableControl)d).ApplySelection();

    /// <summary>
    /// Moves the grid to the shared selection.
    /// </summary>
    /// <remarks>
    /// Guarded against feeding back: setting the grid's selection raises
    /// SelectionChanged, which would write the same value back out and, on a
    /// stale index, fight whatever set it.
    /// </remarks>
    private void ApplySelection()
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            var index = SelectedIndex;

            if (index < 0 || index >= _rows.Count)
            {
                _grid.UnselectAll();
                return;
            }

            // By the row, not by where it sits: sorted, the grid's tenth row
            // is not the tenth shape.
            _grid.SelectedItem = _rows[index];
            _grid.ScrollIntoView(_rows[index]);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Clears the selection when the click lands on nothing.
    /// </summary>
    /// <remarks>
    /// A DataGrid keeps its selection when the empty space below the rows is
    /// clicked, which leaves the matching shape highlighted on the image with
    /// no obvious way to put it down. Column headers are left alone, since a
    /// click there is a sort.
    /// </remarks>
    private void OnGridClicked(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;

        while (hit is not null and not DataGridRow and not DataGridColumnHeader)
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is null)
            _grid.UnselectAll();
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ShapeOf(_grid.SelectedItem) is var chosen and >= 0)
        {
            RowActivated?.Invoke(this, chosen);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        _grid.UnselectAll();
        e.Handled = true;
    }

    private void OnGridDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;

        while (hit is not null and not DataGridRow)
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is DataGridRow row && ShapeOf(row.Item) is var index and >= 0)
            RowActivated?.Invoke(this, index);
    }

    /// <summary>The shape a row is, by its place among the rows as made - not as sorted.</summary>
    private int ShapeOf(object? item)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i], item))
                return i;
        }

        return -1;
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            SelectedIndex = ShapeOf(_grid.SelectedItem);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Rebuild()
    {
        var table = Table ?? ShapeTable.From(Data, Calibration);

        // Columns are rebuilt only when they actually differ, so re-running a
        // graph does not throw away the sort the operator clicked.
        if (!SameColumns(table))
        {
            _grid.Columns.Clear();

            for (var i = 0; i < table.Columns.Count; i++)
            {
                _grid.Columns.Add(new DataGridTextColumn
                {
                    Header = table.Columns[i],
                    Binding = new Binding($"[{i}]"),
                    SortMemberPath = $"[{i}]",
                });
            }
        }

        _rows = table.Rows;
        _grid.ItemsSource = table.Rows;

        // A re-run replaces every row, which clears the grid's selection; the
        // shared one is still the truth, so put it back.
        ApplySelection();
    }

    private bool SameColumns(ShapeTable table)
    {
        if (_grid.Columns.Count != table.Columns.Count)
            return false;

        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (!Equals(_grid.Columns[i].Header, table.Columns[i]))
                return false;
        }

        return true;
    }
}
