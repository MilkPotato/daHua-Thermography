@echo off
taskkill /F /IM ThermalView.exe >nul 2>&1
if errorlevel 1 (
  echo 服务未在运行。
) else (
  echo 服务已停止。
)
timeout /t 2 /nobreak >nul
