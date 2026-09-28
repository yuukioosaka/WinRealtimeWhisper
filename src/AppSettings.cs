using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WinRealtimeWhisper
{
    internal enum AudioSourceKind
    {
        SystemLoopback,
        Microphone,
        Both
    }

    internal sealed class AudioDeviceItem
    {
        public AudioDeviceItem(string id, string name, AudioSourceKind kind)
        {
            Id = id;
            Name = name;
            Kind = kind;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public AudioSourceKind Kind { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>%LOCALAPPDATA%\WinRealtimeWhisper\settings.json に保存する最小限の設定。</summary>
    internal sealed class AppSettings
    {
        /// <summary>{"ModelPath":"...ggml-small.bin","Language":"ja","DeviceId":"{0.0.0...}","SourceKind":"Both"}</summary>
        public string ModelPath { get; set; }
        public string Language { get; set; }

        /// <summary>ループバック録音に使う出力デバイスの ID。空なら既定。</summary>
        public string OutputDeviceId { get; set; }

        /// <summary>マイク録音に使う入力デバイスの ID。空なら既定。</summary>
        public string InputDeviceId { get; set; }

        public AudioSourceKind SourceKind { get; set; }

        /// <summary>Whisper に渡す言語コード（ja / en ...）。日本語は ja。</summary>
        public string WhisperLanguage { get; set; }

        /// <summary>
        /// 認識の遅延と精度のバランス（0=最速, 1=標準, 2=高精度）。
        /// 小さいほど短い区間で区切るため表示が速くなるが、精度は落ちる。
        /// </summary>
        public int LatencyProfile { get; set; }

        /// <summary>認識区間の最大長（秒）。長いほど文脈が増えるが、確定表示が遅くなる。</summary>
        public double MaxChunkSeconds { get; set; }

        /// <summary>無音がこの秒数続いたら区切る。</summary>
        public double SilenceSplitSeconds { get; set; }

        /// <summary>
        /// Vulkan（GPU）を優先するか。true でも Vulkan が使えない環境では CPU へ落ちる。
        /// false にすると常に CPU。GPU で逆に遅くなる環境向けの逃げ道。
        /// </summary>
        public bool PreferGpu { get; set; }

        /// <summary>
        /// UI の表示言語（ja / en）。空なら Windows の表示言語に従う。
        /// 認識する言語（WhisperLanguage）とは別物。
        /// </summary>
        public string UiLanguage { get; set; }

        /// <summary>
        /// テキスト（履歴）の保存先。空なら既定の
        /// ドキュメント\WinRealtimeWhisper\history。
        /// </summary>
        public string HistoryDirectory { get; set; }

        /// <summary>
        /// WAV の保存先。空なら既定のドキュメント\WinRealtimeWhisper\wav。
        /// </summary>
        public string WavDirectory { get; set; }

        /// <summary>
        /// モデルの保存先。空なら既定の %LOCALAPPDATA%\WinRealtimeWhisper\models。
        /// </summary>
        public string ModelDirectory { get; set; }

        /// <summary>
        /// ログの保存先。空なら既定の %LOCALAPPDATA%\WinRealtimeWhisper\logs。
        /// </summary>
        public string LogDirectory { get; set; }

        /// <summary>ウィンドウを常に手前に出すか。</summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>
        /// 録音中に WebVTT（ライブ文字起こし）を書き出すか。
        /// 外部のチェックリスト/感情分析アプリがこのファイルを tail する。
        /// </summary>
        public bool VttEnabled { get; set; }

        /// <summary>
        /// ライブ文字起こし(VTT)の保存先。空なら既定の
        /// %LOCALAPPDATA%\WinRealtimeWhisper\transcripts。
        /// </summary>
        public string VttDirectory { get; set; }

        /// <summary>VTT に書き出す heartbeat の間隔（秒）。</summary>
        public double VttHeartbeatSeconds { get; set; }

        /// <summary>前回終了時のウィンドウ左上 X。-1 なら未保存。</summary>
        public int WindowX { get; set; }

        /// <summary>前回終了時のウィンドウ左上 Y。</summary>
        public int WindowY { get; set; }

        /// <summary>前回終了時のウィンドウ幅。</summary>
        public int WindowWidth { get; set; }

        /// <summary>前回終了時のウィンドウ高さ。</summary>
        public int WindowHeight { get; set; }

        /// <summary>前回終了時に最大化していたか。</summary>
        public bool WindowMaximized { get; set; }

        /// <summary>画面外に出ないことを確認した保存済みの位置と大きさ。無ければ null。</summary>
        public Rectangle? ResolveWindowBounds()
        {
            if (WindowWidth < 200 || WindowHeight < 120)
            {
                return null;
            }

            var bounds = Rectangle.FromLTRB(WindowX, WindowY, WindowX + WindowWidth, WindowY + WindowHeight);

            // モニタを外した後でも見える位置に戻す（作業領域と少しでも重なっていれば可）
            bool visible = false;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds))
                {
                    visible = true;
                    break;
                }
            }

            return visible ? bounds : (Rectangle?)null;
        }

        public AppSettings()
        {
            ModelPath = string.Empty;
            Language = "ja-JP";
            WhisperLanguage = "ja";
            OutputDeviceId = string.Empty;
            InputDeviceId = string.Empty;
            SourceKind = AudioSourceKind.Both;
            LatencyProfile = 1;
            MaxChunkSeconds = 30.0;
            SilenceSplitSeconds = 0.0;
            PreferGpu = true;
            UiLanguage = string.Empty;
            HistoryDirectory = string.Empty;
            WavDirectory = string.Empty;
            ModelDirectory = string.Empty;
            LogDirectory = string.Empty;
            AlwaysOnTop = false;
            VttEnabled = true;
            VttDirectory = string.Empty;
            VttHeartbeatSeconds = 5.0;
            WindowX = -1;
            WindowY = -1;
            WindowWidth = 0;
            WindowHeight = 0;
            WindowMaximized = false;
        }

        /// <summary>テキスト（履歴）の保存先。未設定なら既定値。</summary>
        public string ResolveHistoryDirectory()
        {
            return string.IsNullOrWhiteSpace(HistoryDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WinRealtimeWhisper",
                    "history")
                : HistoryDirectory;
        }

        /// <summary>WAV の保存先。未設定なら既定値。</summary>
        public string ResolveWavDirectory()
        {
            return string.IsNullOrWhiteSpace(WavDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WinRealtimeWhisper",
                    "wav")
                : WavDirectory;
        }

        /// <summary>モデルの保存先。未設定なら既定値。</summary>
        public string ResolveModelDirectory()
        {
            return string.IsNullOrWhiteSpace(ModelDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinRealtimeWhisper",
                    "models")
                : ModelDirectory;
        }

        /// <summary>ログの保存先。未設定なら既定値。</summary>
        public string ResolveLogDirectory()
        {
            return string.IsNullOrWhiteSpace(LogDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinRealtimeWhisper",
                    "logs")
                : LogDirectory;
        }

        /// <summary>ライブ文字起こし(VTT)の保存先。未設定なら既定値。</summary>
        public string ResolveVttDirectory()
        {
            return string.IsNullOrWhiteSpace(VttDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinRealtimeWhisper",
                    "transcripts")
                : VttDirectory;
        }

        /// <summary>heartbeat の間隔。範囲外なら既定値。</summary>
        public TimeSpan ResolveVttHeartbeat()
        {
            double seconds = VttHeartbeatSeconds;
            if (seconds < 1 || seconds > 60)
            {
                seconds = 5.0;
            }

            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>設定と Windows の表示言語から、使う表示言語を決める。</summary>
        public UiLanguage ResolveUiLanguage()
        {
            UiLanguage parsed;
            if (Loc.TryParse(UiLanguage, out parsed))
            {
                return parsed;
            }

            return Loc.DetectFromSystem();
        }

        /// <summary>実際に使うモデルの置き場。CLI の上書き値があればそれを返す。%LOCALAPPDATA%\WinRealtimeWhisper\models\</summary>
        public static string ModelDirectoryEffective
        {
            get
            {
                return ModelDirectoryOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinRealtimeWhisper",
                    "models");
            }
        }

        /// <summary>--model-dir と設定の「モデルの保存先」で差し替えるための上書き値。</summary>
        public static string ModelDirectoryOverride { get; set; }

        /// <summary>
        /// 設定ファイルのパス。環境変数 WINREALTIMEWHISPER_SETTINGS で差し替えられる
        /// （テストが実設定を壊さないようにするため）。
        /// </summary>
        public static string FilePath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("WINREALTIMEWHISPER_SETTINGS");
                if (!string.IsNullOrEmpty(custom))
                {
                    return custom;
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinRealtimeWhisper",
                    "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var values = ParseJson(json);
                    var s = new AppSettings();

                    string v;
                    if (values.TryGetValue("ModelPath", out v)) s.ModelPath = v;
                    if (values.TryGetValue("Language", out v) && v.Length > 0) s.Language = v;
                    if (values.TryGetValue("WhisperLanguage", out v) && v.Length > 0) s.WhisperLanguage = v;
                    if (values.TryGetValue("OutputDeviceId", out v)) s.OutputDeviceId = v;
                    if (values.TryGetValue("InputDeviceId", out v)) s.InputDeviceId = v;
                    if (values.TryGetValue("UiLanguage", out v)) s.UiLanguage = v;
                    if (values.TryGetValue("HistoryDirectory", out v)) s.HistoryDirectory = v;
                    if (values.TryGetValue("WavDirectory", out v)) s.WavDirectory = v;
                    if (values.TryGetValue("ModelDirectory", out v)) s.ModelDirectory = v;
                    if (values.TryGetValue("LogDirectory", out v)) s.LogDirectory = v;
                    if (values.TryGetValue("VttDirectory", out v)) s.VttDirectory = v;

                    if (values.TryGetValue("VttEnabled", out v))
                    {
                        bool vtt;
                        if (bool.TryParse(v, out vtt))
                        {
                            s.VttEnabled = vtt;
                        }
                    }

                    if (values.TryGetValue("VttHeartbeatSeconds", out v))
                    {
                        double hb;
                        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out hb)
                            && hb >= 1 && hb <= 60)
                        {
                            s.VttHeartbeatSeconds = hb;
                        }
                    }

                    if (values.TryGetValue("AlwaysOnTop", out v))
                    {
                        bool onTop;
                        if (bool.TryParse(v, out onTop))
                        {
                            s.AlwaysOnTop = onTop;
                        }
                    }

                    ReadInt(values, "WindowX", x => s.WindowX = x);
                    ReadInt(values, "WindowY", y => s.WindowY = y);
                    ReadInt(values, "WindowWidth", w => s.WindowWidth = w);
                    ReadInt(values, "WindowHeight", h => s.WindowHeight = h);

                    if (values.TryGetValue("WindowMaximized", out v))
                    {
                        bool maximized;
                        if (bool.TryParse(v, out maximized))
                        {
                            s.WindowMaximized = maximized;
                        }
                    }

                    // 旧形式の DeviceId は出力デバイスとして引き継ぐ
                    if (values.TryGetValue("DeviceId", out v) && v.Length > 0)
                    {
                        if (s.OutputDeviceId.Length == 0) s.OutputDeviceId = v;
                        if (s.InputDeviceId.Length == 0) s.InputDeviceId = v;
                    }

                    if (values.TryGetValue("LatencyProfile", out v))
                    {
                        int profile;
                        if (int.TryParse(v, out profile) && profile >= 0 && profile <= 2)
                        {
                            s.LatencyProfile = profile;
                        }
                    }

                    if (values.TryGetValue("MaxChunkSeconds", out v))
                    {
                        double max;
                        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out max) && max >= 2 && max <= 30)
                        {
                            s.MaxChunkSeconds = max;
                        }
                    }

                    if (values.TryGetValue("SilenceSplitSeconds", out v))
                    {
                        double silence;
                        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out silence)
                            && silence >= 0.2 && silence <= 3)
                        {
                            s.SilenceSplitSeconds = silence;
                        }
                    }

                    bool preferGpu;
                    if (values.TryGetValue("PreferGpu", out v) && bool.TryParse(v, out preferGpu))
                    {
                        s.PreferGpu = preferGpu;
                    }

                    AudioSourceKind kind;
                    if (values.TryGetValue("SourceKind", out v) && Enum.TryParse(v, true, out kind))
                    {
                        s.SourceKind = kind;
                    }

                    return s;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new AppSettings();
        }

        public void Save()
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            AppendValue(sb, "ModelPath", ModelPath, true);
            AppendValue(sb, "Language", Language, true);
            AppendValue(sb, "WhisperLanguage", WhisperLanguage, true);
            AppendValue(sb, "OutputDeviceId", OutputDeviceId, true);
            AppendValue(sb, "InputDeviceId", InputDeviceId, true);
            AppendValue(sb, "UiLanguage", UiLanguage, true);
            AppendValue(sb, "HistoryDirectory", HistoryDirectory, true);
            AppendValue(sb, "WavDirectory", WavDirectory, true);
            AppendValue(sb, "ModelDirectory", ModelDirectory, true);
            AppendValue(sb, "LogDirectory", LogDirectory, true);
            AppendValue(sb, "VttDirectory", VttDirectory, true);
            sb.AppendLine("  \"VttEnabled\": " + (VttEnabled ? "true" : "false") + ",");
            sb.AppendLine("  \"VttHeartbeatSeconds\": " + VttHeartbeatSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"AlwaysOnTop\": " + (AlwaysOnTop ? "true" : "false") + ",");
            sb.AppendLine("  \"WindowX\": " + WindowX + ",");
            sb.AppendLine("  \"WindowY\": " + WindowY + ",");
            sb.AppendLine("  \"WindowWidth\": " + WindowWidth + ",");
            sb.AppendLine("  \"WindowHeight\": " + WindowHeight + ",");
            sb.AppendLine("  \"WindowMaximized\": " + (WindowMaximized ? "true" : "false") + ",");
            sb.AppendLine("  \"LatencyProfile\": " + LatencyProfile + ",");
            sb.AppendLine("  \"MaxChunkSeconds\": " + MaxChunkSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"SilenceSplitSeconds\": " + SilenceSplitSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"PreferGpu\": " + (PreferGpu ? "true" : "false") + ",");
            AppendValue(sb, "SourceKind", SourceKind.ToString(), false);
            sb.AppendLine("}");

            File.WriteAllText(FilePath, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        private static void AppendValue(System.Text.StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
            sb.AppendLine(comma ? "," : string.Empty);
        }

        private static void ReadInt(Dictionary<string, string> values, string key, Action<int> setter)
        {
            string v;
            int parsed;
            if (values.TryGetValue(key, out v) && int.TryParse(v, out parsed))
            {
                setter(parsed);
            }
        }

        private static string Escape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        // 依存を増やさないための最小 JSON リーダー(フラットな key:value のみ)
        private static Dictionary<string, string> ParseJson(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;

            while (i < json.Length)
            {
                if (json[i] != '"')
                {
                    i++;
                    continue;
                }

                string key = ReadString(json, ref i);
                while (i < json.Length && json[i] != ':') i++;
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

                if (i < json.Length && json[i] == '"')
                {
                    string value = ReadString(json, ref i);
                    result[key] = value;
                }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
                    result[key] = json.Substring(start, i - start).Trim();
                }
            }

            return result;
        }

        private static string ReadString(string json, ref int i)
        {
            var sb = new System.Text.StringBuilder();
            i++; // 開きクォート

            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\' && i < json.Length)
                {
                    char e = json[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= json.Length)
                            {
                                int code = Convert.ToInt32(json.Substring(i, 4), 16);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
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
    }
}
