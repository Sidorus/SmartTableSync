# Releasing a version

How a new version of the two NuGet packages, [SmartTableSync](https://www.nuget.org/packages/SmartTableSync)
and [SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold), is published.
For maintainers.

## In short

1. Move the changelog's *Unreleased* entries under the new version and merge that to `main`.
2. Tag the commit on `main` and push the tag:

   ```bash
   git switch main && git pull
   git tag v0.5.0
   git push origin v0.5.0
   ```

3. Approve the run: GitHub, **Actions**, *Publish to NuGet*, **Review deployments**, **Approve**.
4. A few minutes later both packages are on nuget.org with version `0.5.0`.

The **tag is the version**: `v0.5.0` publishes `0.5.0`, `v0.5.0-preview.1` publishes the
pre-release `0.5.0-preview.1`. The `<Version>` in the project files is only a local default
(`0.0.0-dev`), so a package built on a developer's machine is never mistaken for a release.

## What the workflow does

[`.github/workflows/publish.yml`](../.github/workflows/publish.yml) runs when a `v*` tag is pushed:

| Step | Stops the release when |
|---|---|
| Version from the tag | the tag isn't `v1.2.3` or `v1.2.3-something` |
| The tagged commit is on main | the tag points to a commit that isn't part of `main` |
| Build and test | the build fails or any unit or integration test fails |
| Pack | packing fails; both packages get the tag's version |
| NuGet login | the trusted publishing policy doesn't match |
| Push | nuget.org refuses the package; a version that's already published is skipped |

The packages are also kept with the run as an artifact, for 90 days.

## What protects a release

| Where | Setting | Effect |
|---|---|---|
| nuget.org | trusted publishing policy for `Sidorus/SmartTableSync`, workflow `publish.yml`, environment `release`, packages `SmartTableSync` and `SmartTableSync.Scaffold` | only this workflow can publish these two packages; there's no API key that could leak |
| GitHub, environment `release` | required reviewer; only `v*` tags; secret `NUGET_USER` | every publish waits for approval, and nothing but a release tag can use the environment |
| GitHub, ruleset *Release tags* | creating, moving and deleting `v*` tags restricted to admins | only a maintainer can start a release, and a published tag can't be moved |
| GitHub, ruleset *Protect main* | pull requests with passing checks | only reviewed, tested code is on `main`, and so only that can be released |

## Good to know

- **A published version is permanent.** nuget.org never lets you replace or delete a version;
  you can only *unlist* it (hide it from search) on its nuget.org page. Use a pre-release
  (`v0.5.0-preview.1`) when in doubt.
- **A failed run publishes nothing** unless it fails after the push step. Fix the cause on `main`,
  then release with a new tag. Re-running the same tag is safe: an already published version is
  skipped.
- **nuget.org needs a few minutes** to validate and index a new version before
  `dotnet add package` finds it.
- **Try a package before tagging:** pack it locally with any version, and use it from a local
  folder.

  ```bash
  dotnet pack SmartTableSync.Core/SmartTableSync.Core.csproj -c Release -o ./artifacts -p:Version=0.5.0-local
  dotnet nuget add source ./artifacts --name smarttablesync-local
  ```
