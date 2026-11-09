@echo off
REM ============================================================================
REM RunCrawler.bat - zero-setup run (SQLite + in-memory queue + in-process worker)
REM Builds the React UI once if it is missing (needs Node.js), opens the browser
REM after a few seconds, then runs the API until Ctrl+C.
REM ============================================================================
cd /d "%~dp0"
echo Hello World! Starting Web Crawler...
if not exist src\WebCrawler.Api\wwwroot\index.html (
    echo :TRYING: building the React UI in web\ ...
    pushd web
    call npm ci --no-audit --no-fund || (echo FAIL npm ci - is Node.js installed? & popd & exit /b 1)
    call npm run build || (echo FAIL npm run build & popd & exit /b 1)
    popd
)
start "" cmd /c "timeout /t 8 >nul & start http://localhost:5000"
dotnet run --project src\WebCrawler.Api
