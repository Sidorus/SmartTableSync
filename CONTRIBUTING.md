# Contributing

Thanks for wanting to help. Bug reports, ideas, questions and pull requests are all welcome.

## Before you start

- **A bug:** open an [issue](https://github.com/Sidorus/SmartTableSync/issues/new/choose) with
  the bug form. The version, where it runs, the table's strategy and the log lines usually make it
  possible to find the cause without asking back.
- **A question:** ask in [Discussions](https://github.com/Sidorus/SmartTableSync/discussions).
- **A bigger change** (a new strategy, a new setting, a change in behaviour): open an issue or a
  discussion first, so we can agree on the approach before you spend time on code.
- **A security problem:** report it privately, see [SECURITY.md](SECURITY.md).

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, for the
integration tests, [Docker](https://docs.docker.com/get-docker/).

```bash
dotnet build SmartTableSync.slnx          # must stay at 0 warnings
dotnet test SmartTableSync.Tests          # unit tests
dotnet test SmartTableSync.IntegrationTests   # real SQL Server in Docker, through Testcontainers
```

The integration tests are the proof for anything that touches SQL: prefer a test against the real
SQL Server over a mock ([why](docs/adr/0012-verify-against-real-disposable-sql-server.md)).

## Pull requests

- One topic per pull request, against `main`. CI (build, tests, container build, Terraform check,
  Semgrep) must pass before it can be merged.
- Add or change tests with the code. A bug fix comes with a test that fails without it.
- Update the docs the change touches, and add a line to `CHANGELOG.md` under *Unreleased*.
- Keep the public API small: a new type stays `internal` unless a host needs it.

## How the code is built

- [How it works](docs/how-it-works.md) and [architecture](docs/architecture.md) give the
  overview; the [decision records](docs/adr/README.md) explain why things are the way they are.
- Every table or column name that goes into SQL text goes through `SqlIdentifier`; data values
  are always parameters.
- Every write to the child is an idempotent `MERGE`, so reading a row twice is always safe.
- The sync never needs rights to change a schema; only the scaffold tool's scripts and Auto mode's
  separate provisioning connection create objects in the child, and nothing ever changes the
  parent.

## License

By contributing you agree that your contribution is licensed under the
[Apache License 2.0](LICENSE), like the rest of the project.
