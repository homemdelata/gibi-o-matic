# Gibi-o-matic

Desktop app (C# / .NET 10 / Avalonia 12) to organize a personal collection of purchased digital comics (Humble Bundle, etc.):
an archive of untouched originals + correct metadata → reading copies generated for **destinations** (Komga, Kindle).
Open source, MIT license: `github.com/homemdelata/gibi-o-matic`.

If there is a `CLAUDE.local.md` (outside git), it holds the context of the current user's machine and collection; follow it too.

## Read before implementing
| Document | What for |
|---|---|
| `docs/SPEC.md` | Functional and technical **source of truth**. When in doubt, it wins. |
| `docs/ROADMAP.md` | Phases with tasks, done criteria and status. Start here to know what to do now. |
| `docs/DESIGN.md` + `docs/design/prototype/` | Interface: color/typography tokens, components and the prototype screens (HTML visual reference). |
| `docs/RESEARCH.md` | Findings already validated (APIs, file formats, Kindle, Komga), with sources. Avoids repeating research. |
| `docs/DECISIONS.md` | Decisions already made (don't reopen them without a reason). Add new technical decisions here. |
| `docs/THIRD-PARTY.md` | Dependencies and licenses. Update it when adding a package. |
| `CONTRIBUTING.md` | Rules for contributors. |
| `reference/python/` | **Validated** reference implementations (quality score, PDF→images). Port the logic to C#; don't use them in production. |

## How to work
- **One phase at a time**, in `ROADMAP.md` order. When a phase is done:
  1. green build and tests;
  2. tick the tasks and the status in `ROADMAP.md`;
  3. commit on a branch, push it and open a pull request; squash-merge it once the CI is green;
  4. **stop** and explain to the user how to test what was delivered (concrete steps).

  Only move on to the next phase after their OK.
- **Product decisions** (behavior, screens, text): ask the user. **Small technical decisions**: decide and record them in `docs/DECISIONS.md`.
- If something in the SPEC is wrong or unfeasible, say so and propose the change; don't deviate silently.
- **Commits**: small, message in English, with the attribution lines the environment indicates.
- **`main` is protected**: changes reach it through a pull request with a green CI (Windows and Linux), squash-merged. Never push straight to `main` or force-push it; the admin bypass is the user's, for emergencies.

## Language and style
- **Everything in the repository is in English**: code, identifiers, comments, documentation (`docs/`), `README.md`, `CONTRIBUTING.md` and commit messages.
- The exceptions are the Portuguese interface (`Strings.resx`) and `README.pt-BR.md`, the README's translation: when one README changes, change the other.
- Interface text in `resx`, with **pt-BR** (`Strings.resx`, the default) and **en** (`Strings.en.resx`).
- Modern C#: nullable enabled, `async`/`await` with `CancellationToken`, MVVM with CommunityToolkit.Mvvm (source generators), dependency injection (Microsoft.Extensions.DependencyInjection), no logic in the Views.

## Inviolable rules
1. **Never modify the library's original files.** Originals are read-only after import; every metadata write goes to `metadata.json`.
2. **Never commit** comics (CBZ/CBR/PDF/EPUB/AZW3), API keys or personal data. Tests use **synthetic files generated in the test itself**: minimal PDFs, CBZs and EPUBs.
3. **No AGPL/GPL dependencies** (e.g. MuPDF/PyMuPDF, even though the reference script uses it). Check the license before adding any package and record it in `docs/THIRD-PARTY.md`.
4. **The ComicVine key is never in the code or in the repository.**
   - In development it lives in .NET *user secrets*, outside the repository:
     - `UserSecretsId` = `3c6e08f6-15a8-4547-9a68-52954af24a45` (put exactly this value in `<UserSecretsId>` in the `GibiOMatic.App` `.csproj` and in integration test projects that need it);
     - configuration key `ComicVine:ApiKey`;
     - file `%APPDATA%\Microsoft\UserSecrets\3c6e08f6-15a8-4547-9a68-52954af24a45\secrets.json`.
     - Read it through `IConfiguration` (`AddUserSecrets`) only in Debug. Never print the key in logs or in the terminal output.
   - Also accept the `GIBIOMATIC_COMICVINE_KEY` environment variable, for the CI and other developers.
   - In production: the user types the key in Settings, and it is stored protected (DPAPI on Windows).
   - Tests that call the real API are marked as integration tests and skipped when there is no key.
5. **External APIs**: our own User-Agent, a local cache of responses, rate limiting and retries with backoff. Never hammer ComicVine.
6. **Don't touch the user's real library, destination folders (Komga) or Kindle** without asking first. For manual tests, use a test folder.
7. Batch operations: dry run first, then confirmation and a log.

## Stack and commands
- **.NET 10 SDK.** If `dotnet --list-sdks` doesn't show 10.x, ask for permission and install it with `winget install Microsoft.DotNet.SDK.10`. The version is pinned in `global.json`.
- Avalonia 12, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite + Dapper, Serilog, xUnit. Central package management (`Directory.Packages.props`).
- Commands:
  - `dotnet build GibiOMatic.sln`
  - `dotnet test --solution GibiOMatic.sln` (Microsoft.Testing.Platform, see `global.json`)
  - `dotnet run --project src/GibiOMatic.App`
  - `powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime linux-x64]` → Velopack installer in `artifacts\releases\<rid>\` (`Gibi-o-matic-win.msi`, `Gibi-o-matic.AppImage`)
  - `dotnet run --project tools/Screenshots` → regenerates the English screenshots in `docs/images` (fictional library); with `-- docs/images pt-BR`, the `-pt-BR` set used by `README.pt-BR.md`
  - `dotnet run --project tools/Reidentify -- report <folder>` (app closed) → checks the approved books against the current identification and writes `report.md` and `plan.json` without changing anything; `-- apply <folder>/plan.json` saves the entries marked `"apply": true`
- **Version**: comes from the latest `vX.Y.Z` git tag (MinVer). To release a version: `/release [patch|minor|major]` (`.claude/skills/release`), or by hand `git tag -a v0.6.0 -m "Gibi-o-matic 0.6.0"` and `git push origin v0.6.0`; the `release.yml` workflow builds the installers and creates a **draft** GitHub release, which has to be published by hand for installed apps to see the update.

## Solution layout
```
GibiOMatic.sln
global.json  Directory.Build.props  Directory.Packages.props
src/
  GibiOMatic.App/            # Avalonia: Views, ViewModels, themes, Assets (fonts), Resources (resx)
  GibiOMatic.Core/           # domain, quality rules, service interfaces
  GibiOMatic.Library/        # on-disk format, metadata.json, SQLite index, import
  GibiOMatic.Metadata/       # IMetadataSource: OpenLibrary, ComicVine, GoogleBooks; identification
  GibiOMatic.Conversion/     # PDF/EPUB/CBZ/CBR → pages, building CBZ, ComicInfo.xml, running KCC
  GibiOMatic.Destinations/   # IDestinationType, Komga, Kindle
tests/
  GibiOMatic.*.Tests/
tools/  docs/  reference/  .github/
```
