@echo off
setlocal
cd /d "%~dp0"

echo === Reclaim: build va tao file cai dat ===

if not exist "third_party\nextdns\nextdns.exe" (
  echo LOI: thieu third_party\nextdns\nextdns.exe ^(xem third_party\nextdns\README.txt^)
  pause & exit /b 1
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
)
if not defined MSBUILD (
  echo LOI: khong tim thay MSBuild. Hay cai Visual Studio voi workload ".NET desktop development".
  pause & exit /b 1
)

echo [1/2] Build Release...
"%MSBUILD%" Reclaim.sln /restore /p:Configuration=Release /t:Rebuild /v:minimal
if errorlevel 1 ( echo LOI: build that bai. & pause & exit /b 1 )

if not exist "build\Release\nextdns\nextdns.exe" (
  echo LOI: build\Release\nextdns\nextdns.exe khong ton tai.
  pause & exit /b 1
)

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo.
  echo Build xong: thu muc build\Release.
  echo Muon tao ReclaimSetup.exe: cai Inno Setup 6 ^(https://jrsoftware.org/isdl.php^) roi chay lai file nay.
  pause & exit /b 0
)

echo [2/2] Tao file cai dat...
"%ISCC%" installer\Reclaim.iss
if errorlevel 1 ( echo LOI: tao file cai dat that bai. & pause & exit /b 1 )

echo.
echo XONG: installer\Output\ReclaimSetup.exe
pause
