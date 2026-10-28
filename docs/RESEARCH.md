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
