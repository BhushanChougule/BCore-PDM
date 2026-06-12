@echo off
setlocal

rem ============================================================
rem  BCore PDM - Deploy to the network ADDIN folder
rem
rem  Run on the BUILD machine after building the RELEASE
rem  configuration in Visual Studio. Copies the add-in (and the
rem  registration files) from this solution's Release output to:
rem
rem      N:\PDM-SolidWorks\ADDIN\
rem
rem  NOTE: PDMLite.dll is LOCKED while any machine has SOLIDWORKS
rem  running with the add-in loaded - if the copy fails with
rem  "file in use", deploy after hours / once everyone closed SW.
rem ============================================================

set "SRC=%~dp0PDMLite\bin\Release"
set "DEST=N:\PDM-SolidWorks\ADDIN"

echo ============================================
echo    BCore PDM - Deploy to %DEST%
echo ============================================
echo.

if not exist "%SRC%\PDMLite.dll" (
    echo ERROR: %SRC%\PDMLite.dll not found.
    echo        Build the RELEASE configuration first:
    echo        Visual Studio ^> Configuration Manager ^> Release ^> Build.
    echo        ^(Debug builds are for the dev machine only and are
    echo        never deployed.^)
    goto :fail
)

if not exist "%SRC%\PdfSharp.dll" (
    echo ERROR: %SRC%\PdfSharp.dll missing from the Release output.
    echo        It is vendored in the project with Copy to Output -
    echo        rebuild Release; without it released PDFs are not
    echo        watermarked.
    goto :fail
)

if not exist "%DEST%" mkdir "%DEST%"
if not exist "%DEST%" (
    echo ERROR: Cannot reach %DEST% - is the N: drive mapped?
    goto :fail
)

echo Copying PDMLite.dll...
copy /y "%SRC%\PDMLite.dll" "%DEST%\" >nul || goto :copyfail
echo Copying PdfSharp.dll...
copy /y "%SRC%\PdfSharp.dll" "%DEST%\" >nul || goto :copyfail
echo Copying registration files...
copy /y "%~dp0InstallPDMLite.bat" "%DEST%\" >nul
copy /y "%~dp0RegisterPDMLite.reg" "%DEST%\" >nul

echo.
echo ============================================
echo    Deployed. New engineer PCs: IT runs
echo    %DEST%\InstallPDMLite.bat as Administrator.
echo    Already-registered PCs just restart SOLIDWORKS.
echo ============================================
echo.
pause
exit /b 0

:copyfail
echo.
echo ERROR: Copy failed. If the error says the file is in use,
echo        a machine still has SOLIDWORKS open with the add-in
echo        loaded - deploy after hours.

:fail
echo.
echo ============================================
echo    DEPLOY FAILED - see the error above.
echo ============================================
echo.
pause
exit /b 1
