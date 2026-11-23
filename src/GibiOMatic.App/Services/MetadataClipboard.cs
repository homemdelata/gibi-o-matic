using GibiOMatic.Core.Domain;

namespace GibiOMatic.App.Services;

/// <summary>
/// The book whose metadata was copied ("Copy metadata", SPEC §7), kept in memory for the open
/// library only (not the system clipboard), and the book a "Paste metadata…" is waiting for while
/// the Editor opens.
/// </summary>
public sealed class MetadataClipboard(LibrarySession session)
{
    private BookMetadata? _copied;
    private string? _library;
    private string? _pendingPaste;

    /// <summary>The copied book, while its library is the open one.</summary>
    public BookMetadata? Copied => _copied is not null && session.Current?.RootPath == _library ? _copied : null;

    public void Copy(BookMetadata book)
    {
        _copied = book;
        _library = session.Current?.RootPath;
    }

    /// <summary>The Editor opening for <paramref name="bookId"/> shows the paste panel.</summary>
    public void RequestPaste(string bookId) => _pendingPaste = bookId;

    /// <summary>True once, when a paste was requested for this book.</summary>
    public bool TakePasteRequest(string bookId)
    {
        if (_pendingPaste != bookId)
        {
            return false;
        }

        _pendingPaste = null;
        return true;
    }
}
