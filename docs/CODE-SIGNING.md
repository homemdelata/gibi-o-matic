# Code signing and privacy

> **Status:** the Windows installer is not signed. SignPath Foundation declined the project's application in
> November 2026, and the project doesn't buy a certificate for now. Windows may warn about an "unknown publisher"
> the first time: click *More info* → *Run anyway*.

## How releases are built

The Windows installer (`Gibi-o-matic-win.msi`) and the Linux AppImage are built by the
[`release.yml`](../.github/workflows/release.yml) workflow on GitHub Actions from a `v*` tag, never on a personal
machine, from this repository's source code. Changes reach `main` only through a pull request with a green CI.

## Privacy policy

Gibi-o-matic has no telemetry and no accounts, and the contents of your files never leave your computer. It only
goes online for these things:

- **Looking for new versions** on GitHub, after it starts and once a day. You can turn this off in
  *Settings → General → Look for updates automatically*. The installer shows this notice before installing.
- **Metadata lookups** when you import or identify a book: Open Library, ComicVine (with your own API key)
  and Google Books (only if you turn it on). Each source can be turned off in *Settings → Metadata*.
- **Downloading Kindle Comic Converter** from GitHub, only when you ask.

Each service's own policy covers what it does with a request:
[GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Open Library (Internet Archive)](https://archive.org/about/terms),
[ComicVine (Fandom)](https://www.fandom.com/privacy-policy) and
[Google Books](https://policies.google.com/privacy).

## Uninstalling

- **Windows:** *Settings → Apps → Installed apps → Gibi-o-matic → Uninstall*.
- **Linux:** delete `Gibi-o-matic.AppImage`.

Uninstalling removes only the program. Your library, settings and index stay where they are, as listed in
[Where things live](../README.md#where-things-live); delete those folders by hand to remove everything.
