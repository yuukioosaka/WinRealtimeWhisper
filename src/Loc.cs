using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinWhisper
{
    /// <summary>UI の表示言語。設定とコマンドラインから選ぶ。</summary>
    internal enum UiLanguage
    {
        Japanese,
        English
    }

    /// <summary>
    /// 表示文字列の切り替え。キーは英語の識別子で持ち、日本語と英語の
    /// 対応表をこのファイル 1 箇所にまとめる。
    ///
    /// 翻訳を足すときは <see cref="Japanese"/> と <see cref="English"/> の
    /// 同じキーを編集する。片方に無いキーは他方で代用し、それも無ければ
    /// キー名をそのまま返すので、実行時に空文字列が出ることはない。
    /// </summary>
    internal static class Loc
    {
        // 初期値は英語。コンソール出力（引数の誤りなど、言語を決める前に
        // 出るメッセージ）が英語になる。GUI は起動時に設定から決め直す。
        private static UiLanguage _current = UiLanguage.English;

        /// <summary>現在の表示言語。</summary>
        public static UiLanguage Current
        {
            get { return _current; }
            set { _current = value; }
        }

        /// <summary>翻訳キーから表示文字列を引く。</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            string value;
            var primary = _current == UiLanguage.Japanese ? Japanese : English;
            if (primary.TryGetValue(key, out value))
            {
                return value;
            }

            var fallback = _current == UiLanguage.Japanese ? English : Japanese;
            if (fallback.TryGetValue(key, out value))
            {
                return value;
            }

            return key;
        }

        /// <summary>書式付きで引く。書式指定が壊れていても元の文字列を返す。</summary>
        public static string T(string key, params object[] args)
        {
            string format = T(key);
            if (args == null || args.Length == 0)
            {
                return format;
            }

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>言語コード（ja / ja-JP / en / en-US ...）から表示言語を決める。</summary>
        public static bool TryParse(string code, out UiLanguage language)
        {
            language = UiLanguage.Japanese;

            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            string head = code.Trim().ToLowerInvariant();
            int dash = head.IndexOf('-');
            if (dash > 0)
            {
                head = head.Substring(0, dash);
            }

            switch (head)
            {
                case "ja":
                    language = UiLanguage.Japanese;
                    return true;
                case "en":
                    language = UiLanguage.English;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Windows の表示言語に合わせる。日本語版 Windows 以外は英語にする。
        /// 設定で明示されていればそちらが優先される。
        /// </summary>
        public static UiLanguage DetectFromSystem()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                if (culture.TwoLetterISOLanguageName == "ja")
                {
                    return UiLanguage.Japanese;
                }

                // 日本語以外のカルチャでも、日本語版 Windows なら日本語にする
                if (culture.Parent != null && culture.Parent.TwoLetterISOLanguageName == "ja")
                {
                    return UiLanguage.Japanese;
                }
            }
            catch (Exception)
            {
            }

            return UiLanguage.English;
        }

        /// <summary>設定に保存する言語コード。</summary>
        public static string CodeFor(UiLanguage language)
        {
            return language == UiLanguage.English ? "en" : "ja";
        }

        /// <summary>言語そのものの名称。言語切り替えの選択肢に出す。</summary>
        public static string DisplayName(UiLanguage language)
        {
            return language == UiLanguage.English ? "English" : "日本語";
        }

        private static readonly Dictionary<string, string> Japanese =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // メイン画面
            { "app.title", "WinWhisper - リアルタイム文字起こし" },
            { "app.title.withState", "WinWhisper - リアルタイム文字起こし  [{0} / {1}]" },
            { "menu.file", "ファイル(&F)" },
            { "menu.file.start", "録音開始(&R)" },
            { "menu.file.stop", "録音停止(&S)" },
            { "menu.file.exit", "終了(&X)" },
            { "menu.tools", "ツール(&T)" },
            { "menu.tools.history", "過去の履歴(&H)..." },
            { "menu.tools.source", "現在の音源: {0}" },
            { "menu.tools.settings", "設定(&S)..." },
            { "menu.help", "ヘルプ(&H)" },
            { "menu.help.log", "ログを開く(&L)..." },
            { "menu.help.logFolder", "ログフォルダを開く(&F)..." },
            { "menu.help.about", "バージョン情報(&A)..." },

            { "toolbar.start", "録音開始" },
            { "toolbar.stop", "録音停止" },
            { "toolbar.startTip", "録音を開始します (F5)" },
            { "toolbar.stopTip", "録音を停止して残りを確定します (F6)" },
            { "toolbar.timerTip", "録音の経過時間" },
            { "toolbar.level", "入力レベル" },

            { "status.idle", "待機中" },
            { "status.starting", "録音を開始しています..." },
            { "status.loadingModel", "モデルを読み込んでいます..." },
            { "status.recording", "録音中（ローカル Whisper 認識）" },
            { "status.recognizingFile", "文字起こし中（ファイル）" },
            { "status.stopping", "停止しています..." },
            { "status.flushing", "残りの音声を認識しています..." },
            { "status.stopped", "停止しました" },
            { "status.stoppedWithWav", "WAV を保存しました: {0}" },
            { "status.fileEnded", "ファイルを最後まで読み終えました" },
            { "status.speech", "発話を検出" },
            { "status.silence", "無音" },
            { "status.micOpenFailed", "マイクを開けませんでした: {0}" },
            { "status.audioError", "音声処理でエラー: {0}" },

            { "source.both", "スピーカー出力 + マイク" },
            { "source.speakers", "スピーカー出力" },
            { "source.mic", "マイク" },

            { "dialog.recordingTitle", "録音中" },
            { "dialog.recordingBody", "録音中です。停止して終了しますか？" },
            { "dialog.errorTitle", "WinWhisper" },
            { "dialog.unknownError", "不明なエラーが発生しました。" },
            { "dialog.logOpenFailed", "ログファイルを作成できませんでした。" },
            { "status.noModel", "モデルが無いため開始できません" },
            { "status.startFailed", "開始できませんでした: {0}" },
            { "status.stopFailed", "停止に失敗しました: {0}" },
            { "status.stoppedWithFile", "停止しました — {0}" },
            { "status.stoppedWithErrors", "停止しました（エラーあり）" },
            { "status.textSaveFailed", "テキスト保存に失敗: {0}" },
            { "status.autoSavePreview", "# (録音中: 自動保存プレビュー)" },
            { "status.errorWithLog", "{0}  (詳細は「ヘルプ > ログを開く」で確認できます)" },
            { "dialog.modelMissingTitle", "モデルがありません" },
            { "dialog.modelMissingBody",
              "Whisper のモデル（{0}）が見つかりません。\n今すぐダウンロードしますか？\n\n保存先: {1}\n目安: {2} MB\n\n「いいえ」を選ぶと設定から別のモデルを選べます。" },
            { "dialog.downloadFailedTitle", "ダウンロードに失敗しました" },
            { "dialog.downloadFailedBody", "{0}\n\nログ: {1}" },

            { "about.title", "バージョン情報" },
            { "about.product", "WinWhisper {0}" },
            { "about.description",
              "スピーカー出力とマイクの音声を、ローカルの Whisper で文字起こしします。\nAPI キーもネットワークも不要です。" },
            { "about.runtime", "実行環境: {0} / Whisper.net {1}" },
            { "about.settings", "設定ファイル: {0}" },
            { "about.log", "ログ: {0}" },

            // 設定ダイアログ
            { "settings.title", "設定" },
            { "settings.ok", "OK" },
            { "settings.cancel", "キャンセル" },
            { "settings.tab.audio", "録音" },
            { "settings.tab.model", "モデル" },
            { "settings.tab.storage", "保存先" },
            { "settings.tab.general", "全般" },

            { "settings.audio.useOutput", "スピーカー出力を取り込む（ループバック）" },
            { "settings.audio.useInput", "マイクを取り込む" },
            { "settings.audio.outputDevice", "出力デバイス" },
            { "settings.audio.inputDevice", "入力デバイス" },
            { "settings.audio.note",
              "両方を取り込むと、会議の音声（出力）と自分の発話（マイク）を同時に文字起こしします。\n取り込む音源が 1 つも選ばれていない場合は、スピーカー出力だけを使います。" },

            { "settings.model.label", "モデル" },
            { "settings.model.download", "ダウンロード" },
            { "settings.model.downloading", "{0} をダウンロードしています..." },
            { "settings.model.downloadProgress", "{0} ... {1} MB ({2}%)" },
            { "settings.model.downloaded", "{0} のダウンロードが完了しました" },
            { "settings.model.downloadFailed", "ダウンロードできませんでした" },
            { "settings.model.downloadError", "ダウンロードに失敗しました: {0}" },
            { "settings.model.present", "取得済み ({0} MB)  {1}" },
            { "settings.model.absent", "未取得 — 「ダウンロード」で取得してください  {0}" },
            { "settings.model.note",
              "モデルは ggml 形式（whisper.cpp 用）です。\nCPU だけで動かす場合、small が速度と精度の妥協点になります。\nmedium 以上は実時間に追いつかないことがあります。" },

            { "settings.storage.openFolder", "保存フォルダを開く" },
            { "settings.storage.paths",
              "テキスト（履歴）\n    {0}\n\nWAV\n    {1}\n\nモデル\n    {2}\n\nログ\n    {3}" },

            { "settings.general.language", "表示言語" },
            { "settings.general.note",
              "表示言語は次回の起動から反映されます。\n認識する言語（音声の言語）は日本語のままです。" },

            { "device.defaultOutput", "（既定の出力デバイス）" },
            { "device.defaultInput", "（既定の入力デバイス）" },

            // 履歴ウィンドウ
            { "history.title", "過去の履歴" },
            { "history.close", "閉じる" },
            { "history.openInEditor", "エディタで開く" },
            { "history.openFolder", "フォルダを開く" },
            { "history.refresh", "更新" },
            { "history.empty", "履歴がありません。" },
            { "history.selectPrompt", "左の一覧から選ぶと内容を表示します。" },
            { "history.readFailed", "読み込めませんでした: {0}" },

            // コマンドライン
            { "cli.unknownOption", "不明なオプションです: {0}" },
            { "cli.unparsedArgument", "解釈できない引数です: {0}" },
            { "cli.inputNotFound", "入力ファイルが見つかりません: {0}" },
            { "cli.inputAndSource", "-i と -s は同時に指定できません（音源はファイルになります）。" },
            { "cli.secondsNegative", "-t には 0 以上の秒数を指定してください。" },
            { "cli.maxChunkRange", "--max-chunk は 2〜30 秒の範囲で指定してください。" },
            { "cli.silenceRange", "--silence は 0.2〜3 秒の範囲で指定してください。" },
            { "cli.valueRequired", "オプション {0} には値が必要です。" },
            { "cli.notANumber", "数値として解釈できません: {0}" },
            { "cli.sourceChoice", "-s には both / speakers / mic のいずれかを指定してください: {0}" },
            { "cli.languageChoice", "-l には ja / en のいずれかを指定してください: {0}" },
            { "cli.errorPrefix", "エラー: {0}" },
            { "cli.helpHint", "使い方は WinWhisper.exe --help を参照してください。" },
            { "cli.version", "WinWhisper {0}" },

            { "cli.modelMissing", "モデルが見つかりません: {0}" },
            { "cli.modelMissingHint", "  設定 → モデル でダウンロードするか、--model で指定してください。" },
            { "cli.modelSearchPath", "  既定の置き場: {0}" },
            { "cli.modelDirFailed", "--model-dir を使えません: {0}" },
            { "cli.audioFile", "音声ファイル: {0}" },
            { "cli.source", "音源: {0}" },
            { "cli.model", "モデル: {0}" },
            { "cli.recordingStarted", "録音を開始しました" },
            { "cli.recordingStartedWav", "録音を開始しました  WAV: {0}" },
            { "cli.pressCtrlC", "停止するには Ctrl+C を押してください。" },
            { "cli.interrupting", "中断します..." },
            { "cli.stopFailed", "停止処理でエラー: {0}" },
            { "cli.stopFailedShort", "停止処理に失敗しました" },
            { "cli.textWriteFailed", "テキストを書き出せません: {0}" },
            { "cli.fileReadFailed", "ファイルの読み込みに失敗しました: {0}" },
            { "cli.resultHeader", "--- 結果 ---" },
            { "cli.resultLines", "行数: {0}" },
            { "cli.resultText", "テキスト: {0}" },
            { "cli.resultWav", "WAV: {0}  ({1} KB)" },
            { "cli.devicesOutput", "出力デバイス（ループバック録音に使えます）:" },
            { "cli.devicesInput", "入力デバイス（マイク）:" },
            { "cli.devicesNone", "  (見つかりません)" },
            { "cli.devicesHint", "--output-device / --input-device には ID か名前の一部をそのまま渡せます。" },
            { "cli.engineError", "エラー: {0}" },
        };

        private static readonly Dictionary<string, string> English =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Main window
            { "app.title", "WinWhisper - Real-time transcription" },
            { "app.title.withState", "WinWhisper - Real-time transcription  [{0} / {1}]" },
            { "menu.file", "&File" },
            { "menu.file.start", "&Start recording" },
            { "menu.file.stop", "&Stop recording" },
            { "menu.file.exit", "E&xit" },
            { "menu.tools", "&Tools" },
            { "menu.tools.history", "&History..." },
            { "menu.tools.source", "Current source: {0}" },
            { "menu.tools.settings", "&Settings..." },
            { "menu.help", "&Help" },
            { "menu.help.log", "Open &log..." },
            { "menu.help.logFolder", "Open log &folder..." },
            { "menu.help.about", "&About..." },

            { "toolbar.start", "Start" },
            { "toolbar.stop", "Stop" },
            { "toolbar.startTip", "Start recording (F5)" },
            { "toolbar.stopTip", "Stop recording and flush the rest (F6)" },
            { "toolbar.timerTip", "Elapsed recording time" },
            { "toolbar.level", "Input level" },

            { "status.idle", "Idle" },
            { "status.starting", "Starting recording..." },
            { "status.loadingModel", "Loading the model..." },
            { "status.recording", "Recording (local Whisper)" },
            { "status.recognizingFile", "Transcribing (file)" },
            { "status.stopping", "Stopping..." },
            { "status.flushing", "Transcribing the remaining audio..." },
            { "status.stopped", "Stopped" },
            { "status.stoppedWithWav", "Saved WAV: {0}" },
            { "status.fileEnded", "Reached the end of the file" },
            { "status.speech", "Speech detected" },
            { "status.silence", "Silence" },
            { "status.micOpenFailed", "Could not open the microphone: {0}" },
            { "status.audioError", "Audio processing error: {0}" },

            { "source.both", "Speakers + microphone" },
            { "source.speakers", "Speakers" },
            { "source.mic", "Microphone" },

            { "dialog.recordingTitle", "Recording" },
            { "dialog.recordingBody", "Recording is in progress. Stop and exit?" },
            { "dialog.errorTitle", "WinWhisper" },
            { "dialog.unknownError", "An unknown error occurred." },
            { "dialog.logOpenFailed", "Could not create the log file." },
            { "status.noModel", "Cannot start without a model" },
            { "status.startFailed", "Could not start: {0}" },
            { "status.stopFailed", "Could not stop: {0}" },
            { "status.stoppedWithFile", "Stopped — {0}" },
            { "status.stoppedWithErrors", "Stopped (with errors)" },
            { "status.textSaveFailed", "Could not save the text: {0}" },
            { "status.autoSavePreview", "# (recording: autosave preview)" },
            { "status.errorWithLog", "{0}  (see Help > Open log for details)" },
            { "dialog.modelMissingTitle", "Model not found" },
            { "dialog.modelMissingBody",
              "The Whisper model ({0}) was not found.\nDownload it now?\n\nDestination: {1}\nEstimated size: {2} MB\n\nChoose No to pick another model in Settings." },
            { "dialog.downloadFailedTitle", "Download failed" },
            { "dialog.downloadFailedBody", "{0}\n\nLog: {1}" },

            { "about.title", "About" },
            { "about.product", "WinWhisper {0}" },
            { "about.description",
              "Transcribes speaker output and microphone audio with a local Whisper model.\nNo API key and no network required." },
            { "about.runtime", "Runtime: {0} / Whisper.net {1}" },
            { "about.settings", "Settings: {0}" },
            { "about.log", "Log: {0}" },

            // Settings
            { "settings.title", "Settings" },
            { "settings.ok", "OK" },
            { "settings.cancel", "Cancel" },
            { "settings.tab.audio", "Audio" },
            { "settings.tab.model", "Model" },
            { "settings.tab.storage", "Storage" },
            { "settings.tab.general", "General" },

            { "settings.audio.useOutput", "Capture speaker output (loopback)" },
            { "settings.audio.useInput", "Capture microphone" },
            { "settings.audio.outputDevice", "Output device" },
            { "settings.audio.inputDevice", "Input device" },
            { "settings.audio.note",
              "Capturing both transcribes meeting audio (output) and your own voice (microphone) at the same time.\nIf neither is selected, speaker output is used." },

            { "settings.model.label", "Model" },
            { "settings.model.download", "Download" },
            { "settings.model.downloading", "Downloading {0}..." },
            { "settings.model.downloadProgress", "{0} ... {1} MB ({2}%)" },
            { "settings.model.downloaded", "Finished downloading {0}" },
            { "settings.model.downloadFailed", "Download failed" },
            { "settings.model.downloadError", "Download failed: {0}" },
            { "settings.model.present", "Downloaded ({0} MB)  {1}" },
            { "settings.model.absent", "Not downloaded — use \"Download\" to fetch it  {0}" },
            { "settings.model.note",
              "Models are in ggml format (for whisper.cpp).\nOn CPU only, small is the sweet spot for speed and accuracy.\nmedium and larger may not keep up with real time." },

            { "settings.storage.openFolder", "Open save folder" },
            { "settings.storage.paths",
              "Text (history)\n    {0}\n\nWAV\n    {1}\n\nModels\n    {2}\n\nLogs\n    {3}" },

            { "settings.general.language", "Display language" },
            { "settings.general.note",
              "The display language takes effect the next time you start the app.\nThe recognition language (spoken language) stays Japanese." },

            { "device.defaultOutput", "(default output device)" },
            { "device.defaultInput", "(default input device)" },

            // History window
            { "history.title", "History" },
            { "history.close", "Close" },
            { "history.openInEditor", "Open in editor" },
            { "history.openFolder", "Open folder" },
            { "history.refresh", "Refresh" },
            { "history.empty", "No history yet." },
            { "history.selectPrompt", "Select an item on the left to see its contents." },
            { "history.readFailed", "Could not read: {0}" },

            // Command line
            { "cli.unknownOption", "Unknown option: {0}" },
            { "cli.unparsedArgument", "Unrecognized argument: {0}" },
            { "cli.inputNotFound", "Input file not found: {0}" },
            { "cli.inputAndSource", "-i and -s cannot be combined (the source becomes the file)." },
            { "cli.secondsNegative", "-t requires a duration of 0 seconds or more." },
            { "cli.maxChunkRange", "--max-chunk must be between 2 and 30 seconds." },
            { "cli.silenceRange", "--silence must be between 0.2 and 3 seconds." },
            { "cli.valueRequired", "Option {0} requires a value." },
            { "cli.notANumber", "Not a number: {0}" },
            { "cli.sourceChoice", "-s must be one of both / speakers / mic: {0}" },
            { "cli.languageChoice", "-l must be either ja or en: {0}" },
            { "cli.errorPrefix", "Error: {0}" },
            { "cli.helpHint", "Run WinWhisper.exe --help for usage." },
            { "cli.version", "WinWhisper {0}" },

            { "cli.modelMissing", "Model not found: {0}" },
            { "cli.modelMissingHint", "  Download it in Settings > Model, or pass --model." },
            { "cli.modelSearchPath", "  Default location: {0}" },
            { "cli.modelDirFailed", "Cannot use --model-dir: {0}" },
            { "cli.audioFile", "Audio file: {0}" },
            { "cli.source", "Source: {0}" },
            { "cli.model", "Model: {0}" },
            { "cli.recordingStarted", "Recording started" },
            { "cli.recordingStartedWav", "Recording started  WAV: {0}" },
            { "cli.pressCtrlC", "Press Ctrl+C to stop." },
            { "cli.interrupting", "Interrupting..." },
            { "cli.stopFailed", "Error while stopping: {0}" },
            { "cli.stopFailedShort", "Stopping failed" },
            { "cli.textWriteFailed", "Could not write the text file: {0}" },
            { "cli.fileReadFailed", "Could not read the file: {0}" },
            { "cli.resultHeader", "--- Result ---" },
            { "cli.resultLines", "Lines: {0}" },
            { "cli.resultText", "Text: {0}" },
            { "cli.resultWav", "WAV: {0}  ({1} KB)" },
            { "cli.devicesOutput", "Output devices (usable for loopback capture):" },
            { "cli.devicesInput", "Input devices (microphones):" },
            { "cli.devicesNone", "  (none found)" },
            { "cli.devicesHint", "--output-device / --input-device accept an ID or part of a name." },
            { "cli.engineError", "Error: {0}" },
        };
    }
}
