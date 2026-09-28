using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// OpenAI Realtime 互換の WebSocket サーバー。127.0.0.1 だけで待ち受ける。
    ///
    /// フレーム処理は .NET Framework の <see cref="HttpListener"/> と
    /// <see cref="System.Net.WebSockets.WebSocket"/> に任せる（自作しない）。
    ///
    /// 方向は一方向で、サーバーからイベントを送るだけ。クライアントから届いた
    /// メッセージは読み捨てる（close だけは処理する）。
    /// 仕様: docs/realtime-websocket.md
    /// </summary>
    internal sealed class RealtimeWebSocketHub : IDisposable
    {
        private const string Prefix = "http://127.0.0.1:{0}/";

        /// <summary>
        /// ブラウザからの接続を許すか。
        ///
        /// WebSocket には CORS の仕組みが無く、Origin を見て可否を決めるのは
        /// サーバーの責任になる。待ち受けは 127.0.0.1 だけなので、許可しても
        /// 届くのは同じ機械のブラウザに限られる。
        /// </summary>
        private bool _allowBrowserOrigins;

        private readonly object _sync = new object();
        private readonly List<Client> _clients = new List<Client>();
        private readonly int _port;

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private volatile bool _disposed;
        private int _totalConnections;

        private string _sessionId = string.Empty;
        private string _model = string.Empty;
        private string _language = "ja";
        private DateTime _startedAt = DateTime.MinValue;

        public RealtimeWebSocketHub(int port)
        {
            _port = port;
        }

        /// <summary>実際に待ち受けているポート。0 を指定した場合は OS が決めた値になる。</summary>
        public int Port { get; private set; }

        /// <summary>
        /// ブラウザからの接続を許可する。既定は無効で、NuGet の設定から切り替える。
        /// 無効のとき Origin 付きの接続は 403 で断る。
        /// </summary>
        public bool AllowBrowserOrigins
        {
            get { return _allowBrowserOrigins; }
            set { _allowBrowserOrigins = value; }
        }

        /// <summary>いま接続しているクライアント数。</summary>
        public int ClientCount
        {
            get
            {
                lock (_sync)
                {
                    return _clients.Count;
                }
            }
        }

        /// <summary>これまでに接続した総数。</summary>
        public int TotalConnections
        {
            get { return Volatile.Read(ref _totalConnections); }
        }

        /// <summary>待ち受けを開始する。同じポートが使えなければ例外。</summary>
        public void Start()
        {
            if (_running)
            {
                return;
            }

            // 127.0.0.1 に固定する。外部から文字起こしを読めないようにするため。
            // HttpListener は接続があるまでポートを掴まないので、先に試し付けして
            // 衝突を早く検出する。
            string prefix = string.Format(Prefix, _port);
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                // 0 は OS に任せる指定だが HttpListener は受け付けない。
                // 空きポートを自分で探して付け直す。
                if (_port == 0)
                {
                    listener = StartOnFreePort();
                }
                else
                {
                    throw;
                }
            }

            _listener = listener;
            Port = ResolvePort(listener, _port);
            _running = true;

            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }

        /// <summary>空いているポートを探して待ち受ける。テストが固定ポートを占有しないようにするため。</summary>
        private static HttpListener StartOnFreePort()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int candidate = FindFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add(string.Format(Prefix, candidate));

                try
                {
                    listener.Start();
                    return listener;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }

            throw new InvalidOperationException("No free TCP port could be reserved for the WebSocket server.");
        }

        private static int FindFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>HttpListener が実際に開いたポートを求める。</summary>
        private static int ResolvePort(HttpListener listener, int requested)
        {
            if (requested != 0)
            {
                return requested;
            }

            try
            {
                // 実際の割当ポートはプロパティに出ないため、prefix から読む
                var any = new Uri(System.Linq.Enumerable.First(listener.Prefixes));
                return any.Port;
            }
            catch (Exception)
            {
                return requested;
            }
        }

        /// <summary>録音セッションの情報を設定する。次の session.created から反映される。</summary>
        public void SetSession(string sessionId, string model, string language, DateTime startedAt)
        {
            lock (_sync)
            {
                _sessionId = sessionId ?? string.Empty;
                _model = model ?? string.Empty;
                _language = string.IsNullOrEmpty(language) ? "ja" : language;
                _startedAt = startedAt;
            }
        }

        /// <summary>接続中のすべてのクライアントへ 1 イベント送る。</summary>
        public void Broadcast(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            Client[] targets;
            lock (_sync)
            {
                targets = _clients.ToArray();
            }

            foreach (Client client in targets)
            {
                client.Send(json);
            }
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    // Stop() で閉じたときもここに来る
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("[ws] accept failed", ex);
                    break;
                }

                // 1 接続の処理が他の接続を止めないよう、待たずに進める。
                var ignored = Task.Run(() => ServeAsync(context));
            }
        }

        /// <summary>1 接続分。ハンドシェイク後は切断されるまで受信を読み捨てる。</summary>
        private async Task ServeAsync(HttpListenerContext context)
        {
            // ブラウザからの接続は Origin の有無で見分ける。
            // WebSocket のハンドシェイクは CORS のプリフライトを通らないので、
            // 応答ヘッダではなくここで判断する必要がある。
            string origin = context.Request.Headers["Origin"];
            bool browserRequest = !string.IsNullOrEmpty(origin);

            if (browserRequest && !_allowBrowserOrigins)
            {
                DiagLog.Write("[ws] rejected browser origin: " + origin);
                await RejectOriginAsync(context, origin).ConfigureAwait(false);
                return;
            }

            // ブラウザからの要求には応答ヘッダを付ける。WebSocket 自体は
            // CORS の対象外だが、事前確認の HEAD/GET に答えるため。
            if (browserRequest)
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Vary"] = "Origin";
            }

            if (!context.Request.IsWebSocketRequest)
            {
                await RejectHttpAsync(context).ConfigureAwait(false);
                return;
            }

            WebSocket socket = null;
            Client client = null;

            try
            {
                HttpListenerWebSocketContext wsContext =
                    await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                socket = wsContext.WebSocket;

                client = new Client(socket);
                lock (_sync)
                {
                    _clients.Add(client);
                }

                if (browserRequest)
                {
                    DiagLog.Write("[ws] browser client accepted from " + origin);
                }

                Interlocked.Increment(ref _totalConnections);

                string sessionId;
                string model;
                string language;
                DateTime startedAt;
                lock (_sync)
                {
                    sessionId = _sessionId;
                    model = _model;
                    language = _language;
                    startedAt = _startedAt;
                }

                client.Send(RealtimeEvents.SessionCreated(sessionId, model, language, startedAt));
                DiagLog.Write("[ws] client connected (" + ClientCount + " active)");

                // クライアントからのメッセージは使わない。close だけ拾って読み捨てる。
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result =
                        await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                            .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("[ws] client failed", ex);
            }
            finally
            {
                if (client != null)
                {
                    lock (_sync)
                    {
                        _clients.Remove(client);
                    }
                }

                if (socket != null)
                {
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                    }

                    socket.Dispose();
                }

                DiagLog.Write("[ws] client disconnected (" + ClientCount + " active)");
            }
        }

        /// <summary>Origin 付きの接続を設定で断ったときの応答。</summary>
        private async Task RejectOriginAsync(HttpListenerContext context, string origin)
        {
            string body = "Browser access is disabled. Enable \"Allow browser access (CORS)\" "
                + "in the settings to accept connections from web pages.\r\n"
                + "Origin: " + origin + "\r\n";

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 403;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;

            // ハンドシェイクを続けないことを明示する。これが無いと
            // クライアントは切断として見て、理由（403）が届かないことがある。
            context.Response.Headers["Connection"] = "close";

            try
            {
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>WebSocket 以外のアクセスには短い案内を返す。</summary>
        private async Task RejectHttpAsync(HttpListenerContext context)
        {
            string path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
            bool known = path.Equals("/v1/realtime", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/", StringComparison.Ordinal);

            string body = known
                ? "This is a WebSocket endpoint. Connect with ws://127.0.0.1:" + Port + "/v1/realtime\r\n"
                : "Unknown path: " + path + "\r\nTry /v1/realtime\r\n";

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = known ? 400 : 404;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;

            try
            {
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;

            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (Exception)
                {
                }

                _cts = null;
            }

            Client[] targets;
            lock (_sync)
            {
                targets = _clients.ToArray();
                _clients.Clear();
            }

            foreach (Client client in targets)
            {
                client.Close();
            }

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch (Exception)
            {
            }

            _listener = null;
        }

        /// <summary>
        /// 1 クライアント分の送信。送信は録音スレッドからも来るため、
        /// 1 接続で同時に 1 つだけ送るよう直列化する。
        ///
        /// 送信の完了は待たない（録音を止めないため）が、次の送信は前の送信が
        /// 終わってから始める。キューに積むだけなので録音側は待たされない。
        /// </summary>
        private sealed class Client
        {
            private readonly WebSocket _socket;
            private readonly object _sync = new object();
            private Task _pending = Task.CompletedTask;
            private volatile bool _alive = true;

            public Client(WebSocket socket)
            {
                _socket = socket;
            }

            public void Send(string json)
            {
                if (!_alive || json == null)
                {
                    return;
                }

                byte[] payload = Encoding.UTF8.GetBytes(json);

                lock (_sync)
                {
                    if (!_alive)
                    {
                        return;
                    }

                    if (_socket.State != WebSocketState.Open)
                    {
                        _alive = false;
                        return;
                    }

                    // 前の送信の後ろにつなげる。録音スレッドはここで待たない。
                    _pending = _pending.ContinueWith(_ => SendCoreAsync(payload),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default).Unwrap();
                }
            }

            private async Task SendCoreAsync(byte[] payload)
            {
                try
                {
                    if (!_alive || _socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await _socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 送れなくなったクライアントは切る。録音は止めない。
                    _alive = false;
                }
            }

            public void Close()
            {
                _alive = false;

                try
                {
                    _socket.Abort();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
