using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinRealtimeWhisper
{
    internal sealed class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly TranscriptionEngine _engine = new TranscriptionEngine();
        private readonly Timer _timer = new Timer();

        private TranscriptionSession _session;
        private string _sessionFilePath;
        private DateTime _lastSaveUtc = DateTime.MinValue;
        private bool _saveQueued;
        private DateTime _lastTextUtc = DateTime.MinValue;
        private bool _starting;
        private bool _stopping;
        private bool _downloading;

        private readonly StringBuilder _display = new StringBuilder();
        private string _pendingText = string.Empty;
        private string _lastRenderedPending = string.Empty;
        private string _errorText = string.Empty;

        private ToolStripMenuItem _miStart;
        private ToolStripMenuItem _miStop;
        private ToolStripMenuItem _miHistory;
        private ToolStripMenuItem _miSettings;
        private ToolStripMenuItem _miSource;
        private ToolStripMenuItem _miAlwaysOnTop;

        private ToolStrip _toolbar;
        private ToolStripButton _btnStart;
        private ToolStripButton _btnStop;
        private ToolStripLabel _lblTimer;
        private ToolStripLabel _lblBacklog;
        private TextBox _txtLive;
        private Label _lblPending;
        private ProgressBar _levelBar;

        /// <summary>本文の編集欄。スモークテストが表示内容を確認するために公開している。</summary>
        internal TextBox EditorBox
        {
            get { return _txtLive; }
        }

        /// <summary>固定の言語。UI からは変更できない。</summary>
        private const string FixedLanguage = "ja";

        public MainForm()
            : this(null)
        {
        }

        /// <summary>
        /// コマンドラインで上書きした設定を受け取る。null なら settings.json を読む。
        /// </summary>
        public MainForm(AppSettings settings)
        {
            DiagLog.Start();

            _settings = settings ?? AppSettings.Load();
            BuildUi();

            _engine.FinalText += OnFinalText;
            _engine.Status += OnStatus;
            _engine.Started += OnStarted;
            _engine.Level += OnLevel;
            _engine.SpeechActivity += OnSpeechActivity;
            _engine.Failed += OnFailed;

            LoadSettingsIntoUi();

            _timer.Interval = 200;
            _timer.Tick += OnTimerTick;
            _timer.Start();

            UpdateButtons();
            UpdateMenuText();
            SetStatus(Loc.T("status.idle"));

            // モデルが無ければ起動時に自動でダウンロードする
            Shown += async (s, e) =>
            {
                RefreshLiveDisplay();
                await EnsureModelAsync();
            };
        }

        private void BuildUi()
        {
            Text = Loc.T("app.title");
            MinimumSize = new Size(520, 240);
            Size = new Size(980, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Yu Gothic UI", 9f);
            Icon = AppIcons.Create();
            BackColor = Color.FromArgb(240, 240, 240);

            var menu = new MenuStrip();

            var miFile = new ToolStripMenuItem(Loc.T("menu.file"));
            _miStart = new ToolStripMenuItem(Loc.T("menu.file.start"), null, async (s, e) => await StartRecordingAsync())
            {
                ShortcutKeys = Keys.F5
            };
            _miStop = new ToolStripMenuItem(Loc.T("menu.file.stop"), null, async (s, e) => await StopRecordingAsync())
            {
                ShortcutKeys = Keys.F6
            };
            var miExit = new ToolStripMenuItem(Loc.T("menu.file.exit"), null, (s, e) => Close())
            {
                ShortcutKeys = Keys.Alt | Keys.F4
            };
            miFile.DropDownItems.Add(_miStart);
            miFile.DropDownItems.Add(_miStop);
            miFile.DropDownItems.Add(new ToolStripSeparator());
            miFile.DropDownItems.Add(miExit);

            var miView = new ToolStripMenuItem(Loc.T("menu.view"));
            _miAlwaysOnTop = new ToolStripMenuItem(Loc.T("menu.view.alwaysOnTop"), null, (s, e) => ToggleAlwaysOnTop())
            {
                CheckOnClick = true
            };
            miView.DropDownItems.Add(_miAlwaysOnTop);

            var miTools = new ToolStripMenuItem(Loc.T("menu.tools"));
            _miHistory = new ToolStripMenuItem(Loc.T("menu.tools.history"), null, (s, e) => ShowHistory());
            _miSettings = new ToolStripMenuItem(Loc.T("menu.tools.settings"), null, async (s, e) => await ShowSettings())
            {
                ShortcutKeys = Keys.Control | Keys.Oemcomma
            };
            _miSource = new ToolStripMenuItem(Loc.T("menu.tools.source", "-")) { Enabled = false };
            miTools.DropDownItems.Add(_miHistory);
            miTools.DropDownItems.Add(_miSource);
            miTools.DropDownItems.Add(new ToolStripSeparator());
            miTools.DropDownItems.Add(_miSettings);

            var miHelp = new ToolStripMenuItem(Loc.T("menu.help"));
            var miLog = new ToolStripMenuItem(Loc.T("menu.help.log"), null, (s, e) => OpenLog());
            var miLogFolder = new ToolStripMenuItem(Loc.T("menu.help.logFolder"), null, (s, e) => OpenFolder(DiagLog.Directory));
            var miAbout = new ToolStripMenuItem(Loc.T("menu.help.about"), null, (s, e) => ShowAbout());
            miHelp.DropDownItems.Add(miLog);
            miHelp.DropDownItems.Add(miLogFolder);
            miHelp.DropDownItems.Add(new ToolStripSeparator());
            miHelp.DropDownItems.Add(miAbout);

            menu.Items.Add(miFile);
            menu.Items.Add(miView);
            menu.Items.Add(miTools);
            menu.Items.Add(miHelp);
            MainMenuStrip = menu;

            // 録音操作のツールバー
            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new EdgeOnlyRenderer(),
                Padding = new Padding(6, 2, 6, 2),
                ImageScalingSize = new Size(20, 20)
            };

            _btnStart = new ToolStripButton(Loc.T("toolbar.start"))
            {
                Image = AppIcons.Record(),
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ToolTipText = Loc.T("toolbar.startTip")
            };
            _btnStart.Click += async (s, e) => await StartRecordingAsync();

            _btnStop = new ToolStripButton(Loc.T("toolbar.stop"))
            {
                Image = AppIcons.Stop(),
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ToolTipText = Loc.T("toolbar.stopTip"),
                Enabled = false
            };
            _btnStop.Click += async (s, e) => await StopRecordingAsync();

            _lblTimer = new ToolStripLabel("00:00:00")
            {
                Font = new Font("Consolas", 10f),
                ForeColor = Color.DimGray,
                Padding = new Padding(8, 0, 8, 0),
                ToolTipText = Loc.T("toolbar.timerTip")
            };

            _lblBacklog = new ToolStripLabel()
            {
                Font = new Font("Consolas", 9f),
                ForeColor = Color.DimGray,
                Padding = new Padding(8, 0, 8, 0),
                ToolTipText = Loc.T("toolbar.backlogTip"),
                Visible = false
            };

            _levelBar = new ProgressBar()
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 140,
                Height = 13,
                Style = ProgressBarStyle.Continuous
            };

            // 左から「録音/再生 | 時間 | インジケーター | 未確定」の順に並べる。
            // 状態メッセージはツールバーではなくタイトルバーに出す。
            _toolbar.Items.Add(_btnStart);
            _toolbar.Items.Add(_btnStop);
            _toolbar.Items.Add(_lblTimer);
            _toolbar.Items.Add(new ToolStripControlHost(_levelBar) { Margin = new Padding(6, 0, 6, 0) });
            _toolbar.Items.Add(_lblBacklog);

            // 本文は読み取り専用の TextBox にする（選択・コピーを可能にするため）。
            // 枠は上辺が白く光って見えるので BorderStyle は None にし、親パネル側で描く。
            _txtLive = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Yu Gothic UI", 12f),
                ScrollBars = ScrollBars.Vertical,
                ShortcutsEnabled = true
            };

            _lblPending = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 0,
                BackColor = Color.White,
                ForeColor = Color.SteelBlue,
                Font = new Font("Yu Gothic UI", 12f),
                UseMnemonic = false,
                TextAlign = ContentAlignment.TopLeft
            };

            var editorPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 4),
                BackColor = Color.FromArgb(240, 240, 240)
            };
            editorPanel.Controls.Add(_lblPending);
            editorPanel.Controls.Add(_txtLive);

            Controls.Add(editorPanel);
            Controls.Add(_toolbar);
            Controls.Add(menu);
        }

        /// <summary>
        /// ツールバーとメニューの境界線を消す。
        /// 既定の System レンダラーは下端に明るいハイライトを描き、白い線に見えるため。
        /// </summary>
        private sealed class EdgeOnlyRenderer : ToolStripProfessionalRenderer
        {
            public EdgeOnlyRenderer()
                : base(new EdgeOnlyColors())
            {
                RoundedEdges = false;
            }
        }

        private sealed class EdgeOnlyColors : ProfessionalColorTable
        {
            private static readonly Color Surface = Color.FromArgb(240, 240, 240);

            // 枠線は描かせない
            public override Color ToolStripBorder => Surface;

            // ハイライトと影は描かせない（明るい線の正体）
            public override Color ToolStripGradientBegin => Surface;

            public override Color ToolStripGradientMiddle => Surface;

            public override Color ToolStripGradientEnd => Surface;
        }

        private static Label NewLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        }

        private void LoadSettingsIntoUi()
        {
            _settings.WhisperLanguage = FixedLanguage;
            _settings.Language = "ja-JP";

            if (string.IsNullOrEmpty(_settings.ModelPath))
            {
                _settings.ModelPath = WhisperModelStore.PathFor(WhisperModelStore.DefaultModelFileName);
            }

            ApplyStorageSettings();
            ApplyAlwaysOnTop(_settings.AlwaysOnTop);
        }

        /// <summary>ウィンドウを常に手前に出すかどうかを切り替えて設定に保存する。</summary>
        private void ToggleAlwaysOnTop()
        {
            ApplyAlwaysOnTop(_miAlwaysOnTop.Checked);
            PersistSettings();
        }

        /// <summary>最前面を適用し、メニューのチェック状態も合わせる。</summary>
        private void ApplyAlwaysOnTop(bool onTop)
        {
            TopMost = onTop;
            _settings.AlwaysOnTop = onTop;

            if (_miAlwaysOnTop != null && _miAlwaysOnTop.Checked != onTop)
            {
                _miAlwaysOnTop.Checked = onTop;
            }
        }

        /// <summary>設定の保存先を、実際に書き込むパスへ反映する。</summary>
        private void ApplyStorageSettings()
        {
            HistoryStore.RootDirectory = _settings.ResolveHistoryDirectory();
            DiagLog.Directory = _settings.ResolveLogDirectory();

            // モデルの保存先は静的プロパティ経由で参照される。
            // CLI の --model-dir が指定されているときはそちらを優先する。
            if (string.IsNullOrWhiteSpace(_settings.ModelDirectory))
            {
                AppSettings.ModelDirectoryOverride = null;
            }
            else
            {
                AppSettings.ModelDirectoryOverride = _settings.ModelDirectory;

                // モデル本体も新しいフォルダのものを指すようにする。
                string fileName = Path.GetFileName(_settings.ModelPath ?? string.Empty);
                if (fileName.Length == 0)
                {
                    fileName = WhisperModelStore.DefaultModelFileName;
                }

                _settings.ModelPath = WhisperModelStore.PathFor(fileName);
            }
        }

        /// <summary>確定モデルのファイル名（フルパスを含む場合もあれば、単なる名前のときもある）。</summary>
        private string CurrentModelFileName()
        {
            return Path.GetFileName(_settings.ModelPath ?? string.Empty);
        }

        /// <summary>現在の状態メッセージ。タイトルバーに出す。</summary>
        private string _statusText;

        /// <summary>状態メッセージを設定してタイトルバーへ反映する。</summary>
        private void SetStatus(string text)
        {
            _statusText = text ?? string.Empty;
            UpdateTitle();
        }

        /// <summary>タイトルバーに状態を出す。モデル名や音源は出さない。</summary>
        private void UpdateTitle()
        {
            string status = _statusText;
            Text = string.IsNullOrEmpty(status)
                ? Loc.T("app.title")
                : Loc.T("app.title.status", status);
        }

        /// <summary>「音源」メニューの表示を今の設定に合わせる。</summary>
        private void UpdateMenuText()
        {
            _miSource.Text = Loc.T("menu.tools.source", SourceLabel());
        }

        private string SourceLabel()
        {
            switch (_settings.SourceKind)
            {
                case AudioSourceKind.Microphone: return Loc.T("source.mic");
                case AudioSourceKind.SystemLoopback: return Loc.T("source.speakers");
                default: return Loc.T("source.both");
            }
        }

        /// <summary>設定ダイアログを開き、OK なら設定を書き戻す。</summary>
        private async Task ShowSettings()
        {
            using (var dialog = new SettingsForm(_settings))
            {
                dialog.DownloadHandler = fileName => DownloadModelAsync(fileName, null);

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    dialog.ApplyToSettings();
                    ApplyStorageSettings();
                    PersistSettings();
                    UpdateMenuText();
                }
            }

            await Task.Yield();
        }

        private void ShowHistory()
        {
            using (var dialog = new HistoryForm())
            {
                dialog.ShowDialog(this);
            }
        }

        private void ShowAbout()
        {
            string body = Loc.T("about.description")
                + Environment.NewLine + Environment.NewLine
                + Loc.T("about.runtime", RuntimeLabel(), WhisperNetVersion())
                + Environment.NewLine
                + Loc.T("about.settings", AppSettings.FilePath)
                + Environment.NewLine
                + Loc.T("about.log", DiagLog.CurrentPath ?? DiagLog.Directory);

            MessageBox.Show(this, body, Loc.T("about.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string RuntimeLabel()
        {
            return ".NET Framework " + Environment.Version + " / x64";
        }

        private static string WhisperNetVersion()
        {
            try
            {
                var asm = typeof(WhisperRecognizer).Assembly;
                var referenced = asm.GetReferencedAssemblies();
                foreach (var name in referenced)
                {
                    if (name.Name == "Whisper.net")
                    {
                        return name.Version.ToString();
                    }
                }
            }
            catch (Exception)
            {
            }

            return "1.9.1";
        }

        private void OpenLog()
        {
            try
            {
                string path = DiagLog.CurrentPath;
                if (string.IsNullOrEmpty(path))
                {
                    path = DiagLog.Start();
                }

                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show(this, Loc.T("dialog.logOpenFailed"), Loc.T("dialog.errorTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, DiagLog.Describe(ex), Loc.T("dialog.errorTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Loc.T("dialog.errorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// モデルが無ければダウンロードする。起動時と録音開始時に呼ぶ。
        /// 取得できたら true。
        /// </summary>
        private async Task<bool> EnsureModelAsync()
        {
            string fileName = CurrentModelFileName();
            if (WhisperModelStore.Exists(fileName))
            {
                return true;
            }

            var answer = MessageBox.Show(this,
                Loc.T("dialog.modelMissingBody", fileName,
                    AppSettings.ModelDirectoryEffective, ApproxMb(fileName)),
                Loc.T("dialog.modelMissingTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                SetStatus(Loc.T("status.noModel"));
                return false;
            }

            return await DownloadModelAsync(fileName, null);
        }

        /// <summary>指定モデルを非同期でダウンロードする。進捗はステータスバーと任意のコールバックへ出す。</summary>
        private async Task<bool> DownloadModelAsync(string fileName, IProgress<long> progress)
        {
            SetStatus(Loc.T("settings.model.downloading", fileName));
            _downloading = true;
            UpdateButtons();

            var storeProgress = new Progress<long>(bytes =>
            {
                var info = FindModel(fileName);
                long percent = info != null && info.ApproxBytes > 0 ? bytes * 100 / info.ApproxBytes : 0;
                SetStatus(Loc.T("settings.model.downloadProgress",
                    fileName, bytes / (1024 * 1024), Math.Min(100, percent)));

                if (progress != null)
                {
                    progress.Report(bytes);
                }
            });

            bool ok = false;
            try
            {
                await WhisperModelStore.DownloadAsync(fileName, storeProgress, System.Threading.CancellationToken.None);
                _settings.ModelPath = WhisperModelStore.PathFor(fileName);
                PersistSettings();
                SetStatus(Loc.T("settings.model.downloaded", fileName));
                ok = true;
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("モデルのダウンロードに失敗", ex);
                SetStatus(Loc.T("settings.model.downloadFailed"));
                MessageBox.Show(this,
                    Loc.T("dialog.downloadFailedBody", DiagLog.Describe(ex), DiagLog.CurrentPath)
                    + Environment.NewLine + Environment.NewLine
                    + WhisperModelStore.UrlFor(fileName)
                    + Environment.NewLine
                    + AppSettings.ModelDirectoryEffective,
                    Loc.T("dialog.downloadFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _downloading = false;
                UpdateMenuText();
                UpdateButtons();
            }

            return ok;
        }

        private static long ApproxMb(string fileName)
        {
            var info = FindModel(fileName);
            return info != null ? info.ApproxBytes / (1024 * 1024) : 0;
        }

        private static WhisperModelStore.ModelInfo FindModel(string fileName)
        {
            foreach (var model in WhisperModelStore.Models)
            {
                if (string.Equals(model.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }

            return null;
        }

        private void PersistSettings()
        {
            _settings.WhisperLanguage = FixedLanguage;
            _settings.Language = "ja-JP";

            try
            {
                _settings.Save();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private async Task StartRecordingAsync()
        {
            if (_starting || _engine.IsRecording)
            {
                return;
            }

            PersistSettings();

            if (!await EnsureModelAsync())
            {
                return;
            }

            _starting = true;
            _errorText = string.Empty;
            _pendingText = string.Empty;
            _display.Clear();
            _txtLive.Text = string.Empty;
            _lblPending.Text = string.Empty;
            _lblTimer.Text = "00:00:00";
            SetStatus(Loc.T("status.starting"));
            UpdateButtons();

            _session = new TranscriptionSession();
            _sessionFilePath = HistoryStore.CreateSessionFilePath(_session.StartedAt);
            _lastSaveUtc = DateTime.MinValue;
            _lastTextUtc = DateTime.UtcNow;
            _linesRendered = 0;

            try
            {
                _engine.StartAsync(_settings, WavPathFor(_session.StartedAt));
                await Task.Yield();
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("status.startFailed", ex.Message));
                _starting = false;
                UpdateButtons();
            }
        }

        /// <summary>
        /// 録音が実際に始まった合図。モデルの読み込み中は _starting を立てたままにするため、
        /// StartRecordingAsync の中ではなくここでボタンを戻す。
        /// </summary>
        private void OnStarted(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke((Action)(() =>
            {
                _starting = false;
                UpdateButtons();
            }));
        }

        private async Task StopRecordingAsync()
        {
            _btnStop.Enabled = false;
            _stopping = true;
            SetStatus(Loc.T("status.stopping"));
            UpdateBacklogLabel();

            try
            {
                await _engine.StopAsync();
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("status.stopFailed", ex.Message));
            }
            finally
            {
                _stopping = false;
            }

            UpdateBacklogLabel();

            if (_session != null)
            {
                _session.StoppedAt = DateTime.Now;
            }

            SaveSessionFile(force: true);
            _pendingText = string.Empty;
            RefreshLiveDisplay();

            if (string.IsNullOrEmpty(_errorText))
            {
                SetStatus(_session != null
                    ? Loc.T("status.stoppedWithFile", Path.GetFileName(_sessionFilePath))
                    : Loc.T("status.stopped"));
            }
            else
            {
                SetStatus(Loc.T("status.stoppedWithErrors"));
            }

            UpdateButtons();
        }

        private string WavPathFor(DateTime startedAt)
        {
            string dir = _settings.ResolveWavDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "rec_" + startedAt.ToString("yyyyMMdd_HHmmss") + ".wav");
        }

        private void OnPartialText(object sender, PartialTextEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            _pendingText = (e.Text ?? string.Empty).Trim();
            _lastTextUtc = DateTime.UtcNow;
            _saveQueued = true;
        }

        private void OnFinalText(object sender, FinalTextEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            TimeSpan offset = TimeSpan.FromTicks(Math.Max(0, e.OffsetTicks));
            string text = e.Text ?? string.Empty;

            if (!string.IsNullOrEmpty(_pendingText))
            {
                offset = OffsetFromPartialText(_pendingText, text, offset);
            }

            _pendingText = string.Empty;
            _lastTextUtc = DateTime.UtcNow;

            if (_session != null)
            {
                // 認識スレッドから呼ばれる。確定の順序を守るため、ここで即座に積む。
                _session.AppendFinal(text, offset);
                _saveQueued = true;
            }

            // 次のタイマーを待たずに画面へ出す
            RequestLiveRefresh();
        }

        /// <summary>確定行をすぐ画面に反映する。どのスレッドから呼んでもよい。</summary>
        private void RequestLiveRefresh()
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || _session == null)
                    {
                        return;
                    }

                    _linesRendered = _session.Lines.Count;
                    _lastRenderedPending = _pendingText;
                    RefreshLiveDisplay();
                }));
            }
            catch (InvalidOperationException)
            {
                // ウィンドウが閉じた直後は無視する
            }
        }

        /// <summary>確定テキストの先頭が暫定表示と一致する分だけ時刻を繰り上げる。</summary>
        private static TimeSpan OffsetFromPartialText(string partial, string final, TimeSpan offset)
        {
            int common = 0;
            int max = Math.Min(partial.Length, final.Length);
            while (common < max && partial[common] == final[common])
            {
                common++;
            }

            if (common == 0)
            {
                return offset;
            }

            double ratio = (double)common / Math.Max(1, final.Length);
            double ticks = offset.Ticks * (1.0 - ratio);
            return TimeSpan.FromTicks((long)Math.Max(0, ticks));
        }

        /// <summary>確定行と暫定行をまとめて描画する(常に UI スレッドから呼ぶ)。</summary>
        private void RefreshLiveDisplay()
        {
            if (_session != null)
            {
                if (_display.Length > 0)
                {
                    _display.Clear();
                }

                foreach (var line in _session.Lines)
                {
                    if (_display.Length > 0)
                    {
                        _display.AppendLine();
                    }

                    _display.Append(line.Text);
                }
            }

            // 確定行と暫定行をまとめる（暫定行は末尾に付く）
            string confirmed = _display.ToString();
            bool pending = !string.IsNullOrEmpty(_pendingText);

            // 末尾が見えるよう、入りきらない分は先頭から切り落とす。
            string body = pending && confirmed.Length > 0
                ? confirmed + Environment.NewLine + _pendingText
                : confirmed + _pendingText;

            if (_session != null && _session.Lines.Count > 0)
            {
                string trimmed = FitToHeight(body, _txtLive.ClientSize);
                SetLiveText(trimmed);
            }
            else
            {
                SetLiveText(body);
            }

            _lblPending.Text = string.Empty;
            _lblPending.Height = 0;
        }

        /// <summary>
        /// 本文を差し替える。テキストを書き換えると選択が外れ、
        /// コピー操作の途中で選択が消えてしまうため、変わるときだけ設定する。
        /// </summary>
        private void SetLiveText(string value)
        {
            if (_txtLive.Text == value)
            {
                return;
            }

            int selStart = _txtLive.SelectionStart;
            int selLength = _txtLive.SelectionLength;

            _txtLive.Text = value;

            if (selLength > 0)
            {
                if (selStart > _txtLive.TextLength)
                {
                    selStart = _txtLive.TextLength;
                }

                if (selStart + selLength > _txtLive.TextLength)
                {
                    selLength = _txtLive.TextLength - selStart;
                }

                _txtLive.Select(selStart, selLength);
            }
            else
            {
                _txtLive.SelectionStart = _txtLive.TextLength;
                _txtLive.SelectionLength = 0;
            }
        }

        /// <summary>本文が入りきらないとき、末尾が残るよう先頭から落とす。</summary>
        private string FitToHeight(string text, Size area)
        {
            if (string.IsNullOrEmpty(text) || area.Width <= 0 || area.Height <= 0)
            {
                return text;
            }

            // 読み取り専用の TextBox は表示領域が広いので、行数ではなく実際の高さで判定する。
            int usable = _txtLive.ClientSize.Height;
            if (usable <= 0)
            {
                return text;
            }

            using (var g = _txtLive.CreateGraphics())
            {
                var limit = new Size(area.Width, int.MaxValue);
                var flags = TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak;

                if (TextRenderer.MeasureText(g, text, _txtLive.Font, limit, flags).Height <= usable)
                {
                    return text;
                }

                // 入りきらない分を少しずつ増やし、収まるところを二分探索する。
                int low = 0;
                int high = text.Length;
                while (low < high)
                {
                    int mid = low + (high - low) / 2;
                    string candidate = text.Substring(mid);
                    int height = TextRenderer.MeasureText(
                        g, candidate, _txtLive.Font, limit, flags).Height;

                    if (height <= usable)
                    {
                        high = mid;
                    }
                    else
                    {
                        low = mid + 1;
                    }
                }

                // 行の途中から始めると読みにくいので、直後の改行まで送る。
                int start = low;
                int nextBreak = text.IndexOf('\n', Math.Min(start, text.Length - 1));
                if (nextBreak >= 0 && nextBreak + 1 < text.Length)
                {
                    start = nextBreak + 1;
                }

                return start > 0 ? text.Substring(start) : text;
            }
        }

        private void OnStatus(object sender, StatusEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke((Action)(() =>
            {
                if (!_engine.IsRecording && !_starting)
                {
                    return;      // 停止後に届いた遅延イベントは無視する
                }

                SetStatus(e.Text);
            }));
        }

        private void OnLevel(object sender, LevelEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke((Action)(() =>
            {
                if (!_engine.IsRecording)
                {
                    return;
                }

                int value = (int)Math.Round(e.Level * 100.0);
                _levelBar.Value = Math.Max(0, Math.Min(100, value));
            }));
        }

        private void OnSpeechActivity(object sender, SpeechActivityEventArgs e)
        {
            // 現状は表示に使っていないが、将来の VAD 表示用に受けておく
        }

        private void OnFailed(object sender, ErrorEventArgs e)
        {
            _errorText = e.Message;

            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke((Action)(() =>
            {
                // 開始に失敗した場合もここに来る。_starting を戻さないとボタンが固まる。
                _starting = false;
                SetStatus(Loc.T("status.errorWithLog", e.Message));
                UpdateButtons();
            }));
        }

        private int _linesRendered;

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_session != null && _engine.IsRecording)
            {
                _lblTimer.Text = (DateTime.Now - _session.StartedAt).ToString(@"hh\:mm\:ss");
                _lblTimer.ForeColor = Color.Firebrick;
            }
            else
            {
                _lblTimer.ForeColor = Color.DimGray;
            }

            UpdateBacklogLabel();

            // Whisper は途中経過を出さないので、新しく確定した行があったかどうかで再描画する。
            // 暫定テキストの変化だけを見ていると、確定行が画面に出ない。
            int lineCount = _session != null ? _session.Lines.Count : 0;
            bool pendingChanged = _pendingText != _lastRenderedPending;

            if (_session != null && (lineCount != _linesRendered || pendingChanged))
            {
                _linesRendered = lineCount;
                _lastRenderedPending = _pendingText;
                RefreshLiveDisplay();
            }

            // ボタンの活性状態を定期確認する（イベントの取りこぼし保険）
            if (_btnStop != null && _btnStop.Enabled != _engine.IsRecording)
            {
                if (_engine.IsRecording && _starting)
                {
                    _starting = false;
                }

                UpdateButtons();
            }

            if (_session != null)
            {
                if (_saveQueued || (DateTime.UtcNow - _lastSaveUtc).TotalSeconds > 10)
                {
                    SaveSessionFile(force: _saveQueued);
                }
            }
        }

        /// <summary>
        /// ยัง確定していない音声の量を表示する。
        /// 推論が追いついているか（録音中）と、停止にどれだけかかるか（停止中）の両方の目安になる。
        /// </summary>
        private void UpdateBacklogLabel()
        {
            if (_lblBacklog == null)
            {
                return;
            }

            bool busy = _starting || _engine.IsRecording || _stopping;
            double backlog = _engine.BacklogSeconds;
            int queue = _engine.PendingChunks;

            // 録音中に残りが無いのは普通なので、何も出さない。
            if (!busy || (backlog < 0.5 && queue == 0))
            {
                _lblBacklog.Visible = false;
                return;
            }

            _lblBacklog.Visible = true;
            _lblBacklog.Text = Loc.T("toolbar.backlog", queue, backlog);

            // 追いつけていないときは目立たせる。
            int warning = _engine.DroppedChunks > 0 ? 2 : (backlog >= 10 ? 1 : 0);
            switch (warning)
            {
                case 2: _lblBacklog.ForeColor = Color.Firebrick; break;
                case 1: _lblBacklog.ForeColor = Color.DarkOrange; break;
                default: _lblBacklog.ForeColor = Color.DimGray; break;
            }
        }

        private void SaveSessionFile(bool force)
        {
            if (_session == null || string.IsNullOrEmpty(_sessionFilePath))
            {
                return;
            }

            bool due = force || (DateTime.UtcNow - _lastSaveUtc).TotalSeconds > 5;
            if (!due)
            {
                return;
            }

            _saveQueued = false;
            _lastSaveUtc = DateTime.UtcNow;

            try
            {
                string text = _session.ToPlainText();
                if (!_session.StoppedAt.HasValue)
                {
                    // 録音中はプレビューとして書き出す(停止時に確定内容で上書き)
                    text = text.TrimEnd() + Environment.NewLine + Loc.T("status.autoSavePreview") + Environment.NewLine;
                }

                HistoryStore.WriteAllText(_sessionFilePath, text);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("status.textSaveFailed", ex.Message));
            }
        }

        private void UpdateButtons()
        {
            bool recording = _engine.IsRecording;
            bool idle = !recording && !_starting && !_downloading;

            _btnStart.Enabled = idle;
            _btnStop.Enabled = recording;
            _miStart.Enabled = idle;
            _miStop.Enabled = recording;
            _miSettings.Enabled = !recording && !_downloading;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_engine.IsRecording)
            {
                var answer = MessageBox.Show(this, Loc.T("dialog.recordingBody"), Loc.T("dialog.errorTitle"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            PersistSettings();
            _timer.Stop();

            if (_session != null)
            {
                _session.StoppedAt = DateTime.Now;
                SaveSessionFile(force: true);
            }

            _engine.Dispose();
            base.OnFormClosing(e);
        }
    }
}
