using System.Windows;
using Sirius.Studio.App.Controls;

namespace Sirius.IV;

/// <summary>
/// Sync View: the viewers shown in step - zoomed and scrolled as one, so
/// the same pixel sits in the same place in each, and the pointer's place
/// on one marked with a small cross on the others.
/// </summary>
/// <remarks>
/// A view is copied as it is - image pixels to screen - so matched pictures
/// of one size line up pixel for pixel; pictures of other sizes line up at
/// their top left corners.
/// </remarks>
public sealed class ViewSync
{
    private readonly List<ImageViewer> _viewers = [];
    private bool _isOn;
    private bool _copying;

    public IReadOnlyList<ImageViewer> Viewers => _viewers;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;

            _isOn = value;

            if (value)
            {
                // The first viewer's view, for them all.
                if (_viewers.Count > 0)
                    Copy(_viewers[0]);
            }
            else
            {
                foreach (var viewer in _viewers)
                    viewer.LinkedCursor = null;
            }
        }
    }

    public void Add(ImageViewer viewer)
    {
        if (_viewers.Contains(viewer))
            return;

        _viewers.Add(viewer);
        viewer.ViewChanged += OnViewChanged;
        viewer.PointerMovedOverImage += OnPointerMoved;

        // A viewer joining - the layout grew - starts where the others are.
        if (_isOn && _viewers.Count > 1)
            viewer.SetView(_viewers[0].View);
    }

    public void Remove(ImageViewer viewer)
    {
        if (!_viewers.Remove(viewer))
            return;

        viewer.ViewChanged -= OnViewChanged;
        viewer.PointerMovedOverImage -= OnPointerMoved;
        viewer.LinkedCursor = null;
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        if (_isOn && sender is ImageViewer source)
            Copy(source);
    }

    private void Copy(ImageViewer source)
    {
        if (_copying)
            return;

        _copying = true;

        try
        {
            foreach (var viewer in _viewers.Where(viewer => viewer != source))
                viewer.SetView(source.View);
        }
        finally
        {
            _copying = false;
        }
    }

    private void OnPointerMoved(object? sender, Point? point)
    {
        if (!_isOn)
            return;

        foreach (var viewer in _viewers)
            viewer.LinkedCursor = viewer == sender ? null : point;
    }
}
