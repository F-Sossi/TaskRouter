#!/usr/bin/env bash
#
# Packs the four packages into a local folder feed so a host can consume them without
# publishing anything.
#
# nuget.org is a publishing target, not a test environment: a version there is permanent and
# validation takes minutes. This is the loop for "does my package work in the host" — the
# loop for "does my code work" is this repo's own suite and the demo sample, which needs no
# packing at all.
#
# Each run produces a NEW version, and that is the point. NuGet caches by id and version in
# ~/.nuget/packages, so re-packing the same version is silently ignored: the host keeps the
# assembly it already has and you conclude your fix did not work. A timestamped suffix makes
# every pack distinct. The alternative is deleting the cached copies before every build, and
# forgetting once is a confusing hour.
#
# Usage:
#   scripts/pack-local.sh [output-directory]
#
# The default output is artifacts/local-feed inside this repo. Point the host at it by
# adding it as a package source, or pass the host's own feed directory as the argument.
#
# It prints the version it produced and the command to build a host against it.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/local-feed}"
SUFFIX="dev.$(date +%Y%m%d%H%M%S)"

PREFIX="$(grep -oP '(?<=<VersionPrefix>)[^<]+' "$ROOT/Directory.Build.props")"
VERSION="$PREFIX-$SUFFIX"

mkdir -p "$OUT"

for project in TaskRouter.Core TaskRouter.EntityFrameworkCore TaskRouter.AspNetCore TaskRouter.Blazor; do
    dotnet pack "$ROOT/src/$project/$project.csproj" \
        --configuration Release \
        -p:VersionSuffix="$SUFFIX" \
        --output "$OUT" \
        --nologo --verbosity quiet
    echo "  packed $project"
done

echo
echo "Version: $VERSION"
echo "Feed:    $OUT"
echo
echo "Build the host against it with:"
echo "  dotnet build -p:TaskRouterVersion=$VERSION"
echo
echo "Nothing was published. Only a v* tag does that."
