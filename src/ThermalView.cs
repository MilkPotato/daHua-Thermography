// ThermalView —— 大华热像仪实时预览服务
//
// 本机是 MJPG 编码（不是 H.264），所以整条链路可以做到零重编码：
//
//   主路径  ffmpeg -rtsp_transport tcp -i rtsp://... -c:v copy -f mjpeg pipe:1
//           源是 H.265 时自动改为 -c:v mjpeg 转码（新固件不再支持 RTSP 主码流 MJPG）
//           -> 按 FFD8/FFD9 拆出完整 JPEG -> 直接转给浏览器
//
//   备路径  摄像头自带 HTTP MJPEG 接口（subtype=1，256x192，无需 ffmpeg）
//           -> Digest 认证 -> 拆 multipart -> 转给浏览器
//
// 浏览器侧就是一个 multipart/x-mixed-replace 的 <img>，不需要任何 JS 库。
//
// 编译: csc /target:exe /out:ThermalView.exe ThermalView.cs /r:System.dll /r:System.Drawing.dll
// 依赖: .NET Framework 4.x（Windows 自带）+ ffmpeg.exe（仅主路径需要）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class ThermalView
{
    // ---------------- 配置（可被 config.ini 覆盖） ----------------
    static string CameraIp = "192.168.1.108";   // 在 config.ini 中覆盖
    static string RtspUser = "admin";
    static string RtspPass = "";                  // 在 config.ini 中设置
    static int RtspChannel = 1;
    static int RtspSubtype = 0;      // 0 = 主码流 1280x960@25（新固件 H.265，程序转码为 MJPG）
    static int HttpSubtype = 1;      // 备路径用的 HTTP 码流（256x192）
    static int Port = 8099;
    static int PushFps = 25;
    static string ForceSource = "";  // "" 自动 | "rtsp" | "http"

    /// <summary>流式连接的空闲超时：10fps 下每 100ms 就该有数据，30s 静默即视为断流</summary>
    const int StreamIdleTimeoutMs = 30000;

    /// <summary>首帧等待上限：超时则杀掉 ffmpeg 重来（离线时它会一直挂着不退出）</summary>
    const int FirstFrameTimeoutSec = 15;

    static string RtspUrl
    {
        get
        {
            return string.Format(CultureInfo.InvariantCulture,
                "rtsp://{0}:{1}@{2}:554/cam/realmonitor?channel={3}&subtype={4}",
                RtspUser, RtspPass, CameraIp, RtspChannel, RtspSubtype);
        }
    }
    static string HttpMjpegUrl
    {
        get
        {
            return string.Format(CultureInfo.InvariantCulture,
                "http://{0}/cgi-bin/mjpg/video.cgi?channel={1}&subtype={2}",
                CameraIp, RtspChannel, HttpSubtype);
        }
    }
    static string HttpSnapUrl
    {
        get
        {
            return string.Format(CultureInfo.InvariantCulture,
                "http://{0}/cgi-bin/snapshot.cgi?channel={1}", CameraIp, RtspChannel);
        }
    }

    // ---------------- 运行状态 ----------------
    static readonly object FrameLock = new object();
    static byte[] _jpeg = null;
    static long _seq = 0;
    static readonly AutoResetEvent FrameReady = new AutoResetEvent(false);

    static int _w = 0, _h = 0;
    static long _decoded = 0;
    static double _fps = 0;
    static bool _online = false;
    static string _source = "—";
    static string _lastError = "";
    static DateTime _startTime = DateTime.Now;
    static DateTime _onlineSince = DateTime.MinValue;
    static int _reconnects = 0;
    static string _ffmpegDir = "";

    // ---------------- 入口 ----------------
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        LoadConfig(baseDir);
        ApplyArgs(args);
        LocateFfmpeg(baseDir);

        Banner();

        Thread w = new Thread(SourceWorker);
        w.IsBackground = true;
        w.Start();

        StartHttpServer();
        return 0;
    }

    static void Banner()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  大华热像仪 · 实时预览服务");
        Console.WriteLine("==================================================");
        Console.WriteLine("  摄像头  : " + CameraIp);
        Console.WriteLine("  主码流  : 1280x960 @25fps (RTSP subtype=" + RtspSubtype + "，MJPG 直通 / H.265 自动转码)");
        Console.WriteLine("  备码流  : 256x192  @10fps MJPG  (HTTP subtype=" + HttpSubtype + ")");
        Console.WriteLine("  ffmpeg  : " + (_ffmpegDir.Length > 0 ? Path.Combine(_ffmpegDir, "ffmpeg.exe") : "未找到（将走 HTTP 备路径）"));
        Console.WriteLine("  界面    : http://127.0.0.1:" + Port + "/");
        Console.WriteLine("==================================================");
        Console.WriteLine();
    }

    static void ApplyArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--ip" && i + 1 < args.Length) CameraIp = args[++i];
            else if (a == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out Port);
            else if (a == "--user" && i + 1 < args.Length) RtspUser = args[++i];
            else if (a == "--pass" && i + 1 < args.Length) RtspPass = args[++i];
            else if (a == "--source" && i + 1 < args.Length) ForceSource = args[++i];
            else if (!a.StartsWith("--") && a.Length > 0) CameraIp = a;
        }
    }

    static void LoadConfig(string baseDir)
    {
        string[] tries = new string[] {
            Path.Combine(baseDir, "config.ini"),
            Path.Combine(baseDir, "..", "config.ini"),
        };
        foreach (string p in tries)
        {
            if (!File.Exists(p)) continue;
            foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                // 剥掉行内注释，否则 "1   # 说明" 会让 int.TryParse 静默失败
                int hash = v.IndexOf('#');
                if (hash >= 0) v = v.Substring(0, hash).Trim();
                int semi = v.IndexOf(';');
                if (semi >= 0) v = v.Substring(0, semi).Trim();
                switch (k)
                {
                    case "ip": CameraIp = v; break;
                    case "user": RtspUser = v; break;
                    case "pass": RtspPass = v; break;
                    case "channel": int.TryParse(v, out RtspChannel); break;
                    case "subtype": int.TryParse(v, out RtspSubtype); break;
                    case "http_subtype": int.TryParse(v, out HttpSubtype); break;
                    case "port": int.TryParse(v, out Port); break;
                    case "source": ForceSource = v; break;
                }
            }
            Console.WriteLine("[配置] 已读取 " + Path.GetFullPath(p));
            return;
        }
    }

    static void LocateFfmpeg(string baseDir)
    {
        string[] cands = new string[] {
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(baseDir, "..", "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(baseDir, "..", "tools", "ffmpeg.exe"),
        };
        foreach (string c in cands)
        {
            if (File.Exists(c)) { _ffmpegDir = Path.GetDirectoryName(Path.GetFullPath(c)); return; }
        }
        _ffmpegDir = "";
    }

    static string FfmpegExe()
    {
        return _ffmpegDir.Length == 0 ? "ffmpeg.exe" : Path.Combine(_ffmpegDir, "ffmpeg.exe");
    }

    // =====================================================================
    //  视频源：主路径(RTSP/ffmpeg) 与 备路径(HTTP MJPEG)，自动降级
    // =====================================================================
    static void SourceWorker()
    {
        int backoff = 1000;

        while (true)
        {
            bool ok = false;
            try
            {
                if (ForceSource == "test")
                {
                    SetSource("测试图案");
                    RunTest();                       // 无限循环，不会返回
                }
                else if (ForceSource == "ffmpegtest")
                {
                    SetSource("ffmpeg 自检");
                    ok = RunFfmpegSelfTest();
                }
                else if (ForceSource == "http")
                {
                    SetSource("HTTP 备码流");
                    ok = RunHttpMjpeg();
                }
                else if (ForceSource == "rtsp")
                {
                    SetSource("RTSP 主码流");
                    ok = RunRtsp();
                }
                else
                {
                    // 自动：每轮都先试 RTSP 主码流（1280x960，OSD 清晰），
                    // 拿不到画面才退回 HTTP 备码流（256x192）。
                    // 不永久降级 —— 否则摄像头恢复后也一直卡在低分辨率上。
                    if (_ffmpegDir.Length > 0 || OnPath("ffmpeg.exe"))
                    {
                        SetSource("RTSP 主码流");
                        ok = RunRtsp();
                    }
                    if (!ok)
                    {
                        SetSource("HTTP 备码流");
                        ok = RunHttpMjpeg();
                    }
                }
            }
            catch (Exception ex) { _lastError = ex.Message; }

            if (ok) backoff = 1000;   // 正常跑过一段，重置退避

            _online = false;
            _reconnects++;
            Console.WriteLine("[源] 断开， " + (backoff / 1000) + "s 后重连   (" + Trim(_lastError, 160) + ")");
            Thread.Sleep(backoff);
            if (backoff < 15000) backoff = Math.Min(backoff * 2, 15000);
        }
    }

    static bool OnPath(string exe)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("where.exe", exe);
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return o.Trim().Length > 0;
            }
        }
        catch { return false; }
    }

    static void SetSource(string s) { _source = s; }

    // ---------------- 主路径：ffmpeg 转发（MJPG 源零重编码，H.265 源转码） ----------------
    static bool _rtspCopy = true;   // 先按 MJPG 源尝试直通；确认不是 MJPG 后改为转码

    static bool RunRtsp()
    {
        string input = "-rtsp_transport tcp -fflags nobuffer -flags low_delay -i \"" + RtspUrl + "\"";

        if (_rtspCopy)
        {
            if (RunFfmpegPipe(input, "-an -c:v copy", "RTSP")) return true;

            // 新固件（实测 2.630.0000000.4.R）主码流是 H.265，
            // copy 到 mjpeg 封装会直接失败；改成解码后再编码成 MJPEG。
            string err = _lastError ?? "";
            bool codecMismatch = err.IndexOf("mjpeg muxer", StringComparison.Ordinal) >= 0
                              || err.IndexOf("codec", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!codecMismatch) return false;

            _rtspCopy = false;
            Console.WriteLine("[RTSP] 主码流不是 MJPG，改为转码为 MJPEG   (" + Trim(err, 120) + ")");
        }

        // 相机主码流 25fps，实测 1280x960 的 H.265→MJPEG 转码能稳定跑满 25fps
        return RunFfmpegPipe(input, "-an -c:v mjpeg -q:v 4 -r 25", "RTSP转码");
    }

    /// <summary>
    /// 用 ffmpeg 内置测试源走一遍完整链路。
    /// 它验证的是 RTSP 路径的全部环节（ffmpeg 启动、参数、mjpeg 输出、拆帧），
    /// 只差输入源不是真的摄像头 —— 摄像头不在场时也能确认代码没问题。
    /// </summary>
    static bool RunFfmpegSelfTest()
    {
        return RunFfmpegPipe(
            "-f lavfi -re -i testsrc=size=1280x960:rate=10",   // -re: 按真实速率，模拟实况
            "-an -c:v mjpeg -q:v 4",
            "ffmpeg自检");
    }

    static bool RunFfmpegPipe(string inputSpec, string videoArgs, string label)
    {
        ProcessStartInfo psi = new ProcessStartInfo(FfmpegExe());
        psi.Arguments =
            "-hide_banner -loglevel warning -nostdin " +
            inputSpec + " " + videoArgs + " -f mjpeg pipe:1";
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardErrorEncoding = Encoding.UTF8;

        Process proc = Process.Start(psi);
        Process cur = proc;

        Thread et = new Thread(delegate ()
        {
            try
            {
                string line;
                while ((line = cur.StandardError.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    // 从 GOP 中间接入时的无害提示，不算故障
                    if (line.IndexOf("Skipping invalid undecodable NALU", StringComparison.Ordinal) >= 0) continue;
                    if (line.IndexOf("rror", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("ailed", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("nvalid", StringComparison.Ordinal) >= 0)
                        _lastError = line.Trim();
                }
            }
            catch { }
        });
        et.IsBackground = true; et.Start();

        Console.WriteLine("[" + label + "] ffmpeg 已启动 (pid " + proc.Id + ")，等待画面…");

        long before = _decoded;
        StartFirstFrameWatchdog(proc, before, label);

        try
        {
            MjpegSplitter split = new MjpegSplitter(proc.StandardOutput.BaseStream);
            byte[] jpg;
            while ((jpg = split.Next()) != null)
            {
                Publish(jpg);
                DetectSize(jpg);
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            try { proc.Dispose(); } catch { }
        }
        // 用增量判断本次是否真的出过帧，而不是看累计值
        return _decoded > before;
    }

    /// <summary>
    /// 首帧看门狗：摄像头离线时 ffmpeg 不会退出，而是一直挂着重试，
    /// 光靠读管道是感知不到的。超过限定时间还没出帧就把它杀掉重来。
    /// </summary>
    static void StartFirstFrameWatchdog(Process proc, long before, string label)
    {
        Thread t = new Thread(delegate ()
        {
            DateTime started = DateTime.Now;
            while (true)
            {
                Thread.Sleep(1000);
                if (_decoded > before) return;                       // 出帧了
                try { if (proc.HasExited) return; } catch { return; }

                if ((DateTime.Now - started).TotalSeconds > FirstFrameTimeoutSec)
                {
                    _lastError = label + ": 超过 " + FirstFrameTimeoutSec + " 秒未收到画面";
                    Console.WriteLine("[" + label + "] 首帧超时，重启 ffmpeg");
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    return;
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ---------------- 备路径：摄像头 HTTP MJPEG（Digest 认证） ----------------
    static bool RunHttpMjpeg()
    {
        HttpWebResponse resp = GetWithDigest(HttpMjpegUrl);
        Console.WriteLine("[HTTP] 已连接 " + HttpMjpegUrl + "，等待画面…");

        long before = _decoded;                 // 用增量判断本次是否真的出过帧
        using (resp)
        using (Stream s = resp.GetResponseStream())
        {
            // multipart/x-mixed-replace: 每个 part 头之后跟一段 JPEG
            MjpegSplitter split = new MjpegSplitter(s);
            byte[] jpg;
            while ((jpg = split.Next()) != null)
            {
                Publish(jpg);
                DetectSize(jpg);
            }
        }
        return _decoded > before;
    }

    // ---------------- 测试图案：摄像头离线时验证界面 ----------------
    static void RunTest()
    {
        const int W = 1280, H = 960;
        _w = W; _h = H;
        _online = true;
        _onlineSince = DateTime.Now;
        Console.WriteLine("[测试] 输出测试图案 " + W + "x" + H + "（不需要摄像头）");

        Bitmap bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        Graphics g = Graphics.FromImage(bmp);
        ImageCodecInfo codec = GetJpegCodec();
        EncoderParameters ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);

        Font fBig = new Font("Microsoft YaHei", 40, FontStyle.Bold);
        Font fMid = new Font("Consolas", 22);

        int t = 0;
        while (true)
        {
            using (System.Drawing.Drawing2D.LinearGradientBrush lg =
                   new System.Drawing.Drawing2D.LinearGradientBrush(
                       new Rectangle(0, 0, W, H),
                       Color.FromArgb(45, 10, 65), Color.FromArgb(95, 20, 95), 45f))
            {
                g.FillRectangle(lg, 0, 0, W, H);
            }

            // 移动的"热源"
            int cx = W / 2 + (int)(W * 0.30 * Math.Sin(t * 0.03));
            int cy = H / 2 + (int)(H * 0.25 * Math.Cos(t * 0.045));
            for (int r = 260; r > 0; r -= 20)
            {
                int a = 60 - r / 6;
                if (a < 6) a = 6;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 140, 20)))
                    g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 250, 220, 120)))
                g.FillEllipse(b, cx - 26, cy - 26, 52, 52);

            using (SolidBrush b = new SolidBrush(Color.White))
            {
                g.DrawString("测试图案 TEST PATTERN", fBig, b, 40, 40);
                g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), fMid, b, 40, 110);
                g.DrawString(W + "x" + H + "   帧 #" + t, fMid, b, 40, 150);
                g.DrawString("摄像头离线时用于验证界面", fMid, b, 40, H - 70);
            }
            t++;

            using (MemoryStream ms = new MemoryStream(128 * 1024))
            {
                bmp.Save(ms, codec, ep);
                Publish(ms.ToArray());
            }
            Thread.Sleep(100);
        }
    }

    static ImageCodecInfo GetJpegCodec()
    {
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
        {
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        }
        return null;
    }

    static long _fpsWindow = 0;
    static DateTime _fpsWindowStart = DateTime.Now;

    static void Publish(byte[] jpg)
    {
        lock (FrameLock) { _jpeg = jpg; _seq++; }
        _decoded++;

        // 只有真的收到画面才算在线。ffmpeg 进程起来 ≠ 流通了
        // （摄像头离线时 ffmpeg 会一直挂着重试，之前在这里误报"在线"）
        if (!_online)
        {
            _online = true;
            _onlineSince = DateTime.Now;
            _lastError = "";   // 已出画面，清掉启动阶段的解码噪声/旧错误
            Console.WriteLine("[源] 已收到画面，进入在线状态");
        }

        _fpsWindow++;
        double el = (DateTime.Now - _fpsWindowStart).TotalSeconds;
        if (el >= 1.0)
        {
            _fps = _fpsWindow / el;
            _fpsWindow = 0;
            _fpsWindowStart = DateTime.Now;
        }

        FrameReady.Set();
    }

    /// <summary>从 JPEG 的 SOF 段里读出真实分辨率（每 50 帧查一次即可）</summary>
    static void DetectSize(byte[] jpg)
    {
        if (_decoded % 50 != 0) return;
        int w, h;
        if (TryParseJpegSize(jpg, out w, out h)) { _w = w; _h = h; }
    }

    static bool TryParseJpegSize(byte[] d, out int w, out int h)
    {
        w = 0; h = 0;
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            // SOF0..SOF15，排除 DHT(C4) DAC(CC) RSTn(D0-D7)
            if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                h = (d[i + 5] << 8) | d[i + 6];
                w = (d[i + 7] << 8) | d[i + 8];
                return w > 0 && h > 0;
            }
            if (i + 3 >= d.Length) break;
            int segLen = (d[i + 2] << 8) | d[i + 3];
            if (segLen < 2) { i++; continue; }
            i += 2 + segLen;
        }
        return false;
    }

    // ---------------- Digest 认证 HTTP ----------------
    static HttpWebResponse GetWithDigest(string url)
    {
        return GetWithDigest(url, 15000);
    }

    static HttpWebResponse GetWithDigest(string url, int timeoutMs)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = StreamIdleTimeoutMs;   // 流式响应：别用 0（非法值）
        req.KeepAlive = true;
        req.UserAgent = "ThermalView/1.0";

        try { return (HttpWebResponse)req.GetResponse(); }
        catch (WebException we)
        {
            HttpWebResponse r = we.Response as HttpWebResponse;
            if (r == null || r.StatusCode != HttpStatusCode.Unauthorized)
                throw new Exception("HTTP 连接失败: " + we.Message);

            string chal = r.Headers["WWW-Authenticate"];
            r.Close();
            if (chal == null) throw new Exception("401 但无 WWW-Authenticate 头");

            string realm = Grab(chal, "realm");
            string nonce = Grab(chal, "nonce");
            string qop = Grab(chal, "qop");
            if (nonce == null) throw new Exception("Digest 缺少 nonce");

            string uriPath = new Uri(url).PathAndQuery;
            string ha1 = Md5Hex(RtspUser + ":" + realm + ":" + RtspPass);
            string ha2 = Md5Hex("GET:" + uriPath);
            string resp;

            if (!string.IsNullOrEmpty(qop) && qop.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 &&
                qop.IndexOf("auth-int", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string nc = "00000001";
                string cnonce = RandHex(16);
                resp = Md5Hex(ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2);
                chal = string.Format(CultureInfo.InvariantCulture,
                    "Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", " +
                    "qop=auth, nc={4}, cnonce=\"{5}\", response=\"{6}\", algorithm=MD5",
                    RtspUser, realm, nonce, uriPath, nc, cnonce, resp);
            }
            else
            {
                resp = Md5Hex(ha1 + ":" + nonce + ":" + ha2);
                chal = string.Format(CultureInfo.InvariantCulture,
                    "Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", response=\"{4}\"",
                    RtspUser, realm, nonce, uriPath, resp);
            }

            HttpWebRequest req2 = (HttpWebRequest)WebRequest.Create(url);
            req2.Method = "GET";
            req2.Timeout = timeoutMs;
            req2.ReadWriteTimeout = StreamIdleTimeoutMs;
            req2.KeepAlive = true;
            req2.UserAgent = "ThermalView/1.0";
            req2.Headers["Authorization"] = chal;
            return (HttpWebResponse)req2.GetResponse();
        }
    }

    static string Grab(string header, string key)
    {
        Match m = Regex.Match(header, key + "=\"?([^\",]+)\"?", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    static string Md5Hex(string s)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            StringBuilder sb = new StringBuilder(32);
            foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    static string RandHex(int n)
    {
        byte[] b = new byte[n];
        new Random().NextBytes(b);
        StringBuilder sb = new StringBuilder(n * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static string Trim(string s, int n)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // =====================================================================
    //  从字节流里按 FFD8..FFD9 拆出完整 JPEG
    // =====================================================================
    class MjpegSplitter
    {
        private readonly Stream _s;
        private readonly byte[] _buf;
        private int _len, _scan;
        private int _start = -1;

        public MjpegSplitter(Stream s, int cap = 6 << 20)
        {
            _s = s;
            _buf = new byte[cap];
        }

        public byte[] Next()
        {
            while (true)
            {
                while (_scan + 1 < _len)
                {
                    if (_buf[_scan] != 0xFF) { _scan++; continue; }

                    byte m = _buf[_scan + 1];
                    if (_start < 0)
                    {
                        if (m == 0xD8) { _start = _scan; _scan += 2; continue; }
                        _scan++;
                    }
                    else
                    {
                        if (m == 0xD9)
                        {
                            int end = _scan + 2;
                            byte[] jpg = new byte[end - _start];
                            Buffer.BlockCopy(_buf, _start, jpg, 0, jpg.Length);

                            int rest = _len - end;
                            if (rest > 0) Buffer.BlockCopy(_buf, end, _buf, 0, rest);
                            _len = rest; _scan = 0; _start = -1;
                            return jpg;
                        }
                        _scan++;
                    }
                }

                // 压缩缓冲：丢掉帧头之前的垃圾
                if (_start > 0)
                {
                    int rest = _len - _start;
                    Buffer.BlockCopy(_buf, _start, _buf, 0, rest);
                    _scan -= _start;
                    _len = rest;
                    _start = 0;
                }
                else if (_start < 0)
                {
                    _len = 0; _scan = 0;
                }

                // 单帧超出缓冲上限：丢弃重来
                if (_len >= _buf.Length)
                {
                    _len = 0; _scan = 0; _start = -1;
                }

                int r;
                try { r = _s.Read(_buf, _len, _buf.Length - _len); }
                catch { return null; }
                if (r <= 0) return null;
                _len += r;
            }
        }
    }

    // =====================================================================
    //  HTTP 服务（裸 TcpListener，避免 HttpListener 的 URL ACL/管理员问题）
    // =====================================================================
    static void StartHttpServer()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            // 允许复用处于 TIME_WAIT 的地址：否则停掉服务后立刻重启会因端口未释放而绑不上
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 端口 " + Port + " 监听失败: " + ex.Message);
            Console.WriteLine("   换端口: ThermalView.exe --port 8100");
            Environment.Exit(1);
        }
        Console.WriteLine("[HTTP] 监听 http://127.0.0.1:" + Port + "/    (Ctrl+C 退出)");
        Console.WriteLine();

        while (true)
        {
            TcpClient c = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(HandleClient, c);
        }
    }

    static void HandleClient(object state)
    {
        TcpClient client = (TcpClient)state;
        try
        {
            client.NoDelay = true;
            using (client)
            using (NetworkStream ns = client.GetStream())
            {
                string path = ReadRequestPath(ns);
                if (path == null) return;

                if (path == "/" || path == "/index.html") ServeFile(ns, "index.html", "text/html; charset=utf-8");
                else if (path == "/stream") ServeMjpeg(ns);
                else if (path == "/snapshot.jpg" || path == "/snapshot") ServeSnapshot(ns, path.EndsWith(".jpg"));
                else if (path == "/status") ServeStatus(ns);
                else if (path == "/roundtrip") ServeRoundTrip(ns);
                else if (path == "/roundtrip/original.jpg") ServeRoundTripImage(ns, "original");
                else if (path == "/roundtrip/lowres.png") ServeRoundTripImage(ns, "low");
                else if (path == "/roundtrip/upscaled.jpg") ServeRoundTripImage(ns, "recon");
                else Serve404(ns);
            }
        }
        catch { }
    }

    static string ReadRequestPath(NetworkStream ns)
    {
        ns.ReadTimeout = 10000;
        StringBuilder head = new StringBuilder();
        byte[] one = new byte[1];
        while (head.Length < 16384)
        {
            int n = ns.Read(one, 0, 1);
            if (n <= 0) return null;
            head.Append((char)one[0]);
            string s = head.ToString();
            if (s.EndsWith("\r\n\r\n") || s.EndsWith("\n\n")) break;
        }
        string[] lines = head.ToString().Split('\n');
        if (lines.Length == 0) return null;
        string[] parts = lines[0].Trim().Split(' ');
        if (parts.Length < 2) return null;
        if (parts[0] != "GET" && parts[0] != "HEAD") return null;
        int q = parts[1].IndexOf('?');
        return q >= 0 ? parts[1].Substring(0, q) : parts[1];
    }

    static void WriteHeader(NetworkStream ns, string status, string ctype, long len, string disp)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Server: ThermalView\r\n");
        if (ctype != null) sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
        if (len >= 0) sb.Append("Content-Length: ").Append(len).Append("\r\n");
        if (disp != null) sb.Append("Content-Disposition: ").Append(disp).Append("\r\n");
        sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
        sb.Append("Pragma: no-cache\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Connection: close\r\n\r\n");
        byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
        ns.Write(b, 0, b.Length);
    }

    static void ServeMjpeg(NetworkStream ns)
    {
        byte[] head = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Server: ThermalView\r\n" +
            "Content-Type: multipart/x-mixed-replace; boundary=--thermalview\r\n" +
            "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Connection: close\r\n\r\n");
        ns.Write(head, 0, head.Length);

        byte[] boundary = Encoding.UTF8.GetBytes("\r\n--thermalview\r\n");
        long seen = -1;
        int delay = Math.Max(15, 1000 / PushFps);

        while (true)
        {
            byte[] jpg; long seq;
            lock (FrameLock) { jpg = _jpeg; seq = _seq; }

            if (jpg != null && seq != seen)
            {
                seen = seq;

                ns.Write(boundary, 0, boundary.Length);
                byte[] part = Encoding.UTF8.GetBytes(
                    "Content-Type: image/jpeg\r\nContent-Length: " + jpg.Length + "\r\n\r\n");
                ns.Write(part, 0, part.Length);
                ns.Write(jpg, 0, jpg.Length);

                FrameReady.WaitOne(delay);
            }
            else
            {
                FrameReady.WaitOne(delay);
            }
        }
    }

    // 高清快照缓存：摄像头 snapshot.cgi 单张要 ~500ms，缓存 3 秒避免连点打爆它
    static byte[] _hdSnap = null;
    static DateTime _hdSnapAt = DateTime.MinValue;
    static readonly object _hdLock = new object();

    /// <summary>取摄像头原生 1280x960 快照（OSD 温度/时间戳在此分辨率下是锐利的）</summary>
    static byte[] GetHdSnapshot()
    {
        lock (_hdLock)
        {
            if (_hdSnap != null && (DateTime.Now - _hdSnapAt).TotalSeconds < 3) return _hdSnap;
        }
        try
        {
            // 快照接口正常 ~500ms 就返回，超时给长了只会让「截图」按钮卡住
            using (HttpWebResponse r = GetWithDigest(HttpSnapUrl, 6000))
            using (Stream s = r.GetResponseStream())
            using (MemoryStream ms = new MemoryStream(128 * 1024))
            {
                byte[] buf = new byte[64 * 1024];
                int n, total = 0;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    total += n;
                    if (total > (4 << 20)) break;
                }
                byte[] jpg = ms.ToArray();
                // 确认是完整 JPEG 再缓存
                if (jpg.Length > 1024 && jpg[0] == 0xFF && jpg[1] == 0xD8 &&
                    jpg[jpg.Length - 2] == 0xFF && jpg[jpg.Length - 1] == 0xD9)
                {
                    lock (_hdLock) { _hdSnap = jpg; _hdSnapAt = DateTime.Now; }
                    return jpg;
                }
            }
        }
        catch (Exception ex) { _lastError = "高清快照失败: " + ex.Message; }
        return null;
    }

    static void ServeSnapshot(NetworkStream ns, bool attach)
    {
        // 优先用摄像头原生高清快照（1280x960，温度值可读）；拿不到再退回当前直播帧
        byte[] jpg = GetHdSnapshot();
        if (jpg == null) { lock (FrameLock) { jpg = _jpeg; } }
        if (jpg == null)
        {
            WriteHeader(ns, "503 Service Unavailable", "text/plain; charset=utf-8", -1, null);
            byte[] m = Encoding.UTF8.GetBytes("暂无画面（摄像头离线或正在重连）");
            ns.Write(m, 0, m.Length);
            return;
        }
        string fn = "thermal_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
        WriteHeader(ns, "200 OK", "image/jpeg", jpg.Length,
                    attach ? "attachment; filename=\"" + fn + "\"" : null);
        ns.Write(jpg, 0, jpg.Length);
    }

    static void ServeStatus(NetworkStream ns)
    {
        double up = (DateTime.Now - _startTime).TotalSeconds;
        double onl = _online ? (DateTime.Now - _onlineSince).TotalSeconds : 0;
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"online\":").Append(_online ? "true" : "false").Append(",");
        sb.Append("\"source\":").Append(J(_source)).Append(",");
        sb.Append("\"fps\":").Append(_fps.ToString("0.0", CultureInfo.InvariantCulture)).Append(",");
        sb.Append("\"width\":").Append(_w).Append(",\"height\":").Append(_h).Append(",");
        sb.Append("\"frames\":").Append(_decoded).Append(",");
        sb.Append("\"uptime\":").Append(up.ToString("0", CultureInfo.InvariantCulture)).Append(",");
        sb.Append("\"onlineSeconds\":").Append(onl.ToString("0", CultureInfo.InvariantCulture)).Append(",");
        sb.Append("\"reconnects\":").Append(_reconnects).Append(",");
        sb.Append("\"jpegBytes\":").Append(_jpeg == null ? 0 : _jpeg.Length).Append(",");
        sb.Append("\"camera\":").Append(J(CameraIp)).Append(",");
        sb.Append("\"lastError\":").Append(J(_lastError));
        sb.Append("}");

        byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
        WriteHeader(ns, "200 OK", "application/json; charset=utf-8", b.Length, null);
        ns.Write(b, 0, b.Length);
    }

    // =====================================================================
    //  有损往返实验：1280×960 → 256×192 → 1280×960
    //  用来量化“高分辨率画面里有没有超过 256×192 的真实细节”
    // =====================================================================
    static readonly object _rtLock = new object();
    static byte[] _rtOriginal, _rtLowPng, _rtReconJpg;
    static double _rtPsnr, _rtSsim, _rtMs;
    static int _rtW, _rtH, _rtLowW, _rtLowH;
    static long _rtOriginalBytes, _rtLowBytes, _rtReconBytes;
    static DateTime _rtAt = DateTime.MinValue;

    static void BuildRoundTrip()
    {
        // 优先用原生高清快照；snapshot.cgi 暂时失败时退而用当前直播帧
        byte[] src = (ForceSource == "test") ? null : GetHdSnapshot();
        if (src == null) { lock (FrameLock) { src = _jpeg; } }
        if (src == null) throw new Exception("摄像头暂无快照（离线或正在重连）");

        Stopwatch sw = Stopwatch.StartNew();
        using (MemoryStream ms = new MemoryStream(src))
        using (Bitmap original = new Bitmap(ms))
        {
            int w = original.Width, h = original.Height;
            if (w < 640 || h < 480)
                throw new Exception("当前画面只有 " + w + "×" + h + "，需要 1280×960 主码流才能做往返对比");
            int lw = Math.Max(1, w / 5), lh = Math.Max(1, h / 5);   // 1280×960 → 256×192

            using (Bitmap low = new Bitmap(lw, lh, PixelFormat.Format24bppRgb))
            using (Bitmap recon = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(low))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(original, new Rectangle(0, 0, lw, lh));
                }
                using (Graphics g = Graphics.FromImage(recon))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(low, new Rectangle(0, 0, w, h));
                }

                byte[] la = LumaBytes(original), lb = LumaBytes(recon);
                _rtPsnr = PsnrLuma(la, lb);
                _rtSsim = SsimBlocks(la, lb, w, h, 8);

                using (MemoryStream o = new MemoryStream())
                {
                    low.Save(o, ImageFormat.Png);
                    _rtLowPng = o.ToArray();
                }
                using (MemoryStream o = new MemoryStream())
                {
                    ImageCodecInfo codec = GetJpegCodec();
                    if (codec != null)
                    {
                        EncoderParameters ep = new EncoderParameters(1);
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        recon.Save(o, codec, ep);
                    }
                    else recon.Save(o, ImageFormat.Jpeg);
                    _rtReconJpg = o.ToArray();
                }

                _rtOriginal = src;
                _rtOriginalBytes = src.Length;
                _rtLowBytes = _rtLowPng.Length;
                _rtReconBytes = _rtReconJpg.Length;
                _rtW = w; _rtH = h; _rtLowW = lw; _rtLowH = lh;
                _rtMs = sw.Elapsed.TotalMilliseconds;
                _rtAt = DateTime.Now;
            }
        }
    }

    /// <summary>取灰度（BT.601 亮度），避免逐像素 GetPixel 带来的巨大开销</summary>
    static byte[] LumaBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte[] lum = new byte[w * h];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            int bpp = Image.GetPixelFormatSize(bd.PixelFormat) / 8;
            if (bpp < 3) throw new Exception("不支持的像素格式: " + bd.PixelFormat);
            byte[] row = new byte[Math.Abs(bd.Stride)];
            for (int y = 0; y < h; y++)
            {
                IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                Marshal.Copy(p, row, 0, row.Length);
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = x * bpp;
                    lum[o + x] = (byte)((54 * row[i] + 183 * row[i + 1] + 19 * row[i + 2]) >> 8);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return lum;
    }

    static double PsnrLuma(byte[] a, byte[] b)
    {
        double mse = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            mse += d * d;
        }
        mse /= a.Length;
        return mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    static double SsimBlocks(byte[] a, byte[] b, int w, int h, int bs)
    {
        const double C1 = 6.5025;    // (0.01*255)^2
        const double C2 = 58.5225;   // (0.03*255)^2
        double sum = 0;
        int blocks = 0;
        for (int by = 0; by < h; by += bs)
        {
            int bh = Math.Min(bs, h - by);
            for (int bx = 0; bx < w; bx += bs)
            {
                int bw = Math.Min(bs, w - bx);
                int n = bw * bh;
                double mx = 0, my = 0;
                for (int y = 0; y < bh; y++)
                {
                    int off = (by + y) * w + bx;
                    for (int x = 0; x < bw; x++) { mx += a[off + x]; my += b[off + x]; }
                }
                mx /= n; my /= n;

                double vx = 0, vy = 0, cxy = 0;
                for (int y = 0; y < bh; y++)
                {
                    int off = (by + y) * w + bx;
                    for (int x = 0; x < bw; x++)
                    {
                        double dx = a[off + x] - mx;
                        double dy = b[off + x] - my;
                        vx += dx * dx; vy += dy * dy; cxy += dx * dy;
                    }
                }
                vx /= n; vy /= n; cxy /= n;
                sum += ((2 * mx * my + C1) * (2 * cxy + C2)) /
                       ((mx * mx + my * my + C1) * (vx + vy + C2));
                blocks++;
            }
        }
        return blocks == 0 ? 0 : sum / blocks;
    }

    static void ServeRoundTrip(NetworkStream ns)
    {
        try
        {
            double psnr, ssim, ms;
            int w, h, lw, lh;
            long ob, lb, rb;
            DateTime at;
            lock (_rtLock)
            {
                BuildRoundTrip();
                psnr = _rtPsnr; ssim = _rtSsim; ms = _rtMs;
                w = _rtW; h = _rtH; lw = _rtLowW; lh = _rtLowH;
                ob = _rtOriginalBytes; lb = _rtLowBytes; rb = _rtReconBytes;
                at = _rtAt;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"ok\":true,");
            sb.Append("\"width\":").Append(w).Append(",\"height\":").Append(h).Append(",");
            sb.Append("\"lowWidth\":").Append(lw).Append(",\"lowHeight\":").Append(lh).Append(",");
            sb.Append("\"psnr\":").Append(psnr.ToString("0.00", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"ssim\":").Append(ssim.ToString("0.0000", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"originalBytes\":").Append(ob).Append(",");
            sb.Append("\"lowBytes\":").Append(lb).Append(",");
            sb.Append("\"reconBytes\":").Append(rb).Append(",");
            sb.Append("\"elapsedMs\":").Append(ms.ToString("0", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"at\":").Append(J(at.ToString("HH:mm:ss")));
            sb.Append("}");

            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            WriteHeader(ns, "200 OK", "application/json; charset=utf-8", b.Length, null);
            ns.Write(b, 0, b.Length);
        }
        catch (Exception ex)
        {
            byte[] b = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":" + J(ex.Message) + "}");
            WriteHeader(ns, "503 Service Unavailable", "application/json; charset=utf-8", b.Length, null);
            ns.Write(b, 0, b.Length);
        }
    }

    static void ServeRoundTripImage(NetworkStream ns, string kind)
    {
        byte[] data = null;
        string ctype = null;
        string err = null;
        try
        {
            lock (_rtLock)
            {
                if (_rtOriginal == null) BuildRoundTrip();
                if (kind == "original") { data = _rtOriginal; ctype = "image/jpeg"; }
                else if (kind == "low") { data = _rtLowPng; ctype = "image/png"; }
                else if (kind == "recon") { data = _rtReconJpg; ctype = "image/jpeg"; }
            }
        }
        catch (Exception ex) { err = ex.Message; }

        if (data == null)
        {
            byte[] m = Encoding.UTF8.GetBytes("实验图片不可用: " + (err ?? "no experiment result"));
            WriteHeader(ns, "503 Service Unavailable", "text/plain; charset=utf-8", m.Length, null);
            ns.Write(m, 0, m.Length);
            return;
        }
        WriteHeader(ns, "200 OK", ctype, data.Length, null);
        ns.Write(data, 0, data.Length);
    }

    static string J(string s)
    {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append('?');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static void ServeFile(NetworkStream ns, string file, string ctype)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string[] tries = new string[] { Path.Combine(dir, "www", file), Path.Combine(dir, file) };
        byte[] body = null;
        foreach (string p in tries) { if (File.Exists(p)) { body = File.ReadAllBytes(p); break; } }
        if (body == null)
        {
            string f = "<!doctype html><meta charset=utf-8><title>ThermalView</title>" +
                "<body style='background:#111;color:#eee;font-family:sans-serif;padding:40px'>" +
                "<h2>ThermalView</h2><p>缺少界面文件 <code>www/index.html</code></p>" +
                "<p>可直接看流：<a style='color:#4af' href='/stream'>/stream</a></p></body>";
            body = Encoding.UTF8.GetBytes(f);
        }
        WriteHeader(ns, "200 OK", ctype, body.Length, null);
        ns.Write(body, 0, body.Length);
    }

    static void Serve404(NetworkStream ns)
    {
        byte[] b = Encoding.UTF8.GetBytes("404 Not Found");
        WriteHeader(ns, "404 Not Found", "text/plain; charset=utf-8", b.Length, null);
        ns.Write(b, 0, b.Length);
    }
}
