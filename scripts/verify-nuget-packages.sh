#!/usr/bin/env bash
set -euo pipefail

# Verify all NuGet PackageReference entries in .csproj files exist on nuget.org.
# Usage: bash scripts/verify-nuget-packages.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Extract unique PackageReference names from all .csproj files
PACKAGES=$(grep -rh '<PackageReference Include="' "$REPO_ROOT" --include="*.csproj" \
  | sed 's/.*Include="\([^"]*\)".*/\1/' \
  | sort -u)

if [ -z "$PACKAGES" ]; then
  echo "No PackageReference entries found."
  exit 0
fi

FAILED=0
TOTAL=0
for pkg in $PACKAGES; do
  TOTAL=$((TOTAL + 1))
  # nuget.org flat container API uses lowercase package IDs
  PKG_LOWER=$(echo "$pkg" | tr '[:upper:]' '[:lower:]')
  HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
    "https://api.nuget.org/v3-flatcontainer/${PKG_LOWER}/index.json")
  if [ "$HTTP_CODE" = "404" ]; then
    echo "::error::Package '$pkg' not found on nuget.org (HTTP 404)"
    FAILED=1
  elif [ "$HTTP_CODE" != "200" ]; then
    echo "::warning::Package '$pkg' returned HTTP $HTTP_CODE (nuget.org may be unavailable)"
  fi
done

if [ "$FAILED" -eq 1 ]; then
  echo "::error::One or more NuGet packages could not be verified"
  exit 1
fi

echo "All $TOTAL NuGet packages verified on nuget.org"
