using System.Windows.Input;

namespace DS5BtHaptics.App.Ui.ViewModels;

/// <summary>A command that simply calls a method.</summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
