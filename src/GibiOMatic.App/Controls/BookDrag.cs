using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace GibiOMatic.App.Controls;

/// <summary>
/// Attached behaviours for moving a book inside the app by dragging it: a control with
/// <c>BookDrag.BookId</c> starts the drag when pressed; a control with <c>BookDrag.DropCommand</c>
/// receives the book's id. While a book is over a target, the target gets the <c>dragover</c> class.
/// </summary>
public static class BookDrag
{
    private static readonly DataFormat<string> Format = DataFormat.CreateInProcessFormat<string>("gibiomatic-book");

    public static readonly AttachedProperty<string?> BookIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("BookId", typeof(BookDrag));

    public static readonly AttachedProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("DropCommand", typeof(BookDrag));

    static BookDrag()
    {
        BookIdProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            if (e.NewValue is string)
            {
                control.AddHandler(InputElement.PointerPressedEvent, OnPressed);
            }
        });

        DropCommandProperty.Changed.AddClassHandler<Control>((control, e) =>
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

    public static string? GetBookId(Control control) => control.GetValue(BookIdProperty);

    public static void SetBookId(Control control, string? value) => control.SetValue(BookIdProperty, value);

    public static ICommand? GetDropCommand(Control control) => control.GetValue(DropCommandProperty);

    public static void SetDropCommand(Control control, ICommand? value) => control.SetValue(DropCommandProperty, value);

    private static async void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || GetBookId(control) is not { } bookId || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Format, bookId));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var isBook = e.DataTransfer.Contains(Format);
        e.DragEffects = isBook ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Control control)
        {
            control.Classes.Set("dragover", isBook);
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
        if (e.DataTransfer.TryGetValue(Format) is { } bookId && GetDropCommand(control) is { } command && command.CanExecute(bookId))
        {
            command.Execute(bookId);
        }

        e.Handled = true;
    }
}
