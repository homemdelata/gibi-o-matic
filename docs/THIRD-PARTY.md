# Third-party dependencies

Every dependency needs a license compatible with MIT (no AGPL/GPL). Update this table when adding or removing packages.

| Component | Version | Use | License | Status |
|---|---|---|---|---|
| Avalonia (+ Desktop, Themes.Fluent) | 12.1.4 | UI | MIT | in use (F0) |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM | MIT | in use (F0) |
| Microsoft.Extensions.DependencyInjection (+ Abstractions) / Configuration (+ Abstractions, UserSecrets, EnvironmentVariables) / Logging | 10.0.x | DI, configuration, logging | MIT | in use (F0/F2) |
| System.Security.Cryptography.ProtectedData | 10.0.x | DPAPI (secrets on Windows) | MIT | in use (F0) |
| Microsoft.Data.Sqlite | 10.0.x | index | MIT | in use (F0) |
| Dapper | 2.1.89 | data access | Apache-2.0 | in use (F0) |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4 / 10.0 / 7.0 | file logging | Apache-2.0 | in use (F0) |
| xUnit v3 | 4.0.2 | tests | Apache-2.0 | in use (F0) |
| Avalonia.Headless | 12.1.4 | only `tools/Screenshots` (documentation screenshots); not shipped in the app | MIT | in use (F6) |
| Velopack (library) and vpk (tool, `dotnet-tools.json`) | 1.2.161 | installer, updates (GitHub Releases) and AppImage | MIT | in use (F6) |
| MinVer | 8.0.0 | assembly version from the git `v*` tag; build only, not shipped in the app | Apache-2.0 | in use (F6) |
| Anybody (Etcetera Type Co.) | — | font for titles and large numbers (embedded, `Assets/Fonts`); replaced Bricolage Grotesque. The static Bold (700) and ExtraBold (800) TTFs, width 100, are instances of the variable font from google/fonts (`ofl/anybody`), generated with `fontTools.varLib.instancer`, like the `static/` folder of the Google Fonts package | SIL OFL 1.1 (no Reserved Font Name) | in use (F7) |
| IBM Plex Sans, IBM Plex Mono | — | text fonts (embedded, `Assets/Fonts`) | SIL OFL 1.1 (Reserved Font Name "Plex") | in use (F0) |
| Lucide (folder-plus, folder-open, rotate-cw icons) | — | icons, as geometry in `Themes/Icons.axaml` | ISC | in use (F0) |
| PDFium (`bblanchon.PDFium.Win32` and `.Linux`) | 157.0.8100 | render PDF pages (cover, CBZ), PDF Info, extract the pages' original JPEG; called through our own P/Invoke | Apache-2.0 (binaries); PDFium: BSD-3-Clause | in use (F1) |
| SharpCompress | 1.0.0 | read CBZ and CBR (RAR) | MIT (RAR decompression derives from unrar, whose license only forbids recreating the RAR compressor) | in use (F1) |
| SkiaSharp (+ NativeAssets.Linux) | 3.119.4 | covers and thumbnails; the same version as Avalonia.Skia | MIT | in use (F1) |
| Avalonia.Controls.WebView | 12.1.0 | reader: text EPUB, with the system's web engine (WebView2 on Windows, WebKitGTK on Linux); no embedded Chromium | MIT | in use (F3.5) |
| Lucide (reader icons: chevron-right, chevrons, book-open, maximize, list, columns) | — | icons, as geometry in `Themes/Icons.axaml` | ISC | in use (F3.5) |
| MediaDevices | 1.10.0 | Kindle over USB (MTP / Windows Portable Devices); 1.10 has a plain `net7.0` target (2.0 only has `net*-windows`), so the project stays cross-platform and MTP is only used on Windows | MIT (confirmed in the package) | in use (F4) |
| KCC (Kindle Comic Converter), `kcc-c2e` | 12.0.0 | conversion for Kindle, called as an **external process**. **Not distributed with the app**: the user installs it, or the app downloads the file from the official GitHub release when the user asks, checking the SHA-256 pinned in the code. The KCC executable includes PyMuPDF (AGPL) and 7-Zip; since the app only runs it and doesn't redistribute it, none of that goes into the app | ISC (KCC) | in use (F4) |
| KindleGen (from Kindle Previewer, Amazon) | — | used by KCC to produce MOBI; comes with Kindle Previewer, which the user installs | Amazon's license (not redistributed) | external dependency (F4) |

The font licenses (`OFL-*.txt`) ship with the files in `src/GibiOMatic.App/Assets/Fonts/`. The fonts are distributed unmodified, except Anybody, which ships as static instances of the variable font (allowed by the OFL).

**Do not use**: MuPDF / PyMuPDF (AGPL), Calibre as a library (GPL).
