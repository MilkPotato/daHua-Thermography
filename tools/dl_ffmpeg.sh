#!/usr/bin/env bash
# 多路并行分片下载 ffmpeg（单连接被限速，并行可显著提速），带续传与完整性校验
set -u

# 用 n8.1 发行分支的 lgpl-shared 包：比 master 稳定，体积也只有一半
# （我们的用途是 RTSP 解复用 + MJPG 直通，不需要 GPL 编码器）
URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"
OUT="ffmpeg.zip"
PARTS=8
# 目录按分片数隔离：分片数变了，旧的边界就对不上，不能被 cat part_* 混进来
DIR="parts-$PARTS"

cd "$(dirname "$0")" || exit 1

# 1) 取总大小
TOTAL=$(curl -sIL --max-time 30 "$URL" | grep -i '^content-length:' | tail -1 | tr -d '\r' | awk '{print $2}')
if [ -z "$TOTAL" ]; then echo "!! 无法获取文件大小"; exit 1; fi
echo "[下载] 总大小: $TOTAL 字节 ($((TOTAL/1024/1024)) MB), 分 $PARTS 片"

mkdir -p "$DIR"
CHUNK=$(( (TOTAL + PARTS - 1) / PARTS ))

# 2) 分批并行下载各分片（已下完的跳过）
#    一次开 16 个 curl 会把 Git Bash 的 fork 资源耗尽，所以分 4 个一批
BATCH=4
i=0
while [ "$i" -lt "$PARTS" ]; do
  batch_end=$((i + BATCH))
  [ "$batch_end" -gt "$PARTS" ] && batch_end=$PARTS

  for j in $(seq $i $((batch_end-1))); do
    S=$((j * CHUNK))
    E=$((S + CHUNK - 1))
    [ "$E" -ge "$TOTAL" ] && E=$((TOTAL - 1))
    P="$DIR/part_$(printf '%02d' $j)"
    WANT=$((E - S + 1))

    if [ -f "$P" ] && [ "$(stat -c%s "$P" 2>/dev/null || echo 0)" -eq "$WANT" ]; then
      echo "  [跳过] 分片 $j 已完成"
      continue
    fi
    (
      curl -sL --retry 5 --retry-delay 2 --max-time 3600 -r "${S}-${E}" -o "$P" "$URL"
      GOT=$(stat -c%s "$P" 2>/dev/null || echo 0)
      if [ "$GOT" -ne "$WANT" ]; then
        echo "  [警告] 分片 $j 大小不符: 期望 $WANT 实得 $GOT"
      fi
    ) &
  done
  wait
  echo "  [进度] 已完成 $batch_end/$PARTS 批"
  i=$batch_end
done
echo "[下载] 全部分片完成，合并中…"

# 2.5) 合并前确认远端文件没被换掉（BtbN 的 latest 标签会滚动更新，换掉则分片作废）
NOW=$(curl -sIL --max-time 30 "$URL" | grep -i '^content-length:' | tail -1 | tr -d '\r' | awk '{print $2}')
if [ -n "$NOW" ] && [ "$NOW" != "$TOTAL" ]; then
  echo "!! 远端文件已变更 ($TOTAL -> $NOW)，分片作废，请重跑"
  exit 4
fi

# 3) 合并：显式按编号拼接，且每片都要存在
: > "$OUT"
for j in $(seq 0 $((PARTS-1))); do
  P="$DIR/part_$(printf '%02d' $j)"
  if [ ! -f "$P" ]; then
    echo "!! 缺少分片 $j"
    exit 5
  fi
  cat "$P" >> "$OUT"
done

GOT=$(stat -c%s "$OUT")
echo "[下载] 合并后: $GOT 字节"

if [ "$GOT" -ne "$TOTAL" ]; then
  echo "!! 大小不符，期望 $TOTAL 实得 $GOT"
  exit 2
fi

# 4) 校验 zip 完整性
if command -v unzip >/dev/null 2>&1; then
  if unzip -tq "$OUT" >/dev/null 2>&1; then
    echo "[校验] ZIP 完整性 OK"
  else
    echo "!! ZIP 校验失败"
    exit 3
  fi
fi

rm -rf "$DIR"
echo "[完成] $OUT 就绪"
