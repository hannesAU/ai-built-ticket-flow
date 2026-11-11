@echo off
rem Builds the UI if needed and starts Ticket Flow (opens the browser once it's listening).
setlocal
cd /d "%~dp0"
if not exist "src\TicketFlow.Server\wwwroot\index.html" (
  echo Building the web UI...
  pushd web
  call npm install --no-audit --no-fund || exit /b 1
  call npm run build || exit /b 1
  popd
)
dotnet run --project src\TicketFlow.Server -c Release
