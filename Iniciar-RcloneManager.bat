@echo off
setlocal
cd /d "%~dp0"

set "UI_EXE=%~dp0src\RcloneManager.UI\bin\Release\net10.0-windows\RcloneManager.UI.exe"
set "SVC_EXE=%~dp0src\RcloneManager.Service\bin\Release\net10.0\RcloneManager.Service.exe"

if not exist "%UI_EXE%" (
    echo Compilando RcloneManager em modo Release...
    dotnet build "%~dp0RcloneManager.sln" -c Release
    if errorlevel 1 (
        echo Falha ao compilar RcloneManager.sln.
        pause
        exit /b 1
    )
) else if not exist "%SVC_EXE%" (
    echo Compilando RcloneManager.Service em modo Release...
    dotnet build "%~dp0RcloneManager.sln" -c Release
)

start "" "%UI_EXE%"
endlocal
