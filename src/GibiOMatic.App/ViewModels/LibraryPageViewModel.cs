using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Libraries;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>The library (book grid). In F0 it only shows the empty state.</summary>
public sealed partial class LibraryPageViewModel(
    LibrarySession session,
    ILibraryService libraries,
    ILogger<LibraryPageViewModel> logger) : PageViewModel
{
    public override string Title => Strings.Nav_Library;

    public string LibraryPath => session.Current?.RootPath ?? string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int? BookCount { get; private set; }

    public bool IsEmpty => BookCount == 0;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        if (session.Current is not { } library)
        {
            return;
        }

        try
        {
            var count = await libraries.CountBooksAsync(library, ct);
            BookCount = count;
            Subtitle = count == 1
                ? Strings.Library_SubtitleOne
                : string.Format(CultureInfo.CurrentCulture, Strings.Library_SubtitleMany, count);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            logger.LogError(ex, "Could not read the index of library {LibraryId}", library.Id);
        }
    }
}
