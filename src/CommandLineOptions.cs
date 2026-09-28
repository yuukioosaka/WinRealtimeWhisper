using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// コマンドライン引数の解釈。GUI と共用するため、指定された項目だけを
    /// AppSettings の複製に上書きし、残りは settings.json の値を使う。
    /// </summary>
    internal sealed class CommandLineOptions
    {
        public bool HelpRequested { get; private set; }
        public bool VersionRequested { get; private set; }
        public bool ListDevicesRequested { get; private set; }
        public bool Headless { get; private set; }

        /// <summary>秒指定の録音。0 なら Ctrl+C かウィンドウを閉じるまで続ける。</summary>
        public double Seconds { get; private set; }

        /// <summary>音声をファイルから読む（録音デバイスを開かない）。</summary>
        public string InputFile { get; private set; }

        /// <summary>WAV の保存先。空なら <see cref="WavDirectory"/> に自動命名する。</summary>
        public string WavPath { get; private set; }

        /// <summary>文字起こしテキストの保存先。空なら <see cref="HistoryStore.RootDirectory"/> に自動命名する。</summary>
        public string TextPath { get; private set; }

        /// <summary>音源の種類。指定が無ければ null（settings.json の値を使う）。</summary>
        public AudioSourceKind? Source { get; private set; }

        /// <summary>モデルのファイル名または絶対パス。</summary>
        public string Model { get; private set; }

        public string ModelDirectory { get; private set; }

        public string OutputDevice { get; private set; }

        public string InputDevice { get; private set; }

        public string Language { get; private set; }

        /// <summary>UI の表示言語。指定が無ければ null（設定または Windows の言語に従う）。</summary>
        public UiLanguage? UiLanguage { get; private set; }

        /// <summary>認識区間の最大長（秒）。</summary>
        public double MaxChunkSeconds { get; private set; }

        /// <summary>無音がこの秒数続いたら区切る。</summary>
        public double SilenceSplitSeconds { get; private set; }

        /// <summary>ライブ文字起こし(VTT)の保存先。未指定なら null（設定に従う）。</summary>
        public string VttDirectory { get; private set; }

        /// <summary>ライブ文字起こし(VTT)を書かない。</summary>
        public bool VttDisabled { get; private set; }

        /// <summary>OpenAI Realtime 互換の WebSocket サーバーを立てる。</summary>
        public bool RealtimeServer { get; private set; }

        /// <summary>WebSocket サーバーのポート。0 なら未指定（設定に従う）。</summary>
        public int RealtimePort { get; private set; }

        /// <summary>Web ページ（ブラウザ）からの接続を許可する。null なら未指定。</summary>
        public bool? RealtimeBrowserOrigins { get; private set; }

        /// <summary>何も指定されなければ GUI を起動する。</summary>
        public bool RunsHeadless
        {
            get { return Headless || Seconds > 0 || !string.IsNullOrEmpty(InputFile); }
        }

        public string WavDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WinRealtimeWhisper",
                    "wav");
            }
        }

        public static CommandLineOptions Parse(string[] args)
        {
            var o = new CommandLineOptions();
            if (args == null || args.Length == 0)
            {
                return o;
            }

            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                if (arg[0] != '-' && arg[0] != '/')
                {
                    positional.Add(arg);
                    continue;
                }

                string name = arg.TrimStart('-', '/');
                string inline = null;

                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }

                switch (name.ToLowerInvariant())
                {
                    case "h":
                    case "help":
                    case "?":
                        o.HelpRequested = true;
                        break;

                    case "version":
                        o.VersionRequested = true;
                        break;

                    case "list-devices":
                        o.ListDevicesRequested = true;
                        o.Headless = true;
                        break;

                    case "headless":
                    case "n":
                    case "no-gui":
                        o.Headless = true;
                        break;

                    case "seconds":
                    case "t":
                    case "duration":
                        o.Seconds = ParseSeconds(Value(args, ref i, inline, name));
                        break;

                    case "input":
                    case "i":
                        o.InputFile = Value(args, ref i, inline, name);
                        break;

                    case "output":
                    case "o":
                        o.WavPath = Value(args, ref i, inline, name);
                        break;

                    case "text":
                        o.TextPath = Value(args, ref i, inline, name);
                        break;

                    case "source":
                    case "s":
                        o.Source = ParseSource(Value(args, ref i, inline, name));
                        break;

                    case "model":
                    case "m":
                        o.Model = Value(args, ref i, inline, name);
                        break;

                    case "model-dir":
                        o.ModelDirectory = Value(args, ref i, inline, name);
                        break;

                    case "output-device":
                        o.OutputDevice = Value(args, ref i, inline, name);
                        break;

                    case "input-device":
                        o.InputDevice = Value(args, ref i, inline, name);
                        break;

                    case "language":
                    case "lang":
                        o.Language = Value(args, ref i, inline, name);
                        break;

                    case "ui-language":
                    case "ui-lang":
                        o.UiLanguage = ParseUiLanguage(Value(args, ref i, inline, name));
                        break;

                    case "max-chunk":
                        o.MaxChunkSeconds = ParseSeconds(Value(args, ref i, inline, name));
                        break;

                    case "silence":
                        o.SilenceSplitSeconds = ParseSeconds(Value(args, ref i, inline, name));
                        break;

                    case "vtt-dir":
                        o.VttDirectory = Value(args, ref i, inline, name);
                        break;

                    case "no-vtt":
                        o.VttDisabled = true;
                        break;

                    case "ws":
                    case "realtime":
                    case "websocket":
                        o.RealtimeServer = true;
                        break;

                    case "ws-port":
                    case "realtime-port":
                    case "websocket-port":
                        o.RealtimeServer = true;
                        o.RealtimePort = ParsePort(Value(args, ref i, inline, name));
                        break;

                    case "ws-cors":
                    case "realtime-cors":
                    case "websocket-cors":
                        o.RealtimeServer = true;
                        o.RealtimeBrowserOrigins = ParseToggle(args, ref i, inline, name);
                        break;

                    default:
                        throw new ArgumentException(Loc.T("cli.unknownOption", arg));
                }
            }

            // 位置引数: 入力ファイル、または秒数
            foreach (string p in positional)
            {
                double seconds;
                if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                    && seconds > 0)
                {
                    o.Seconds = seconds;
                }
                else if (string.IsNullOrEmpty(o.InputFile) && File.Exists(p))
                {
                    o.InputFile = p;
                }
                else
                {
                    throw new ArgumentException(Loc.T("cli.unparsedArgument", p));
                }
            }

            o.Validate();
            return o;
        }

        private void Validate()
        {
            if (InputFile != null && !File.Exists(InputFile))
            {
                throw new ArgumentException(Loc.T("cli.inputNotFound", InputFile));
            }

            if (InputFile != null && Source.HasValue && Source.Value != AudioSourceKind.SystemLoopback)
            {
                throw new ArgumentException(Loc.T("cli.inputAndSource"));
            }

            if (Seconds < 0)
            {
                throw new ArgumentException(Loc.T("cli.secondsNegative"));
            }

            if (MaxChunkSeconds > 0 && (MaxChunkSeconds < 2 || MaxChunkSeconds > 30))
            {
                throw new ArgumentException(Loc.T("cli.maxChunkRange"));
            }

            if (SilenceSplitSeconds > 0 && (SilenceSplitSeconds < 0.2 || SilenceSplitSeconds > 3))
            {
                throw new ArgumentException(Loc.T("cli.silenceRange"));
            }

            if (RealtimePort < 0 || RealtimePort > 65535)
            {
                throw new ArgumentException(Loc.T("cli.portRange"));
            }
        }

        /// <summary>settings.json を読んだ内容に、コマンドラインの指定を重ねる。</summary>
        public AppSettings BuildSettings()
        {
            var settings = AppSettings.Load();

            if (!string.IsNullOrEmpty(Model))
            {
                settings.ModelPath = Model;
            }

            if (!string.IsNullOrEmpty(OutputDevice))
            {
                settings.OutputDeviceId = OutputDevice;
            }

            if (!string.IsNullOrEmpty(InputDevice))
            {
                settings.InputDeviceId = InputDevice;
            }

            if (!string.IsNullOrEmpty(Language))
            {
                settings.WhisperLanguage = Language;
            }

            if (UiLanguage.HasValue)
            {
                settings.UiLanguage = Loc.CodeFor(UiLanguage.Value);
            }

            if (MaxChunkSeconds > 0)
            {
                settings.MaxChunkSeconds = MaxChunkSeconds;
            }

            if (SilenceSplitSeconds > 0)
            {
                settings.SilenceSplitSeconds = SilenceSplitSeconds;
            }

            if (VttDisabled)
            {
                settings.VttEnabled = false;
            }

            if (!string.IsNullOrEmpty(VttDirectory))
            {
                settings.VttDirectory = Path.GetFullPath(VttDirectory);
            }

            if (RealtimeServer)
            {
                settings.RealtimeServerEnabled = true;
            }

            if (RealtimePort > 0)
            {
                settings.RealtimeServerPort = RealtimePort;
            }

            if (RealtimeBrowserOrigins.HasValue)
            {
                settings.RealtimeAllowBrowserOrigins = RealtimeBrowserOrigins.Value;
            }

            // ファイル入力にループバックは無いので、録音デバイスを開かない種類に寄せる
            if (InputFile != null)
            {
                settings.SourceKind = AudioSourceKind.SystemLoopback;
            }
            else if (Source.HasValue)
            {
                settings.SourceKind = Source.Value;
            }

            return settings;
        }

        /// <summary>モデルの絶対パスを返す。見つからなければ null。</summary>
        public string ResolveModelPath(AppSettings settings)
        {
            string path = WhisperModelStore.Resolve(settings.ModelPath);
            return WhisperModelStore.Exists(path) ? path : null;
        }

        public static void WriteHelp(TextWriter w)
        {
            if (Loc.Current == WinRealtimeWhisper.UiLanguage.English)
            {
                WriteHelpEnglish(w);
            }
            else
            {
                WriteHelpJapanese(w);
            }
        }

        private static void WriteHelpJapanese(TextWriter w)
        {
            w.WriteLine("WinRealtimeWhisper - リアルタイム文字起こし");
            w.WriteLine();
            w.WriteLine("使い方:");
            w.WriteLine("  WinRealtimeWhisper.exe                        GUI を起動します。");
            w.WriteLine("  WinRealtimeWhisper.exe [オプション]           GUI を起動し、設定を上書きします。");
            w.WriteLine("  WinRealtimeWhisper.exe -t 60                  60 秒だけ録音して終了します（GUI なし）。");
            w.WriteLine("  WinRealtimeWhisper.exe -t 0                   停止操作まで録音します（GUI なし）。");
            w.WriteLine("  WinRealtimeWhisper.exe -i speech.wav          音声ファイルを文字起こしします。");
            w.WriteLine();
            w.WriteLine("録音と入力:");
            w.WriteLine("  -t, --seconds <秒>      録音する長さ。0 で停止操作まで続けます。");
            w.WriteLine("  -i, --input <ファイル>  文字起こしする音声ファイル（WAV など）。");
            w.WriteLine("  -s, --source <種類>     音源: both | speakers | mic  (既定: 設定に従う)");
            w.WriteLine("      --output-device <id|名前>  ループバック録音する出力デバイス。");
            w.WriteLine("      --input-device <id|名前>   録音する入力デバイス（マイク）。");
            w.WriteLine();
            w.WriteLine("出力:");
            w.WriteLine("  -o, --output <ファイル>  WAV の保存先。");
            w.WriteLine("      --text <ファイル>    文字起こしテキストの保存先。");
            w.WriteLine("                          省略時は ドキュメント\\WinRealtimeWhisper\\ に自動命名で保存します。");
            w.WriteLine("      --vtt-dir <フォルダ> ライブ文字起こし(WebVTT)の保存先。");
            w.WriteLine("                          外部アプリがこのファイルを tail してリアルタイム解析できます。");
            w.WriteLine("      --no-vtt             ライブ文字起こし(WebVTT)を書かない。");
            w.WriteLine("      --ws                 OpenAI Realtime 互換の WebSocket サーバーを立てる。");
            w.WriteLine("      --ws-port <ポート>   待ち受けポート (既定 8765)。--ws も同時に有効になります。");
            w.WriteLine("      --ws-cors [on|off]   Web ページからの接続を許可する (既定 off)。");
            w.WriteLine();
            w.WriteLine("認識:");
            w.WriteLine("  -m, --model <名前|パス> ggml モデル。名前だけなら既定のフォルダから探します。");
            w.WriteLine("      --model-dir <フォルダ>  モデルの置き場を上書きします。");
            w.WriteLine("  -l, --language <コード> Whisper の言語。日本語は ja。");
            w.WriteLine("      --max-chunk <秒>     認識区間の最大長 (2〜30, 既定 30)。");
            w.WriteLine("      --silence <秒>       無音がこの長さ続いたら区切る (0.2〜3, 既定 0.2)。");
            w.WriteLine();
            w.WriteLine("その他:");
            w.WriteLine("      --list-devices       利用できる録音デバイスを一覧して終了します。");
            w.WriteLine("      --ui-language <ja|en>  UI の表示言語。");
            w.WriteLine("  -n, --headless           GUI を出さずに実行します。");
            w.WriteLine("  -h, --help               このヘルプを表示します。");
            w.WriteLine("      --version            バージョンを表示します。");
            w.WriteLine();
            w.WriteLine("終了コード: 0=成功 / 1=実行時エラー / 2=引数の誤り");
            w.WriteLine();
            w.WriteLine("例:");
            w.WriteLine("  WinRealtimeWhisper.exe -t 300 -s speakers -o C:\\tmp\\meeting.wav");
            w.WriteLine("  WinRealtimeWhisper.exe -i C:\\tmp\\interview.wav --text C:\\tmp\\interview.txt");
        }

        private static void WriteHelpEnglish(TextWriter w)
        {
            w.WriteLine("WinRealtimeWhisper - Real-time transcription");
            w.WriteLine();
            w.WriteLine("Usage:");
            w.WriteLine("  WinRealtimeWhisper.exe                       Launch the GUI.");
            w.WriteLine("  WinRealtimeWhisper.exe [options]             Launch the GUI with overrides.");
            w.WriteLine("  WinRealtimeWhisper.exe -t 60                 Record for 60 seconds, then exit (no GUI).");
            w.WriteLine("  WinRealtimeWhisper.exe -t 0                  Record until stopped (no GUI).");
            w.WriteLine("  WinRealtimeWhisper.exe -i speech.wav         Transcribe an audio file.");
            w.WriteLine();
            w.WriteLine("Recording and input:");
            w.WriteLine("  -t, --seconds <sec>     Recording length. 0 keeps going until stopped.");
            w.WriteLine("  -i, --input <file>      Audio file to transcribe (WAV and similar).");
            w.WriteLine("  -s, --source <kind>     Source: both | speakers | mic  (default: from settings)");
            w.WriteLine("      --output-device <id|name>  Output device to capture via loopback.");
            w.WriteLine("      --input-device <id|name>   Input device (microphone) to capture.");
            w.WriteLine();
            w.WriteLine("Output:");
            w.WriteLine("  -o, --output <file>     Where to save the WAV.");
            w.WriteLine("      --text <file>       Where to save the transcript.");
            w.WriteLine("                          Defaults to Documents\\WinRealtimeWhisper\\ with an automatic name.");
            w.WriteLine("      --vtt-dir <folder>  Where to write the live WebVTT transcript.");
            w.WriteLine("                          External apps can tail this file for real-time analysis.");
            w.WriteLine("      --no-vtt            Do not write the live WebVTT transcript.");
            w.WriteLine("      --ws                 Run an OpenAI Realtime compatible WebSocket server.");
            w.WriteLine("      --ws-port <port>     Listening port (default 8765). Also enables --ws.");
            w.WriteLine("      --ws-cors [on|off]   Accept connections from web pages (default off).");
            w.WriteLine();
            w.WriteLine("Recognition:");
            w.WriteLine("  -m, --model <name|path> ggml model. A bare name is looked up in the default folder.");
            w.WriteLine("      --model-dir <folder>  Override the model folder.");
            w.WriteLine("  -l, --language <code>   Whisper language. Japanese is ja.");
            w.WriteLine("      --max-chunk <sec>   Longest recognition segment (2-30, default 30).");
            w.WriteLine("      --silence <sec>     Split after this much silence (0.2-3, default 0.2).");
            w.WriteLine();
            w.WriteLine("Other:");
            w.WriteLine("      --list-devices      List the available recording devices and exit.");
            w.WriteLine("      --ui-language <ja|en>  Language of the user interface.");
            w.WriteLine("  -n, --headless          Run without the GUI.");
            w.WriteLine("  -h, --help              Show this help.");
            w.WriteLine("      --version           Show the version.");
            w.WriteLine();
            w.WriteLine("Exit codes: 0=success / 1=runtime error / 2=bad arguments");
            w.WriteLine();
            w.WriteLine("Examples:");
            w.WriteLine("  WinRealtimeWhisper.exe -t 300 -s speakers -o C:\\tmp\\meeting.wav");
            w.WriteLine("  WinRealtimeWhisper.exe -i C:\\tmp\\interview.wav --text C:\\tmp\\interview.txt");
        }

        /// <summary>
        /// 引数が壊れていても --ui-language だけは拾いたいときのための寛容な解釈。
        /// 解釈できなければ null を返し、例外は投げない。
        /// </summary>
        public static CommandLineOptions ParseLenient(string[] args)
        {
            try
            {
                return Parse(args);
            }
            catch (ArgumentException)
            {
            }

            var o = new CommandLineOptions();
            if (args == null)
            {
                return o;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                string name = arg.TrimStart('-', '/').ToLowerInvariant();
                string inline = null;

                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }

                if (name != "ui-language" && name != "ui-lang")
                {
                    continue;
                }

                string text = inline;
                if (text == null && i + 1 < args.Length)
                {
                    text = args[i + 1];
                }

                UiLanguage language;
                if (Loc.TryParse(text, out language))
                {
                    o.UiLanguage = language;
                }
            }

            return o;
        }

        /// <summary>コマンドラインの言語指定を、他の出力より先に効かせる。
        /// GUI を出さない実行では、既定を英語にする（コンソールは英語の方が扱いやすいため）。</summary>
        public static void ApplyUiLanguage(CommandLineOptions options)
        {
            if (options != null && options.UiLanguage.HasValue)
            {
                Loc.Current = options.UiLanguage.Value;
                return;
            }

            Loc.Current = options != null && (options.RunsHeadless || options.HelpRequested || options.VersionRequested)
                ? WinRealtimeWhisper.UiLanguage.English
                : AppSettings.Load().ResolveUiLanguage();
        }

        private static string Value(string[] args, ref int i, string inline, string name)
        {
            if (inline != null)
            {
                return inline;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException(Loc.T("cli.valueRequired", name));
            }

            return args[++i];
        }

        private static int ParsePort(string text)
        {
            int port;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535)
            {
                throw new ArgumentException(Loc.T("cli.portRange"));
            }

            return port;
        }

        /// <summary>
        /// on/off 形式の値を読む。値を省略した場合は有効として扱う。
        /// </summary>
        private static bool ParseToggle(string[] args, ref int i, string inline, string name)
        {
            string text;
            if (inline != null)
            {
                text = inline;
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                text = Value(args, ref i, null, name);
            }
            else
            {
                text = "on";
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "yes":
                case "1":
                    return true;

                case "off":
                case "false":
                case "no":
                case "0":
                    return false;

                default:
                    throw new ArgumentException(Loc.T("cli.notAToggle", text));
            }
        }

        private static double ParseSeconds(string text)
        {
            double seconds;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                throw new ArgumentException(Loc.T("cli.notANumber", text));
            }

            return seconds;
        }

        private static AudioSourceKind ParseSource(string text)
        {
            switch ((text ?? string.Empty).ToLowerInvariant())
            {
                case "both":
                case "all":
                    return AudioSourceKind.Both;
                case "speakers":
                case "output":
                case "loopback":
                    return AudioSourceKind.SystemLoopback;
                case "mic":
                case "microphone":
                case "input":
                    return AudioSourceKind.Microphone;
                default:
                    throw new ArgumentException(Loc.T("cli.sourceChoice", text));
            }
        }

        private static UiLanguage ParseUiLanguage(string text)
        {
            UiLanguage language;
            if (Loc.TryParse(text, out language))
            {
                return language;
            }

            throw new ArgumentException(Loc.T("cli.languageChoice", text));
        }

        /// <summary>拡張子を確かめつつ、保存先の絶対パスを組み立てる。</summary>
        public string ResolveTextPath(DateTime startedAt)
        {
            if (!string.IsNullOrEmpty(TextPath))
            {
                return Path.GetFullPath(TextPath);
            }

            return HistoryStore.CreateSessionFilePath(startedAt);
        }

        public string ResolveWavPath(DateTime startedAt)
        {
            if (!string.IsNullOrEmpty(WavPath))
            {
                return Path.GetFullPath(WavPath);
            }

            string dir = WavDirectory;
            System.IO.Directory.CreateDirectory(dir);
            string name = "session_" + startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".wav";
            return Path.Combine(dir, name);
        }

        /// <summary>引数の誤りを、使い方の案内つきで標準エラーへ出す。</summary>
        public static int ReportBadArguments(string message, TextWriter error)
        {
            error.WriteLine(Loc.T("cli.errorPrefix", message));
            error.WriteLine();
            error.WriteLine(Loc.T("cli.helpHint"));
            return 2;
        }

        public static string Version
        {
            get
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string info = asm.GetName().Version.ToString();
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute[])
                    asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);

                return attr.Length > 0 ? attr[0].InformationalVersion : info;
            }
        }

        /// <summary>コンソール出力がリダイレクトされていても日本語を正しく出す。</summary>
        public static void PrepareConsole()
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // コンソールが無い場合など。出力自体は続行する。
            }
        }
    }
}
