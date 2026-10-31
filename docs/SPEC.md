# Gibi-o-matic — Specification

> Version 0.2 · 2026-10-17 · status: draft for review
> Interface prototype: Claude Design (the author's private link)

## 1. Vision

Gibi-o-matic is a desktop app for people who buy digital comics (Humble Bundle, DRM-free stores, etc.) and want to:

1. **keep the originals** safe, with correct metadata;
2. **read comfortably** on a tablet (through a server such as Komga) and on e-readers (Kindle);
3. **without repetitive manual work**: metadata fetched automatically, reviewed in a few clicks, and reading copies generated on their own.

### 1.1 Goals
- Replace Calibre in the comics workflow, with an interface focused only on that.
- Never modify the original files.
- Work on Windows from the first version; Linux later (macOS if it comes for free).
- Be usable by non-technical people: good defaults, advanced options tucked away.
- Be publicly distributable in the future (permissive dependency licenses, the user's own API keys, interface in pt-BR and English).

### 1.2 Out of scope (for now)
- A full comic reader (Komga and the Kindle do that). There is a simple reader, only to check which edition a book is (F3.5, user decision of 2026-10-18): pages, zoom, thumbnails and text EPUB, without annotations or progress sync.
- A store, automatic download of purchases, DRM removal.
- Reflowable text books as a focus. They can be kept in the archive and sent to destinations that accept them (section 9.1), but with no special conversions in the first version.
- Server/web mode.
- Migration from Calibre: the collection will be rebuilt from freshly downloaded originals (section 10).

## 2. Concepts

| Term | Meaning |
|---|---|
| **Archive** (library) | Root folder holding the originals and the metadata. In the author's case, on Google Drive. |
| **Book** | One unit of the collection: single issue (#1), collected edition (Vol. 1/TPB), omnibus, artbook. It has 1 main original and, optionally, extra formats. |
| **Original** | The file as downloaded (PDF, CBZ, CBR, EPUB). Read-only after import. |
| **Inbox** | Watched folder where new downloads arrive. |
| **Review** | Queue of books whose metadata needs human confirmation. |
| **Destination** | A place reading copies are generated for: a Komga server, a Kindle, etc. Each destination has its own type, settings and rules. |
| **Metadata source** | A service queried to fill in metadata (Open Library, ComicVine…). |

## 3. Overall flow

```
INBOX ──import──► ARCHIVE (original + metadata.json) ──generate──► DESTINATIONS
  │                     ▲          │                                ├─ Komga (CBZ + ComicInfo.xml)
  └── automatic lookup ─┘          └── Review / Editor              └─ Kindle (AZW3 via KCC)
```

1. A file arrives in the Inbox.
2. The app identifies the book (embedded metadata + online sources) and computes a confidence.
3. High confidence: the book goes straight into the library. Low confidence: it goes to the Review.
4. Approving or editing metadata marks the destination copies as outdated.
5. Automatic destinations regenerate on their own; manual destinations wait for the user.

## 4. The Archive (on-disk format)

### 4.1 Principles
- **Source of truth = files on disk**, not the database. Each book has a `metadata.json` next to the original. The SQLite index is only a cache and can be rebuilt at any time by scanning the folders.
- **The index lives outside the Drive**, in `%LOCALAPPDATA%\GibiOMatic\` (Linux: `~/.local/share/gibiomatic/`). SQLite inside a synced folder is a classic source of corruption.
- **Atomic writes**: write to `*.tmp` and rename.
- **Immutable originals**: the SHA-256 hash is recorded on import and verified on demand.

### 4.2 Layout

```
<Archive>/
  gibiomatic-library.json            # library id, format version
  Books/
    Image/
      Saga/
        Saga, Vol. 1 [k3f9a2]/
          Saga, Vol. 1.pdf         # original (content untouched; only the name is cleaned)
          cover.jpg
          metadata.json
    BOOM! Studios/
      House of Slaughter/
        House of Slaughter, Vol. 1 [p81xq0]/
          ...
    _No publisher/              # approved without a publisher
      ...
    _To review/                 # not reviewed yet: {Title} [id]/, without publisher and series
      ...
  .gibiomatic/
    history/<id>/<timestamp>.json  # previous versions of metadata.json (last 20)
```

- `[k3f9a2]` = the book's short, stable id (6 characters of a ULID). It keeps names unique and lets the book be found even after a rename.
- While the book has not been reviewed, it stays in `Books/_To review/{Title} [id]/`, because publisher and series are still guesses. Once approved (automatically, in the Review or in the Editor), it moves to `{Publisher}/{Series}/`; approved without a publisher, to `_No publisher/`.
- The folder is **renamed** when the publisher, series, title or review status change. On the Drive this is a cheap "move".
- The original's name becomes `{Title}.{ext}`. The content is never modified; the download name is recorded in `metadata.json`.

### 4.3 `metadata.json`

(The `//` comments below are only explanatory; the real file is plain JSON.)

```json
{
  "schema": 1,
  "id": "01JABCK3F9A2...",
  "kind": "tpb",                      // issue | tpb | omnibus | artbook | book
  "title": "Saga, Vol. 1",
  "series": { "name": "Saga", "number": "1", "volume": null },
  "date": { "year": 2012, "month": 10, "day": 10 },
  "publisher": "Image Comics",
  "imprint": null,
  "credits": [
    { "role": "writer", "name": "Brian K. Vaughan" },
    { "role": "artist", "name": "Fiona Staples" }
  ],
  "summary": "…",
  "language": "en",
  "identifiers": { "isbn": "9781607066019", "comicvine": "4000-123456" },
  "tags": [],
  "universes": ["Massive-Verse"],
  "files": [
    {
      "role": "original",
      "name": "Saga, Vol. 1.pdf",
      "downloadedAs": "Saga_Vol1_Image.pdf",
      "format": "pdf",
      "sha256": "…",
      "bytes": 412345678,
      "source": "Humble Bundle",
      "quality": "original"            // original | converted | unknown
    }
  ],
  "review": {
    "status": "approved",              // pending | approved
    "approvedAt": "2026-10-17T14:00:00Z",
    "matchedBy": "isbn",               // isbn | comicvine-volume | manual
    "confidence": 0.99
  },
  "createdAt": "…",
  "updatedAt": "…"
}
```

**Credit roles** (`role`): `author` (author of the whole work, as in a book that is not a comic), `writer`, `artist` (pencils), `inker`, `colorist`, `letterer`, `cover`, `editor`, `translator`, `other`. "Authors", in places that only take one field, means the `author`s and the `writer`s (in ComicInfo, both go into `Writer`).

## 5. Metadata quality

Used in the Library (badges and filters) and to sort the Review. A score from 0 to 100, weighted in the author's order of priority:

| Field | Problems detected |
|---|---|
| Title (weight 1.0) | lowercase only, words run together, "_" or an extension, "Untitled", junk such as "(prop)", all caps |
| Authors (0.9) | missing, "Unknown"/"Various", several names glued into one, duplicates, single-word name |
| Date (0.7) | missing, suspicious (same as the Humble purchase date, repeated across many books) |
| Series (0.7) | the title implies a series but the field is empty; number differs from the title |
| Secondary (0.3) | invalid ISBN, no publisher |

Bands: **Poor** (< 60), **Review** (60–84), **OK** (≥ 85). The rule already exists in Python (`reference/python/analyze.py`) and is ported to C#.

## 6. Inbox (import)

1. Watches the inbox folder (FileSystemWatcher) and also accepts drag and drop.
2. For each file: waits for the write to finish, computes the SHA-256 and detects the format from the content.
3. **Duplicates**:
   - same hash as an existing original: warns and skips;
   - same book identified with a different file: asks *Replace the current one* / *Keep both* (extra format) / *Skip*. It suggests "Replace" when the new file is in a preferred format (see section 10).
4. Reads embedded metadata: `ComicInfo.xml` (CBZ/CBR), OPF (EPUB), Info/XMP (PDF), ISBN on the credits page (optional, later phase).
5. Runs the identification (section 8) and shows the result in the Inbox screen.
6. On confirmation: copies the original into the Archive, extracts the cover and writes the `metadata.json`. Once the copy has been verified by hash, the file in the watched folder leaves the Inbox according to the "After importing" setting: **system Recycle Bin** (default), move to `Inbox/_imported/` (keeping subfolders) or delete. On a drive without a Recycle Bin (network, USB stick, Google Drive), the Recycle Bin option moves to `_imported`; the app never deletes for good unless the user chose "Delete". Files arriving in the watched folder are confirmed on their own, with the suggested decision for duplicates; those added by hand keep waiting for "Import" and stay where they are.
7. **Background**: to watch the folder without the window open, the app lives in the system tray and can start with the system. Only one instance runs; opening it again brings up the existing window.
   - **Closing the window** sends the app to the tray (default). The first time, a notice explains that the app keeps running in the tray and lets the user choose: *Keep in the tray* or *Close the app*. The choice is saved and can be changed in Settings. To really quit: tray menu → *Exit*.
   - **Start with the system**: off by default. When turned on, the user chooses whether the app opens the window or only the tray icon (default: only the tray).
   - **Notifications**: one summary system notification when a batch of files finishes processing (e.g. "5 new books: 3 in the library, 2 awaiting approval"), not one per book. Clicking opens the Review when there are books to review, the Inbox when a file failed, and the Library otherwise (imported files leave the Inbox list).

## 7. Review and Editor

- **Review**: queue sorted by quality score. For each book: candidates with a confidence % and a *field · current · proposed* table, with one checkbox per field. Actions: Approve and next, Skip, Edit by hand.
- **Review by series** (section 7.1): the same queue, one series at a time.
- **Editor**: every field from section 4.3, credits with roles, cover (replace or use page 1), preview of the names each destination will generate.
- **Batch editing**: select N books and edit the fields they share. Empty fields change nothing. "Number in sequence" is for the series number.
- Every save creates an entry in `.gibiomatic/history` and can be undone.
- **Remove from library** (right-click menu in the Library, in "Continue reading" and in the Review queue; Editor for a single book): asks for confirmation, with two ways out. "Remove" moves the book's folder (original, cover, `metadata.json`) to `.gibiomatic/trash/<date>-<id6>/`, with a `.trash.json`; "Remove and delete the files" deletes the folder and the history for good. The book leaves the index, the identification, the reading status and the destination lists; destination copies are deleted, but never the one already on the Kindle.
- **Show in Explorer** ("Show in folder" outside Windows), in the same places and next to the book's path: opens the file manager with the original selected.
- **File not found**: when the library opens, the app checks whether each book's original is still on disk. Missing ones get a badge on the cover and the "No file" filter; the Review, the Editor and the book panel warn about it and offer removal. If a book's whole folder is gone (deleted or moved by hand), the index is rebuilt from the `metadata.json` files.

### 7.1 Review by series

**Why**: the books of one series are identified one at a time, and they often come back different: one book matched the series' volume, another matched a "Series: Subtitle" volume, another was not found, and the titles come out in different shapes. Today the user has to compare them by hand before approving each one. Here the user decides once for the whole series.

**Where**: a **By book | By series** switch at the top of the Review. Both modes show the same queue (books not reviewed yet); a book approved in one mode leaves both. The switch is remembered.

**Groups** (left column):
- Only books in the Review queue are grouped. A group is a **proposal**: nothing is written to `metadata.json` until it is approved.
- Automatic grouping, recomputed when the queue changes:
  1. key = the series that `TitleParser.Parse` reads from the title, normalized (case, punctuation, accents, a leading "The" and spaces ignored). "Deadly Class, Vol. 3" and "DeadlyClass_v5" share the key `deadlyclass`;
  2. groups whose books' best candidates point to the same ComicVine volume are merged;
  3. a key with a single book does not form a group.
- Books outside every group sit in a fixed **No group** entry at the end, reviewed one by one as in the By book mode.
- Each group shows its name, the number of books and a warning when its books disagree: "2 diverge" (best candidates in different volumes, or no candidate), "number repeated", "number missing". Groups with warnings come first.
- **Manual corrections**, kept in the index until the books are approved, so the automatic grouping never undoes them:
  - **Remove from group**: the book goes to No group;
  - **Ungroup**: every book of the group goes to No group;
  - **Add to group**: pick books from No group or from other groups, with a search box, or drag a book onto a group;
  - **Merge with…**: joins two groups (e.g. the one with the file name typo);
  - **Create group**: from books selected in No group.

**The open group** (right side):
1. **Series**: the series every book of the group will get (name, publisher, start year). A list of the ComicVine volumes found by the group's books, with cover, publisher, year and issue count, most used first; or a ComicVine link; or typed by hand.
2. **Title format**: a template applied to every book's title, with a live preview. Fields: `{Series}`, `{Number}` (`{Number:00}` pads), `{Name}` (the issue's name on ComicVine, where it keeps the collected edition's subtitle), `{Year}`. Default: `{Series} Vol. {Number}: {Name}`; a part whose field is empty is dropped together with its separator. "Keep each book's title" turns it off. The last template used is remembered.
3. **Table**, one row per book: file name, number read from the title, what the book matched today (volume and issue), and the result: the new title, the issue it will get and its cover. Rows that change are highlighted.
   - Each book's record: the issue with the book's number in the chosen volume (one ComicVine call for the whole group: `issues/?filter=volume:ID`, cached). If the volume has no such issue (collected editions that are volumes of their own on ComicVine), the book's own best candidate is kept when its number agrees. Otherwise the row says "No match" and offers a list of the volume's issues to pick from.
   - The group's series (step 1) is written to every book, whatever volume its record came from.
   - The other fields follow the Review's default: the ones the Review would tick for that record.
   - Row actions: open the book in the reader, choose another issue, edit by hand, remove from group.

**Approve the group** (the table is the dry run):
- A confirmation summarizes it: "Approve 6 books? 2 change volume, 1 without a match stays in the Review".
- Books with a record are approved as in the Review (`matchedBy: comicvine-volume`, folder moved to `{Publisher}/{Series}/`, automatic destinations triggered). Books without a match stay in the group, with the series already chosen.
- Every book gets its own history entry, all of them tagged with the same batch, so the whole group can be undone at once.
- After approving, the next group opens.

**Not in scope**: regrouping books that are already approved. Those are fixed in the Editor or the batch Editor, or sent back to the Review.

## 8. Metadata sources and identification

### 8.1 Sources (configurable order)
| Source | Use | Notes |
|---|---|---|
| **Open Library** | by ISBN | no key; tested successfully (Deadly Class Vol. 11, Saga) |
| **ComicVine** | collected editions and single issues, credits by role | the user's personal key; respect the usage limits and keep a local cache of responses |
| **Google Books** | by ISBN, optional | API v1 with an aggressive limit without a key; off by default |

### 8.2 Algorithm
1. **Valid ISBN** (embedded or already present): Open Library (and Google Books, if on). ComicVine does not search by ISBN; the title found through the ISBN guides the ComicVine search, and the ComicVine candidate that agrees with it gets the ISBN evidence. If it matches, confidence is ≥ 0.95.
2. **Title and series**: splits the title into parts (`TitleParser.Parse`) and searches ComicVine *volumes*. Nothing in the title that names the book is dropped:
   - the title is cut at " - " and ":" (Humble Bundle writes "The Boys Vol. 11 - Over the Hill…" where ComicVine writes ":"); the text before the number is the series, the text after it is the subtitle: "Deadly Class, Vol. 11: A Fond Farewell" → series "Deadly Class", no. 11, kind TPB, subtitle "A Fond Farewell"; "Hellboy - Krampusnacht" → "Hellboy", subtitle "Krampusnacht";
   - numbers: "Vol. 3", "vol3", "v3", "Book One", "Omnibus 1", "TPB 2" (volume) and "#5", "Issue 5" (issue). A volume number followed by an issue number names a mini-series: "RoboCop Vol. 3 - Last Stand #5" → series "RoboCop: Last Stand", issue 5;
   - format tags ("TP", "HC", "OGN", "One-Shot") and download leftovers ("(v2)", "[Digital]", "(Free Issue)") are removed; "(2003)" is the series' start year;
   - searches, most specific first, stopping when the book's own volume shows up: series and subtitle ("The Boys Over the Hill…"), the series ("The Boys"), and the part of the series before a colon.
   - On ComicVine, collected editions are often volumes of their own ("Deadly Class: Reagan Youth"). Both the series' volumes and the "Series: Subtitle" volumes have to be considered.
   - Filters by known publisher, language (drops foreign editions, such as Splitter's German *House of Slaughter*) and year.
   - Scores each candidate by title similarity, number, publisher, year and kind. The subtitle counts when it matches the volume's subtitle or the issue's name (where ComicVine writes the title of a collected edition); without a number, the issue's name is what picks the book. A creator's name at the start of the volume ("Richard Stark's Parker") is ignored in the comparison; an issue name repeated by several issues of a volume ("Book One: …") is a story arc, not a collected edition.
3. **Safe automatic approval**: on by default, but only when **all** the conditions below hold. Any other case goes to the Review, with the candidates.
   - **Strong evidence**, of one of these kinds:
     - the file's ISBN matches the result;
     - **two independent sources agree** on title, number and publisher;
     - or, on ComicVine alone: series, number, publisher and year match exactly and there is **a single** candidate above 0.5.
   - **No existing "good" field would be overwritten by a different one.** For example, a date already filled in that disagrees goes to the Review.
   - **The computed confidence is ≥ 0.95.**
   - **Safeguards:**
     - every automatically approved book is marked `matchedBy: auto` and shows up in the "Approved automatically" filter, for spot checks;
     - everything can be undone through the history;
     - the "Always review" setting turns automatic approval off.

   Between 0.50 and 0.95, the book goes to the Review with candidates; below 0.50, it goes to the Review with no suggestion.
4. **Credits**: only the creative roles (writer, art, inks, colors, letters, cover). Editors are left out by default; the Calibre plugin mixed them in with the authors.

### 8.3 Lessons from the Calibre diagnosis (2026-10-17)
- The ComicVine plugin 0.14.2 connects, but only searches *single issues*, and so it gets collected editions wrong ("Saga, Vol. 01" → *Cable #73*).
- Calibre's Google source gets HTTP 500; Amazon fails because searches are blocked.
- Conclusion: identifying collected editions needs logic of its own, which is what sets the app apart.

## 9. Destinations

### 9.1 Common model
```csharp
public interface IDestinationType {
    string Id { get; }                        // "komga", "kindle"
    DestinationSettings DefaultSettings();
    IDestination Create(DestinationSettings s);
}
public interface IDestination {
    Task<DestinationStatus> GetStatusAsync(CancellationToken ct);
    bool Includes(Book book);                 // "which books" rule
    Task<OutputState> GenerateAsync(Book book, IProgress<double> p, CancellationToken ct);
    Task DeliverAsync(IEnumerable<Book> books, CancellationToken ct); // Kindle: copy over USB
}
```
- **Rules of every destination**: *Which books* (the whole library / chosen ones / series) and *When* (automatically on approval / manually).
- **Capabilities per type**: each type declares which kinds of book it accepts (`issue`, `tpb`, `omnibus`, `artbook`, `book`). If the type accepts books that are not comics (`book`), the "Include books that aren't comics" option shows up in the destination, **off by default**. If it doesn't, the option doesn't even appear.
  - **Komga**: accepts them, since it reads reflowable EPUB since version 1.8, with its own reader. Text books go as EPUB, unconverted.
  - **Kindle**: KCC only converts images. Text books would need another converter or Send to Kindle, so they are **to do** (backlog). Until then, the option doesn't appear.
- **State per book and destination** (a table in the index): the hash of the metadata plus the hash of the original used in the last generation. If either changes, the book is "outdated".
- Destination settings are **per machine** (`%APPDATA%\GibiOMatic\settings.json`), because paths like `Z:\` and the connected Kindle are local.

### 9.2 Komga destination
- **How Komga builds series**: every subfolder that contains books becomes a series, at any depth, and every file becomes a book of the series of the folder it is in. The books in a one-shots folder each become a series of a single book. That folder's name is the library's "One-Shots directory" option in Komga.
- **Default output** (template editable under Advanced):
  - books with a series: `{Folder}/{Series}/{Series} v{Number:00} ({Year}).cbz`;
  - one-shots: `{Folder}/{One-shots folder}/{Title} ({Year}).cbz`.
- **Why not `Publisher/Series/`**: the publisher folder contains no files, so in theory Komga ignores it, but this **has not been tested**. The publisher already goes in `ComicInfo.xml` and Komga can filter by it. It stays as an optional template, to validate in F3.
- **One-shot**: a book without a series, or whose series has a single book (a stand-alone graphic novel, such as *Plutona*). It comes from the identification (ComicVine with 1 issue) and can be marked by hand in the Editor ("It is a one-shot").
- **"One-shots folder" setting** of the destination, mirroring Komga's *One-Shots directory* option:
  - default `_oneshots`;
  - empty = no special handling, and each one-shot stays in its own series folder;
  - the wizard and the destination screen remind the user to set the same name in Komga;
  - if Komga's address and key are configured, the app reads the library's option and warns if the names differ.
- **Building the CBZ**:
  - CBZ original: copy and inject or replace `ComicInfo.xml`;
  - CBR original: extract and repack as CBZ;
  - PDF original: per page, if it is a single JPEG image covering the whole page, extract the original bytes without recompression; otherwise (text or vectors on top, such as the balloons in Radiant Black), render at the resolution of the largest image and save as JPEG at quality 92. Validated in the Python prototype with CLOSET, Dead Lucky and Radiant Black;
  - EPUB original: extract the images in *spine* order.
- **ComicInfo.xml**: `Title, Series, Number, Volume, Count, Summary, Year, Month, Day, Writer, Penciller, Inker, Colorist, Letterer, CoverArtist, Editor, Publisher, Imprint, Genre, Tags, SeriesGroup (universes, comma-separated; Komga creates collections), Web, PageCount, LanguageISO, Format (TPB/Issue), GTIN`.
- **Server refresh (optional)**: with Komga's URL and API key, it triggers the library scan when done. The endpoints are yet to be confirmed in Komga's documentation.
- **Default**: the whole library, automatic.

### 9.3 Kindle destination
- **Conversion**: Kindle Comic Converter (KCC, `kcc-c2e`) with the device profile (Colorsoft = `KCS`, 1272×1696, color with `--forcecolor`). Input: a temporary CBZ generated from the original (with `ComicInfo.xml`).
- **Format**: KCC's MOBI, which is also AZW3 ("dual"); `.mobi` is the extension KCC uses. EPUB for Send to Kindle stays in the backlog.
- **Prepare and send** (user decision, 2026-10-18): the chosen books are converted into a local folder while the Kindle is away; sending happens when it is plugged in and the user confirms. The app never deletes anything from the Kindle on its own; there is a manager for deleting by hand.
- **Delivery**:
  - Windows: Kindles from 2024 on use **MTP**; copy to `documents/` through Windows Portable Devices (for example, the `MediaDevices` NuGet package).
  - Linux: **to do**. Until then, the "Open folder" button lets the user drag files by hand, and that button exists on every system.
- **Panel by panel**: the Kindle offers "Panel View" (Aa menu, double tap) on KCC files. Real panel detection stays in the backlog.
- **Default**: chosen books, prepared as soon as they are chosen; sending only with confirmation.
- **Risks**: KCC needs KindleGen to produce MOBI/AZW3. KindleGen comes with Kindle Previewer, which only exists for Windows and macOS, so on Linux the output may be limited to EPUB.
- **KCC license**: ISC. But the published executable bundles PyMuPDF (AGPL), so the app does **not** distribute it: the user installs it, or the app downloads the official release when the user asks. Called as an external process, it imposes nothing on GibiOMatic's license.

### 9.4 Backlog of types
Kavita, Kobo (KEPUB via KCC), plain folder (copy as CBZ/PDF/EPUB), OPDS server, Calibre-Web.

## 10. Initial load (bulk import)

There is no migration from Calibre. The collection is rebuilt from originals downloaded again from Humble Bundle, and every book's metadata is redone.

1. The user points the Inbox at the folder with the downloads, which can be hundreds of files.
2. The import runs **in batches**, in the background, with progress and the ability to pause and resume. The state lives in the index, so closing the app loses nothing.
3. The identification respects the APIs' limits, with a queue, cache and retries. The initial load may take hours, and that is expected.
4. When it ends, it shows a summary:
   - how many were approved automatically;
   - how many are in the Review;
   - how many are duplicates, for example the same book as PDF and as CBZ.
5. **Format duplicates**: when the same book arrives in more than one format, the best one becomes the main original and the others are kept as extra formats. The order of preference is CBZ > PDF > EPUB > CBR, configurable.
6. The Calibre library is neither read nor modified. It stays as a backup until the user decides to delete it.

## 11. Settings

- **General** (`%APPDATA%\GibiOMatic\settings.json`): language (pt-BR, en), theme (dark, light, system), Archive folder, Inbox folder, what to do with the file after importing (Recycle Bin, `_imported` or delete), closing the window (tray or quit), start with the system (off, tray only, with the window), metadata sources (order and on/off), automatic approval threshold, destinations.
- **Secrets** (API keys) **never go to text files**: Windows DPAPI (`ProtectedData`) and, on Linux, libsecret.
- **Library settings** (`gibiomatic-library.json`, on the Drive): only what belongs to the collection, independent of the machine (format version, id).

## 12. Interface

Based on the Claude Design prototype. Screens:

| Screen | Contents |
|---|---|
| Library | Cover grid (series or books), quality badges, filters (Needs review, No date, No series, Unknown author, Complete), search, panel for the selected book |
| Inbox | New files with the identification result, duplicate handling, import |
| Review | Queue, candidates, field-by-field comparison; By book / By series modes (§7.1) |
| Editor | One book or a batch, credits by role, preview of the output names |
| Destinations | Cards with status and rules; "Add destination" |
| Destination detail | Main action (Kindle: choose and send) and settings with "Advanced" collapsed |
| Destination wizard | 3 steps: type → details → name and rules |
| Settings | Folders, Metadata, General |

- **Look**: a night-blue dark theme by default and a cream-paper light theme, lemon yellow (`#FFF27A`) as the accent, cyan and magenta only in the selection's offset, Anybody for titles, IBM Plex Sans/Mono for everything else (F7 revamp; details in `docs/DESIGN.md`).
- **Accessibility**: keyboard navigation, AA contrast, touch targets ≥ 44 px.
- **Languages**: `resx`, with pt-BR and en from the start.

## 13. Technical architecture

- **.NET 10 (LTS)**. .NET 8 support ends in Nov 2026; the SDK 10 has to be installed.
- **Avalonia 12** with MVVM (CommunityToolkit.Mvvm).

```
GibiOMatic.sln
  src/
    GibiOMatic.App/              # Avalonia: Views, ViewModels, themes, resources (resx)
    GibiOMatic.Core/             # domain (Book, Credit, Destination…), quality rules, services
    GibiOMatic.Library/          # on-disk format, metadata.json, SQLite index, import (inbox and bulk load)
    GibiOMatic.Metadata/         # IMetadataSource: OpenLibrary, ComicVine, GoogleBooks; identification
    GibiOMatic.Conversion/       # reading PDF/EPUB/CBZ/CBR, building CBZ, ComicInfo.xml, running KCC
    GibiOMatic.Destinations/     # IDestinationType + Komga + Kindle (MTP)
  tests/
    GibiOMatic.Core.Tests/ …     # xUnit; synthetic samples, never real comics in the repository
```

| Need | Library | License |
|---|---|---|
| UI | Avalonia, CommunityToolkit.Mvvm | MIT |
| Database/index | Microsoft.Data.Sqlite + Dapper | MIT / Apache-2.0 |
| Render PDF | PDFium (bblanchon/pdfium-binaries binaries, our own P/Invoke) | BSD/Apache |
| Extract PDF images losslessly | PDFium (page objects, raw JPEG bytes) | BSD/Apache |
| CBR (RAR) | SharpCompress | MIT |
| Images | SkiaSharp (already comes with Avalonia) | MIT |
| Logging | Serilog | Apache-2.0 |
| MTP (Windows) | MediaDevices | MIT (to be confirmed) |

**Avoid** MuPDF/PyMuPDF (AGPL), which would force the whole app to be AGPL.

**External processes**: KCC is called on the command line, with a configurable path and a timeout, and progress is read from its output.

## 14. Integrity and security

- Immutable originals, with the hash checked and an optional verification ("verify file").
- Every batch operation has a dry run, confirmation and a log in `%LOCALAPPDATA%\GibiOMatic\logs`.
- No secrets in the repository, nor in the files on the Drive.
- The app sends data nowhere except the chosen metadata sources.

## 15. Delivery phases

| Phase | Delivery | Done when |
|---|---|---|
| **F0 · Foundation** | .NET solution, CI on GitHub Actions (build and tests), settings, Archive format and index | the app opens, points at a folder and creates the empty library |
| **F1 · Import + Library** | Manual and bulk import (section 10), copy to the Archive, cover, embedded metadata, grid, filters and quality score, panel | the downloaded originals show up in the library with a quality score |
| **F2 · Metadata** | Open Library + ComicVine, identification, safe automatic approval, Review, Editor (single book and batch), history and undo | the whole collection goes through identification and the Review queue is manageable |
| **F3 · Komga destination** | CBZ building (PDF/EPUB/CBZ/CBR), ComicInfo.xml, naming template, sync state, optional scan | the library shows up in Komga with correct series and metadata |
| **F4 · Kindle destination** | KCC integration, profiles, sending over MTP on Windows | a chosen book reaches the Colorsoft and opens with Panel View |
| **F5 · Automatic inbox** | Watched folder, identification on arrival, duplicates and replacement | a new Humble download goes from download to Komga with no manual steps, or only the approval |
| **F6 · Polish** | English, light theme, Windows installer, Linux build, user documentation | ready for someone else to install on their own |
| **F8 · Review by series** | Groups of the Review queue by series, one series and title format per group, group approval and undo (§7.1) | a mixed series of ~6 books is approved with one decision |

### Backlog
Real panel detection (Panel View with per-panel regions), other destinations (section 9.4), reading the ISBN by OCR from the credits page, suggesting missing series, automatic app updates, server/Docker mode.

## 16. Open questions

1. KCC's behavior without KindleGen on Linux.
2. Publisher folders in Komga (`Publisher/Series/`): the naming template allows `{publisher}/{series}/…`; still to be tested on the user's Komga.

**Decided (2026-10-17)**:
- Name: **Gibi-o-matic** (repository `gibi-o-matic`, namespace `GibiOMatic`).
- MIT license (`LICENSE` file).
- No migration from Calibre; initial load from the originals.
- Automatic approval on, with the safety rules of section 8.2.
- Books that are not comics: an option per destination, according to the type's capabilities.
- Copying to the Kindle on Linux: to do.
- Background (2026-10-23): closing the window goes to the tray by default, with a notice and a choice the first time; start with the system can open the window or only the tray; summary notifications (section 6, item 7).
- Komga API: `GET /api/v1/libraries` and `POST /api/v1/libraries/{id}/scan`, with the `X-API-Key` header (see `RESEARCH.md`).
