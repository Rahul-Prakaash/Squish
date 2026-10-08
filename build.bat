@echo off
rem Builds Squish.exe with the C# compiler that ships with Windows. Nothing to install.
rem Then makes Portable\Squish\ (the app plus 7-Zip's command-line engine) and a zip of it,
rem which runs on any Windows 10/11 PC, with or without 7-Zip installed.
setlocal
set HERE=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find the .NET Framework C# compiler.
    pause
    exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:"%HERE%app.ico" /r:Microsoft.VisualBasic.dll /resource:"%HERE%fonts\Jost-400-Book.ttf" /resource:"%HERE%fonts\Jost-500-Medium.ttf" /resource:"%HERE%fonts\Jost-600-Semi.ttf" /out:"%HERE%Squish.exe" "%HERE%Squish.cs"
if errorlevel 1 (
    pause
    exit /b 1
)
echo Built Squish.exe

rem Find the installed 7-Zip to bundle its engine.
set SZ=
for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\7-Zip" /v Path 2^>nul ^| find "Path"') do set SZ=%%b
if not defined SZ if exist "%ProgramFiles%\7-Zip\7z.exe" set SZ=%ProgramFiles%\7-Zip\
if not defined SZ (
    echo 7-Zip is not installed here, so the portable folder was not made.
    exit /b 0
)

set OUT=%HERE%Portable\Squish
if exist "%HERE%Portable" rd /s /q "%HERE%Portable"
mkdir "%OUT%\7-Zip"
copy /y "%HERE%Squish.exe" "%OUT%\" >nul
copy /y "%HERE%README.txt" "%OUT%\" >nul
copy /y "%SZ%7z.exe" "%OUT%\7-Zip\" >nul
copy /y "%SZ%7z.dll" "%OUT%\7-Zip\" >nul
if exist "%SZ%7z.sfx" copy /y "%SZ%7z.sfx" "%OUT%\7-Zip\" >nul
copy /y "%SZ%License.txt" "%OUT%\7-Zip\" >nul
copy /y "%HERE%fonts\OFL.txt" "%OUT%\Font license (Jost).txt" >nul
"%SZ%7z.exe" a -tzip -mx=9 -bso0 -bsp0 "%HERE%Portable\Squish-Portable.zip" "%OUT%" >nul
echo Made Portable\Squish\ and Portable\Squish-Portable.zip
