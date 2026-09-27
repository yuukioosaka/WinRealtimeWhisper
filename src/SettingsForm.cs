using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinWhisper
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
        private Label _lblModelState;
        private Label _lblPaths;

        private Button _btnDownload;
        private Button _btnOpenFolder;
        private Button _btnOk;
        private Button _btnCancel;
        private ProgressBar _progress;
        private Label _lblProgress;

        private bool _busy;

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
            ClientSize = new Size(560, 344);

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
                RowCount = 7
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

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

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(3, 8, 3, 3),
                ForeColor = Color.DimGray,
                Text = Loc.T("settings.audio.note")
            };
            layout.Controls.Add(note, 0, 4);
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
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
            _cmbLanguage.Items.Add(new LanguageItem(WinWhisper.UiLanguage.Japanese));
            _cmbLanguage.Items.Add(new LanguageItem(WinWhisper.UiLanguage.English));
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
            public LanguageItem(WinWhisper.UiLanguage language)
            {
                Language = language;
            }

            public WinWhisper.UiLanguage Language { get; private set; }

            public override string ToString()
            {
                return Loc.DisplayName(Language);
            }
        }

        private TabPage BuildStoragePage()
        {
            var page = new TabPage(Loc.T("settings.tab.storage")) { Padding = new Padding(12), BackColor = SystemColors.Control };

            _lblPaths = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray
            };

            _btnOpenFolder = new Button { Text = Loc.T("settings.storage.openFolder"), Width = 150, Height = 28, Dock = DockStyle.Top };
            _btnOpenFolder.Click += (s, e) => OpenFolder(HistoryStore.RootDirectory);

            var inner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inner.Controls.Add(_btnOpenFolder, 0, 0);
            inner.Controls.Add(_lblPaths, 0, 1);

            page.Controls.Add(inner);
            return page;
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
            UpdateModelState();
            UpdateEnabled();
            UpdatePaths();
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

        private void UpdatePaths()
        {
            _lblPaths.Text = Loc.T("settings.storage.paths",
                HistoryStore.RootDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinWhisper", "wav"),
                AppSettings.ModelDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWhisper", "logs"));
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

        private void SelectLanguageInCombo(WinWhisper.UiLanguage language)
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
                MessageBox.Show(this, ex.Message, "WinWhisper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
        }
    }
}
