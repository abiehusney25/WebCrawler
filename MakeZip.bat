@echo off
REM ============================================================================
REM MakeZip.bat - the submission zip, built from git (committed files only), so
REM bin/obj/Output/*.db/.env/secrets.csv can never end up in it.
REM Output: Output\WebCrawler_<yyyyMMdd_HHmm>.zip (timestamped, never overwritten)
REM ============================================================================
cd /d "%~dp0"
git rev-parse --is-inside-work-tree >nul 2>&1 || (echo FAIL not a git repo: run git init and commit first & exit /b 1)

for /f %%s in ('git status --porcelain') do (echo WARNING uncommitted changes are NOT in the zip - commit first & goto stamp)
:stamp
for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmm"') do set stamp=%%t
if not exist Output mkdir Output

git archive --format=zip --prefix=WebCrawler/ -o Output\WebCrawler_%stamp%.zip HEAD || (echo FAIL git archive & exit /b 1)
echo Saved -^> Output\WebCrawler_%stamp%.zip
