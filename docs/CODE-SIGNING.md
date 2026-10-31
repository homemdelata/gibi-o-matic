# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

> **Status:** the application to SignPath Foundation is pending. Until it is approved, the Windows installer is
> not signed, and Windows may warn about an "unknown publisher" the first time.

## What is signed

Only the Windows release of Gibi-o-matic: the installer (`Gibi-o-matic-win.msi`) and the executables built from
this repository's source code. The [`release.yml`](../.github/workflows/release.yml) workflow builds them on
GitHub Actions from a `v*` tag, never on a personal machine. This project doesn't sign the third-party files in
the installer: they keep their own maintainers' signatures, if any. The Linux AppImage is not signed.

An approver approves each signing request by hand, one release at a time.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [@homemdelata](https://github.com/homemdelata) |
| Approvers | [@homemdelata](https://github.com/homemdelata) |

Changes from anyone else reach `main` only through a pull request reviewed by a committer, with a green CI.
Everyone in a role uses multi-factor authentication on GitHub and on SignPath.

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
