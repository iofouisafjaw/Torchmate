@echo off
call "%~dp0scripts\run_python.bat" scripts\start_qwen.py %*
set "taskExit=%errorlevel%"
pause
exit /b %taskExit%
