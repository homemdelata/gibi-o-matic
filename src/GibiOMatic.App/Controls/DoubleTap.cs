using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GibiOMatic.App.Controls;

/// <summary>
/// Attached behaviour: a double click on the control runs <c>DoubleTap.Command</c>. It also works on
/// buttons, whose first click has already run their own command.
/// </summary>
public static class DoubleTap
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(DoubleTap));

    static DoubleTap()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
            if (e.NewValue is ICommand)
            {
                control.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
            }
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && GetCommand(control) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
