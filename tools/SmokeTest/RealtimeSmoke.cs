using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>
    /// OpenAI Realtime 互換 WebSocket サーバーを、実際に ClientWebSocket でつないで確認する。
    /// ハンドシェイク、イベント封筒、一方向性（クライアントからのメッセージを無視する）まで見る。
    /// 仕様: docs/realtime-websocket.md
    /// </summary>
    internal static class RealtimeSmoke
    {
        public static int Run()
        {
            using (var hub = new RealtimeWebSocketHub(0))
            {
                hub.Start();

                var started = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
                hub.SetSession("2026-01-02_0304", "ggml-small.bin", "ja", started);

                using (var client = new WsClient(hub.Port))
                {
                    client.Connect();

                    // 接続直後に session.created が来る
                    string json = client.ReadText();
                    string type = RealtimeEvents.ExtractType(json);
                    Check(type == "session.created", "first event must be session.created, got " + type);
                    Check(RealtimeEvents.Extract(json, "id") == "2026-01-02_0304",
                        "session id mismatch: " + json);
                    Check(RealtimeEvents.Extract(json, "language") == "ja",
                        "language mismatch: " + json);
                    Check(json.IndexOf("\"source\":\"WinRealtimeWhisper\"", StringComparison.Ordinal) >= 0,
                        "source marker missing: " + json);

                    // 確定テキストを配る
                    hub.Broadcast(RealtimeEvents.TranscriptionCompleted(
                        1, "item_1", "今日は天気が良いですね。", "Remote",
                        TimeSpan.FromMilliseconds(12500), TimeSpan.FromMilliseconds(1700)));

                    json = client.ReadText();
                    type = RealtimeEvents.ExtractType(json);
                    Check(type == "conversation.item.input_audio_transcription.completed",
                        "unexpected event type: " + type);
                    Check(RealtimeEvents.Extract(json, "transcript") == "今日は天気が良いですね。",
                        "text mismatch: " + json);
                    Check(RealtimeEvents.Extract(json, "speaker") == "Remote",
                        "speaker mismatch: " + json);
                    Check(RealtimeEvents.Extract(json, "audio_start_ms") == "12500",
                        "audio_start_ms mismatch: " + json);
                    Check(RealtimeEvents.Extract(json, "audio_end_ms") == "14200",
                        "audio_end_ms mismatch: " + json);
                    Check(RealtimeEvents.Extract(json, "sequence") == "1",
                        "sequence mismatch: " + json);

                    // 引用符と改行が JSON として壊れないこと
                    hub.Broadcast(RealtimeEvents.TranscriptionCompleted(
                        2, "item_2", "彼は\"了解\"と\n言った。", null, TimeSpan.Zero, TimeSpan.Zero));
                    json = client.ReadText();
                    Check(RealtimeEvents.Extract(json, "transcript") == "彼は\"了解\"と\n言った。",
                        "escaping round-trip failed: " + json);

                    // 発話の開始 / 終了
                    hub.Broadcast(RealtimeEvents.SpeechActivity(true, TimeSpan.FromMilliseconds(4210)));
                    json = client.ReadText();
                    Check(RealtimeEvents.ExtractType(json) == "input_audio_buffer.speech_started",
                        "speech_started missing: " + json);
                    Check(RealtimeEvents.Extract(json, "audio_start_ms") == "4210",
                        "speech offset mismatch: " + json);

                    hub.Broadcast(RealtimeEvents.SpeechActivity(false, TimeSpan.FromMilliseconds(5000)));
                    json = client.ReadText();
                    Check(RealtimeEvents.ExtractType(json) == "input_audio_buffer.speech_stopped",
                        "speech_stopped missing: " + json);

                    // クライアントから何か送っても、サーバーは壊れず、それだけでは何も返らない
                    client.SendText("{\"type\":\"session.update\"}");
                    hub.Broadcast(RealtimeEvents.Error("sample error", "server_error"));
                    json = client.ReadText();
                    Check(RealtimeEvents.ExtractType(json) == "error",
                        "error event missing: " + json);

                    // 全イベントに event_id がある
                    Check(RealtimeEvents.Extract(json, "event_id").StartsWith("evt_", StringComparison.Ordinal),
                        "event_id missing: " + json);

                    // 連続送信が順序どおり届く（送信の直列化の確認）
                    for (int i = 1; i <= 20; i++)
                    {
                        hub.Broadcast(RealtimeEvents.TranscriptionCompleted(
                            i, "item_" + i, "行 " + i, null, TimeSpan.Zero, TimeSpan.Zero));
                    }

                    for (int i = 1; i <= 20; i++)
                    {
                        json = client.ReadText();
                        Check(RealtimeEvents.Extract(json, "sequence") == i.ToString(),
                            "out of order at " + i + ": " + json);
                    }
                }

                // 切断後は配っても落ちない
                hub.Broadcast(RealtimeEvents.SpeechActivity(true, TimeSpan.Zero));

                // 複数クライアントに同じ内容が届く
                using (var a = new WsClient(hub.Port))
                using (var b = new WsClient(hub.Port))
                {
                    a.Connect();
                    b.Connect();
                    a.ReadText();
                    b.ReadText();

                    hub.Broadcast(RealtimeEvents.TranscriptionCompleted(
                        3, "item_3", "同時配信", null, TimeSpan.Zero, TimeSpan.Zero));

                    Check(RealtimeEvents.Extract(a.ReadText(), "transcript") == "同時配信",
                        "client A missed the broadcast");
                    Check(RealtimeEvents.Extract(b.ReadText(), "transcript") == "同時配信",
                        "client B missed the broadcast");
                }

                Console.WriteLine("  hub through .NET WebSocket: port=" + hub.Port
                    + " total=" + hub.TotalConnections);

                // WebSocket 以外のアクセスはハンドシェイクせずに案内を返す
                string httpBody;
                Check(TryHttpGet(hub.Port, out httpBody), "HTTP GET should get a response");
                Check(httpBody.IndexOf("400", StringComparison.Ordinal) >= 0
                    || httpBody.IndexOf("WebSocket", StringComparison.Ordinal) >= 0,
                    "HTTP response should reject a plain GET: " + httpBody);
            }

            Console.WriteLine("REALTIME OK");
            return 0;
        }

        private static bool TryHttpGet(int port, out string body)
        {
            body = null;

            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(5);
                    body = http.GetStringAsync("http://127.0.0.1:" + port + "/v1/realtime").Result;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 4xx が返るので HttpClient は例外にする。本文だけ取りたいので中身を見る。
                var http = FindHttpRequestException(ex);
                if (http != null)
                {
                    body = http.Message;
                    return true;
                }

                return false;
            }
        }

        private static System.Net.Http.HttpRequestException FindHttpRequestException(Exception ex)
        {
            while (ex != null)
            {
                var http = ex as System.Net.Http.HttpRequestException;
                if (http != null)
                {
                    return http;
                }

                ex = ex.InnerException;
            }

            return null;
        }

        /// <summary>ClientWebSocket で喋る最小のクライアント。</summary>
        private sealed class WsClient : IDisposable
        {
            private readonly int _port;
            private ClientWebSocket _socket;

            public WsClient(int port)
            {
                _port = port;
            }

            public void Connect()
            {
                _socket = new ClientWebSocket();
                var uri = new Uri("ws://127.0.0.1:" + _port + "/v1/realtime");
                _socket.ConnectAsync(uri, CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
                Check(_socket.State == WebSocketState.Open,
                    "client should be open, state=" + _socket.State);
            }

            /// <summary>サーバーからのテキストメッセージを 1 つ読む。</summary>
            public string ReadText()
            {
                var buffer = new byte[8192];
                var accumulated = new System.IO.MemoryStream();

                while (true)
                {
                    var task = _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), CancellationToken.None);
                    Check(task.Wait(TimeSpan.FromSeconds(10)), "receive timed out");

                    WebSocketReceiveResult result = task.Result;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("server closed the connection");
                    }

                    accumulated.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                return Encoding.UTF8.GetString(accumulated.ToArray());
            }

            public void SendText(string text)
            {
                byte[] payload = Encoding.UTF8.GetBytes(text);
                _socket.SendAsync(new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text, true, CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(5));
            }

            public void Dispose()
            {
                try
                {
                    if (_socket != null && _socket.State == WebSocketState.Open)
                    {
                        _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                            CancellationToken.None).Wait(TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception)
                {
                }

                if (_socket != null)
                {
                    _socket.Dispose();
                }
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
