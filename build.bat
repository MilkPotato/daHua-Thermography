@echo off
setlocal

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
  exit /b 1
)

echo [编译] ThermalView.exe
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%~dp0ThermalView.exe" ^
  "%~dp0src\ThermalView.cs" ^
  /r:System.dll ^
  /r:System.Drawing.dll

if errorlevel 1 (
  echo [失败] 编译未通过
  exit /b 1
)

echo [完成] %~dp0ThermalView.exe
endlocal
