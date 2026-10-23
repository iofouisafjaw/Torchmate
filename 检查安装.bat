@echo off
call "%~dp0scripts\run_python.bat" scripts\setup_windows.py --check %*
set "taskExit=%errorlevel%"
pause
exit /b %taskExit%
