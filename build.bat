@echo off
rem Compile Quadri-Size avec le compilateur C# fourni avec Windows (.NET Framework 4).
rem Rien a installer. Resultat : bin\QuadriSize.exe
setlocal
cd /d "%~dp0"

set "NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NET%\csc.exe" set "NET=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%NET%\csc.exe" (
    echo Compilateur C# introuvable : il faut le .NET Framework 4.
    exit /b 1
)
set "CSC=%NET%\csc.exe"

if not exist bin mkdir bin

rem Interface WPF : les fichiers XAML de src\ui sont integres a l'executable.
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:bin\QuadriSize.exe ^
    /win32manifest:src\app.manifest ^
    /win32icon:assets\quadri-size.ico ^
    /lib:"%NET%\WPF" ^
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
    /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll ^
    /resource:src\ui\Theme.xaml,QuadriSize.ui.Theme.xaml ^
    /resource:src\ui\MainWindow.xaml,QuadriSize.ui.MainWindow.xaml ^
    /resource:src\ui\Dialog.xaml,QuadriSize.ui.Dialog.xaml ^
    src\*.cs
if errorlevel 1 exit /b 1

if /i "%~1"=="test" (
    "%CSC%" /nologo /codepage:65001 /target:exe /out:bin\ScanTest.exe ^
        src\Scanner.cs src\Programs.cs tests\ScanTest.cs || exit /b 1
    bin\ScanTest.exe || exit /b 1
)

rem Raccourci "Quadri-Size" sur le bureau (avec l'icone). Une erreur ici ne fait pas echouer la compilation.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0raccourci.ps1" || echo Raccourci du bureau non cree.

echo OK : bin\QuadriSize.exe
