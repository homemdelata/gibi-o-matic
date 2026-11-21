namespace GibiOMatic.Metadata.Assisted;

/// <summary>
/// The prompts and answer schemas of the assisted identification (SPEC §8.4), in English, as tested
/// on 2026-11-21 (RESEARCH.md, "Claude Code"). Raise <see cref="Version"/> when a change should make
/// the answers of an older prompt be asked again.
/// </summary>
public static class AssistedPrompts
{
    /// <summary>1: the prompts of the spike of 2026-11-21.</summary>
    public const int Version = 1;

    /// <summary>The user message of a book run.</summary>
    public const string BookRequest = "Identify this book.";

    /// <summary>The user message of a group run.</summary>
    public const string GroupRequest = "Identify this group of books.";

    private const string Common = """
        The book comes from a personal library of purchased digital comics (mostly Humble Bundle downloads; publishers like Image, IDW, BOOM!, Dynamite, Dark Horse) plus a few art books. Its own title is often useless (a file name such as "BTMMFDRS" or "SKYBOUND LOGO white.eps").

        The current folder holds book.json (the title and file name we have, format, page count, embedded metadata, the candidates our own algorithm found on ComicVine, often wrong, and the list of image files), cover.jpg and a few inner pages (page-NNN.jpg). The credits/indicia page usually gives the real title, publisher, date, the issues it collects, ISBN(s) and the edition (regular, limited, hardcover).

        ComicVine is reached only through the comicvine_search, comicvine_volume and comicvine_issue tools (ComicVine blocks web fetches, so never fetch comicvine.gamespot.com). A collected edition is usually an issue inside a volume, often a one-issue volume of its own, with a URL like https://comicvine.gamespot.com/<slug>/4000-<id>/; give that URL. If ComicVine has only the series (https://comicvine.gamespot.com/<slug>/4050-<id>/), give the series URL in comicVineSeriesUrl and leave comicVineUrl null. comicvine_issue saves the record's cover to a local file; Read it and compare it with cover.jpg when you are not sure of the edition.

        Web search is limited to a fixed list of sites. Only report what you checked; never invent. If unsure, lower the confidence and explain in doubts. If it is not a book at all, say so. Write in English, briefly.
        """;

    /// <summary>The quick search (SPEC §8.4): ComicVine, the cover and the inner pages.</summary>
    public const string Quick = "You identify one digital book.\n\n" + Common + """


        Steps:
        1. Read book.json, then cover.jpg and EVERY image listed in it, before searching.
        2. Work out the exact edition: series, volume or issue number, subtitle, publisher, year, kind (single issue, trade paperback, hardcover, omnibus, art book).
        3. Find its ComicVine record. Use the web only to confirm.

        Budget: at most 12 ComicVine tool calls, 6 web searches and 6 page fetches.
        """;

    /// <summary>The deep search (SPEC §8.4): the full record, with the source of every field.</summary>
    public const string Deep = "You identify one digital book and build its full record. This is the DEEP search: a quick search may have found only the series, or an edition whose cover differs from the book's.\n\n" + Common + """


        Goals:
        1. Pin down the exact edition from the book itself (the credits/indicia page).
        2. Find the series on ComicVine and, if it exists, the record of this exact edition. Compare its cover with cover.jpg; say whether they match and, if not, why (variant, limited edition, solicitation art, other printing).
        3. If ComicVine has no record of this edition, or to fill gaps, use the web: the publisher's site, League of Comic Geeks, Amazon, Open Library or Goodreads (search by the ISBN when you have it). Fill summary, release date, page count and credits.
        4. For every field, record in sources where it came from (for example "indicia page-004", "ComicVine 4000-92387", "imagecomics.com product page"). Leave a field null if you could not check it. Note the sites that failed.

        Budget: at most 15 ComicVine tool calls, 15 web searches and 15 page fetches.
        """;

    /// <summary>One run for a group of the Review by series (SPEC §8.4, "A run for a group").</summary>
    public const string Group = """
        You identify a GROUP of digital books from a personal library of purchased comics (mostly Humble Bundle downloads). The app grouped them because their titles look like the same series; the user approves the whole group at once, so your answer fills the group's table.

        The current folder holds group.json (each book: id, title, file name, page count, embedded ISBN, the app's own weak candidates, its cover file and, for one or two books, inner pages such as the credits/indicia), covers/<id>.jpg and pages/*.jpg. Read group.json first, then every cover and every inner page listed.

        Goals:
        1. Understand the series once: which ComicVine volume(s) the books belong to and HOW ComicVine catalogues these collected editions. Often each trade paperback is its own one-issue ComicVine volume (for example "The Boys: We Gotta Go Now", whose issue is named "Vol. 4") rather than an issue of the main series; sometimes the trades are numbered issues of a "TPB" volume. Use comicvine_search / comicvine_volume / comicvine_issue (ComicVine blocks web fetches, so never fetch comicvine.gamespot.com).
        2. For EACH book, find the record of its exact edition (the trade, not the single issues) and its number in the series. Read the ComicVine cover (comicvine_issue saves it to a local file) for the books where you are unsure, and say whether it matches the book's cover.
        3. Flag books that do not belong to the group (a different series, a spin-off mini-series, an art book), with the series they belong to, and report numbers that are missing or repeated.
        4. Use the web (limited to a fixed list of sites) only for books ComicVine lacks.
        5. Never invent: when you could not find an edition, set its URL to null with a short reason; the app will run a deeper search on that book alone.

        Budget: up to 40 ComicVine tool calls, 10 web searches, 10 page fetches. Write in English.
        """;

    /// <summary>The answer of a book run, quick or deep.</summary>
    public const string BookSchema = """
        {"type":"object","additionalProperties":false,"required":["identified","confidence","evidence"],"properties":{
        "identified":{"type":"boolean"},"isComic":{"type":["boolean","null"]},
        "title":{"type":["string","null"]},"series":{"type":["string","null"]},"number":{"type":["string","null"]},"subtitle":{"type":["string","null"]},
        "kind":{"type":["string","null"],"enum":["issue","tpb","hc","omnibus","artbook","book","other",null]},
        "publisher":{"type":["string","null"]},"imprint":{"type":["string","null"]},"releaseDate":{"type":["string","null"]},
        "pageCount":{"type":["integer","null"]},"isbn":{"type":["string","null"]},"otherIsbns":{"type":"array","items":{"type":"string"}},
        "collects":{"type":["string","null"]},"summary":{"type":["string","null"]},
        "credits":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["role","name"],"properties":{"role":{"type":"string"},"name":{"type":"string"}}}},
        "comicVineUrl":{"type":["string","null"],"description":"The edition's record, https://comicvine.gamespot.com/<slug>/4000-<id>/"},
        "comicVineSeriesUrl":{"type":["string","null"],"description":"The series volume, https://comicvine.gamespot.com/<slug>/4050-<id>/"},
        "coverMatches":{"type":["boolean","null"]},"coverExplanation":{"type":["string","null"]},
        "sources":{"type":"object","additionalProperties":{"type":"string"},"description":"field name -> where it came from"},
        "sitesThatFailed":{"type":"array","items":{"type":"string"}},
        "confidence":{"type":"number","minimum":0,"maximum":1},"evidence":{"type":"array","items":{"type":"string"}},"doubts":{"type":["string","null"]}}}
        """;

    /// <summary>The answer of a group run.</summary>
    public const string GroupSchema = """
        {"type":"object","additionalProperties":false,"required":["series","pattern","books","notInGroup","missingNumbers"],"properties":{
        "series":{"type":"object","additionalProperties":false,"required":["name","publisher"],"properties":{"name":{"type":"string"},"publisher":{"type":["string","null"]},"startYear":{"type":["integer","null"]},"comicVineVolumeUrl":{"type":["string","null"]}}},
        "pattern":{"type":"string","description":"How ComicVine catalogues this series' collected editions, in one or two sentences"},
        "books":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","belongs","number","comicVineIssueUrl","confidence","evidence"],"properties":{
          "id":{"type":"string"},"belongs":{"type":"boolean"},"number":{"type":["string","null"]},"title":{"type":["string","null"]},
          "kind":{"type":["string","null"],"enum":["issue","tpb","hc","omnibus","artbook","book","other",null]},"releaseDate":{"type":["string","null"]},
          "comicVineIssueUrl":{"type":["string","null"]},"coverMatches":{"type":["boolean","null"]},
          "confidence":{"type":"number","minimum":0,"maximum":1},"evidence":{"type":"array","items":{"type":"string"}},"doubts":{"type":["string","null"]}}}},
        "notInGroup":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","reason"],"properties":{"id":{"type":"string"},"reason":{"type":"string"},"suggestedSeries":{"type":["string","null"]}}}},
        "missingNumbers":{"type":"array","items":{"type":"string"}},"repeatedNumbers":{"type":"array","items":{"type":"string"}},"notes":{"type":["string","null"]}}}
        """;

    public static string SystemPrompt(AssistedLevel level) => level switch
    {
        AssistedLevel.Quick => Quick,
        AssistedLevel.Deep => Deep,
        _ => Group,
    };

    public static string Schema(AssistedLevel level) => level == AssistedLevel.Group ? GroupSchema : BookSchema;

    public static string Request(AssistedLevel level) => level == AssistedLevel.Group ? GroupRequest : BookRequest;
}
