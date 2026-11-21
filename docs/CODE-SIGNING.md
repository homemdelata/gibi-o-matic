# Code signing and privacy

> **Status:** the Windows installer is not signed. SignPath Foundation declined the project's application in
> November 2026, and the project doesn't buy a certificate for now. Windows may warn about an "unknown publisher"
> the first time: click *More info* → *Run anyway*.

## How releases are built

The Windows installer (`Gibi-o-matic-win.msi`) and the Linux AppImage are built by the
[`release.yml`](../.github/workflows/release.yml) workflow on GitHub Actions from a `v*` tag, never on a personal
machine, from this repository's source code. Changes reach `main` only through a pull request with a green CI.

## Privacy policy

Gibi-o-matic has no telemetry and no accounts, and the contents of your files never leave your computer unless
you turn on Claude Code (below). It only goes online for these things:

- **Looking for new versions** on GitHub, after it starts and once a day. You can turn this off in
  *Settings → General → Look for updates automatically*. The installer shows this notice before installing.
- **Metadata lookups** when you import or identify a book: Open Library, ComicVine (with your own API key)
  and Google Books (only if you turn it on). Each source can be turned off in *Settings → Metadata*.
- **Downloading Kindle Comic Converter** from GitHub, only when you ask.
- **Claude Code**, only if you turn it on in *Settings → Claude* (off by default) and have it installed and logged
  in. For each book it asks about, the app gives your own Claude Code the book's cover, a few inner pages (the ones
  after the cover and the last two, where the credits usually are), the title, the file name and the candidates
  found; Claude Code sends them to Anthropic through your account, under your plan. Its web searches are limited to
  a list of sites you can edit, and it reaches ComicVine only through the app, so your ComicVine key never leaves
  the app. Nothing else from the library is sent.

Each service's own policy covers what it does with a request:
[GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Open Library (Internet Archive)](https://archive.org/about/terms),
[ComicVine (Fandom)](https://www.fandom.com/privacy-policy),
[Google Books](https://policies.google.com/privacy) and
[Anthropic](https://www.anthropic.com/legal/privacy).

## Uninstalling

- **Windows:** *Settings → Apps → Installed apps → Gibi-o-matic → Uninstall*.
- **Linux:** delete `Gibi-o-matic.AppImage`.

Uninstalling removes only the program. Your library, settings and index stay where they are, as listed in
[Where things live](../README.md#where-things-live); delete those folders by hand to remove everything.
