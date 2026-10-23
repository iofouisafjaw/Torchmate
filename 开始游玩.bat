@echo off
call "%~dp0scripts\run_python.bat" scripts\play_local.py %*
set "taskExit=%errorlevel%"
pause
exit /b %taskExit%
