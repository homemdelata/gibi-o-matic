# Research and findings

Everything here was verified on 2026-10-16/17, in a session analyzing the user's old Calibre library. Revalidate whatever depends on external services before trusting it blindly.

## Metadata

### Why Calibre didn't do the job
- Calibre's ComicVine plugin 0.14.2: it connects and the key works, but it only searches **single issues**. For collected editions it gets things wrong:
  - "Saga, Vol. 01" → *Cable #73*;
  - "Deadly Class, Vol. 11" → single issue #11 (2015) instead of the TPB (2022);
  - "House of Slaughter #1" → Splitter's **German** edition.
- The plugin also lists editors and letterers as "authors".
- Calibre's Google source gets **HTTP 500**. The Amazon source fails because it depends on a Google search (blocked) or a Bing search (irrelevant Brazilian results).

### Amazon and Goodreads (2026-10-25)
- **Amazon:** there is no usable API. The Product Advertising API requires an affiliate account with sales, and scraping the pages goes against the terms and gets blocked. But the link already helps: for the **print** edition, the link's ASIN (`/dp/1607066017`, `/gp/product/…`) is the **ISBN-10** itself. For the Kindle edition, the ASIN starts with `B0` and is not an ISBN.
- **Goodreads:** the public API closed in December 2020. The link (`/book/show/15704307-saga-volume-1`) only has Goodreads' internal number, and scraping the page goes against the terms. The ISBN appears on the page, under "Edition details".
- That's why the Review uses only the ISBN from either: the one in the Amazon link, or an ISBN pasted by the user. The lookup goes through Open Library and Google Books.

### ComicVine API
- Base: `https://comicvine.gamespot.com/api/`. Parameters: `api_key`, `format=json`, `field_list` to trim the response. Requires a custom User-Agent.
- Search: `search/?resources=volume&query=...` worked. Example for "Deadly Class":
  - the single-issue series is "Deadly Class" (Image, 2014, 56 issues);
  - **collected editions show up as volumes of their own**: "Deadly Class: Reagan Youth" (2014, 1 issue), "Deadly Class: The Snake Pit", "Deadly Class: Carousel", etc.;
  - foreign editions show up too (Urban Comics, Cross Cult), so filtering by publisher and language is needed.
- Usage limits: there is a limit per resource per hour (official documentation; confirm the current number). Use a cache, rate limiting and backoff between retries.
- Terms: the key is personal; each user of the app uses their own.
- **Verified on 2026-10-17 with real calls** (F2):
  - Documented limit: 200 requests per resource (`search`, `issues`, `issue`…) per hour, plus velocity detection. The app spaces calls 1.2 s apart and stops at 190 per resource per hour.
  - A collected edition is a volume of its own with 1 issue, and the **issue name** carries the TPB number: "Deadly Class: A Fond Farewell Part One" → issue "Volume 11"; "Deadly Class: Reagan Youth" → "Vol. 1". The volume description says "Trade paperback collecting …".
  - `issues/?filter=volume:A|B|C` brings the issues of several volumes in one call; `filter=volume:A|B,issue_number:11` combines both filters (it worked).
  - Issue detail (`issue/4000-ID`): `person_credits` with a `role` per person, sometimes several separated by commas ("letterer, designer"); `store_date` (on-sale date) and `cover_date` (months ahead). The app uses `store_date`.
  - Error responses come with HTTP 200 and a `status_code`: 100 = invalid key, 107 = rate limit exceeded, 101 = not found.
  - ComicVine has no ISBN search; the ISBN goes to Open Library, and its title guides the ComicVine search.
  - Cost per book: 1 search + 1 or 2 issue listings + 1 detail ≈ 200 books per hour.
- **Seen on 2026-10-30 in the app's cache** (titles of the maintainer's 636 books in the Review):
  - Between 2026-10-23 and 2026-10-26, 470 `search` answers came with `status_code` 1 but `publisher: null` and `count_of_issues: 0` for **every** volume; the same searches, before and after, were complete. Read as true, a 72-issue series looks like a one-shot. The app now refuses such an answer (an error, the book can be searched again later) and doesn't keep it in the cache.
  - The search ranks by words: "RoboCop last stand" brings *Robocop: Last Stand* (Boom!, 2013) first, "RoboCop" alone doesn't bring it at all. Likewise, "The Boys" brings the single issues and the German and French editions, not Dynamite's trade paperbacks.
  - Collected editions are also issues of a volume named like the series, with the subtitle as the **issue name**: *The Boys* (Dynamite, 2007, 12 issues), issue #11 "Over the Hill with the Swords of a Thousand Men"; *Richard Stark's Parker* (IDW), issue #1 "The Hunter".
  - Issue names also carry story arcs that look like book numbers: issues #1-#5 of *Wynd* (Boom!, 2020) are all named "Book One: The Flight of the Prince"; the trade paperback is issue #1 of another *Wynd* volume (2021).
  - Some volumes start with the creator's name: "Richard Stark's Parker", "Frank Miller's RoboCop", "Michael Turner's Fathom".

### Claude Code (spike of 2026-11-21, CLI 2.1.316)
Spike for F9 (SPEC §8.4).
- **Material**: 14 books from the maintainer's Review queue, copied to a test folder. For 9 of them the algorithm had found no result; the other 5 had only weak or wrong candidates.
- **Each run**: `claude -p` in a folder holding `book.json`, the cover and 5 inner pages. ComicVine came through a small MCP server of our own, and the web was limited to a list of sites.

**Command line** (all of these flags exist in 2.1.316):
- `--model`, `--effort low|medium|high|xhigh|max`, `--output-format json`;
- `--json-schema` (the answer comes in `structured_output`), `--append-system-prompt`;
- `--restricted` (no Bash and similar tools; user settings ignored; `--settings` still applies);
- `--tools Read,WebSearch,WebFetch`, `--strict-mcp-config`, `--mcp-config`, `--settings`;
- `--permission-mode dontAsk`, `--no-session-persistence`, `--max-budget-usd`.

The JSON output also has `is_error`, `terminal_reason`, `num_turns`, `duration_ms` and `total_cost_usd`. On a subscription, `total_cost_usd` is only what the API would have charged; the user isn't billed for it.

**Login and environment**:
- The command line has its own login, separate from the desktop app's. `claude auth status` answers JSON with `loggedIn` and `authMethod`.
- Started from inside another Claude session, the child inherits `CLAUDE_*` and `ANTHROPIC_*` (`ANTHROPIC_BASE_URL` among them) and fails with "OAuth session expired". Start it without those variables.
- Without `< /dev/null`, `-p` waits 3 s for stdin.

**ComicVine and the web**:
- Claude's `WebFetch` of comicvine.gamespot.com gets **HTTP 403**, so ComicVine has to come through the app (MCP).
- With the MCP, almost all the work came from ComicVine, the cover and the credits page: the web search was used only 3 times in 28 runs.
- A `PreToolUse` hook given in `--settings` can rewrite a tool's input (`updatedInput`) and allow or deny it, so it can force `allowed_domains` on `WebSearch` and deny a `WebFetch` outside the list.
- With `humblebundle.com` in `allowed_domains`, **every** search failed ("not accessible to our user agent"); without it, they worked.

**Results of the quick search**:
- Haiku (effort low) and Sonnet (effort medium) took 13 to 41 s per book. Both found a ComicVine record for all 14 books and agreed on 12. In the other 2, Haiku gave only the series and Sonnet the edition.
- The titles that said nothing were solved from the cover and the credits page: "BTMMFDRS" → *BTTM FDRS* (Fantagraphics, 2019); "SKYBOUND LOGO white.eps" → *Outcast #1*; "Monstress_Template" → *Monstress #1*; "RoboCop - Dead or Alive #7" → *RoboCop* (Boom!, 2014) #7, where the algorithm had proposed *Crossed*.
- Sonnet didn't open the inner pages when it wasn't told their names (there is no Glob tool); Haiku tried names and read them, and got the ISBN from the credits page of 4 books. `book.json` has to list the images.
- The model's confidence doesn't track what it checked: Sonnet gave 0.85 to books whose credits page it had not looked at.

**Results of the deep search** (Sonnet, effort medium, on 2 books, about 35 s each):
- *Snotgirl Vol. 4*: ComicVine has the series and issues #16-20 but not the trade paperback. The deep search got the ISBN, the date, the page count and the summary from imagecomics.com (the page was fetched), with the source of each field.
- *Wynd Book Three*: it compared the file's cover with the ComicVine record's (saved by the MCP for it to look at): same book, different art. The credits page lists the regular ISBN and a "Limited Edition" ISBN, which League of Comic Geeks lists as the hardcover.
- Error: on the Snotgirl credits page, "Image Comics logo designed by Rob Liefeld" was read as a "book design" credit. Credits from that page need the user's check.
- Amazon was never fetched: its data came only from search results.

**One run for a group** (the 14 books grouped by title as *The Boys*; Haiku and Sonnet, effort medium):
- **Material**: 11 trade paperbacks titled "Vol. 1" to "Vol. 12" (no Vol. 5), *Herogasm*, and *Dear Becky* #1 and #2. Each run got `group.json`, the 14 covers at about 500 px and the credits pages of only 2 books.
- **Cost**: about 2.5 min per run (154 s Sonnet, 166 s Haiku), with 30 and 35 ComicVine calls. No web search was needed.
- **The pattern**: both models found how ComicVine catalogues the series. Each trade paperback is its own one-issue volume ("The Boys: Get Some", 4050-…), whose issue is named "Vol. 2". The algorithm's candidates were single issues of the main volume 4050-18033, all wrong.
- **Records**: both models gave the same 14 edition records (4000-217033 … 4000-370329 for the trades, 4000-766280 and 4000-772465 for Dear Becky). Both saw that *Herogasm* is Vol. 5, so the run is complete from 1 to 12.
- **The spin-off**:
  - Haiku put *Dear Becky* (a separate 2020 mini-series) in "doesn't belong", with the suggested series "The Boys: Dear Becky";
  - Sonnet kept it in the group and only reported the numbers 1 and 2 as repeated, with a note to split it.
- **Covers**: the models opened only 2 or 3 ComicVine covers and matched the rest by title and number. Afterwards all 14 ComicVine covers were downloaded and compared by dHash: all matched, at 0 to 8 bits of 64. The app has to check every row itself.

### Open Library
- `https://openlibrary.org/api/books?bibkeys=ISBN:<isbn>&format=json&jscmd=data` got it right the first time:
  - `9781534321236` → *Deadly Class, Vol. 11* (Rick Remender, Wes Craig, 2022);
  - `9781607067238` → *Saga* (Brian K. Vaughan, Fiona Staples, 2012).
- No key. A good first step when there is an ISBN.
- Authors come **without a role** (`9781534321236` lists writer Rick Remender and artist Wes Craig as authors), so the app doesn't use those names as credits; credits come from ComicVine.

### Google Books
- `https://www.googleapis.com/books/v1/volumes?q=isbn:...` answered **429** (too many requests) without a key. That's why it is optional and off by default.

### Typical metadata problems (from the analysis of 661 books)
- Titles that are file names (`somethingiskillingthechildren vol3`, `wyndbookone flightoftheprince`) with the author "Unknown".
- Several authors glued into one (Calibre swaps commas for `|`): `James Tynion IV| Chris O\'Halloran| Gavin Fullerton`.
- The Humble Bundle date (Dec 10–14, 2015) used as the publication date.
- Empty series on titles with "Vol. N".
- Authors with 5 to 12 names, mixing writers with colorists and letterers.
- Fake ISBNs (`hum-irredeemable_vol3`, Diamond codes such as `MAY160548`).
- The scoring rule is in `reference/python/analyze.py`.

## Comic files

### Humble PDFs
Observed in 3 books:
- **CLOSET Vol. 1** (114 pp.) and **Dead Lucky Vol. 2** (87 pp.): each page is **a single JPEG** covering the whole page (about 1988×3056), with no text or vectors. The original bytes can be extracted losslessly.
- **Radiant Black Vol. 5** (170 pp.): only 17 pages are like that. The others have several overlapping images (PNG with a mask) and **vector lettering** on top, so they have to be **rendered**: at the resolution of the largest image (about 2000×3060), JPEG quality 92.
- Validated heuristic (`reference/python/pdf2epub.py`): single image + covers the page (2 pt tolerance) + no rotation + no text + no vector drawings + RGB/gray JPEG without a mask → copy the bytes. Otherwise → render.
- Sizes: a 104 MB PDF → 105 MB output (lossless copy); 381 MB → 255 MB (rendered).
- The script uses PyMuPDF (**AGPL — do not use in the app**). In C#, PDFium itself does both: `FPDFPage_CountObjects`/`FPDFPage_GetObject`, `FPDFPageObj_GetBounds`/`GetMatrix`, `FPDFImageObj_GetImageFilter` and `GetImageDataRaw` (undecoded JPEG bytes) to inspect and extract, and `FPDF_RenderPageBitmap` to render.

### EPUBs
- Calibre's PDF→EPUB conversion produces reflowable EPUB with PNGs of up to 50 MB per page and files 2–3× larger than the PDF. Avoid.
- Publisher/Humble EPUBs usually have one image per page (BOOM uses `hum-<book>-0001.xhtml`; Image uses `OPS/fb.opf`; IDW includes `com.apple.ibooks.display-options.xml`). To extract pages, follow the OPF *spine* order.
- A fixed-layout EPUB has `rendition:layout = pre-paginated` in the OPF.

## Kindle Colorsoft
- Kindles from 2024 on use **MTP** instead of USB mass storage. On Windows, MTP is native: the Kindle shows up as a portable device, and you have to choose "Transfer files" in the Kindle's USB notification and use a data cable.
- **KCC (Kindle Comic Converter)** has the **`KCS` = Kindle Colorsoft (1272×1696)** profile and recommends **MOBI** (dual MOBI/AZW3) for modern Kindles. Command line: `kcc-c2e [options] [input]`, with `-p` (profile), `-f` (format) and `-o` (output).
- **Panel View**: on KCC files, it is turned on in the Kindle (**Aa** menu) and works with a double tap. It moves through regions of the page, not through the real panels.
- **Send to Kindle** compresses the images and doesn't accept MOBI/AZW3. The Colorsoft doesn't read CBZ.
- **KCC license: ISC** (permissive; keep the copyright notice). KCC uses KindleGen, which comes with Kindle Previewer (Windows and macOS only), to produce MOBI.

## Komga
- Creates **a series for every subfolder that contains books**, at any depth. Each file becomes a book of the series of its folder.
  - Not confirmed: whether folders without files, such as `Publisher/`, are ignored. Test it.
- **One-Shots directory** (a library option): any folder whose path contains the configured text (e.g. `_oneshots`) produces a one-book series per file. Prefixing it with `/` makes it match only the start of the folder name.
  - For one-shots, the series title and summary come from the book, the status is "Ended" and the count is 1.
- Formats: CBZ, CBR, PDF, EPUB. Imports metadata from `ComicInfo.xml` (CBZ/CBR) and from the OPF (EPUB).
- EPUB: supported since 1.8.0, with its own web reader for reflowable books. Since 1.9.0, image-only pre-paginated EPUBs use the comic reader.
- REST API (checked in the source code, gotson/komga's `LibraryController.kt`):
  - authentication by an API key in the `X-API-Key` header (the key is created in the user's account, in Komga);
  - `GET /api/v1/libraries` → a list with `id`, `name`, `root` and `oneshotsDirectory` (among others);
  - `POST /api/v1/libraries/{id}/scan` (optional `deep` parameter) → 202; requires the ADMIN role, otherwise 403.
- Sync with KOReader: OPDS v1 and the `KOREADER_SYNC` role.

## Sources
- KCC: https://github.com/ciromattia/kcc · license: https://github.com/ciromattia/kcc/blob/master/LICENSE.txt
- Komga: https://github.com/gotson/komga/blob/master/komga/src/main/kotlin/org/gotson/komga/interfaces/api/rest/LibraryController.kt · https://komga.org/docs/guides/scan-analysis-refresh · https://komga.org/docs/guides/oneshots · https://komga.org/docs/introduction · https://komga.org/blog/ebook-support · https://komga.org/docs/guides/koreader
- MTP on the new Kindles: https://goodereader.com/blog/kindle/new-kindle-e-readers-no-longer-appear-on-computers
- Calibre FAQ: https://manual.calibre-ebook.com/faq.html
- MobileRead (Colorsoft + KCC): https://www.mobileread.com/forums/showthread.php?p=4461987
