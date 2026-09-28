using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// OpenAI Realtime API 互換のイベントを JSON で組み立てる。
    ///
    /// 本家は音声の双方向だが、このアプリは「確定テキストを一方向に流す」だけなので、
    /// 対応するのは次のイベントに限る:
    ///
    ///   session.created
    ///   input_audio_buffer.speech_started
    ///   input_audio_buffer.speech_stopped
    ///   conversation.item.input_audio_transcription.completed
    ///   error
    ///
    /// すべて {"type": "...", "event_id": "evt_..."} の形を守る。
    /// 仕様: docs/realtime-websocket.md
    /// </summary>
    internal static class RealtimeEvents
    {
        public const string TypeSessionCreated = "session.created";
        public const string TypeSpeechStarted = "input_audio_buffer.speech_started";
        public const string TypeSpeechStopped = "input_audio_buffer.speech_stopped";
        public const string TypeTranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
        public const string TypeError = "error";

        private static int _counter;

        /// <summary>イベント ID。接続をまたいで一意ならよいので、通し番号で足りる。</summary>
        public static string NewEventId()
        {
            int n = System.Threading.Interlocked.Increment(ref _counter);
            return "evt_" + n.ToString(CultureInfo.InvariantCulture) + "_"
                + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 接続直後に送る。クライアントが「何につながったか」を判断できるようにする。
        /// session id はセッション（録音開始）ごとに変え、同じ録音中の再接続では同じ値になる。
        /// </summary>
        public static string SessionCreated(string sessionId, string model, string language, DateTime startedAt)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"").Append(TypeSessionCreated).Append('"');
            sb.Append(",\"event_id\":\"").Append(NewEventId()).Append('"');
            sb.Append(",\"session\":{");
            sb.Append("\"id\":").Append(Quote(sessionId));
            sb.Append(",\"object\":\"realtime.session\"");
            sb.Append(",\"model\":").Append(Quote(model));
            sb.Append(",\"input_audio_format\":\"pcm16\"");
            sb.Append(",\"input_audio_transcription\":{\"model\":\"whisper-1\",\"language\":")
                .Append(Quote(language)).Append('}');
            sb.Append(",\"turn_detection\":{\"type\":\"server_vad\"}");
            sb.Append(",\"source\":\"WinRealtimeWhisper\"");
            sb.Append(",\"started_at\":").Append(Quote(VttTranscriptWriter.Iso(startedAt)));
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>確定した 1 区間。item_id は VTT のキュー番号に対応する。</summary>
        public static string TranscriptionCompleted(
            long itemId, string itemIdText, string text, string speaker, TimeSpan offset, TimeSpan duration)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"").Append(TypeTranscriptionCompleted).Append('"');
            sb.Append(",\"event_id\":\"").Append(NewEventId()).Append('"');
            sb.Append(",\"item_id\":").Append(Quote(itemIdText));
            sb.Append(",\"content_index\":0");
            sb.Append(",\"transcript\":").Append(Quote(text));

            if (!string.IsNullOrEmpty(speaker))
            {
                sb.Append(",\"speaker\":").Append(Quote(speaker));
            }

            sb.Append(",\"audio_start_ms\":").Append(Millis(offset).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"audio_end_ms\":").Append(Millis(offset + duration).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sequence\":").Append(itemId.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>発話の開始・終了（VAD）。audio_start_ms はセッション相対。</summary>
        public static string SpeechActivity(bool speaking, TimeSpan offset)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"").Append(speaking ? TypeSpeechStarted : TypeSpeechStopped).Append('"');
            sb.Append(",\"event_id\":\"").Append(NewEventId()).Append('"');
            sb.Append(",\"item_id\":null");
            sb.Append(",\"audio_start_ms\":").Append(Millis(offset).ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>サーバー側のエラー。接続は維持する。</summary>
        public static string Error(string message, string code)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"").Append(TypeError).Append('"');
            sb.Append(",\"event_id\":\"").Append(NewEventId()).Append('"');
            sb.Append(",\"error\":{");
            sb.Append("\"type\":\"server_error\"");
            sb.Append(",\"code\":").Append(Quote(code));
            sb.Append(",\"message\":").Append(Quote(message));
            sb.Append("}}");
            return sb.ToString();
        }

        private static long Millis(TimeSpan value)
        {
            return value <= TimeSpan.Zero ? 0 : (long)value.TotalMilliseconds;
        }

        /// <summary>JSON 文字列を組み立てる。依存を増やさないため自前でエスケープする。</summary>
        internal static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>JSON オブジェクトの値を 1 つ取り出す（テストとログ用の最小実装）。</summary>
        internal static string Extract(string json, string key)
        {
            string needle = "\"" + key + "\":";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            int i = at + needle.Length;
            if (i >= json.Length)
            {
                return null;
            }

            if (json[i] == '"')
            {
                var sb = new StringBuilder();
                for (int j = i + 1; j < json.Length; j++)
                {
                    char c = json[j];
                    if (c == '\\' && j + 1 < json.Length)
                    {
                        j++;
                        char esc = json[j];
                        switch (esc)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (j + 4 < json.Length)
                                {
                                    int code;
                                    if (int.TryParse(json.Substring(j + 1, 4),
                                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    {
                                        sb.Append((char)code);
                                        j += 4;
                                    }
                                }

                                break;
                            default: sb.Append(esc); break;
                        }
                    }
                    else if (c == '"')
                    {
                        break;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                return sb.ToString();
            }

            int end = json.IndexOfAny(new[] { ',', '}' }, i);
            return end < 0 ? json.Substring(i) : json.Substring(i, end - i);
        }

        /// <summary>イベントから type だけ取り出す。</summary>
        internal static string ExtractType(string json)
        {
            return Extract(json, "type");
        }

        /// <summary>テスト用に、任意のイベント列をまとめて作る。</summary>
        internal static IEnumerable<string> Describe(IEnumerable<string> events)
        {
            foreach (string e in events)
            {
                yield return ExtractType(e);
            }
        }
    }
}
