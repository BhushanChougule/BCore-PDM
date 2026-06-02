@echo off
echo ============================================
echo    BCore PDM - SOLIDWORKS Add-in Installer
echo ============================================
echo.

echo Step 1: Registering DLL with Windows...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" "D:\06 SOLIDWORKS_Automation\08_Documentation\PDMLite_CL\PDMLite\PDMLite\bin\Debug\PDMLite.dll" /codebase /nologo

echo.
echo Step 2: Registering with SOLIDWORKS...
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" /ve /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" /v "Title" /t REG_SZ /d "BCore PDM" /f
reg add "HKLM\SOFTWARE\SolidWorks\AddIns\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" /v "Description" /t REG_SZ /d "BCore PDM - Property Enforcer and Vault" /f

echo.
echo ============================================
echo    Done! Please restart SOLIDWORKS now.
echo ============================================
echo.
pause