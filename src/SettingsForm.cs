using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 設定ダイアログ。デバイス・モデル・履歴の保存先などをここに集約する。
    /// 「OK」で確定したときだけ Settings が書き換わる。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly AppSettings _settings;

        private ComboBox _cmbOutput;
        private ComboBox _cmbInput;
        private CheckBox _chkUseOutput;
        private CheckBox _chkUseInput;
        private ComboBox _cmbModel;
        private ComboBox _cmbLanguage;
        private ComboBox _cmbWhisperLanguage;
        private ComboBox _cmbCycle;
        private NumericUpDown _numSilence;
        private CheckBox _chkPreferGpu;
        private Label _lblModelState;

        private Button _btnDownload;
        private TextBox _txtHistoryDir;
        private TextBox _txtWavDir;
        private TextBox _txtModelDir;
        private TextBox _txtLogDir;
        private TextBox _txtVttDir;
        private CheckBox _chkVtt;
        private CheckBox _chkRealtime;
        private CheckBox _chkRealtimeCors;
        private NumericUpDown _numRealtimePort;
        private Button _btnHistoryDir;
        private Button _btnWavDir;
        private Button _btnModelDir;
        private Button _btnLogDir;
        private Button _btnVttDir;
        private Button _btnOk;
        private Button _btnCancel;
        private ProgressBar _progress;
        private Label _lblProgress;

        private bool _busy;

        /// <summary>区切りのサイクル（秒）。短いほど表示が速いが、精度は落ちる。</summary>
        internal static readonly int[] CycleChoices = { 5, 10, 15, 30 };

        /// <summary>認識させる言語（Whisper に渡すコード）。--language と同じ範囲。</summary>
        internal static readonly string[,] WhisperLanguageChoices =
        {
            { "ja", "settings.audio.whisperLang.ja" },
            { "en", "settings.audio.whisperLang.en" },
            { "zh", "settings.audio.whisperLang.zh" },
            { "ko", "settings.audio.whisperLang.ko" },
            { "auto", "settings.audio.whisperLang.auto" }
        };

        /// <summary>区切りサイクルのコンボの 1 項目。</summary>
        private sealed class CycleItem
        {
            public CycleItem(int seconds)
            {
                Seconds = seconds;
            }

            public int Seconds { get; private set; }

            public override string ToString()
            {
                return Loc.T("settings.audio.cycleItem", Seconds);
            }
        }

        /// <summary>認識言語コンボの 1 項目。</summary>
        private sealed class WhisperLanguageItem
        {
            public WhisperLanguageItem(string code, string labelKey)
            {
                Code = code;
                LabelKey = labelKey;
            }

            public string Code { get; private set; }
            public string LabelKey { get; private set; }

            public override string ToString()
            {
                return string.IsNullOrEmpty(LabelKey) ? Code : Loc.T(LabelKey);
            }
        }

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            BuildUi();
            LoadFromSettings();
        }

        /// <summary>ダウンロードは MainForm 側で実行する(モデルストアの進捗を一元管理するため)。</summary>
        public Func<string, Task<bool>> DownloadHandler;

        private void BuildUi()
        {
            Text = Loc.T("settings.title");
            Font = new Font("Yu Gothic UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 580);

            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(14, 6)
            };

            tabs.TabPages.Add(BuildGeneralPage());
            tabs.TabPages.Add(BuildAudioPage());
            tabs.TabPages.Add(BuildModelPage());
            tabs.TabPages.Add(BuildStoragePage());

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 8) };

            _progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Width = 200,
                Height = 16,
                Location = new Point(12, 16),
                Visible = false
            };
            _lblProgress = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(220, 18),
                Text = string.Empty
            };

            _btnOk = new Button { Text = Loc.T("settings.ok"), Width = 92, Height = 30, DialogResult = DialogResult.OK };
            _btnCancel = new Button { Text = Loc.T("settings.cancel"), Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 4, 2, 4)
            };
            buttons.Controls.Add(_btnOk);
            buttons.Controls.Add(_btnCancel);

            bottom.Controls.Add(_progress);
            bottom.Controls.Add(_lblProgress);
            bottom.Controls.Add(buttons);

            Controls.Add(tabs);
            Controls.Add(bottom);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            _btnOk.Click += (s, e) => { if (_busy) { DialogResult = DialogResult.None; } };
        }

        private TabPage BuildAudioPage()
        {
            var page = new TabPage(Loc.T("settings.tab.audio")) { Padding = new Padding(12), BackColor = SystemColors.Control };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 13
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < layout.RowCount; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            _chkUseOutput = new CheckBox { Text = Loc.T("settings.audio.useOutput"), AutoSize = true, Checked = true };
            _chkUseOutput.CheckedChanged += (s, e) => UpdateEnabled();
            layout.Controls.Add(_chkUseOutput, 0, 0);
            layout.SetColumnSpan(_chkUseOutput, 2);

            layout.Controls.Add(NewLabel(Loc.T("settings.audio.outputDevice")), 0, 1);
            _cmbOutput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 3, 8) };
            layout.Controls.Add(_cmbOutput, 1, 1);

            _chkUseInput = new CheckBox { Text = Loc.T("settings.audio.useInput"), AutoSize = true, Checked = true };
            _chkUseInput.CheckedChanged += (s, e) => UpdateEnabled();
            layout.Controls.Add(_chkUseInput, 0, 2);
            layout.SetColumnSpan(_chkUseInput, 2);

            layout.Controls.Add(NewLabel(Loc.T("settings.audio.inputDevice")), 0, 3);
            _cmbInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 3, 8) };
            layout.Controls.Add(_cmbInput, 1, 3);

            layout.Controls.Add(NewLabel(Loc.T("settings.audio.cycle")), 0, 4);
            _cmbCycle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Margin = new Padding(3, 2, 3, 8) };
            foreach (int seconds in CycleChoices)
            {
                _cmbCycle.Items.Add(new CycleItem(seconds));
            }

            layout.Controls.Add(_cmbCycle, 1, 4);

            layout.Controls.Add(NewLabel(Loc.T("settings.audio.silence")), 0, 5);
            _numSilence = new NumericUpDown
            {
                DecimalPlaces = 2,
                Minimum = 0.20M,
                Maximum = 3.00M,
                Increment = 0.05M,
                Width = 90,
                Margin = new Padding(3, 2, 3, 8)
            };
            layout.Controls.Add(_numSilence, 1, 5);

            layout.Controls.Add(NewLabel(Loc.T("settings.audio.whisperLanguage")), 0, 6);
            _cmbWhisperLanguage = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 200,
                Margin = new Padding(3, 2, 3, 8)
            };
            for (int i = 0; i < WhisperLanguageChoices.GetLength(0); i++)
            {
                _cmbWhisperLanguage.Items.Add(new WhisperLanguageItem(
                    WhisperLanguageChoices[i, 0], WhisperLanguageChoices[i, 1]));
            }
            layout.Controls.Add(_cmbWhisperLanguage, 1, 6);

            _chkPreferGpu = new CheckBox
            {
                Text = Loc.T("settings.audio.preferGpu"),
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 2)
            };
            layout.Controls.Add(_chkPreferGpu, 0, 7);
            layout.SetColumnSpan(_chkPreferGpu, 2);

            var gpuNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 0, 3, 8),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.audio.gpuNote")
            };
            layout.Controls.Add(gpuNote, 0, 8);
            layout.SetColumnSpan(gpuNote, 2);

            var silenceNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 0, 3, 8),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.audio.silenceNote")
            };
            layout.Controls.Add(silenceNote, 0, 9);
            layout.SetColumnSpan(silenceNote, 2);

            var cycleNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 0, 3, 8),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.audio.cycleNote")
            };
            layout.Controls.Add(cycleNote, 0, 10);
            layout.SetColumnSpan(cycleNote, 2);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 8, 3, 3),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.audio.note")
            };
            layout.Controls.Add(note, 0, 11);
            layout.SetColumnSpan(note, 2);

            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildModelPage()
        {
            var page = new TabPage(Loc.T("settings.tab.model")) { Padding = new Padding(12), BackColor = SystemColors.Control };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < layout.RowCount; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            layout.Controls.Add(NewLabel(Loc.T("settings.model.label")), 0, 0);

            var modelPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            _cmbModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 250 };
            foreach (var model in WhisperModelStore.Models)
            {
                _cmbModel.Items.Add(model.DisplayName);
            }
            _cmbModel.TextChanged += (s, e) => UpdateModelState();
            _btnDownload = new Button { Text = Loc.T("settings.model.download"), Width = 104, Height = 25 };
            _btnDownload.Click += async (s, e) => await DownloadAsync();
            modelPanel.Controls.Add(_cmbModel);
            modelPanel.Controls.Add(_btnDownload);
            layout.Controls.Add(modelPanel, 1, 0);

            _lblModelState = new Label
            {
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 3),
                ForeColor = Color.DimGray
            };
            layout.Controls.Add(_lblModelState, 0, 1);
            layout.SetColumnSpan(_lblModelState, 2);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 8, 3, 3),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.model.note")
            };
            layout.Controls.Add(note, 0, 2);
            layout.SetColumnSpan(note, 2);

            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildGeneralPage()
        {
            var page = new TabPage(Loc.T("settings.tab.general")) { Padding = new Padding(12), BackColor = SystemColors.Control };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(NewLabel(Loc.T("settings.general.language")), 0, 0);
            _cmbLanguage = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 200,
                Margin = new Padding(3, 2, 3, 8)
            };
            _cmbLanguage.Items.Add(new LanguageItem(WinRealtimeWhisper.UiLanguage.Japanese));
            _cmbLanguage.Items.Add(new LanguageItem(WinRealtimeWhisper.UiLanguage.English));
            layout.Controls.Add(_cmbLanguage, 1, 0);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 8, 3, 3),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.general.note")
            };
            layout.Controls.Add(note, 0, 1);
            layout.SetColumnSpan(note, 2);

            page.Controls.Add(layout);
            return page;
        }

        /// <summary>言語コンボの 1 項目。</summary>
        private sealed class LanguageItem
        {
            public LanguageItem(WinRealtimeWhisper.UiLanguage language)
            {
                Language = language;
            }

            public WinRealtimeWhisper.UiLanguage Language { get; private set; }

            public override string ToString()
            {
                return Loc.DisplayName(Language);
            }
        }

        private TabPage BuildStoragePage()
        {
            var page = new TabPage(Loc.T("settings.tab.storage")) { Padding = new Padding(12), BackColor = SystemColors.Control };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 9
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 9; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            _txtHistoryDir = AddPathRow(layout, 0, "settings.storage.textLabel",
                _settings.HistoryDirectory, _settings.ResolveHistoryDirectory,
                out _btnHistoryDir);
            _txtWavDir = AddPathRow(layout, 1, "settings.storage.wavLabel",
                _settings.WavDirectory, _settings.ResolveWavDirectory,
                out _btnWavDir);
            _txtModelDir = AddPathRow(layout, 2, "settings.storage.modelLabel",
                _settings.ModelDirectory, _settings.ResolveModelDirectory,
                out _btnModelDir);
            _txtLogDir = AddPathRow(layout, 3, "settings.storage.logLabel",
                _settings.LogDirectory, _settings.ResolveLogDirectory,
                out _btnLogDir);

            _txtVttDir = AddPathRow(layout, 4, "settings.storage.vttLabel",
                _settings.VttDirectory, _settings.ResolveVttDirectory,
                out _btnVttDir);

            _chkVtt = new CheckBox
            {
                Text = Loc.T("settings.storage.vttEnabled"),
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 2)
            };
            layout.Controls.Add(_chkVtt, 0, 5);
            layout.SetColumnSpan(_chkVtt, 3);

            var vttNote = new Label
            {
                Text = Loc.T("settings.storage.vttNote"),
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 0, 3, 10)
            };
            layout.Controls.Add(vttNote, 0, 6);
            layout.SetColumnSpan(vttNote, 3);

            _chkRealtime = new CheckBox
            {
                Text = Loc.T("settings.realtime.enabled"),
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 2)
            };
            _chkRealtime.CheckedChanged += (s, e) => UpdateRealtimeState();
            layout.Controls.Add(_chkRealtime, 0, 7);
            layout.SetColumnSpan(_chkRealtime, 3);

            var portHost = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(3, 2, 3, 2)
            };
            portHost.Controls.Add(NewLabel(Loc.T("settings.realtime.port")));
            _numRealtimePort = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Width = 80,
                Value = AppSettings.DefaultRealtimePort
            };
            portHost.Controls.Add(_numRealtimePort);
            layout.Controls.Add(portHost, 0, 8);
            layout.SetColumnSpan(portHost, 3);

            _chkRealtimeCors = new CheckBox
            {
                Text = Loc.T("settings.realtime.cors"),
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 2)
            };
            layout.Controls.Add(_chkRealtimeCors, 0, 9);
            layout.SetColumnSpan(_chkRealtimeCors, 3);

            var corsNote = new Label
            {
                Text = Loc.T("settings.realtime.corsNote"),
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 0, 3, 10)
            };
            layout.Controls.Add(corsNote, 0, 10);
            layout.SetColumnSpan(corsNote, 3);

            page.Controls.Add(layout);
            return page;
        }

        /// <summary>「ラベル｜テキストボックス｜参照ボタン」の 1 行を追加して、テキストボックスを返す。</summary>
        private TextBox AddPathRow(TableLayoutPanel layout, int row, string labelKey,
            string currentValue, Func<string> resolve, out Button browse)
        {
            layout.Controls.Add(NewLabel(Loc.T(labelKey)), 0, row);

            // 未設定でも実際に使われる既定のパスを見せる。
            string shown = string.IsNullOrWhiteSpace(currentValue) ? resolve() : currentValue;

            var box = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(3, 3, 6, 6),
                Text = shown ?? string.Empty
            };
            layout.Controls.Add(box, 1, row);

            var button = new Button
            {
                Text = Loc.T("settings.storage.browse"),
                Width = 90,
                Height = 25,
                Margin = new Padding(0, 2, 0, 4)
            };
            button.Click += (s, e) => BrowseFolder(box, Loc.T(labelKey), resolve);
            layout.Controls.Add(button, 2, row);

            browse = button;
            return box;
        }

        /// <summary>フォルダーを選ばせる。選んだらテキストボックスに入れる。</summary>
        private void BrowseFolder(TextBox target, string title, Func<string> resolve)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.ShowNewFolderButton = true;

                string current = (target.Text ?? string.Empty).Trim();
                if (current.Length == 0)
                {
                    current = resolve != null ? resolve() : string.Empty;
                }

                if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.SelectedPath;
                }
            }
        }

        private static Label NewLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        }

        private void LoadFromSettings()
        {
            var outputDevices = new List<AudioDeviceItem>();
            var inputDevices = new List<AudioDeviceItem>();

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        using (device)
                        {
                            outputDevices.Add(new AudioDeviceItem(
                                device.ID, "🔊 " + device.FriendlyName, AudioSourceKind.SystemLoopback));
                        }
                    }

                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    {
                        using (device)
                        {
                            inputDevices.Add(new AudioDeviceItem(
                                device.ID, "🎤 " + device.FriendlyName, AudioSourceKind.Microphone));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("デバイス列挙に失敗", ex);
            }

            outputDevices.Insert(0, new AudioDeviceItem(string.Empty, Loc.T("device.defaultOutput"), AudioSourceKind.SystemLoopback));
            inputDevices.Insert(0, new AudioDeviceItem(string.Empty, Loc.T("device.defaultInput"), AudioSourceKind.Microphone));

            foreach (var item in outputDevices) _cmbOutput.Items.Add(item);
            foreach (var item in inputDevices) _cmbInput.Items.Add(item);

            _cmbOutput.SelectedIndex = IndexOfId(outputDevices, _settings.OutputDeviceId);
            _cmbInput.SelectedIndex = IndexOfId(inputDevices, _settings.InputDeviceId);

            _chkUseOutput.Checked = _settings.SourceKind != AudioSourceKind.Microphone;
            _chkUseInput.Checked = _settings.SourceKind != AudioSourceKind.SystemLoopback;

            SelectLanguageInCombo(_settings.ResolveUiLanguage());
            SelectModelInCombo(_settings.ModelPath);
            SelectCycleInCombo(_settings.MaxChunkSeconds);

            decimal silence = (decimal)_settings.SilenceSplitSeconds;
            if (silence < _numSilence.Minimum) silence = _numSilence.Minimum;
            if (silence > _numSilence.Maximum) silence = _numSilence.Maximum;
            _numSilence.Value = silence;

            SelectWhisperLanguageInCombo(_settings.WhisperLanguage);

            _chkPreferGpu.Checked = _settings.PreferGpu;

            // 未設定でも実際に使われる既定のパスを初期表示する。
            _txtHistoryDir.Text = string.IsNullOrWhiteSpace(_settings.HistoryDirectory)
                ? _settings.ResolveHistoryDirectory()
                : _settings.HistoryDirectory;
            _txtWavDir.Text = string.IsNullOrWhiteSpace(_settings.WavDirectory)
                ? _settings.ResolveWavDirectory()
                : _settings.WavDirectory;
            _txtModelDir.Text = string.IsNullOrWhiteSpace(_settings.ModelDirectory)
                ? _settings.ResolveModelDirectory()
                : _settings.ModelDirectory;
            _txtLogDir.Text = string.IsNullOrWhiteSpace(_settings.LogDirectory)
                ? _settings.ResolveLogDirectory()
                : _settings.LogDirectory;
            _txtVttDir.Text = string.IsNullOrWhiteSpace(_settings.VttDirectory)
                ? _settings.ResolveVttDirectory()
                : _settings.VttDirectory;
            _chkVtt.Checked = _settings.VttEnabled;

            _chkRealtime.Checked = _settings.RealtimeServerEnabled;
            _chkRealtimeCors.Checked = _settings.RealtimeAllowBrowserOrigins;
            int port = _settings.ResolveRealtimePort();
            _numRealtimePort.Value = port >= _numRealtimePort.Minimum && port <= _numRealtimePort.Maximum
                ? port
                : AppSettings.DefaultRealtimePort;
            UpdateRealtimeState();

            UpdateModelState();
            UpdateEnabled();
        }

        private static int IndexOfId(List<AudioDeviceItem> items, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return 0;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

        private void UpdateEnabled()
        {
            _cmbOutput.Enabled = _chkUseOutput.Checked && !_busy;
            _cmbInput.Enabled = _chkUseInput.Checked && !_busy;
        }

        /// <summary>コンボの選択から ggml のファイル名を取り出す。</summary>
        private string SelectedModelFileName()
        {
            string text = _cmbModel.Text ?? string.Empty;
            if (text.Length == 0)
            {
                return WhisperModelStore.DefaultModelFileName;
            }

            foreach (var model in WhisperModelStore.Models)
            {
                if (text.StartsWith(model.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    return model.FileName;
                }
            }

            return Path.GetFileName(text);
        }

        private void SelectLanguageInCombo(WinRealtimeWhisper.UiLanguage language)
        {
            for (int i = 0; i < _cmbLanguage.Items.Count; i++)
            {
                var item = _cmbLanguage.Items[i] as LanguageItem;
                if (item != null && item.Language == language)
                {
                    _cmbLanguage.SelectedIndex = i;
                    return;
                }
            }

            _cmbLanguage.SelectedIndex = 0;
        }

        private void SelectModelInCombo(string modelPath)
        {
            string fileName = string.IsNullOrEmpty(modelPath)
                ? WhisperModelStore.DefaultModelFileName
                : Path.GetFileName(modelPath);

            foreach (object item in _cmbModel.Items)
            {
                string text = item as string;
                if (text != null && text.StartsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbModel.SelectedItem = item;
                    return;
                }
            }

            _cmbModel.Text = modelPath ?? string.Empty;
        }

        /// <summary>認識言語に一致する項目を選ぶ。未知のコードはそのまま追加して選ぶ。</summary>
        private void SelectWhisperLanguageInCombo(string code)
        {
            string value = (code ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                value = "ja";
            }

            for (int i = 0; i < _cmbWhisperLanguage.Items.Count; i++)
            {
                var item = _cmbWhisperLanguage.Items[i] as WhisperLanguageItem;
                if (item != null && string.Equals(item.Code, value, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbWhisperLanguage.SelectedIndex = i;
                    return;
                }
            }

            // 表に無いコード（設定ファイルの手編集）も失わないようにする。
            var custom = new WhisperLanguageItem(value, null);
            _cmbWhisperLanguage.Items.Add(custom);
            _cmbWhisperLanguage.SelectedItem = custom;
        }

        /// <summary>現在のサイクルに最も近い選択肢を選ぶ。</summary>
        private void SelectCycleInCombo(double maxChunkSeconds)
        {
            int best = 0;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < _cmbCycle.Items.Count; i++)
            {
                var item = _cmbCycle.Items[i] as CycleItem;
                if (item == null)
                {
                    continue;
                }

                double diff = Math.Abs(item.Seconds - maxChunkSeconds);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = i;
                }
            }

            _cmbCycle.SelectedIndex = best;
        }

        /// <summary>WebSocket サーバーを有効にしたときだけ、ポートを編集できるようにする。</summary>
        private void UpdateRealtimeState()
        {
            if (_numRealtimePort != null)
            {
                _numRealtimePort.Enabled = _chkRealtime.Checked && !_busy;
            }

            if (_chkRealtimeCors != null)
            {
                _chkRealtimeCors.Enabled = _chkRealtime.Checked && !_busy;
            }
        }

        private void UpdateModelState()
        {
            string fileName = SelectedModelFileName();
            string path = WhisperModelStore.PathFor(fileName);

            if (WhisperModelStore.Exists(fileName))
            {
                long mb = new FileInfo(path).Length / (1024 * 1024);
                _lblModelState.Text = Loc.T("settings.model.present", mb, path);
                _lblModelState.ForeColor = Color.SeaGreen;
            }
            else
            {
                _lblModelState.Text = Loc.T("settings.model.absent", path);
                _lblModelState.ForeColor = Color.Firebrick;
            }
        }

        private async Task DownloadAsync()
        {
            string fileName = SelectedModelFileName();

            if (DownloadHandler == null)
            {
                return;
            }

            SetBusy(true);
            _lblProgress.Text = Loc.T("settings.model.downloading", fileName);

            var progress = new Progress<long>(bytes =>
            {
                var info = FindModel(fileName);
                long percent = info != null && info.ApproxBytes > 0 ? bytes * 100 / info.ApproxBytes : 0;
                _progress.Value = (int)Math.Max(0, Math.Min(100, percent));
                _lblProgress.Text = Loc.T("settings.model.downloadProgress",
                    fileName, bytes / (1024 * 1024), Math.Min(100, percent));
            });

            try
            {
                bool ok = await DownloadHandler(fileName);
                _lblProgress.Text = ok
                    ? Loc.T("settings.model.downloaded", fileName)
                    : Loc.T("settings.model.downloadFailed");
            }
            catch (Exception ex)
            {
                _lblProgress.Text = Loc.T("settings.model.downloadError", ex.Message);
            }
            finally
            {
                SetBusy(false);
                UpdateModelState();
            }
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

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _progress.Visible = busy;
            _progress.Value = 0;
            _btnDownload.Enabled = !busy;
            _cmbModel.Enabled = !busy;
            _btnOk.Enabled = !busy;
            UpdateEnabled();
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "WinRealtimeWhisper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>テキストボックスの内容を、空なら null として返す。</summary>
        private static string TrimmedOrNull(string text)
        {
            string value = (text ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>OK が押されたときだけ呼ぶ。ダイアログの内容を設定へ反映する。</summary>
        public void ApplyToSettings()
        {
            var output = _cmbOutput.SelectedItem as AudioDeviceItem;
            var input = _cmbInput.SelectedItem as AudioDeviceItem;

            _settings.OutputDeviceId = output != null ? output.Id : string.Empty;
            _settings.InputDeviceId = input != null ? input.Id : string.Empty;

            if (_chkUseOutput.Checked && _chkUseInput.Checked)
            {
                _settings.SourceKind = AudioSourceKind.Both;
            }
            else if (_chkUseInput.Checked)
            {
                _settings.SourceKind = AudioSourceKind.Microphone;
            }
            else
            {
                _settings.SourceKind = AudioSourceKind.SystemLoopback;
            }

            string fileName = SelectedModelFileName();
            _settings.ModelPath = WhisperModelStore.PathFor(fileName);

            var language = _cmbLanguage.SelectedItem as LanguageItem;
            if (language != null)
            {
                _settings.UiLanguage = Loc.CodeFor(language.Language);
            }

            var cycle = _cmbCycle.SelectedItem as CycleItem;
            if (cycle != null)
            {
                _settings.MaxChunkSeconds = cycle.Seconds;
            }

            _settings.SilenceSplitSeconds = (double)_numSilence.Value;
            _settings.PreferGpu = _chkPreferGpu.Checked;

            var whisperLanguage = _cmbWhisperLanguage.SelectedItem as WhisperLanguageItem;
            if (whisperLanguage != null)
            {
                _settings.WhisperLanguage = whisperLanguage.Code;
            }

            // 既定のパスと同じなら「未設定」として保存する。
            // 表示は既定値でも、保存値は空にして将来の既定変更に追従させる。
            _settings.HistoryDirectory = KeepIfNotDefault(_txtHistoryDir.Text, DefaultHistoryDirectory());
            _settings.WavDirectory = KeepIfNotDefault(_txtWavDir.Text, DefaultWavDirectory());
            _settings.ModelDirectory = KeepIfNotDefault(_txtModelDir.Text, DefaultModelDirectory());
            _settings.LogDirectory = KeepIfNotDefault(_txtLogDir.Text, DefaultLogDirectory());
            _settings.VttDirectory = KeepIfNotDefault(_txtVttDir.Text, DefaultVttDirectory());
            _settings.VttEnabled = _chkVtt.Checked;
            _settings.RealtimeServerEnabled = _chkRealtime.Checked;
            _settings.RealtimeServerPort = (int)_numRealtimePort.Value;
            _settings.RealtimeAllowBrowserOrigins = _chkRealtimeCors.Checked;
        }

        /// <summary>入力が既定パスと同じなら空（未設定）を返す。</summary>
        private static string KeepIfNotDefault(string text, string defaultPath)
        {
            string value = TrimmedOrNull(text);
            if (value == null)
            {
                return string.Empty;
            }

            return string.Equals(value, defaultPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;
        }

        // 既定値は設定に依存せずに求める必要がある。
        // (設定が変更済みだと ResolveXxx() は変更後の値を返すため)
        private static string DefaultHistoryDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinRealtimeWhisper",
                "history");
        }

        private static string DefaultWavDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinRealtimeWhisper",
                "wav");
        }

        private static string DefaultModelDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinRealtimeWhisper",
                "models");
        }

        private static string DefaultLogDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinRealtimeWhisper",
                "logs");
        }

        private static string DefaultVttDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinRealtimeWhisper",
                "transcripts");
        }
    }
}
