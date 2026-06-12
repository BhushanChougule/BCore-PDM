@echo off
setlocal

rem ============================================================
rem  BCore PDM - SOLIDWORKS Add-in Installer
rem
rem  Run AS ADMINISTRATOR on each engineer PC. Registers the
rem  DEPLOYED copy of the add-in:
rem
rem      N:\PDM-SolidWorks\ADDIN\PDMLite.dll
rem
rem  Elevated sessions often do NOT have the N: drive mapping.
rem  If N: is not visible here, either map it in this window
rem  (net use N: \\server\share) or pass the UNC ADDIN folder:
rem
rem      InstallPDMLite.bat "\\server\PDM-SolidWorks\ADDIN"
rem ============================================================

set "ADDIN=N:\PDM-SolidWorks\ADDIN"
if not "%~1"=="" set "ADDIN=%~1"
set "DLL=%ADDIN%\PDMLite.dll"
set "REGASM=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "GUID={A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"

echo ============================================
echo    BCore PDM - SOLIDWORKS Add-in Installer
echo ============================================
echo.
echo Add-in folder: %ADDIN%
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: This installer must be run AS ADMINISTRATOR.
    echo        Right-click InstallPDMLite.bat and choose "Run as administrator".
    goto :fail
)

if not exist "%DLL%" (
    echo ERROR: %DLL% not found.
    echo.
    echo  - Deploy the add-in first: run DeployPDMLite.bat on the build machine.
    echo  - Elevated sessions often lack the N: mapping. Map it in this window
    echo    ^(net use N: \\^<server^>\^<share^>^) or rerun with the UNC path:
    echo        InstallPDMLite.bat "\\^<server^>\PDM-SolidWorks\ADDIN"
    goto :fail
)

if not exist "%ADDIN%\PdfSharp.dll" (
    echo WARNING: PdfSharp.dll not found next to PDMLite.dll.
    echo          Released PDFs will NOT be watermarked until it is deployed.
    echo.
)

if not exist "%REGASM%" (
    echo ERROR: 64-bit .NET Framework 4.x RegAsm not found at:
    echo        %REGASM%
    echo        Install .NET Framework 4.8 and retry.
    goto :fail
)

echo Step 1: Registering DLL with Windows ^(COM^)...
"%REGASM%" "%DLL%" /codebase /nologo
if errorlevel 1 (
    echo.
    echo ERROR: RegAsm failed - the SOLIDWORKS add-in keys were NOT written,
    echo        so SOLIDWORKS will not show a broken add-in entry.
    echo        Fix the error above and rerun.
    goto :fail
)

echo.
echo Step 2: Registering with SOLIDWORKS...
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\%GUID%" /ve /t REG_DWORD /d 1 /f || goto :regfail
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\%GUID%" /v "Title" /t REG_SZ /d "BCore PDM" /f || goto :regfail
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\%GUID%" /v "Description" /t REG_SZ /d "BCore PDM - Property Enforcer and Vault" /f || goto :regfail

echo.
echo ============================================
echo    Done! Please restart SOLIDWORKS now.
echo ============================================
echo.
pause
exit /b 0

:regfail
echo.
echo ERROR: Writing the SOLIDWORKS registry keys failed ^(see above^).

:fail
echo.
echo ============================================
echo    INSTALL FAILED - see the error above.
echo ============================================
echo.
pause
exit /b 1
