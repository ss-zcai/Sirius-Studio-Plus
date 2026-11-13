using System.Windows.Input;

namespace Sirius.Builder;

/// <summary>
/// A toolbar or menu command that does what the document in front does:
/// Save saves the screen while the screen is in front, a workflow while a
/// workflow is - as any program with several documents open works.
/// </summary>
/// <remarks>
/// Enabled as its target is, and told when that changes - including when
/// the document in front changes (<see cref="Retarget"/>). With no target -
/// arranging, while a workflow is in front - it is disabled.
/// </remarks>
public sealed class ActiveCommand : ICommand
{
    private readonly Func<ICommand?> _target;
    private ICommand? _watched;

    public ActiveCommand(Func<ICommand?> target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        Retarget();
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>The command it stands for now, or null.</summary>
    public ICommand? Target => _target();

    /// <summary>The document in front changed: follow its command.</summary>
    public void Retarget()
    {
        var target = _target();

        if (!ReferenceEquals(target, _watched))
        {
            if (_watched is not null)
                _watched.CanExecuteChanged -= Relay;

            _watched = target;

            if (_watched is not null)
                _watched.CanExecuteChanged += Relay;
        }

        Relay(this, EventArgs.Empty);
    }

    public bool CanExecute(object? parameter) => _target()?.CanExecute(parameter) == true;

    public void Execute(object? parameter)
    {
        if (_target() is { } target && target.CanExecute(parameter))
            target.Execute(parameter);
    }

    private void Relay(object? sender, EventArgs e) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
