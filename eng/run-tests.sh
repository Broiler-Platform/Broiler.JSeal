#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
configuration="${1:-Release}"
dotnet test Broiler.JSeal.slnx -c "$configuration" --no-build --nologo
