using GibiOMatic.Library.Reading;

namespace GibiOMatic.App.Services;

/// <summary>
/// Reading positions and statuses of the open library, shared by the reader windows and the
/// library page, which refreshes "Continue reading" when a book is read or marked.
/// </summary>
public sealed class ReadingTracker(LibrarySession session)
{
    /// <summary>Null while no library is open.</summary>
    public ReadingStateStore? Store => session.Index is { } index ? new ReadingStateStore(index) : null;

    /// <summary>Raised (on any thread) with the id of the book whose status may have changed.</summary>
    public event EventHandler<string>? Changed;

    public void NotifyChanged(string bookId) => Changed?.Invoke(this, bookId);
}
