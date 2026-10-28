# Security policy

## Supported versions

Only the **latest release** gets security fixes. The installed app checks for updates and offers them, so the fix
reaches you through the normal update.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report them privately through GitHub: open the repository's
[**Security** tab](https://github.com/homemdelata/gibi-o-matic/security) and choose **Report a vulnerability**.
Only you and the maintainer can see the report.

Please include:

- what the problem is and what an attacker could do with it;
- the steps to reproduce it, and the version of the app and the operating system;
- a proof of concept, if you have one. If it needs a crafted file, describe how to build it or attach a small
  synthetic one: **never attach real comics**.

This is a spare-time project. You should get a first answer within about a week. Once the issue is confirmed, the
plan is to release a fix as soon as practical and then publish an advisory, crediting you unless you prefer
otherwise.

## Scope

Things that are especially relevant for this app:

- **Untrusted files.** The app opens PDF, CBZ, CBR and EPUB files from anywhere: parsing bugs, path traversal when
  extracting archives, or a file that escapes the reader's sandbox (EPUB is shown without JavaScript or network
  access; PDF without JavaScript, forms or actions).
- **Secrets.** The ComicVine and Komga API keys must never end up in `settings.json`, in the library, in the logs or
  on screen.
- **Downloads and updates.** Kindle Comic Converter is downloaded from its official GitHub release and checked
  against a pinned SHA-256; app updates come from this repository's GitHub releases.
- **The library.** Anything that could make the app modify or delete an original file, or write outside the
  library and destination folders.

Out of scope: problems in third-party services (ComicVine, Open Library, Komga, Amazon) and in tools the user
installs separately (Kindle Comic Converter, Kindle Previewer). Please report those to their own projects.
