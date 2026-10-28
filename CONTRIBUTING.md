# Contributing to Gibi-o-matic

Thanks for your interest! Gibi-o-matic is a personal project maintained in spare time. Contributions are welcome,
and this guide explains how to make them land smoothly. Issues, pull requests and discussions can be in
**English or Brazilian Portuguese**.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- **Report a bug.** Open an issue with the *Bug report* form. The app's log
  (`%LOCALAPPDATA%\GibiOMatic\logs`, Linux: `~/.local/share/gibiomatic/logs`) usually tells the story; please
  remove anything personal from it before attaching it.
- **Suggest a feature.** Open an issue with the *Feature request* form and describe the problem before the
  solution.
- **Fix a bug.** Bug fixes can go straight to a pull request. If the fix is large or changes behavior, open an
  issue first.
- **Add a feature or change a screen.** Please **open an issue first** and wait for a go-ahead before writing
  code. The app has a specification and a visual design, and it is much easier to agree on the shape of a change
  before the work is done than to rework it afterwards.
- **Translate.** The interface lives in `src/GibiOMatic.App/Resources/Strings*.resx`. Corrections to the English
  text are very welcome; for a new language, open an issue first.
- **Security issues** go through private reporting, never public issues: see [`SECURITY.md`](SECURITY.md).

Please never attach comic files (or links to them) to issues or pull requests, even to reproduce a bug. Describe
the file instead (format, page count, where it came from), or build a small synthetic one.

## Development setup

- **.NET 10 SDK**; the exact version is pinned in [`global.json`](global.json).
- Windows 10/11 or Linux. Sending to a Kindle over USB (MTP) only exists on Windows.
- Any editor works: Visual Studio, Rider or VS Code with the C# Dev Kit.

```
dotnet build GibiOMatic.sln
dotnet test --solution GibiOMatic.sln
dotnet run --project src/GibiOMatic.App
```

The tests run on Microsoft.Testing.Platform (see `global.json`), so `dotnet test` needs `--solution`.

Try your changes against a **test library in a scratch folder**, not your real collection. The library format is
plain folders and JSON, so an empty folder is enough to start.

### ComicVine API key

The tests never call real APIs, except the integration tests, which are skipped when there is no key. To run the
app or those tests against ComicVine, get a free key at [comicvine.gamespot.com/api](https://comicvine.gamespot.com/api/)
and use one of these (never put the key in code or in the repository):

- .NET user secrets (read in Debug builds only):
  `dotnet user-secrets set ComicVine:ApiKey <key> --project src/GibiOMatic.App`
- the `GIBIOMATIC_COMICVINE_KEY` environment variable.

## Project layout

| Project | Contents |
|---|---|
| `src/GibiOMatic.App` | Avalonia: Views, ViewModels (MVVM with CommunityToolkit.Mvvm), themes, interface text (`Resources/Strings*.resx`), tray, updates |
| `src/GibiOMatic.Core` | domain, quality score, settings, secrets |
| `src/GibiOMatic.Library` | on-disk format, `metadata.json`, SQLite index, Inbox (import and watched folder), identification, editing |
| `src/GibiOMatic.Metadata` | sources (ComicVine, Open Library, Google Books), cache, rate limits, identification |
| `src/GibiOMatic.Conversion` | reading PDF, CBZ, CBR and EPUB, building CBZ, `ComicInfo.xml`, KCC |
| `src/GibiOMatic.Destinations` | Komga and Kindle destinations and synchronization |
| `tests/` | one test project per project; `GibiOMatic.Testing` generates the synthetic files |
| `tools/` | `publish.ps1` (installers), `Screenshots` (documentation screenshots), `IconGen` (icon) |
| `docs/` | specification, roadmap, design, research and decisions (in Brazilian Portuguese) |

When in doubt about how something should behave, [`docs/SPEC.md`](docs/SPEC.md) is the source of truth, and
[`docs/DECISIONS.md`](docs/DECISIONS.md) explains why things are the way they are. Those documents are in
Portuguese; a machine translation is fine for reading them, and you don't need to write in Portuguese to
contribute.

## Ground rules

These are not up for negotiation in a pull request; if you think one is wrong, open an issue.

1. **Originals are never modified.** After import they are read-only; metadata goes to `metadata.json` and to the
   destination copies.
2. **No comics, keys or personal data in the repository.** Tests generate tiny synthetic PDFs, CBZs, CBRs and
   EPUBs on the fly (`tests/GibiOMatic.Testing`).
3. **No GPL or AGPL dependencies** (for example MuPDF/PyMuPDF, or Calibre as a library). The project is MIT and
   its dependencies must be permissive. Check the license before adding a package, add the version to
   `Directory.Packages.props` and record it in [`docs/THIRD-PARTY.md`](docs/THIRD-PARTY.md).
4. **Be polite to external APIs:** our own User-Agent, a local cache, rate limits and retries with backoff.
5. **Batch operations** show a preview first, then ask for confirmation, and everything goes to the log.

## Code style

- Code, identifiers and comments in **English**.
- Interface text always goes in the `resx` files, in **Brazilian Portuguese** (`Strings.resx`, the default) and
  **English** (`Strings.en.resx`). A test checks that both have the same keys and the same placeholders. If you
  don't speak Portuguese, add the English text to both files and say so in the pull request.
- Modern C#: nullable reference types, `async`/`await` with `CancellationToken`, dependency injection, MVVM with
  the CommunityToolkit source generators, and no logic in the Views.
- Colors only through the tokens in `Themes/Tokens.axaml`, with `DynamicResource`. Both themes define the same
  keys, and text contrast is tested (WCAG AA). A button without text needs `AutomationProperties.Name`.
- Compiler warnings are errors (`TreatWarningsAsErrors`).
- Match the surrounding code: naming, comment density and idioms.

## Tests

- Every bug fix should come with a test that fails without it, when that is practical.
- New behavior needs tests in the matching `tests/` project.
- Build test inputs with the helpers in `tests/GibiOMatic.Testing`; never read files from outside the test's
  temporary folder.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep the change focused: one fix or feature per pull request.
3. Make sure `dotnet build` and `dotnet test --solution GibiOMatic.sln` pass. The CI runs both on Windows and Linux.
4. If you changed a screen, regenerate the screenshots with `dotnet run --project tools/Screenshots` (Portuguese)
   and `dotnet run --project tools/Screenshots -- docs/images en` (English). They use a fictional library in a
   neutral folder.
5. A new technical decision goes in [`docs/DECISIONS.md`](docs/DECISIONS.md) and a new dependency in
   [`docs/THIRD-PARTY.md`](docs/THIRD-PARTY.md). Writing those entries in English is fine.
6. Open the pull request and fill in the template. Link the issue it closes.

Commits should be small and have a clear message saying what changed and why. The existing history is in
Portuguese; English messages are just as welcome.

Using AI coding assistants is fine (the project itself is developed with Claude Code, following
[`CLAUDE.md`](CLAUDE.md)). You are still responsible for the change: read it, understand it, and test it before
asking for a review.

### Review

This is a spare-time project, so a review may take a few days, sometimes longer. A pull request may be declined if
it doesn't fit the project's scope (see "What it is not" in the [README](README.md)); opening an issue first is
the best way to avoid that.

## License

By contributing, you agree that your contributions are licensed under the project's [MIT License](LICENSE).

## Releases (maintainers)

The version comes from the latest `vX.Y.Z` git tag (MinVer). To release:

```
git tag v0.6.0
git push origin v0.6.0
```

The `release.yml` workflow runs the tests, builds the Windows installer and the Linux AppImage (Velopack) and
creates a **draft** GitHub release. Publishing the draft is what makes installed apps see the update. Tags with a
`-` (`v0.7.0-beta.1`) become pre-releases. To build the packages locally:
`powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime linux-x64]`.

To test an update without GitHub, set the `GIBIOMATIC_UPDATE_SOURCE` environment variable to the packages folder
(`artifacts\releases\win-x64`): the installed app then looks for new versions there. Install one version, build
another (after a new commit) into the same folder, and open the app.
