@echo off
rem Builds Window View into dist\: the OpenXR layer (C++, 64-bit and a 32-bit copy, needs Visual Studio 2022 Build Tools),
rem the stand-in game for the tests (build\xr_test_app.exe, 64-bit) and, once it exists, the app (C#, the compiler that
rem ships with Windows' .NET Framework 4). Run install.ps1 afterwards for the desktop icon.
setlocal
set HERE=%~dp0
set VCVARS=
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (echo Visual Studio Build Tools not found & exit /b 1)
call "%VCVARS%" >nul
if not exist "%HERE%dist" mkdir "%HERE%dist"
if not exist "%HERE%build" mkdir "%HERE%build"
if not exist "%HERE%build\x86" mkdir "%HERE%build\x86"

cl /nologo /O2 /EHsc /std:c++17 /LD /MT /W4 /I"%HERE%third_party" "%HERE%layer\window_view_layer.cpp" ^
   /Fo"%HERE%build\\" /Fe"%HERE%dist\window_view_layer.dll" /link /NOLOGO /IMPLIB:"%HERE%build\window_view_layer.lib"
if errorlevel 1 (echo LAYER BUILD FAILED & exit /b 1)

rem a 32-bit copy of the layer for 32-bit OpenXR games (built in its own shell, so the 64-bit tools above stay)
set "VCVARS32=%VCVARS:vcvars64=vcvars32%"
cmd /c "call "%VCVARS32%" >nul && cl /nologo /O2 /EHsc /std:c++17 /LD /MT /W4 /I"%HERE%third_party" "%HERE%layer\window_view_layer.cpp" /Fo"%HERE%build\x86\\" /Fe"%HERE%dist\window_view_layer32.dll" /link /NOLOGO /IMPLIB:"%HERE%build\x86\window_view_layer32.lib""
if errorlevel 1 (echo 32-BIT LAYER BUILD FAILED & exit /b 1)

rem 1.5: the window pictures through Windows Graphics Capture (C++/WinRT from the Windows SDK); the app falls back to PrintWindow without it
cl /nologo /O2 /EHsc /std:c++17 /MT /W3 /DNOMINMAX /LD "%HERE%capture\window_capture.cpp" ^
   /Fo"%HERE%build\\" /Fe"%HERE%dist\window_view_capture.dll" /link /NOLOGO /IMPLIB:"%HERE%build\window_view_capture.lib"
if errorlevel 1 (echo CAPTURE BUILD FAILED & exit /b 1)

cl /nologo /O2 /EHsc /std:c++17 /MT /W4 /I"%HERE%third_party" "%HERE%test\xr_test_app.cpp" ^
   /Fo"%HERE%build\\" /Fe"%HERE%build\xr_test_app.exe" /link /NOLOGO
if errorlevel 1 (echo TEST APP BUILD FAILED & exit /b 1)

if exist "%HERE%app\WindowView.cs" (
    "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /unsafe /win32icon:"%HERE%app\window_view.ico" ^
       /out:"%HERE%dist\WindowView.exe" "%HERE%app\*.cs"
    if errorlevel 1 (echo APP BUILD FAILED & exit /b 1)
    copy /y "%HERE%app\window_view.ico" "%HERE%dist\" >nul
)
echo BUILD OK: %HERE%dist
