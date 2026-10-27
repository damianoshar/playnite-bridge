namespace PlayniteBridge.Server
{
    using System;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Web.Script.Serialization;
    using Playnite.SDK;
    using PlayniteBridge.Handlers;

    internal class HttpApiServer
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private HttpListener _httpListener;
        private Thread _serverThread;
        private readonly int _port;
        private readonly AuthHandler _auth;
        private readonly Router _router;
        private readonly RequestGate _gate;

        public bool IsNetworkBound { get; private set; }

        public HttpApiServer(int port, AuthHandler auth, Router router, RequestGate gate = null)
        {
            _port = port;
            _auth = auth;
            _router = router;
            // The gate is owned by the caller (the plugin disposes it).
            // A default instance lives for the process lifetime.
            _gate = gate ?? new RequestGate();
        }

        public void Start()
        {
            string[] prefixes = new[]
            {
                $"http://+:{_port}/",
                $"http://*:{_port}/",
                $"http://localhost:{_port}/"
            };

            IsNetworkBound = false;

            foreach (var prefix in prefixes)
            {
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add(prefix);
                    _httpListener.Start();
                    _serverThread = new Thread(ServerLoop) { IsBackground = true };
                    _serverThread.Start();
                    IsNetworkBound = prefix.Contains("+") || prefix.Contains("*");
                    Logger.Info($"HTTP server listening on {prefix}");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to bind {prefix}: {ex.Message}");
                    try { _httpListener?.Close(); } catch { }
                }
            }

            Logger.Error("Failed to start HTTP server on any prefix");
        }

        public void Stop()
        {
            try { _httpListener?.Stop(); _httpListener?.Close(); } catch { }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        private void ServerLoop()
        {
            while (_httpListener?.IsListening == true)
            {
                try
                {
                    var ctx = _httpListener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Logger.Error(ex, "HTTP server error"); }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.ContentType = "application/json; charset=utf-8";
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Headers", "Authorization, Content-Type");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.OutputStream.Close();
                return;
            }

            try
            {
                // Auth check
                var authHeader = req.Headers["Authorization"];
                if (!_auth.ValidateAuth(authHeader))
                {
                    res.StatusCode = 401;
                    WriteJson(res, new { error = "Unauthorized. Header required: Authorization: Bearer <token>" });
                    return;
                }

                var requestContext = RequestContext.FromHttpRequest(req);
                // Bounded + de-duplicated: identical concurrent GETs share one
                // execution instead of stampeding the Playnite process.
                var result = _gate.Execute(requestContext, () => _router.Route(requestContext));

                if (result == null)
                {
                    res.StatusCode = 404;
                    result = new { error = "Not found", help = "GET /api for endpoint list" };
                }

                if (result is ImageResult img)
                {
                    WriteImage(req, res, img);
                }
                else
                {
                    WriteJson(res, result);
                }
            }
            catch (Exception ex) when (IsClientDisconnect(ex))
            {
                // The phone went away mid-request (scroll cancels, network
                // drops). Expected traffic noise — never an error, and never
                // allowed to become an unhandled ThreadPool exception.
                Logger.Debug($"Client disconnected during {req.HttpMethod} {req.RawUrl}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"HTTP request error: {req.RawUrl}");
                // Guarded: if the client is already gone, writing the 500 must
                // not throw out of this handler (that path previously escaped
                // to AppDomain-unhandled and took Playnite down with it).
                TryWriteError(res, ex);
            }
            finally
            {
                try { res.OutputStream.Close(); } catch { }
            }
        }

        private static void WriteImage(HttpListenerRequest req, HttpListenerResponse res, ImageResult img)
        {
            var inm = req.Headers["If-None-Match"];
            if (!string.IsNullOrEmpty(img.ETag) && ETagMatches(inm, img.ETag))
            {
                res.StatusCode = 304;
                return;
            }

            if (!string.IsNullOrEmpty(img.ETag))
                res.Headers.Add("ETag", img.ETag);
            res.ContentType = img.ContentType;
            res.ContentLength64 = img.Data.Length;
            res.Headers.Add("Cache-Control", "public, max-age=86400");
            res.OutputStream.Write(img.Data, 0, img.Data.Length);
        }

        private static bool ETagMatches(string ifNoneMatch, string etag)
        {
            if (string.IsNullOrEmpty(ifNoneMatch)) return false;
            var want = etag.Trim().Trim('"');
            foreach (var part in ifNoneMatch.Split(','))
            {
                var candidate = part.Trim();
                if (candidate == "*") return true;
                if (candidate.StartsWith("W/")) candidate = candidate.Substring(2).Trim();
                if (string.Equals(candidate.Trim('"'), want, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsClientDisconnect(Exception ex)
        {
            return ex is HttpListenerException
                || ex is ObjectDisposedException
                || ex is IOException;
        }

        private void TryWriteError(HttpListenerResponse res, Exception ex)
        {
            try
            {
                res.StatusCode = 500;
                WriteJson(res, new { error = ex.Message });
            }
            catch (Exception inner)
            {
                Logger.Debug("Failed to write error response: " + inner.Message);
            }
        }

        private void WriteJson(HttpListenerResponse res, object obj)
        {
            var json = _json.Serialize(obj);
            var buffer = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
        }
    }
}
