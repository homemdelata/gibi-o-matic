# Reference implementations (Python)

Scripts written and validated while analyzing the old Calibre library. **They exist to port the logic to C#**, not to run in the app.

| File | What it does | Ported to |
|---|---|---|
| `analyze.py` | Metadata quality score (0–100) and a list of problems per field (title, authors, date, series, secondary). It reads a CSV exported from Calibre, but the rules don't depend on that. | `GibiOMatic.Core` (F1), SPEC §5 |
| `pdf2epub.py` | Comic PDF → pages: extracts the original JPEG when the page is a single image covering it entirely, otherwise renders it. Produces a fixed-layout EPUB. | `GibiOMatic.Conversion` (F1 cover, F3 CBZ), SPEC §9.2 |

⚠️ `pdf2epub.py` uses **PyMuPDF (AGPL)**. The app uses PDFium instead (see `docs/THIRD-PARTY.md`). Copy the heuristic, not the library.

Details and test results are in `docs/RESEARCH.md`, section "Humble PDFs".
