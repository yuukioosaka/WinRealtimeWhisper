using System;
using System.Collections.Generic;
using System.IO;

namespace WinWhisper
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

    /// <summary>%LOCALAPPDATA%\WinWhisper\settings.json に保存する最小限の設定。</summary>
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
        /// UI の表示言語（ja / en）。空なら Windows の表示言語に従う。
        /// 認識する言語（WhisperLanguage）とは別物。
        /// </summary>
        public string UiLanguage { get; set; }

        public AppSettings()
        {
            ModelPath = string.Empty;
            Language = "ja-JP";
            WhisperLanguage = "ja";
            OutputDeviceId = string.Empty;
            InputDeviceId = string.Empty;
            SourceKind = AudioSourceKind.Both;
            LatencyProfile = 1;
            MaxChunkSeconds = 6.0;
            SilenceSplitSeconds = 0.45;
            UiLanguage = string.Empty;
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

        /// <summary>モデルの既定の置き場。%LOCALAPPDATA%\WinWhisper\models\</summary>
        public static string ModelDirectory
        {
            get
            {
                return ModelDirectoryOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinWhisper",
                    "models");
            }
        }

        /// <summary>--model-dir で差し替えるための上書き値。プロセス内でのみ有効。</summary>
        public static string ModelDirectoryOverride { get; set; }

        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinWhisper",
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
            sb.AppendLine("  \"LatencyProfile\": " + LatencyProfile + ",");
            sb.AppendLine("  \"MaxChunkSeconds\": " + MaxChunkSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"SilenceSplitSeconds\": " + SilenceSplitSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ",");
            AppendValue(sb, "SourceKind", SourceKind.ToString(), false);
            sb.AppendLine("}");

            File.WriteAllText(FilePath, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        private static void AppendValue(System.Text.StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
            sb.AppendLine(comma ? "," : string.Empty);
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
