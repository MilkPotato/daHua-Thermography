# RS-485 总线监听器
#
# 轮询常用波特率监听串口，收到数据就以十六进制打印出来并标注对应的波特率。
# 用途：验证摄像头 RS-485 是否真的在发数据。
#
# 注意：RS-485 是主从总线，摄像头作为从机不会主动说话。
#       被动监听只有在「有人在操作云台/传感器主动上报」时才会看到数据。

param(
    [string]$Port = 'COM3',
    [int[]]$Bauds = @(9600, 2400, 4800, 19200, 38400, 57600, 115200),
    [int]$SecondsPerBaud = 4
)

Write-Host ""
Write-Host "=================================================="
Write-Host "  RS-485 总线监听器"
Write-Host "=================================================="
Write-Host "  串口    : $Port"
Write-Host "  波特率  : $($Bauds -join ', ')"
Write-Host "  每档监听: ${SecondsPerBaud}s"
Write-Host "  退出    : Ctrl+C"
Write-Host "=================================================="
Write-Host ""

# 确认串口存在
if (-not ([System.IO.Ports.SerialPort]::GetPortNames() -contains $Port)) {
    Write-Host "[错误] 找不到串口 $Port" -ForegroundColor Red
    Write-Host "       当前可用: $([System.IO.Ports.SerialPort]::GetPortNames() -join ', ')"
    Read-Host "按回车退出"
    exit 1
}

$totalHits = 0
$index = 0

while ($true) {
    $baud = $Bauds[$index % $Bauds.Count]
    $index++

    $sp = New-Object System.IO.Ports.SerialPort($Port, $baud, 'None', 8, 'One')
    try {
        $sp.ReadTimeout = 200
        $sp.Open()
    } catch {
        Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] 打开 $baud 失败: $($_.Exception.Message)" -ForegroundColor Red
        Start-Sleep -Seconds 2
        continue
    }

    $bytes = New-Object System.Collections.Generic.List[byte]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt ($SecondsPerBaud * 1000)) {
        try {
            if ($sp.BytesToRead -gt 0) {
                $bytes.Add([byte]$sp.ReadByte())
            } else {
                Start-Sleep -Milliseconds 15
            }
        } catch { }
    }
    try { $sp.Close() } catch { }

    if ($bytes.Count -gt 0) {
        $totalHits++
        $hex  = ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
        $ascii = -join ($bytes | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } })

        Write-Host ""
        Write-Host ("=" * 60) -ForegroundColor Green
        Write-Host "  ★ 收到数据！波特率 $baud ，$($bytes.Count) 字节" -ForegroundColor Green
        Write-Host "  时间 : $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
        Write-Host "  HEX  : $hex"
        Write-Host "  ASCII: $ascii"
        Write-Host ("=" * 60) -ForegroundColor Green
        Write-Host ""
    }
}

Write-Host "监听结束，共捕获 $totalHits 次数据。"
