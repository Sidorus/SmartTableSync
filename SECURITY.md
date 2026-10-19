# Security policy

## Supported versions

Security fixes go into the latest released version of the NuGet packages
[SmartTableSync](https://www.nuget.org/packages/SmartTableSync) and
[SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold). Older versions
don't get separate fixes: upgrade to the latest one. The [changelog](CHANGELOG.md) says what to
check when upgrading.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately instead:

1. Open the [Security tab](https://github.com/Sidorus/SmartTableSync/security) of this repository.
2. Click **Report a vulnerability**
   ([direct link](https://github.com/Sidorus/SmartTableSync/security/advisories/new)).

Only the maintainers can see the report. It helps to include:

- the package and version, and how you host it (Azure Function, container, your own app);
- what an attacker could do, and what they need to do it;
- steps or a small example that shows the problem;
- logs or error messages, with connection strings, passwords and keys removed.

## What happens next

- You get a first answer within 7 days.
- Once the problem is confirmed, it's fixed in a new release, and a security advisory is published
  on this repository with the affected and fixed versions. You're credited in the advisory unless
  you'd rather not be.
- Please keep the details private until the fixed version is released.

## Scope

In scope: the code in this repository, that is the engine, the scaffold tool, the example hosts and
the scripts and Terraform in `demo/` and `quickstart/`.

Not in scope: weaknesses of SQL Server, Azure SQL or Azure services themselves (report those to
Microsoft), and setups that grant the sync identities more rights than the
[documented ones](docs/setup.md#identities-and-their-rights).
