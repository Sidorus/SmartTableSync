# Releasing a version

How a new version of the two NuGet packages, [SmartTableSync](https://www.nuget.org/packages/SmartTableSync)
and [SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold), is published.
For maintainers.

## In short

1. **Pick the version** ([how](#choosing-the-version)), for example `0.5.0`.
2. **Write the changelog entry**: rename `## [Unreleased]` in `CHANGELOG.md` to
   `## [0.5.0] - 2026-10-20` and add a new empty `## [Unreleased]` above it. Merge that pull
   request into `main`.
3. **Tag and push the tag** from `main`:

   ```bash
   git switch main
   git pull
   git tag v0.5.0
   git push origin v0.5.0
   ```

4. **Approve the run**: GitHub, **Actions**, *Publish to NuGet*, **Review deployments**,
   **Approve**.
5. **Wait for nuget.org** to validate the packages, usually 5 to 15 minutes. Meanwhile they show
   as *Unlisted* under [Manage Packages](https://www.nuget.org/account/Packages); they become
   public by themselves.

That's all. The version is never edited in a file: **the tag is the version**.

## Choosing the version

Versions follow [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

| Change since the last release | Raise | Example |
|---|---|---|
| Only fixes, nothing a user has to change | PATCH | `0.5.0` to `0.5.1` |
| New features or settings; existing setups keep working | MINOR | `0.5.1` to `0.6.0` |
| Something a user must change: a renamed setting, a removed method, a new required right | MAJOR | `0.6.0` to `1.0.0` |

While the version starts with `0.`, a breaking change may also raise only MINOR; say so in the
changelog's *Upgrading* part.

**Pre-releases** have a suffix after a hyphen: `v0.6.0-preview.1`, `v0.6.0-preview.2`,
`v0.6.0-rc.1`. They are published like any version, but NuGet only offers them to people who ask
for pre-releases (`--prerelease`), and GitHub marks them as pre-releases. Use one to try a
release before making it final. A pre-release doesn't need its own changelog section.

## What happens after the tag is pushed

[`.github/workflows/publish.yml`](../.github/workflows/publish.yml) has two jobs.

**1. Publish to NuGet** (needs your approval):

| Step | Stops the release when |
|---|---|
| Version from the tag | the tag isn't `v1.2.3` or `v1.2.3-something` |
| The tagged commit is on main | the tag points to a commit that isn't part of `main` |
| Build and test | the build fails or any unit or integration test fails |
| Pack | packing fails; both packages get the tag's version |
| NuGet login | the trusted publishing policy doesn't match |
| Push | nuget.org refuses the package; a version that's already published is skipped |

The packages are also kept with the run as an artifact, for 90 days.

**2. GitHub Release** (only after the push succeeded): creates the release for the tag on the
[Releases page](https://github.com/Sidorus/SmartTableSync/releases), with:

- the version as its title;
- the changelog section `## [version]` as its notes, or, when there is none (as for most
  pre-releases), the list of pull requests merged since the previous release;
- the two `.nupkg` files attached;
- the *Pre-release* label for a version with a suffix, otherwise *Latest*.

The badges at the top of the README (NuGet version, downloads, latest release) update by
themselves within a few hours.

## Building a package locally

To try a package before releasing it, pack it with any version and use it from a local folder:

```bash
dotnet pack SmartTableSync.Core/SmartTableSync.Core.csproj -c Release -o ./artifacts -p:Version=0.6.0-local
dotnet pack SmartTableSync.Scaffold/SmartTableSync.Scaffold.csproj -c Release -o ./artifacts -p:Version=0.6.0-local
```

Use the library from that folder in another project:

```bash
dotnet nuget add source /full/path/to/artifacts --name smarttablesync-local
dotnet add package SmartTableSync --version 0.6.0-local
```

Install the scaffold tool from it, without touching your global tools:

```bash
dotnet tool install SmartTableSync.Scaffold --tool-path ./tool-test --add-source ./artifacts --version 0.6.0-local
./tool-test/smarttablesync-scaffold --help
```

Without `-p:Version`, a local package gets the default version `0.0.0-dev` from the project files,
so it can never be mistaken for a release. Remove the local source again with
`dotnet nuget remove source smarttablesync-local`.

## What protects a release

| Where | Setting | Effect |
|---|---|---|
| nuget.org | trusted publishing policy for `Sidorus/SmartTableSync`, workflow `publish.yml`, environment `release`, packages `SmartTableSync` and `SmartTableSync.Scaffold` | only this workflow can publish these two packages; there's no API key that could leak |
| GitHub, environment `release` | required reviewer; only `v*` tags; secret `NUGET_USER` | every publish waits for approval, and nothing but a release tag can use the environment |
| GitHub, ruleset *Release tags* | creating, moving and deleting `v*` tags restricted to admins | only a maintainer can start a release, and a published tag can't be moved |
| GitHub, ruleset *Protect main* | pull requests with passing checks | only reviewed, tested code is on `main`, and so only that can be released |

## When something goes wrong

- **A published version is permanent.** nuget.org never lets you replace or delete a version;
  you can only *unlist* it (hide it from search) on its nuget.org page. Fix the problem and
  release the next version. Use a pre-release when in doubt.
- **The run failed before "Push"**: nothing was published. Fix the cause on `main`, then release
  with a **new** tag (for example `v0.5.1`).
- **The run failed at "GitHub Release"**: the packages are on nuget.org already. Re-run the
  failed job from the run's page; an existing release is kept.
- **Re-running the whole run** is safe: an already published version is skipped, and an
  existing release is kept.
- **Wrong tag pushed and the run not approved yet**: reject it under **Review deployments**, then
  delete the tag (`git push origin :refs/tags/v0.5.0` and `git tag -d v0.5.0`; only admins can).
