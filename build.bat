@echo off
rem Compile Quadri-Size avec le compilateur C# fourni avec Windows (.NET Framework 4).
rem Rien a installer. Resultat : bin\QuadriSize.exe
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Compilateur C# introuvable : il faut le .NET Framework 4.
    exit /b 1
)

if not exist bin mkdir bin

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:bin\QuadriSize.exe ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs
if errorlevel 1 exit /b 1

if /i "%~1"=="test" (
    "%CSC%" /nologo /codepage:65001 /target:exe /out:bin\ScanTest.exe src\Scanner.cs tests\ScanTest.cs || exit /b 1
    bin\ScanTest.exe || exit /b 1
)

echo OK : bin\QuadriSize.exe
