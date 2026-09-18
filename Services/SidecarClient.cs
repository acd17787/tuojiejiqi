using Newtonsoft.Json;
using Rhino;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIRenderer.Services
{
    /// <summary>
    /// Routes all HttpClient requests through a separate Sidecar.exe process via named pipe.
    /// This allows API calls to work even when Rhino.exe is blocked by firewall.
    /// Falls back to net48 Sidecar if net7.0 Sidecar fails to start (missing global runtime).
    /// </summary>
    public class SidecarHttpMessageHandler : HttpMessageHandler
    {
        private static Process s_process;
        private static string s_activeSidecarPath;
        private static readonly object s_lock = new object();
        private static int s_refCount;
        private static bool s_net48FallbackActive;

        private readonly string _pipeName;
        private bool _disposed;

        public SidecarHttpMessageHandler()
        {
            _pipeName = $"TuoJieSidecar-{Process.GetCurrentProcess().Id}";
            lock (s_lock)
            {
                // 计数语义：构造 +1、Dispose -1，一一对应。起进程的路径（首次 / 进程已死 /
                // 重启）都不再碰计数。
                // 原来用 s_refCount = Math.Max(1, s_refCount) 兜底：进程自行退出后新建的
                // handler 不会被计入，别的 handler 释放时会把计数打到 0，把新进程杀掉。
                s_refCount++;
                try
                {
                    if (s_process == null || s_process.HasExited)
                        StartSidecarLocked();
                }
                catch
                {
                    s_refCount--;   // 起不来就别占着计数
                    throw;
                }
            }
        }

        /// <summary>
        /// 启动侧车进程。只负责起进程，不碰引用计数（计数由构造 / Dispose 管）。
        /// 调用方必须已持有 s_lock。
        /// </summary>
        private void StartSidecarLocked()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            LogSidecar($"Plugin directory: {pluginDir}");

            // Try net7.0 Sidecar first, then net48 fallback
            string[] candidates = s_net48FallbackActive
                ? new[] { Path.Combine(pluginDir ?? ".", "net48-sidecar", "TuoJieSidecar.exe") }
                : new[] {
                    Path.Combine(pluginDir ?? ".", "TuoJieSidecar.exe"),
                    Path.Combine(pluginDir ?? ".", "net48-sidecar", "TuoJieSidecar.exe"),
                    Path.Combine(pluginDir ?? ".", "TuoJieSidecar-net48.exe")
                  };

            Exception lastError = null;
            foreach (var sidecarExe in candidates)
            {
                LogSidecar($"Checking Sidecar candidate: {sidecarExe}");
                if (!File.Exists(sidecarExe))
                {
                    LogSidecar($"Missing Sidecar candidate: {sidecarExe}");
                    continue;
                }

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = sidecarExe,
                        Arguments = $"\"{_pipeName}\" {Process.GetCurrentProcess().Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };

                try
                {
                    proc.Start();
                    LogSidecar($"Started {Path.GetFileName(sidecarExe)} pid={proc.Id}");

                    // Wait and verify it stays alive (not an immediate crash)
                    Thread.Sleep(500);

                    if (proc.HasExited)
                    {
                        var stderr = proc.StandardError.ReadToEnd();
                        var exitCode = proc.ExitCode;
                        var exeName = Path.GetFileName(sidecarExe);
                        RhinoApp.WriteLine($"[TuoJie] {exeName} exited immediately (code {exitCode}): {stderr}");
                        LogSidecar($"{exeName} exited immediately (code {exitCode}): {stderr}");
                        lastError = new Exception($"Sidecar exited with code {exitCode}: {stderr}");
                        proc.Dispose();
                        continue;
                    }

                    s_process = proc;
                    s_activeSidecarPath = sidecarExe;
                    s_net48FallbackActive = sidecarExe.Contains("net48");
                    if (s_net48FallbackActive)
                        RhinoApp.WriteLine("[TuoJie] Using net48 Sidecar (fallback mode)");
                    LogSidecar($"Sidecar active: {s_activeSidecarPath}, pid={proc.Id}, fallback={s_net48FallbackActive}");
                    return;
                }
                catch (Exception ex)
                {
                    // Start() 抛异常时进程句柄同样要释放，否则等 GC 终结器才回收
                    proc.Dispose();
                    LogSidecar($"Failed to start {sidecarExe}: {ex}");
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                $"Failed to start Sidecar process. Last error: {lastError?.Message ?? "Sidecar not found"}");
        }

        private static void LogSidecar(string message)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIRenderer", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, $"sidecar_client_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Build sidecar request
            var sidecarReq = new SidecarRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = request.Method.ToString(),
                Url = request.RequestUri.ToString()
            };

            // Collect request headers
            var headers = new Dictionary<string, string[]>();
            foreach (var h in request.Headers)
                headers[h.Key] = h.Value.ToArray();
            if (request.Content?.Headers != null)
            {
                foreach (var h in request.Content.Headers)
                    headers[h.Key] = h.Value.ToArray();
            }
            sidecarReq.Headers = headers;

            // Read body
            if (request.Content != null)
                sidecarReq.Body = await request.Content.ReadAsByteArrayAsync();

            // Send through pipe
            var sidecarRes = await SendToSidecarAsync(sidecarReq, cancellationToken);

            if (sidecarRes == null || (!sidecarRes.Success && sidecarRes.StatusCode == 0))
            {
                var err = sidecarRes?.Error ?? "Sidecar communication failed";
                throw new HttpRequestException(err);
            }

            // Build response
            var response = new HttpResponseMessage((HttpStatusCode)sidecarRes.StatusCode);
            if (sidecarRes.Body != null && sidecarRes.Body.Length > 0)
            {
                response.Content = new ByteArrayContent(sidecarRes.Body);
                if (sidecarRes.Headers != null &&
                    sidecarRes.Headers.TryGetValue("Content-Type", out var ct) &&
                    ct.Length > 0)
                {
                    response.Content.Headers.ContentType =
                        MediaTypeHeaderValue.Parse(string.Join(",", ct));
                }
            }

            // Copy response headers (skip content-type, transfer-encoding — those go on content)
            var skipHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Content-Type", "Transfer-Encoding", "Content-Length" };
            if (sidecarRes.Headers != null)
            {
                foreach (var h in sidecarRes.Headers)
                {
                    if (skipHeaders.Contains(h.Key)) continue;
                    response.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            return response;
        }

        private async Task<SidecarResponse> SendToSidecarAsync(
            SidecarRequest request, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(json);
            var lengthPrefix = BitConverter.GetBytes(bytes.Length);
            string lastError = null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", _pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous);

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                    await pipe.ConnectAsync(connectCts.Token);

                    // Write request
                    await pipe.WriteAsync(lengthPrefix, 0, 4, ct);
                    await pipe.WriteAsync(bytes, 0, bytes.Length, ct);
                    await pipe.FlushAsync(ct);

                    // Read response length
                    var lenBuf = new byte[4];
                    await ReadExactAsync(pipe, lenBuf, 4, ct);
                    var respLen = BitConverter.ToInt32(lenBuf, 0);

                    if (respLen <= 0 || respLen > 50 * 1024 * 1024)
                        throw new IOException($"Invalid response length: {respLen}");

                    // Read response body
                    var respBuf = new byte[respLen];
                    await ReadExactAsync(pipe, respBuf, respLen, ct);
                    var respJson = Encoding.UTF8.GetString(respBuf, 0, respLen);

                    return JsonConvert.DeserializeObject<SidecarResponse>(respJson);
                }
                catch (OperationCanceledException)
                {
                    // 这里无法区分「用户取消」和「HttpClient 超时」：传进来的 ct 是
                    // HttpClient 自己的 linked token（Timeout 也挂在上面），超时同样会
                    // 让 IsCancellationRequested 为真。所以文案必须两者都覆盖——
                    // 之前写死 "Request cancelled"，真超时会被人当成「我取消的」，
                    // 排查时误导。重启侧车只在内部连接超时（下面那个分支）做。
                    if (ct.IsCancellationRequested)
                        return new SidecarResponse { Id = request.Id, StatusCode = 0, Error = "请求已取消或超时" };

                    // 内部超时（10 秒没连上）：说明侧车卡住了。按失败处理并重启，
                    // 否则那个卡死的侧车一直占着连接，后续请求全部连不上。
                    lastError = "连接侧车超时";
                    if (attempt == 0)
                        RestartSidecar();
                }
                catch (Exception ex) when (attempt == 0)
                {
                    // 第一次失败：重启侧车并重试一次
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                    RestartSidecar();
                }
                catch (Exception ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            // 带上最后一次的真实异常：原来只报「unreachable」，真正的失败原因被丢掉了
            return new SidecarResponse
            {
                Id = request.Id,
                StatusCode = 0,
                Error = string.IsNullOrEmpty(lastError)
                    ? "Sidecar unreachable after retry"
                    : "Sidecar unreachable after retry: " + lastError
            };
        }

        /// <summary>重启侧车：只换进程，不改引用计数——调用方自己那份计数还在。</summary>
        private void RestartSidecar()
        {
            lock (s_lock)
            {
                KillSidecarLocked();
                try
                {
                    StartSidecarLocked();
                }
                catch (Exception ex)
                {
                    // 起不来就保持没有进程的状态，下一次请求会再试一次
                    LogSidecar($"Restart sidecar failed: {ex.Message}");
                }
            }
        }

        /// <summary>杀掉并释放侧车进程。调用方必须已持有 s_lock。</summary>
        private static void KillSidecarLocked()
        {
            try
            {
                if (s_process != null)
                {
                    if (!s_process.HasExited)
                        s_process.Kill();
                    // 原来 Kill 之后直接置 null，Process 句柄要等 GC 终结器才释放
                    s_process.Dispose();
                }
            }
            catch { }
            finally
            {
                s_process = null;
            }
        }

        private static async Task ReadExactAsync(PipeStream pipe, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await pipe.ReadAsync(buffer, offset, count - offset, ct);
                if (read == 0)
                    throw new EndOfStreamException("Pipe closed unexpectedly");
                offset += read;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (s_lock)
                {
                    // 与构造函数里的 +1 一一对应
                    s_refCount--;
                    if (s_refCount <= 0)
                        KillSidecarLocked();
                }
            }
            base.Dispose(disposing);
        }

        // ── Protocol types ──────────────────────────────────────────────

        class SidecarRequest
        {
            public string Id { get; set; }
            public string Method { get; set; }
            public string Url { get; set; }
            public Dictionary<string, string[]> Headers { get; set; }
            public byte[] Body { get; set; }
        }

        class SidecarResponse
        {
            public string Id { get; set; }
            public int StatusCode { get; set; }
            public Dictionary<string, string[]> Headers { get; set; }
            public byte[] Body { get; set; }
            public string Error { get; set; }

            [JsonIgnore]
            public bool Success => Error == null;
        }
    }
}
