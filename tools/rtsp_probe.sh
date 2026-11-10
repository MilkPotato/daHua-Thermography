#!/usr/bin/env bash
# RTSP 探测：匿名 DESCRIBE 取 Digest challenge -> 新连接带认证 DESCRIBE -> 输出 SDP
# 用法: ./rtsp_probe.sh [host] [port] [user] [pass] [path]
set -u

HOST="${1:-192.168.1.108}"
PORT="${2:-554}"
USER="${3:-admin}"
PASS="${4:-}"
PATHQ="${5:-/cam/realmonitor?channel=1&subtype=0}"

if [ -z "$PASS" ]; then
    echo "用法: $0 [host] [port] [user] [pass] [path]"
    echo "请通过第 4 个参数传入密码，不要把密码写死在脚本里。"
    exit 1
fi

URI="rtsp://${HOST}:${PORT}${PATHQ}"
md5() { printf '%s' "$1" | openssl dgst -md5 -r | cut -d' ' -f1; }

# rtsp_talk <fd> <request-text> : 发送并打印响应行
# 用法靠全局的 /tmp/rt_$$.txt 中转

# ---------- 第 1 步：匿名 DESCRIBE，抓 challenge ----------
: > /tmp/rt1.txt
{ printf 'DESCRIBE %s RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\nUser-Agent: RTSPProbe\r\n\r\n' "$URI"; } > /tmp/rt1.txt

timeout 8 bash -c "
  exec 3<>/dev/tcp/${HOST}/${PORT} || exit 1
  cat /tmp/rt1.txt >&3
  while IFS= read -r -t 5 -u 3 l; do printf '%s\n' \"\${l%\$'\r'}\"; done
" > /tmp/rt1_resp.txt 2>&1

echo "--- 第一轮响应 ---"
cat /tmp/rt1_resp.txt

realm="$(sed -n 's/.*realm="\([^"]*\)".*/\1/p' /tmp/rt1_resp.txt | head -1)"
nonce="$(sed -n 's/.*nonce="\([^"]*\)".*/\1/p' /tmp/rt1_resp.txt | head -1)"
echo
if [ -z "$nonce" ]; then echo "!! 未取得 Digest challenge"; exit 2; fi
echo "realm = $realm"
echo "nonce = $nonce"

HA1="$(md5 "${USER}:${realm}:${PASS}")"
HA2="$(md5 "DESCRIBE:${URI}")"
RESP="$(md5 "${HA1}:${nonce}:${HA2}")"
echo "resp  = $RESP"
echo

# ---------- 第 2 步：新连接，带 Digest ----------
{ printf 'DESCRIBE %s RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\nUser-Agent: RTSPProbe\r\nAuthorization: Digest username="%s", realm="%s", nonce="%s", uri="%s", response="%s"\r\n\r\n' \
    "$URI" "$USER" "$realm" "$nonce" "$URI" "$RESP"; } > /tmp/rt2.txt

echo "--- 第二轮响应 / SDP ---"
timeout 10 bash -c "
  exec 3<>/dev/tcp/${HOST}/${PORT} || exit 1
  cat /tmp/rt2.txt >&3
  while IFS= read -r -t 6 -u 3 l; do printf '%s\n' \"\${l%\$'\r'}\"; done
" 2>&1
