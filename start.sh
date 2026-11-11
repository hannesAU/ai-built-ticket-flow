#!/usr/bin/env sh
# Builds the UI if needed and starts Ticket Flow (opens the browser once it's listening).
set -e
cd "$(dirname "$0")"
if [ ! -f src/TicketFlow.Server/wwwroot/index.html ]; then
  echo "Building the web UI..."
  (cd web && npm install --no-audit --no-fund && npm run build)
fi
exec dotnet run --project src/TicketFlow.Server -c Release
