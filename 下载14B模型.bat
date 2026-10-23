@echo off
call "%~dp0scripts\run_python.bat" scripts\get_model.py %*
set "taskExit=%errorlevel%"
pause
exit /b %taskExit%
