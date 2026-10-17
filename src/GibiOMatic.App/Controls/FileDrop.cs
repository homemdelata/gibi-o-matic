using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace GibiOMatic.App.Controls;

/// <summary>
/// Attached behaviour: files and folders dropped on the control are passed to
/// <c>FileDrop.Command</c> as a <c>string[]</c> of local paths. While something is dragged over
/// it, the control gets the <c>dragover</c> class for styling.
/// </summary>
public static class FileDrop
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(FileDrop));

    static FileDrop()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.RemoveHandler(DragDrop.DropEvent, OnDrop);
            var enabled = e.NewValue is ICommand;
            DragDrop.SetAllowDrop(control, enabled);
            if (enabled)
            {
                control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
                control.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender is Control control)
        {
            control.Classes.Set("dragover", hasFiles);
        }

        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragover", false);
        }
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        control.Classes.Set("dragover", false);
        var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        var command = GetCommand(control);
        if (paths.Length > 0 && command?.CanExecute(paths) == true)
        {
            command.Execute(paths);
        }

        e.Handled = true;
    }
}
