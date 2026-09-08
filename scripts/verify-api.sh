#!/usr/bin/env bash
set -euo pipefail
# Last commit before the approved .NET 10-only migration. Additions are allowed; removals fail.
baseline=82cb2dbad1238e4bc42e21ae48c283dc2dd02e0e
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/baseline"
git archive "$baseline" | tar -x -C "$scratch/baseline"
dotnet build "$scratch/baseline/src/Andy.MCP.AspNetCore" --framework net10.0 --configuration Release -p:TargetFrameworks=net10.0
dotnet tool install Microsoft.DotNet.ApiCompat.Tool --version 10.0.400 --tool-path "$scratch/tools"
for assembly in Andy.MCP Andy.MCP.AspNetCore; do
  "$scratch/tools/apicompat" \
    -l "$scratch/baseline/src/$assembly/bin/Release/net10.0/$assembly.dll" \
    -r "src/$assembly/bin/Release/net10.0/$assembly.dll"
done
