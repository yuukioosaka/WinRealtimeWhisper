using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinWhisper
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

        private ToolStrip _toolbar;
        private ToolStripButton _btnStart;
        private ToolStripButton _btnStop;
        private ToolStripLabel _lblStatus;
        private ToolStripLabel _lblTimer;
        private Label _txtLive;
        private Label _lblPending;
        private ProgressBar _levelBar;
        private ToolStripLabel _lblActivity;

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
            MinimumSize = new Size(720, 520);
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
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ToolTipText = Loc.T("toolbar.startTip"),
                AutoSize = false,
                Width = 118
            };
            _btnStart.Click += async (s, e) => await StartRecordingAsync();

            _btnStop = new ToolStripButton(Loc.T("toolbar.stop"))
            {
                Image = AppIcons.Stop(),
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ToolTipText = Loc.T("toolbar.stopTip"),
                AutoSize = false,
                Width = 118,
                Enabled = false
            };
            _btnStop.Click += async (s, e) => await StopRecordingAsync();

            _lblStatus = new ToolStripLabel(Loc.T("status.idle"))
            {
                ForeColor = Color.DimGray,
                Padding = new Padding(10, 0, 0, 0)
            };

            _lblTimer = new ToolStripLabel("00:00:00")
            {
                Font = new Font("Consolas", 10f),
                ForeColor = Color.DimGray,
                Padding = new Padding(8, 0, 8, 0),
                ToolTipText = Loc.T("toolbar.timerTip")
            };

            _lblActivity = new ToolStripLabel(Loc.T("toolbar.level"))
            {
                ForeColor = Color.DimGray,
                Padding = new Padding(2, 0, 6, 0)
            };

            _levelBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 140,
                Height = 13,
                Style = ProgressBarStyle.Continuous
            };

            // 録音操作と状態はすべて 1 本のツールバーにまとめる
            _toolbar.Items.Add(_btnStart);
            _toolbar.Items.Add(_btnStop);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_lblStatus);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_lblTimer);
            _toolbar.Items.Add(_lblActivity);
            _toolbar.Items.Add(new ToolStripControlHost(_levelBar) { Margin = new Padding(0, 0, 0, 0) });

            // 本文は Label で描く。RichTextBox の枠は上辺が白く光って見えるため使わない。
            _txtLive = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Yu Gothic UI", 12f),
                UseMnemonic = false,
                TextAlign = ContentAlignment.TopLeft
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
            editorPanel.Paint += PaintEditorFrame;
            editorPanel.Controls.Add(_lblPending);
            editorPanel.Controls.Add(_txtLive);

            Controls.Add(editorPanel);
            Controls.Add(_toolbar);
            Controls.Add(menu);
        }

        /// <summary>本文の周囲に 1px の淡い枠を描く。RichTextBox の枠の代わり。</summary>
        private void PaintEditorFrame(object sender, PaintEventArgs e)
        {
            var panel = (Panel)sender;
            var r = panel.ClientRectangle;
            var inner = Rectangle.FromLTRB(
                panel.Padding.Left,
                panel.Padding.Top,
                r.Right - panel.Padding.Right,
                r.Bottom - panel.Padding.Bottom);

            using (var fill = new SolidBrush(Color.White))
            {
                e.Graphics.FillRectangle(fill, inner.Left, inner.Top,
                    Math.Max(0, inner.Width), Math.Max(0, inner.Height));
            }

            using (var pen = new Pen(Color.FromArgb(200, 200, 200)))
            {
                e.Graphics.DrawRectangle(pen, inner.Left - 1, inner.Top - 1,
                    inner.Width + 1, inner.Height + 1);
            }
        }

        /// <summary>
        /// ツールバー/ステータスバーの枠を 1px の淡い線だけにする。
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
            private static readonly Color Edge = Color.FromArgb(200, 200, 200);

            public override Color ToolStripBorder => Edge;

            // ハイライトと影は描かせない（明るい線の正体）
            public override Color ToolStripGradientBegin => Color.FromArgb(240, 240, 240);

            public override Color ToolStripGradientMiddle => Color.FromArgb(240, 240, 240);

            public override Color ToolStripGradientEnd => Color.FromArgb(240, 240, 240);
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
        }

        /// <summary>確定モデルのファイル名（フルパスを含む場合もあれば、単なる名前のときもある）。</summary>
        private string CurrentModelFileName()
        {
            return Path.GetFileName(_settings.ModelPath ?? string.Empty);
        }

        /// <summary>ステータスバーとタイトルに現在のモデルと音源を出す。</summary>
        private void UpdateMenuText()
        {
            string model = CurrentModelFileName();
            string source = SourceLabel();
            _miSource.Text = Loc.T("menu.tools.source", source);

            if (!_engine.IsRecording && !_starting)
            {
                Text = Loc.T("app.title.withState", model, source);
            }
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
                    AppSettings.ModelDirectory, ApproxMb(fileName)),
                Loc.T("dialog.modelMissingTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                _lblStatus.Text = Loc.T("status.noModel");
                return false;
            }

            return await DownloadModelAsync(fileName, null);
        }

        /// <summary>指定モデルを非同期でダウンロードする。進捗はステータスバーと任意のコールバックへ出す。</summary>
        private async Task<bool> DownloadModelAsync(string fileName, IProgress<long> progress)
        {
            _lblStatus.Text = Loc.T("settings.model.downloading", fileName);
            _downloading = true;
            UpdateButtons();

            var storeProgress = new Progress<long>(bytes =>
            {
                var info = FindModel(fileName);
                long percent = info != null && info.ApproxBytes > 0 ? bytes * 100 / info.ApproxBytes : 0;
                _lblStatus.Text = Loc.T("settings.model.downloadProgress",
                    fileName, bytes / (1024 * 1024), Math.Min(100, percent));

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
                _lblStatus.Text = Loc.T("settings.model.downloaded", fileName);
                ok = true;
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("モデルのダウンロードに失敗", ex);
                _lblStatus.Text = Loc.T("settings.model.downloadFailed");
                MessageBox.Show(this,
                    Loc.T("dialog.downloadFailedBody", DiagLog.Describe(ex), DiagLog.CurrentPath)
                    + Environment.NewLine + Environment.NewLine
                    + WhisperModelStore.UrlFor(fileName)
                    + Environment.NewLine
                    + AppSettings.ModelDirectory,
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
            _lblStatus.Text = Loc.T("status.starting");
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
                _lblStatus.Text = Loc.T("status.startFailed", ex.Message);
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
            _lblStatus.Text = Loc.T("status.stopping");

            try
            {
                await _engine.StopAsync();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Loc.T("status.stopFailed", ex.Message);
            }

            if (_session != null)
            {
                _session.StoppedAt = DateTime.Now;
            }

            SaveSessionFile(force: true);
            _pendingText = string.Empty;
            RefreshLiveDisplay();

            if (string.IsNullOrEmpty(_errorText))
            {
                _lblStatus.Text = _session != null
                    ? Loc.T("status.stoppedWithFile", Path.GetFileName(_sessionFilePath))
                    : Loc.T("status.stopped");
            }
            else
            {
                _lblStatus.Text = Loc.T("status.stoppedWithErrors");
            }

            UpdateButtons();
        }

        private static string WavPathFor(DateTime startedAt)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinWhisper",
                "wav");
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

            string confirmed = _display.ToString();
            bool pending = !string.IsNullOrEmpty(_pendingText);

            string body = pending && confirmed.Length > 0
                ? confirmed + Environment.NewLine + _pendingText
                : confirmed + _pendingText;

            // 末尾が見えるよう、入りきらない分は先頭を切り落とす
            _txtLive.Text = FitToHeight(body, _txtLive.ClientSize);

            // 暫定行は青く見せたいので、その行だけ別ラベルに載せる
            if (pending && confirmed.Length > 0 && _txtLive.Text == body)
            {
                _txtLive.Text = confirmed;
                _lblPending.Text = _pendingText;
                _lblPending.Height = _lblPending.PreferredHeight;
            }
            else
            {
                _lblPending.Text = string.Empty;
                _lblPending.Height = 0;
            }
        }

        /// <summary>本文が入りきらないとき、末尾が残るよう先頭の行を落とす。</summary>
        private string FitToHeight(string text, Size area)
        {
            if (string.IsNullOrEmpty(text) || area.Width <= 0 || area.Height <= 0)
            {
                return text;
            }

            int usable = area.Height - _lblPending.Height - 8;
            if (_txtLive.PreferredHeight <= usable)
            {
                return text;
            }

            string[] lines = text.Split('\n');
            for (int start = 1; start < lines.Length; start++)
            {
                string candidate = string.Join("\n", lines, start, lines.Length - start);
                using (var g = _txtLive.CreateGraphics())
                {
                    var size = TextRenderer.MeasureText(
                        g, candidate, _txtLive.Font,
                        new Size(area.Width, int.MaxValue), TextFormatFlags.TextBoxControl);

                    if (size.Height <= usable)
                    {
                        return candidate;
                    }
                }
            }

            return lines[lines.Length - 1];
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

                _lblStatus.Text = e.Text;
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
                _lblStatus.Text = Loc.T("status.errorWithLog", e.Message);
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
                _lblStatus.Text = Loc.T("status.textSaveFailed", ex.Message);
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
