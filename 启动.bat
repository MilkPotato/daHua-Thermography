@echo off
title 大华热像仪 · 实时预览
cd /d "%~dp0"

if not exist "ThermalView.exe" (
  echo [错误] 找不到 ThermalView.exe，请先运行 build.bat 编译。
  pause
  exit /b 1
)

echo 正在启动服务...
echo.
echo   如果摄像头 IP 变了，请先修改 config.ini 里的 ip =
echo   摄像头是 DHCP 分配地址，重启后可能变化。建议在路由器上给它绑定静态 IP。
echo.

start "" "ThermalView.exe"

rem 等服务起来再开浏览器
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:8099/"

echo 已打开浏览器。关闭本窗口不会停止服务，需要停止请运行 停止.bat
exit /b 0
