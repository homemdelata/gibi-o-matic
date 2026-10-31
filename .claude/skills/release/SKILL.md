---
name: release
description: Creates the next version tag (vX.Y.Z) on main and pushes it, which starts the release workflow (installers + draft GitHub release). Bumps the patch by default; "minor" or "major" bump those instead.
argument-hint: "[patch|minor|major]"
disable-model-invocation: true
allowed-tools: Bash(git fetch:*) Bash(git status:*) Bash(git rev-parse:*) Bash(git tag:*) Bash(git log:*) Bash(git ls-remote:*) Bash(gh run list:*) Bash(gh run view:*) Bash(gh run watch:*) Bash(gh release view:*)
---

# Release a new version

The version of every build comes from the latest `vX.Y.Z` tag (MinVer). Pushing a `v*` tag runs
`.github/workflows/release.yml`: tests, the Windows MSI and the Linux AppImage, and a **draft** GitHub
release. Installed apps only see the update once the draft is published, and that stays a manual step.

Tags are protected on GitHub: once pushed, a `v*` tag can't be moved or deleted. So check everything
before creating it, and stop at the first problem: explain it to the user and don't work around it.

Talk to the user in their language.

## 1. Which part to bump

The argument is `$ARGUMENTS`.
- Empty or `patch`: `X.Y.Z` → `X.Y.(Z+1)`.
- `minor`: `X.Y.Z` → `X.(Y+1).0`.
- `major`: `X.Y.Z` → `(X+1).0.0`.
- Anything else: stop and show the usage, `/release [patch|minor|major]`.

## 2. Preconditions

1. `git fetch origin --tags --prune`.
2. The current branch is `main` (`git rev-parse --abbrev-ref HEAD`) and the working tree is clean
   (`git status --porcelain` prints nothing).
3. Local `main` is exactly `origin/main` (`git rev-parse HEAD` = `git rev-parse origin/main`). If it
   is behind, say so and suggest `git pull`; never push commits to `main` (it is protected).
4. The latest release tag is the highest tag matching `vMAJOR.MINOR.PATCH` with no suffix:
   `git tag -l "v*" --sort=-v:refname`, first one matching `^v\d+\.\d+\.\d+$`. Without one, the
   previous version is `0.0.0`.
5. There are commits since that tag (`git log --oneline <tag>..HEAD` isn't empty) and `HEAD` isn't
   tagged already (`git tag --points-at HEAD`).
6. The new tag doesn't exist locally or on the remote (`git ls-remote --tags origin <new tag>`).
7. The CI of `HEAD` is green:
   `gh run list --workflow ci.yml --branch main --commit <HEAD sha> --json databaseId,status,conclusion`.
   - Completed with `success`: go on.
   - Still running: wait for it with `gh run watch <id> --exit-status`, in the background.
   - Failed, cancelled or missing: stop.

## 3. Confirm

Show the user, in a short summary:
- the latest tag and the new one (`v0.1.0 → v0.1.1`);
- the commits that go in (`git log --oneline <tag>..HEAD`);
- that the tag can't be removed afterwards, and that the release will be a draft.

Then ask for confirmation with AskUserQuestion. Only an explicit yes goes on.

## 4. Create and push the tag

```
git tag -a vX.Y.Z -m "Gibi-o-matic X.Y.Z"
git push origin vX.Y.Z
```

The tag is annotated, like `v0.1.0`. Push only the tag: never `git push --tags` and never a branch.

## 5. Follow the release workflow

1. Find the run: `gh run list --workflow release.yml --branch vX.Y.Z --json databaseId,status,url`.
   It can take a few seconds to show up.
2. Wait with `gh run watch <id> --exit-status` in the background. It takes about 10 minutes.
3. Failed: show the failed step (`gh run view <id> --log-failed`, the relevant lines only). The tag
   stays; the fix is a new commit and a new patch release.
4. Succeeded: `gh release view vX.Y.Z --json url,isDraft,assets`, and check that the assets include
   `Gibi-o-matic-win.msi` and `Gibi-o-matic.AppImage`.

## 6. Report

Tell the user:
- the version created and the link to the draft release;
- the installers it has;
- that installed apps only see the update after the draft is published, on the release page
  ("Publish release") or with `gh release edit vX.Y.Z --draft=false`.

Don't publish the draft yourself unless the user asks for it in this conversation.
