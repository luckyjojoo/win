@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo Cannot find the .NET Framework C# compiler.
    echo Use GitHub Actions or build from a Windows machine with Visual Studio Build Tools.
    exit /b 1
)

if not exist "release" mkdir release

"%CSC%" ^
  /nologo ^
  /target:winexe ^
  /platform:anycpu ^
  /optimize+ ^
  /codepage:utf8 ^
  /out:release\AppDataLens.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  Program.cs

if errorlevel 1 exit /b %errorlevel%

echo Built release\AppDataLens.exe
