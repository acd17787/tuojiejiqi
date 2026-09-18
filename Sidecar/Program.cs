using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TuoJieSidecar
{
    class Program
    {
        [DllImport("kernel32.dll")]
        private static extern ErrorModes SetErrorMode(ErrorModes uMode);

        [Flags]
        private enum ErrorModes : uint
        {
            SEM_FAILCRITICALERRORS = 0x0001,
            SEM_NOGPFAULTERRORBOX = 0x0002,
            SEM_NOOPENFILEERRORBOX = 0x8000
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        static async Task Main(string[] args)
        {
            SetErrorMode(
                ErrorModes.SEM_FAILCRITICALERRORS |
                ErrorModes.SEM_NOGPFAULTERRORBOX |
                ErrorModes.SEM_NOOPENFILEERRORBOX);

            try
            {
                if (args.Length > 0 &&
                    string.Equals(args[0], "--diag", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await RunAsync(args);
            }
            catch (Exception ex)
            {
                try
                {
                    var logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AIRenderer", "logs");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(
                        Path.Combine(logDir, $"sidecar_{DateTime.Now:yyyyMMdd}.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] Fatal: {ex}\r\n");
                }
                catch { }

                // 原来吞掉异常后正常返回，退出码是 0，客户端把它报成
                // 「exited immediately (code 0)」——崩溃被说成正常退出，排查时误导。
                Environment.ExitCode = 1;
            }
        }

        static async Task RunAsync(string[] args)
        {
            var pipeName = args.Length > 0 ? args[0] : "TuoJieSidecar";
            var parentPid = args.Length > 1 && int.TryParse(args[1], out var pid) ? pid : 0;

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.ConnectionClose = false;

            // 父进程一退出就取消。比原来「每 5 秒超时轮询一次」干净：不再反复创建/销毁
            // 内核管道对象，父进程死了也能立刻收摊。手工启动（没给 pid）时不取消，
            // 一直服务到被显式结束——原来 parentPid<=0 时 IsCompleted 恒为真，服务一次就退。
            using var shutdown = new CancellationTokenSource();
            if (parentPid > 0)
                _ = WatchParentAsync(parentPid, shutdown);

            while (!shutdown.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                try
                {
                    await server.WaitForConnectionAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    server.Dispose();
                    break;
                }
                catch (IOException)
                {
                    server.Dispose();
                    break;
                }

                // 连接交给独立任务处理，立刻回去 accept 下一条。
                // 原来 maxInstances=1 且串行处理：一次生图占住连接好几分钟，第二个请求
                // 连都连不上，客户端等 10 秒判定「侧车卡死」并 Kill 掉它——连正在服务的
                // 那条连接一起打断。两个窗口同时生成就会踩到。
                _ = HandleConnectionAsync(server, http);
            }
        }

        private static async Task WatchParentAsync(int parentPid, CancellationTokenSource shutdown)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                await Task.Run(() => parent.WaitForExit());
            }
            catch { }

            try { shutdown.Cancel(); } catch { }
        }

        private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, HttpClient http)
        {
            try
            {
                await ServeRequestsAsync(pipe, http);
            }
            catch (Exception ex)
            {
                LogError("Connection handling failed", ex);
            }
            finally
            {
                try
                {
                    if (pipe.IsConnected)
                        pipe.Disconnect();
                }
                catch { }

                pipe.Dispose();
            }
        }

        private static async Task ServeRequestsAsync(NamedPipeServerStream pipe, HttpClient http)
        {
            var lengthBuffer = new byte[4];
            while (pipe.IsConnected)
            {
                // Read message length
                if (!await ReadExactAsync(pipe, lengthBuffer, 4))
                    break;

                var msgLen = BitConverter.ToInt32(lengthBuffer, 0);
                if (msgLen <= 0 || msgLen > 50 * 1024 * 1024) // max 50 MB
                {
                    // 静默 break 会让客户端只看到 EndOfStreamException，分不清是
                    // 「请求过大」还是「JSON 非法」，还会触发它重启侧车。这里留下日志。
                    LogInfo($"Rejected request frame: length={msgLen} (limit 50MB)");
                    break;
                }

                var buffer = new byte[msgLen];
                if (!await ReadExactAsync(pipe, buffer, msgLen))
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, msgLen);
                var request = JsonConvert.DeserializeObject<SidecarRequest>(json, JsonSettings);
                if (request == null)
                {
                    LogInfo("Rejected request: JSON deserialized to null");
                    break;
                }

                var response = await ExecuteRequestAsync(http, request);

                var responseJson = JsonConvert.SerializeObject(response, JsonSettings);
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                var lengthPrefix = BitConverter.GetBytes(responseBytes.Length);

                // 写入必须带超时：客户端中途放弃读取（例如它自己超时了）时，
                // 这里会永久阻塞在 WriteAsync 上，这条连接就再也回不来。
                using (var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    await pipe.WriteAsync(lengthPrefix, 0, 4, writeCts.Token);
                    await pipe.WriteAsync(responseBytes, 0, responseBytes.Length, writeCts.Token);
                    await pipe.FlushAsync(writeCts.Token);
                }
            }
        }

        private static async Task<SidecarResponse> ExecuteRequestAsync(HttpClient http, SidecarRequest request, int attempt = 0)
        {
            var requestInfo = BuildRequestInfo(request, attempt);
            try
            {
                // 与客户端的 HttpClient 超时（10 分钟）对齐：谁先放弃都要一致，
                // 否则客户端先超时、侧车还在跑，就成了上面那条「写入阻塞」的触发条件。
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var requestUrl = GetRetryUrl(request.Url, attempt);
                var httpRequest = new HttpRequestMessage(
                    new HttpMethod(request.Method),
                    requestUrl);

                if (request.Headers != null)
                {
                    foreach (var kv in request.Headers)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            continue; // set on content instead

                        foreach (var v in kv.Value)
                            httpRequest.Headers.TryAddWithoutValidation(kv.Key, v);
                    }
                }

                if (request.Body != null && request.Body.Length > 0)
                {
                    var contentType = request.Headers?.ContainsKey("Content-Type") == true
                        ? string.Join(",", request.Headers["Content-Type"])
                        : "application/json";

                    httpRequest.Content = new ByteArrayContent(request.Body);
                    httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }

                LogInfo($"HTTP request start | {requestInfo} | ActualUrl={requestUrl}");
                var httpResponse = await http.SendAsync(httpRequest, cts.Token);
                var responseBody = httpResponse.Content != null
                    ? await httpResponse.Content.ReadAsByteArrayAsync()
                    : null;
                LogInfo($"HTTP response | {requestInfo} | Status={(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase} | ResponseBytes={responseBody?.Length ?? 0}");

                var responseHeaders = new Dictionary<string, string[]>();
                foreach (var header in httpResponse.Headers)
                    responseHeaders[header.Key] = (string[])header.Value;
                // Also include content headers
                if (httpResponse.Content != null)
                {
                    foreach (var header in httpResponse.Content.Headers)
                        responseHeaders[header.Key] = (string[])header.Value;
                }

                return new SidecarResponse
                {
                    Id = request.Id,
                    StatusCode = (int)httpResponse.StatusCode,
                    Headers = responseHeaders,
                    Body = responseBody
                };
            }
            catch (HttpRequestException ex) when (IsTransientTransportError(ex) && attempt < 2)
            {
                LogNetworkFailure($"Transient HTTP transport error, retry {attempt + 1} | {requestInfo}", ex);
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
                return await ExecuteRequestAsync(http, request, attempt + 1);
            }
            catch (TaskCanceledException)
            {
                LogInfo($"HTTP request timed out | {requestInfo}");
                return new SidecarResponse { Id = request.Id, StatusCode = 0, Error = "Request timed out (10 min)" };
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
            {
                return new SidecarResponse
                {
                    Id = request.Id,
                    StatusCode = 0,
                    Error = "Firewall is blocking TuoJieSidecar.exe from accessing the network. " +
                            "Run this command in an Administrator Command Prompt:\n" +
                            "netsh advfirewall firewall add rule name=\"TuoJieSidecar\" dir=out program=\"<path>\\TuoJieSidecar.exe\" action=allow"
                };
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                LogNetworkFailure($"HTTP request failed | {requestInfo}", ex);
                return new SidecarResponse { Id = request.Id, StatusCode = 0, Error = inner.Message };
            }
        }

        private static string GetRetryUrl(string url, int attempt)
        {
            if (attempt <= 0 || string.IsNullOrWhiteSpace(url))
                return url;

            try
            {
                var uri = new Uri(url);
                if (uri.Host.Equals("api.apiyi.com", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new UriBuilder(uri) { Host = "vip.apiyi.com" };
                    return builder.Uri.ToString();
                }
            }
            catch { }

            return url;
        }

        private static string BuildRequestInfo(SidecarRequest request, int attempt)
        {
            var contentType = request.Headers?.ContainsKey("Content-Type") == true
                ? string.Join(",", request.Headers["Content-Type"])
                : "";

            string host = "";
            string path = request.Url ?? "";
            try
            {
                var uri = new Uri(request.Url);
                host = uri.Host;
                path = uri.PathAndQuery;
            }
            catch { }

            return $"Attempt={attempt + 1} | Method={request.Method} | Host={host} | Path={path} | BodyBytes={request.Body?.Length ?? 0} | ContentType={contentType}";
        }

        private static bool IsTransientTransportError(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                var message = current.Message ?? "";
                if (message.IndexOf("unexpected EOF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("0 bytes from the transport stream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("SSL connection could not be established", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                current = current.InnerException;
            }

            return false;
        }

        private static void LogInfo(string message)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIRenderer", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, $"sidecar_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }

        private static void LogNetworkFailure(string message, Exception ex)
        {
            var details = DescribeExceptionChain(ex);
            LogError($"{message}\r\n{details}", ex);
        }

        private static string DescribeExceptionChain(Exception ex)
        {
            var sb = new StringBuilder();
            int depth = 0;
            var current = ex;
            while (current != null)
            {
                sb.AppendLine($"  Exception[{depth}]: {current.GetType().FullName}: {current.Message}");

                if (current is System.Net.Http.HttpRequestException httpEx)
                    sb.AppendLine($"    StatusCode: {GetHttpRequestStatusCode(httpEx)}");

                if (current is System.Net.Sockets.SocketException sockEx)
                {
                    sb.AppendLine($"    SocketErrorCode: {sockEx.SocketErrorCode}");
                    sb.AppendLine($"    NativeErrorCode: {sockEx.NativeErrorCode}");
                }

                if (current is System.ComponentModel.Win32Exception win32Ex)
                    sb.AppendLine($"    NativeErrorCode: {win32Ex.NativeErrorCode}");

                current = current.InnerException;
                depth++;
            }

            return sb.ToString();
        }

        private static string GetHttpRequestStatusCode(System.Net.Http.HttpRequestException ex)
        {
            try
            {
                var prop = ex.GetType().GetProperty("StatusCode");
                var value = prop?.GetValue(ex);
                return value?.ToString() ?? "(none)";
            }
            catch
            {
                return "(unavailable)";
            }
        }

        private static void LogError(string message, Exception ex)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIRenderer", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, $"sidecar_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}: {ex}\r\n");
            }
            catch { }
        }

        private static async Task<bool> ReadExactAsync(NamedPipeServerStream pipe, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await pipe.ReadAsync(buffer, offset, count - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
    }

    class SidecarRequest
    {
        public string Id { get; set; }
        public string Method { get; set; } = "POST";
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
    }
}
