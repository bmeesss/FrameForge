#!/usr/bin/env bash
set -euo pipefail
dotnet build FrameForge.sln -c "${1:-Release}"
dotnet run --project tests/FrameForge.Tests -c "${1:-Release}" --no-build
