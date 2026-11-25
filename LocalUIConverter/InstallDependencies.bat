@echo off
setlocal DisableDelayedExpansion
title Local PSD UI - Install Python Dependencies

echo Local PSD UI dependency installer
echo Required: Python 3.11 or newer
echo.
echo Use the SAME Python executable configured in the Unity tool.
echo You can paste its full path here, or pass it as the first argument.
echo.

set "PSD_PYTHON=%~1"
if defined PSD_PYTHON goto validate
set "PSD_DEFAULT=python"
if exist "E:\Python\python.exe" set "PSD_DEFAULT=E:\Python\python.exe"
echo Default: %PSD_DEFAULT%
set /p "PSD_PYTHON=Python executable (Enter to use default): "
if not defined PSD_PYTHON set "PSD_PYTHON=%PSD_DEFAULT%"
set "PSD_PYTHON=%PSD_PYTHON:"=%"

:validate
echo.
echo Checking Python: "%PSD_PYTHON%"
"%PSD_PYTHON%" -c "import sys; print(sys.executable); print(sys.version); sys.exit(0 if sys.version_info >= (3, 11) else 1)"
if errorlevel 1 goto python_failed

"%PSD_PYTHON%" -m pip --version
if not errorlevel 1 goto install
echo pip is missing. Trying Python ensurepip...
"%PSD_PYTHON%" -m ensurepip --upgrade
if errorlevel 1 goto failed

:install
echo.
echo Installing / repairing PSD import and export dependencies...
"%PSD_PYTHON%" -m pip install --upgrade "psd-tools[composite]==1.17.1"
if errorlevel 1 goto failed

echo.
echo Verifying dependencies with the Unity tool's checker...
"%PSD_PYTHON%" "%~dp0psd_export.py" --check
if errorlevel 1 goto failed
echo.
echo SUCCESS. In Unity, set the Python executable to the path printed above.
echo You can now use Parse PSD and Export PSD.
pause
exit /b 0

:python_failed
echo.
echo Python could not run, or its version is older than 3.11.
echo Install Python 3.11+ first, then run this file with its python.exe path.
goto failed

:failed
echo.
echo FAILED. See the error above. Check the Python path, permissions and network.
echo Do not change the Unity Python path to a different installation afterwards.
pause
exit /b 1
