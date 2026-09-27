using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WinWhisper
{
    /// <summary>
    /// 過去の履歴を一覧して中身を確認するウィンドウ。
    /// 一覧のダブルクリック、または「エディタで開く」でテキストファイルを開く。
    /// </summary>
    internal sealed class HistoryForm : Form
    {
        private ListBox _list;
        private RichTextBox _view;
        private Label _info;
        private Button _btnOpen;
        private Button _btnRefresh;

        public HistoryForm()
        {
            BuildUi();
            RefreshList();
        }

        private void BuildUi()
        {
            Text = Loc.T("history.title");
            Font = new Font("Yu Gothic UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(880, 560);
            MinimumSize = new Size(560, 360);
            ShowInTaskbar = false;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6
            };

            split.Panel1.Padding = new Padding(4);

            var header = new Panel { Dock = DockStyle.Top, Height = 30 };
            var btnRefresh = new Button { Text = Loc.T("history.refresh"), Width = 64, Height = 24, Location = new Point(2, 3) };
            btnRefresh.Click += (s, e) => RefreshList();
            _btnOpen = new Button { Text = Loc.T("history.openInEditor"), AutoSize = true, Height = 24, Location = new Point(72, 3) };
            _btnOpen.Click += (s, e) => OpenSelected();
            _btnOpen.Enabled = false;
            header.Controls.Add(btnRefresh);
            header.Controls.Add(_btnOpen);
            _btnRefresh = btnRefresh;

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _list.SelectedIndexChanged += (s, e) => OnSelected();
            _list.DoubleClick += (s, e) => OpenSelected();

            split.Panel1.Controls.Add(_list);
            split.Panel1.Controls.Add(header);

            split.Panel2.Padding = new Padding(4);

            _info = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Color.DimGray,
                Text = string.Empty
            };

            _view = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle
            };

            split.Panel2.Controls.Add(_view);
            split.Panel2.Controls.Add(_info);

            Controls.Add(split);

            Shown += (s, e) =>
            {
                int width = split.ClientSize.Width;
                if (width >= 500)
                {
                    try
                    {
                        split.Panel2MinSize = 240;
                        split.SplitterDistance = 300;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            };
        }

        private void RefreshList()
        {
            string selected = null;
            var current = _list.SelectedItem as HistoryItem;
            if (current != null)
            {
                selected = current.Path;
            }

            _list.Items.Clear();
            foreach (string path in HistoryStore.ListSessionFiles())
            {
                _list.Items.Add(new HistoryItem(path));
            }

            if (_list.Items.Count == 0)
            {
                _view.Clear();
                _info.Text = Loc.T("history.empty");
                _btnOpen.Enabled = false;
                return;
            }

            int index = 0;
            if (selected != null)
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var item = (HistoryItem)_list.Items[i];
                    if (string.Equals(item.Path, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            _list.SelectedIndex = index;
        }

        private void OnSelected()
        {
            var item = _list.SelectedItem as HistoryItem;
            if (item == null)
            {
                return;
            }

            _btnOpen.Enabled = true;
            _info.Text = item.Detail;

            try
            {
                _view.Text = HistoryStore.ReadAllText(item.Path);
                _view.SelectionStart = 0;
                _view.ScrollToCaret();
            }
            catch (Exception ex)
            {
                _view.Text = Loc.T("history.readFailed", ex.Message);
            }
        }

        private void OpenSelected()
        {
            var item = _list.SelectedItem as HistoryItem;
            if (item == null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, DiagLog.Describe(ex), "WinWhisper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>一覧に出す 1 行分。表示は時刻、ツールチップと詳細にファイル名を出す。</summary>
        private sealed class HistoryItem
        {
            public HistoryItem(string path)
            {
                Path = path;
                FileName = System.IO.Path.GetFileName(path);

                // session_yyyyMMdd_HHmmss.txt から読みやすい形にする
                string stamp = System.IO.Path.GetFileNameWithoutExtension(path);
                DateTime parsed;
                if (stamp.StartsWith("session_", StringComparison.OrdinalIgnoreCase)
                    && DateTime.TryParseExact(stamp.Substring(8), "yyyyMMdd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out parsed))
                {
                    Display = parsed.ToString("yyyy/MM/dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    Display = stamp;
                }
            }

            public string Path { get; private set; }
            public string FileName { get; private set; }
            public string Display { get; private set; }

            public string Detail
            {
                get { return Display + "   " + FileName; }
            }

            public override string ToString()
            {
                return Display;
            }
        }
    }
}
