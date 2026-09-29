@echo off
setlocal

set "NO_PAUSE="
set "SIGN_RELEASE="
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto :parse_args
)
if /i "%~1"=="--sign" (
    set "SIGN_RELEASE=1"
    shift
    goto :parse_args
)
echo [ERROR] Unsupported publish option.
exit /b 2
:args_done

pushd "%~dp0"
if errorlevel 1 (
    echo [ERROR] Could not open the repository directory.
    if not defined NO_PAUSE pause
    exit /b 1
)

set "PROJECT=src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj"
set "OUTPUT=%CD%\dist\turtle-ai-quartet-hub"

echo ===================================================
echo  Turtle AI Code Quartet Hub - Release Publish
echo ===================================================
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] The dotnet SDK was not found. Install .NET 10 SDK and reopen this terminal.
    goto :failed
)

if defined SIGN_RELEASE (
    powershell.exe -NoProfile -File "scripts\Sign-Release.ps1" -Mode Preflight
    if errorlevel 1 (
        echo [ERROR] Signed release preflight failed.
        goto :failed
    )
)

echo Restoring packages for a self-contained win-x64 publish...
dotnet restore "%PROJECT%" -r win-x64 ^
    -p:Configuration=Release ^
    -p:SelfContained=true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:NuGetAudit=false ^
    -p:RestoreIgnoreFailedSources=true
if errorlevel 1 (
    echo [WARN] Restore with configured NuGet sources failed. Retrying with official nuget.org...
    dotnet restore "%PROJECT%" -r win-x64 ^
        --source https://api.nuget.org/v3/index.json ^
        -p:Configuration=Release ^
        -p:SelfContained=true ^
        -p:PublishSingleFile=true ^
        -p:IncludeNativeLibrariesForSelfExtract=true ^
        -p:DebugType=None ^
        -p:NuGetAudit=false
    if errorlevel 1 (
        echo [ERROR] Required .NET publish packs could not be restored.
        echo [INFO] Check the .NET 10 SDK, NuGet sources, and access to https://api.nuget.org/v3/index.json.
        echo [INFO] Configured NuGet sources:
        dotnet nuget list source
        goto :failed
    )
)

echo Publishing a self-contained, single-file win-x64 executable...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true --no-restore ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:NuGetAudit=false ^
    -o "%OUTPUT%"
if errorlevel 1 (
    echo [ERROR] Publish failed.
    goto :failed
)

if not exist "%OUTPUT%\TurtleAIQuartetHub.exe" (
    echo [ERROR] The executable was not created.
    goto :failed
)

if not exist "LICENSE.txt" (
    echo [ERROR] LICENSE.txt was not found in the repository.
    goto :failed
)
if not exist "config\turtle-ai-quartet-hub.example.json" (
    echo [ERROR] The example configuration was not found in the repository.
    goto :failed
)
if not exist "%OUTPUT%\config" mkdir "%OUTPUT%\config"
if errorlevel 1 (
    echo [ERROR] Could not create the output config directory.
    goto :failed
)
copy /Y "LICENSE.txt" "%OUTPUT%\LICENSE.txt" >nul
if errorlevel 1 (
    echo [ERROR] Could not copy LICENSE.txt into the release folder.
    goto :failed
)
copy /Y "config\turtle-ai-quartet-hub.example.json" "%OUTPUT%\config\turtle-ai-quartet-hub.example.json" >nul
if errorlevel 1 (
    echo [ERROR] Could not copy the example configuration into the release folder.
    goto :failed
)
if not exist "%OUTPUT%\LICENSE.txt" (
    echo [ERROR] LICENSE.txt was not included.
    goto :failed
)
if not exist "%OUTPUT%\config\turtle-ai-quartet-hub.example.json" (
    echo [ERROR] The example configuration was not included.
    goto :failed
)

if defined SIGN_RELEASE (
    powershell.exe -NoProfile -File "scripts\Sign-Release.ps1" -Mode Sign
    if errorlevel 1 (
        echo [ERROR] Release signing failed.
        goto :failed
    )
) else (
    powershell.exe -NoProfile -File "scripts\Sign-Release.ps1" -Mode Inspect
    if errorlevel 1 echo [WARN] Could not inspect the signature. Check it before distribution.
)

echo.
if defined SIGN_RELEASE (
    echo [SUCCESS] Signed release publish completed.
) else (
    echo [SUCCESS] Publish completed. Review the signature status above before distribution.
)
echo Executable: "%OUTPUT%\TurtleAIQuartetHub.exe"
echo Distribute the contents of "%OUTPUT%" together.
echo.
popd
if not defined NO_PAUSE pause
exit /b 0

:failed
echo.
popd
if not defined NO_PAUSE pause
exit /b 1
