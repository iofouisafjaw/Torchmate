@echo off
setlocal
cd /d "%~dp0.."
python -c "import struct,sys;sys.exit(0 if sys.version_info >= (3,11) and struct.calcsize('P') == 8 else 1)" >nul 2>nul
if not errorlevel 1 goto usepython
py -3 -c "import struct,sys;sys.exit(0 if sys.version_info >= (3,11) and struct.calcsize('P') == 8 else 1)" >nul 2>nul
if not errorlevel 1 goto usepy
echo Python 3.11+ x64 is required. Install it from https://www.python.org/downloads/windows/ and reopen this file.
exit /b 1
:usepython
python %*
exit /b %errorlevel%
:usepy
py -3 %*
exit /b %errorlevel%
