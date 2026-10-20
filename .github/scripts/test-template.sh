#!/usr/bin/env bash
# Tests the smarttablesync-azure template with the packages in a folder (SmartTableSync,
# SmartTableSync.Scaffold and SmartTableSync.Templates, packed with the same version):
# installs the template into a throwaway template store, creates a project from it, builds the
# project against the packed SmartTableSync package, and runs the packed scaffold tool through
# the project's tool manifest, the way quickstart/deploy.ps1 does in Manual mode.
#
#   .github/scripts/test-template.sh <folder with the .nupkg files>
set -euo pipefail

# A Windows path in Git Bash (pwd -W), so NuGet can read it from nuget.config
packages="$(cd "$1" && { pwd -W 2>/dev/null || pwd; })"
template="$(ls "$packages"/SmartTableSync.Templates.*.nupkg)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The packed packages first, nuget.org for everything else
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

cd "$work"
# An empty package cache, so nothing comes from an earlier build
export NUGET_PACKAGES="$work/nuget-cache"

dotnet new install "$template" --debug:custom-hive "$work/templates"
dotnet new smarttablesync-azure -n MySync --debug:custom-hive "$work/templates"

cd MySync
dotnet build MySync --configuration Release -warnaserror

dotnet tool restore
dotnet smarttablesync-scaffold --infrastructure --output "$work/sql"
test -s "$work/sql/00_infrastructure.sql"

# The project name replaced the Function's folder name in the script that publishes it
grep -q "Join-Path \$repoRoot 'MySync'" quickstart/deploy.ps1

echo "Template OK: $(basename "$template")"
