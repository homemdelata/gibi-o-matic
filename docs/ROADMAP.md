# Roadmap

Status: ⬜ not started · 🟨 in progress · ✅ done. `§` sections refer to `SPEC.md`.

| Phase | Name | Status |
|---|---|---|
| F0 | Foundation | ✅ |
| F1 | Import + Library | ✅ |
| F2 | Metadata | ✅ |
| F3 | Komga destination | ✅ |
| F3.5 | Reader | ✅ |
| F3.6 | Universes | ✅ |
| F4 | Kindle destination | ✅ (the user's test with the Kindle is pending: on 10/23 Windows did not recognize the device; **test again**) |
| F5 | Automatic inbox | ✅ |
| F6 | Polish and distribution | ✅ |
| F7 | Visual revamp | ✅ (the user's test is pending) |

When each phase is done: green build and tests → update this file → commit and push → **stop and explain to the user how to test it**.

---

## F0 · Foundation

**Goal**: a solid app skeleton, running, with the prototype's look and the empty library created on disk.

- [x] Check for the .NET 10 SDK; if missing, ask for permission and install it (`winget install Microsoft.DotNet.SDK.10`). Create `global.json`.
- [x] `GibiOMatic.sln` with the `src/` and `tests/` projects from `CLAUDE.md`.
  - `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `ImplicitUsings`.
  - `Directory.Packages.props`: centralized versions.
- [x] **Interface shell (Avalonia)**:
  - main window with the prototype's side menu (Library, Inbox, Review, Editor, Destinations, Settings);
  - empty pages with a title;
  - navigation through ViewModels.
- [x] **Theme**: the tokens from `docs/DESIGN.md` as resources (colors, radii, spacing), styles for the basic components (primary and secondary button, chip, card, text field, menu item) and embedded fonts (Bricolage Grotesque, IBM Plex Sans/Mono; OFL license — record in `THIRD-PARTY.md`).
- [x] **i18n**: `resx` with pt-BR (default) and en; language switch in Settings (may require a restart).
- [x] **Settings** (§11):
  - `ISettingsService` persisting to `%APPDATA%\GibiOMatic\settings.json`, with atomic writes;
  - `ISecretStore` with a DPAPI implementation on Windows and a *stub* for Linux, to do.
- [x] **Library format** (§4):
  - create and open a library, generating `gibiomatic-library.json` (id, format version), `Books/` and `.gibiomatic/`;
  - SQLite index in `%LOCALAPPDATA%\GibiOMatic\index\<libraryId>.db`, with versioned schema migrations;
  - "rebuild index" by scanning the `metadata.json` files.
- [x] **First run**: welcome screen → "Create a new library" or "Open an existing library" → pick the folder → the app opens on the Library (empty, with a friendly empty state). On the next start, it remembers the library.
- [x] Logging with Serilog in `%LOCALAPPDATA%\GibiOMatic\logs`.
- [x] Tests: settings serialization, creating and opening the library, rebuilding the index from synthetic `metadata.json` files.
- [x] **CI**: `.github/workflows/ci.yml` with build and tests on `windows-latest` and `ubuntu-latest`.
- [x] Start `docs/DECISIONS.md` and `docs/THIRD-PARTY.md` with whatever gets added.

**Done when**:
- the app opens with the prototype's look;
- the user creates a library in a test folder;
- after closing and reopening, the library is remembered;
- the CI is green.

---

## F1 · Import + Library

**Goal**: bring the downloaded originals into the Archive and see them in the Library with a quality score.

- [x] **Format detection** from the content (*magic bytes*): PDF, ZIP/CBZ, RAR/CBR, EPUB.
- [x] **Manual import** (§6): "Import files…" button and drag and drop.
  - Computes the SHA-256;
  - detects exact duplicates (same hash);
  - copies to `Books/{Publisher}/{Series}/{Title} [{id6}]/` (with no metadata yet, it lands in `_No publisher/…`), with the cleaned name and the `downloadedAs` recorded;
  - writes `metadata.json` (§4.3) atomically.
- [x] **Bulk import** (§10):
  - queue persisted in the index, in the background, with progress, pause and resume;
  - survives closing the app;
  - final summary.
- [x] **Format duplicates**: the same book in several formats becomes a single book, with the main original chosen by the preference CBZ > PDF > EPUB > CBR (configurable) and the others as extra formats. In F1, recognizing "the same book" uses an embedded ISBN or the normalized title and number; F2 refines it.
- [x] **Embedded metadata**: `ComicInfo.xml` (CBZ/CBR), OPF (EPUB) and Info/XMP (PDF) → initial filling of `metadata.json`.
- [x] **Cover** (`cover.jpg`):
  - CBZ/CBR: first image in natural order;
  - EPUB: the cover image declared in the OPF;
  - PDF: render page 1 with PDFium (see §13 and `reference/python/pdf2epub.py`).
  - Generate thumbnails in a local cache.
- [x] **Quality score** (§5): port `reference/python/analyze.py` to `GibiOMatic.Core`, with tests covering each rule (titles run together, "Unknown", glued authors, "Untitled", series in the title without a series field, etc.).
- [x] **Library screen** (prototype `Main.dc.html`):
  - virtualized cover grid with an OK, Review or Poor badge;
  - Series/Books toggle;
  - search;
  - filters (Needs review, No date, No series, Unknown author, Complete);
  - summary cards;
  - panel for the selected book: what is missing, files and actions, still disabled.
- [x] **Inbox screen**, manual version (prototype `Inbox.dc.html`): list of what is being imported and duplicate handling (Replace, Keep both, Skip).
- [x] Performance: open a 1000-book library in under 2 s with the index ready, and smooth scrolling.

**Done when**: the user imports the folder with the new downloads and sees them all in the Library, with cover and quality score, and the originals in the Archive with their hash verified.

---

## F2 · Metadata (§7, §8)

- [x] `IMetadataSource`: Open Library (ISBN), ComicVine (volume and issue search, details, credits by role) and Google Books (optional, off by default). Each one with a cache, rate limit and retries.
- [x] Identification:
  - title normalization (series, number, TPB/single issue kind);
  - candidates;
  - scoring;
  - publisher, language and year filters;
  - one-shot detection.
- [x] Safe automatic approval (§8.2, item 3), with the `matchedBy: auto` mark and the "Approved automatically" filter.
- [x] **Review screen** (`Review.dc.html`): queue by severity, candidates, field-by-field table with checkboxes, Approve and next, Skip.
- [x] **Editor** (`Editor.dc.html`):
  - single book and batch;
  - credits by role;
  - cover (replace or use page 1);
  - "It is a one-shot";
  - preview of the output names per destination.
- [x] History (`.gibiomatic/history`) and undo.
- [x] Rename the book's folder when the publisher, series or title change.
- [x] Settings → Metadata: order and on/off of the sources, ComicVine key (DPAPI), "Always review".

**Done when**: the whole collection goes through identification, and the Review queue can be handled in a few clicks per book.

---

## F3 · Komga destination (§9.1, §9.2)

- [x] Destination model: `IDestinationType`/`IDestination`, "which books" and "when" rules, capabilities per type, sync state per book (hashes).
- [x] Building the CBZ from CBZ, CBR, PDF and EPUB:
  - PDF: extract the original JPEG without recompression when the page is a single image covering it entirely; otherwise, render (§9.2);
  - write `ComicInfo.xml`.
- [x] Naming templates (default without a publisher folder) and configurable one-shots folder.
- [x] "Include books that aren't comics" option (text EPUBs go as they are).
- [x] Optional: Komga's URL and key to trigger the library scan and compare the one-shots setting. Endpoints confirmed in Komga's source code (see `RESEARCH.md`).
- [x] **Screens**: Destinations (cards), Komga destination detail and the "Add destination" wizard (`Destinations.dc.html`, `NewDestination.dc.html`).
- [x] State of each book's copy per destination in the Library panel; "Include in …" / "Remove from …" on the selection, for "Books I choose" destinations.
- [x] Test with the user whether `Publisher/Series/` works on their Komga (open question 3): tested on the user's Komga, it worked.

**Done when**: the library shows up in the user's Komga with correct series, one-shots and metadata, and editing a book updates its copy automatically.

---

## F3.5 · Reader (user request)

Goal: open a book inside the app to check which edition it is (large cover, inner pages, credits) before choosing the metadata. It does not replace Komga or the Kindle for reading.

- [x] Our own image reader for CBZ, CBR, PDF and comic EPUB (one image per page): only the current page and its neighbors in memory, rendering in the background.
- [x] Text EPUB in `Avalonia.Controls.WebView` (WebView2 on Windows, WebKitGTK on Linux), with table of contents, previous/next chapter, font size and dark theme.
- [x] Security:
  - EPUB served from inside the file, without extracting to disk, without JavaScript, without internet access and without navigating outside the book;
  - PDF rendered by PDFium without JavaScript/XFA, without forms and without running actions (links, attached files).
- [x] Features:
  - navigation by arrows, clicking the sides, mouse wheel, Home/End and a "12 / 148" counter;
  - fit to height (default), to width and actual size;
  - zoom with Ctrl+wheel and dragging;
  - collapsible thumbnail strip;
  - full screen (F11, Esc exits);
  - two-page spread, with the cover on its own;
  - right-to-left reading;
  - remember each book's last page (in the index, never in the original).
- [x] "Open" in the Library panel (cover, button and each file) and in the Review (cover and "Open book"). The reader opens in its own window, which can sit next to the Review while the candidates are compared.
- [x] Reading status (user request after the first test):
  - double-clicking a card in the Library opens the reader;
  - "reading" after more than 5 pages seen in a session (opening just to see the cover doesn't count); "read" when reaching the last page of a book already started, or marked by hand;
  - Reading, Read and Unread filters;
  - in the panel: Mark as read / unread and Clear progress;
  - "Continue reading" strip at the top of the Library (up to 8 books, most recent first) and a progress bar on the cards being read.

**Done when**: the four formats open in the reader from the Library and from the Review, and a reopened book goes back to the page it was on.

---

## F3.6 · Universes (user request)

Goal: group series from the same universe or publishing line (e.g. Radiant Black, Radiant Red and Radiant Pink in the Massive-Verse). ComicVine has no such field, so it is filled in by hand.

- [x] `universes` field (a list) in `metadata.json`; read from the ComicInfo `SeriesGroup` on import.
- [x] Editor: universes as removable tags, with suggestions from the ones that already exist; in a batch, adding or removing a universe applies to every book, without touching each book's other universes.
- [x] Library: "Universes" mode (universe → series → books), search by universe and universes in the book panel.
- [x] Komga: universes in the ComicInfo `SeriesGroup`, which Komga turns into collections.

**Done when**: the user tags a universe's series in a batch, sees the universe in the Library and the collection shows up in Komga after syncing.

---

## F4 · Kindle destination (§9.3)

- [x] KCC integration (`kcc-c2e`):
  - find or configure the executable (the one the app downloaded, one picked by hand or the one on the PATH);
  - device profile (Colorsoft = `KCS`), with color (`--forcecolor`);
  - MOBI format (which is also AZW3);
  - progress from the steps KCC prints, and a timeout.
  - Bundle KCC with the app: **no** (the executable includes PyMuPDF, AGPL). The app downloads the official release when the user asks, with its SHA-256 verified.
- [x] **Prepare without the Kindle connected** (user request):
  - the chosen books ("Books I choose") are converted in the background, one at a time, into a local staging folder (default `%LOCALAPPDATA%\GibiOMatic\kindle`, configurable; never on the Drive);
  - status per book: Queued, Preparing, Ready to send, On the Kindle, Outdated (metadata changed after preparing → redone automatically).
- [x] **Send when connected**: delivery over MTP on Windows (detect the Kindle, copy to `documents/`). On connection, a banner offers "Kindle connected: N books ready. Send?", with the list and a confirmation. The app remembers what it already sent and doesn't send it again. Once sent, the prepared copy is deleted (resending converts again). Linux: **to do**. The "Open folder" button always works.
- [x] **The app never deletes anything from the Kindle on its own.** Removing a book from the list doesn't delete it from the device.
- [x] **Mini Kindle manager**: list the books in the `documents/` folder (marking the ones the app sent) and delete the chosen ones, only by manual action and with confirmation.
- [x] **Kindle destination screen** (`KindleDestination.dc.html`):
  - Send tab (the list with the preparation status, send);
  - On the Kindle tab (the mini manager);
  - Settings tab with "Advanced" collapsed;
  - History tab.
- [x] Books enter the list from the Library (user request): right-click menu on a book ("Add to the Kindle list", and also open, mark as read/unread, clear progress, edit and review metadata) or the button in the panel; the Kindle screen only shows the list. The panel shows the copy's state.

**Done when**: a chosen book reaches the user's Colorsoft and opens with Panel View.

---

## F5 · Automatic inbox (§6)

- [x] Watched folder (FileSystemWatcher, subfolders included) with a scan on opening and every 5 minutes; a file only comes in once it stops changing and nobody is writing to it.
- [x] Identification on arrival; approved books go straight to the library and to the automatic destinations. Files from the folder come in without confirmation; those added by hand still ask for "Import".
- [x] Replacing an original with a better version: the Inbox's suggestion is applied on its own (user decision).
- [x] Move the processed files to `_imported/` (with the subfolders); files identical to what is already in the library too (user decision); failed ones stay. Subfolders left empty are deleted (user request).
- [x] Run in the background (user request), so the folder keeps being watched with the window closed:
  - icon in the system tray (Windows; Linux where supported), with a menu (open, pause the Inbox, exit) and the current state in the tooltip;
  - closing the window leaves the app in the tray (default); the first time, a notice explains it and lets the user choose between staying in the tray and closing the app; the choice lives in Settings → General;
  - option to start with the system (Windows: the user's `Run` key; Linux: `~/.config/autostart`), off by default; when turned on, choose between opening only the tray (default) or the window;
  - summary system notification per processed batch ("2 files processed in the Inbox · 1 book approved · 1 to review"); clicking opens the Review (if there is something to review), the Inbox (if there was an error) or the Library;
  - a single instance: opening the app again brings up the window that is already running.
- [x] Sidebar: "Inbox" and "Metadata" lines in the status card, on every screen (user request); the metadata bar left the top of the Library.
- [x] Inbox screen: card with the watched folder (or the notice that it is off or was not found) and the "automatic" mark on files that came from it.

**Done when**: a new Humble download goes from download to Komga with no manual steps, or only the approval.

---

## F6 · Polish and distribution

- [x] Complete English interface (checked: every key in both languages, nothing hard-coded), light theme and "follow the system".
- [x] Version from the git tag (MinVer) and `tools/publish.ps1` (*self-contained* build as `.zip`/`.tar.gz`).
- [x] Windows installer and Linux AppImage with Velopack (user decision), automatic updates through GitHub Releases and a release produced by the CI on a `v*` tag (draft).
- [x] User test (2026-10-25): installing over an existing install and automatic update from a local folder (`GIBIOMATIC_UPDATE_SOURCE`), from 0.1.0-alpha.0.98 to 0.1.0-alpha.0.99.
- [x] User documentation (README with screenshots generated by `tools/Screenshots`) and `CONTRIBUTING.md`.
- [x] Accessibility review (keyboard, contrast): AA contrast of both themes and the accessible name of buttons without text became tests; the reader toolbar buttons got names. Open: 34 px icon buttons, below the 44 px of SPEC §12 (proposed to the user).

---

## F7 · Visual revamp (user request)

A "subtle Spider-Verse" look, specified in `docs/design/revamp/REVAMP.md`.

- [x] New icon (`tools/IconGen`), `.ico` and the sidebar logo (also on the welcome screen).
- [x] Anybody font and new tokens for both themes (night-blue dark, cream-paper light), with the contrast tests. Adjustments for AA: a new `BadText`; in the light theme, `Bad` `#E8448A` and `OkText` `#0A6A85`.
- [x] Shape and controls, with the offset selection (cyan on the left, magenta on the right).
- [x] Cover stack for series and universes, with tests for the choice of covers (`CoverStackTests`).
- [x] Decorative halftone and the title ornament.
- [x] Pass over every screen (Inbox, Review, Editor, Destinations, Settings, reader, welcome and the close notice).
- [x] Screenshots regenerated (`tools/Screenshots` works again: the Velopack updater broke the shell), `DESIGN.md` and `DECISIONS.md` updated.

**Done when**: the user approves both themes in the app.

---

## Adjustments after F7 (user requests)

- [x] Inbox: after importing, the file goes to the Recycle Bin (default), to `_imported` or is deleted, chosen in Settings.
- [x] Library: unreviewed books stay in `Books/_To review/` and only move to `{Publisher}/{Series}/` after approval.
- [x] Remove from library (internal trash or delete for good), "Show in Explorer" and a notice when the original file is not found (badge, "No file" filter, Review and Editor).

---

## Open source

- [x] English documentation and project files, contributor guide, Code of Conduct, security policy, issue and PR templates, Dependabot, English screenshots (2026-10-28).
- [ ] First public release and the repository made public.

---

## Backlog
- Real panel detection (Panel View with per-panel regions).
- Destinations: Kavita, Kobo, plain folder, OPDS, Calibre-Web.
- Kindle: text books; MTP on Linux.
- MOBI/AZW3 as originals (the user has a few): import with metadata from the header (EXTH), cover, reader (images and text, no DRM), CBZ for Komga when it is a comic, sending straight to the Kindle.
- OCR of the ISBN on the credits page.
- Suggest a series' missing volumes.
- Server/Docker mode.
- Sign the installer (code signing certificate), so Windows doesn't warn about an "unknown publisher".
- Associate the file types (CBZ, CBR, PDF, EPUB; optional, per type) with the app, so it becomes the default reader: opening a file from Explorer opens it in the reader, whether or not it is in the library (outside it, the app offers to import it). Windows: per-user registration (or through the installer); Linux: a `.desktop` with `MimeType`.
