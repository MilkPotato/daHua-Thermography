@echo off
title RS-485 总线监听
cd /d "%~dp0"

echo.
echo ==================================================
echo   RS-485 总线监听器（自动轮询波特率）
echo ==================================================
echo   串口: COM3
echo   轮询: 9600 2400 4800 19200 38400 57600 115200
echo   每档 4 秒，收到数据会高亮显示
echo   退出: 按 Ctrl 加 C
echo ==================================================
echo.
echo   [提示] 若一直提示"打开失败 拒绝访问"，
echo          说明 COM3 被别的程序占用（比如 XCOM），
echo          先关掉那个程序再运行。
echo          串口号不对的话，改本文件末尾那行的 COM3。
echo.

powershell -NoProfile -Command "$p='COM3'; $bd=9600,2400,4800,19200,38400,57600,115200; $i=-1; while($true){ $i++; if($i -ge $bd.Count){ $i=0 }; $b=$bd[$i]; try { $s=New-Object System.IO.Ports.SerialPort($p,$b,'None',8,'One'); $s.Open() } catch { Write-Host ('  !! ' + $_.Exception.Message) -ForegroundColor Red; Start-Sleep -Seconds 3; continue }; $n=0; $h=''; $t=[System.Diagnostics.Stopwatch]::StartNew(); while($t.ElapsedMilliseconds -lt 4000){ if($s.BytesToRead -gt 0){ $v=$s.ReadByte(); $n++; if($h.Length -lt 90){ $h = $h + $v.ToString('X2') + ' ' } } else { Start-Sleep -Milliseconds 15 } }; try { $s.Close() } catch {}; if($n -gt 0){ Write-Host ''; Write-Host '  ****************************************************' -ForegroundColor Green; Write-Host ('  * 收到数据！波特率 ' + $b + '  共 ' + $n + ' 字节') -ForegroundColor Green; Write-Host ('  * HEX: ' + $h) -ForegroundColor Green; Write-Host '  ****************************************************' -ForegroundColor Green; Write-Host '' } else { Write-Host ('  监听中 ' + $b + ' bps ...      ') -NoNewline } }"

echo.
echo 监听已结束。
pause
