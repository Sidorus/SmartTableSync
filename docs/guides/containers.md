# Run it in a container (Linux)

SmartTableSync runs on Linux like on Windows: it's plain .NET 10. The repository includes a
container host, [`SmartTableSync.Worker`](../../SmartTableSync.Worker), and its
[`Dockerfile`](../../SmartTableSync.Worker/Dockerfile). This page shows what you need, how to
build the image, and how to run it on Docker, Azure Container Apps and Kubernetes.

The databases are prepared the same way whatever the host: [Manual mode](manual-mode.md) or
[Auto mode](auto-mode.md). This page covers the container itself.

## What you need

| | |
|---|---|
| **To build the image** | Docker (or any OCI builder such as Podman or `az acr build`) and this repository. No .NET SDK on your machine: the build runs inside the SDK image. |
| **To run it** | Any place that runs Linux containers: Docker on a server, Azure Container Apps, Kubernetes (AKS or any other), Azure Container Instances. |
| **Network** | The container must reach both SQL servers on port 1433. For Azure SQL: the server's firewall must allow the container's outbound address, or "Allow Azure services" for containers in Azure, or a private endpoint in the container's virtual network. |
| **A sign-in for SQL** | In the connection strings; see [sign-in](#sign-in-to-sql). |
| **The settings** | As environment variables; see [settings](#settings). |

## Two ways to run

| | Always on | Run once, on a schedule |
|---|---|---|
| How | the container keeps running and syncs every `SmartTableSync__Worker__IntervalMinutes` (default 5) | the container syncs once and exits (`--once`); the platform starts it on a cron |
| Good for | Docker on a server, Kubernetes Deployment, Container Apps app | Kubernetes CronJob, Azure Container Apps **job**: you pay only while it runs |
| Exit code | — | `0` when the run completed, `1` when it couldn't start (missing settings, unreachable database). A single table that fails is logged and doesn't change the exit code. |

Both are safe to overlap: if a run is still busy when the next one starts, the new one does
nothing, through a lock in the child database. That holds across replicas too, so running two
replicas only wastes resources; one is enough.

## Build the image

From the repository root:

```bash
docker build -f SmartTableSync.Worker/Dockerfile -t smarttablesync-worker .
```

The image is built in two stages: the .NET 10 SDK compiles and publishes, and only the result is
copied onto the smaller .NET 10 runtime image. It runs as a non-root user and needs no volume.

To run it elsewhere, push it to a registry, for example Azure Container Registry:

```bash
az acr build --registry <registry name> --image smarttablesync-worker:1.0 --file SmartTableSync.Worker/Dockerfile .
```

`az acr build` builds in Azure, so it doesn't need Docker on your machine.

## Settings

All settings are environment variables. `:` in a setting name becomes `__`.

| Variable | Required | Value |
|---|---|---|
| `ParentDbConnectionString` | yes | connection string to the parent |
| `ChildDbConnectionString` | yes | connection string to the child |
| `ChildProvisioningConnectionString` | Auto mode | a child connection string that may create tables |
| `SmartTableSync__TableList` | yes | `Product,Customer,sales.Order` |
| `SmartTableSync__Mode` | no | `Manual` (default) or `Auto` |
| `SmartTableSync__Worker__IntervalMinutes` | no | minutes between runs when always on, default `5` |
| `SmartTableSync__Worker__RunOnce` | no | `true` does the same as `--once` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | sends the metrics over OTLP, see [logs and metrics](#logs-and-metrics) |
| `Logging__Console__FormatterName` | no | `json` for one JSON object per log line, if your log system prefers it |

Every other engine setting works the same way, for example
`SmartTableSync__TimestampColumns__Customer=ModifiedAt`; see the
[configuration reference](../configuration.md).

Pass passwords as **secrets** of the platform (Docker secrets or an env file you don't commit,
Container Apps secrets, Kubernetes Secrets), not as plain values in a script or a YAML file in git.

## Run it with Docker

Always on:

```bash
docker run -d --name smarttablesync --restart unless-stopped \
  --env-file smarttablesync.env \
  smarttablesync-worker
```

with `smarttablesync.env` (keep it out of git):

```text
ParentDbConnectionString=Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;User Id=sync_reader;Password=...;Encrypt=True
ChildDbConnectionString=Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;User Id=sync_writer;Password=...;Encrypt=True
SmartTableSync__TableList=Product,Customer
SmartTableSync__Worker__IntervalMinutes=5
```

Once, for example from cron on a Linux server:

```bash
docker run --rm --env-file smarttablesync.env smarttablesync-worker --once
```

See what it did: `docker logs smarttablesync`. Each run ends with one line per table, for example
`Product: 150 synced, 10 deleted, 0 errors, 0.4s (incremental)`.

## Run it on Azure Container Apps

**As a scheduled job** (recommended: it runs for a few seconds every 5 minutes and costs only
that time):

```bash
az containerapp job create \
  --name smarttablesync --resource-group <group> --environment <container apps environment> \
  --trigger-type Schedule --cron-expression "*/5 * * * *" \
  --replica-timeout 1800 --replica-retry-limit 0 --parallelism 1 \
  --image <registry>.azurecr.io/smarttablesync-worker:1.0 \
  --registry-server <registry>.azurecr.io --registry-identity system --mi-system-assigned \
  --args "--once" \
  --mi-user-assigned <runtime identity resource id> \
  --env-vars \
      "ParentDbConnectionString=Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Managed Identity;User Id=<runtime client id>;Encrypt=True" \
      "ChildDbConnectionString=Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Managed Identity;User Id=<runtime client id>;Encrypt=True" \
      "SmartTableSync__TableList=Product,Customer"
```

The system-assigned identity pulls the image (give it the `AcrPull` role on the registry); the
user-assigned one signs in to SQL. With a managed identity there's no password; the identity needs its database users and rights
like any other ([Manual](manual-mode.md#1-create-the-database-users) or
[Auto mode](auto-mode.md#1-parent-the-runtime-user-and-read-access)). With a SQL login, store the
connection strings as secrets (`--secrets parent-db=<string>`) and refer to them
(`ParentDbConnectionString=secretref:parent-db`).

**Always on**, as a Container Apps app with one replica and no ingress: `az containerapp create`
with the same image, settings and `--min-replicas 1 --max-replicas 1`, without `--args`.

Logs appear in the job's or app's **Log stream** and in its Log Analytics workspace
(`ContainerAppConsoleLogs_CL`).

## Run it on Kubernetes

As a CronJob that runs every 5 minutes:

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: smarttablesync
spec:
  schedule: "*/5 * * * *"
  concurrencyPolicy: Forbid          # never two at once (the engine's lock would skip one anyway)
  jobTemplate:
    spec:
      backoffLimit: 0
      template:
        spec:
          restartPolicy: Never
          containers:
            - name: smarttablesync
              image: <registry>/smarttablesync-worker:1.0
              args: ["--once"]
              envFrom:
                - secretRef:
                    name: smarttablesync      # ParentDbConnectionString, ChildDbConnectionString
              env:
                - name: SmartTableSync__TableList
                  value: "Product,Customer"
              resources:
                requests: { cpu: "100m", memory: "128Mi" }
                limits: { memory: "512Mi" }
```

with the connection strings in a Secret:

```bash
kubectl create secret generic smarttablesync \
  --from-literal=ParentDbConnectionString='Server=...;Database=ParentDb;User Id=...;Password=...;Encrypt=True' \
  --from-literal=ChildDbConnectionString='Server=...;Database=ChildDb;User Id=...;Password=...;Encrypt=True'
```

For always on, use a Deployment with `replicas: 1`, the same container without `args`.

## Sign in to SQL

The engine passes the connection string to SqlClient as it is, so the sign-in is whatever the
string says. What works where:

| Where the container runs | Sign-in | Connection string |
|---|---|---|
| Azure Container Apps, Azure Container Instances | managed identity (no password) | `Authentication=Active Directory Managed Identity;User Id=<client id>` |
| AKS with workload identity | federated identity (no password) | `Authentication=Active Directory Workload Identity`; the pod gets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_FEDERATED_TOKEN_FILE` from the workload identity setup |
| Anywhere | SQL login | `User Id=<login>;Password=<password>`, from a secret |
| Anywhere, for Azure SQL | service principal | `Authentication=Active Directory Service Principal;User Id=<app id>;Password=<client secret>`, from a secret |
| Linux, to SQL Server with Windows authentication | Kerberos | possible, but needs a keytab and Kerberos configuration in the container; a SQL login is usually simpler |

The image includes `Microsoft.Data.SqlClient.Extensions.Azure`, which every
`Authentication=Active Directory ...` method needs.

## Logs and metrics

- **Logs** go to the container's standard output, one line per message, with a UTC timestamp.
  Every platform collects them: `docker logs`, the Container Apps log stream and Log Analytics,
  `kubectl logs`. Set `Logging__Console__FormatterName=json` for JSON lines.
- **Metrics** (`smarttablesync.rows_synced`, `rows_errored`, `sync_duration_seconds`, ...) go out
  over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, for example to an OpenTelemetry Collector,
  Grafana Alloy or the Container Apps OpenTelemetry agent. Without it, no metrics are exported
  and the sync runs as usual. Every metric is described in [observability](../observability.md).

## Troubleshooting

| What you see | Cause and fix |
|---|---|
| Exit code 1, *"SmartTableSync:TableList must list at least one table name"* | `SmartTableSync__TableList` is missing; on Linux the name uses `__`, not `:` |
| *"ParentDbConnectionString is not configured"* | the variable is missing or misspelled |
| *"A network-related or instance-specific error"* / timeout | the container can't reach the SQL server: firewall, private endpoint or DNS |
| *"Login failed for user ..."* | wrong password, the user doesn't exist in that database, or the server allows only Microsoft Entra sign-in |
| *"ManagedIdentityCredential authentication unavailable"* | the platform has no managed identity attached, or `User Id` isn't its client ID |
| The container restarts again and again (always on) | read the last lines of its log: a startup error ends the process with exit code 1 |

More in the setup guide's [troubleshooting](../setup.md#troubleshooting).

## Running the scripts from Linux

The demo and quick start scripts are PowerShell 7, which runs on Linux and macOS too. Install
PowerShell (for example `sudo apt-get install -y powershell` after adding Microsoft's package
repository) and the tools listed in each script's README, then run them with `pwsh`:

```bash
pwsh ./quickstart/deploy.ps1 -Suffix acme1 -ParentServer ... -DryRun
```

They haven't been run on Linux as part of this project's tests yet; a `-DryRun` is a safe first try.
