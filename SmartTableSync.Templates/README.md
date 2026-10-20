# SmartTableSync.Templates

`dotnet new` template for [SmartTableSync](https://github.com/Sidorus/SmartTableSync): an Azure
Function that keeps tables in a child Azure SQL database up to date with a parent database, with a
script and Terraform that deploy it and start the first sync.

## Install and create a project

```bash
dotnet new install SmartTableSync.Templates
dotnet new smarttablesync-azure -n MySync
cd MySync
```

The project uses the [SmartTableSync](https://www.nuget.org/packages/SmartTableSync) package and
the [SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold) tool, in the
same version as this template. Its packages are restored as soon as it is created.

```text
MySync/
├── MySync/                     the Function (references the SmartTableSync package)
├── quickstart/deploy.ps1       deploys it to Azure and starts the first sync
├── quickstart/terraform/       the Azure resources
├── .config/dotnet-tools.json   the scaffold tool, for Manual mode
└── README.md
```

## Deploy it

```powershell
az login
./quickstart/deploy.ps1 -Suffix acme1 `
    -ParentConnectionString $env:PARENT_DB -ChildConnectionString $env:CHILD_DB `
    -Tables "Product,Customer"
```

What you need and every parameter:
[the quick start guide](https://github.com/Sidorus/SmartTableSync/blob/main/quickstart/README.md).

## Update or remove the template

```bash
dotnet new update
dotnet new uninstall SmartTableSync.Templates
```

## License

Apache-2.0
