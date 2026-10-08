@echo off
rem Builds GameCompressor.exe with the C# compiler that ships with Windows. Nothing to install.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find the .NET Framework C# compiler.
    pause
    exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /r:Microsoft.VisualBasic.dll /out:"%~dp0GameCompressor.exe" "%~dp0GameCompressor.cs"
if errorlevel 1 (
    pause
    exit /b 1
)
echo Built GameCompressor.exe