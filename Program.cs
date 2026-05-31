using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AppDataLens
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class LargeFile
    {
        public long Sequence;
        public string Path;
        public string Name;
        public long Size;
        public DateTime Modified;
        public string Extension;
    }

    internal sealed class ExtensionStat
    {
        public long Size;
        public long Count;
    }

    internal sealed class ScanIssue
    {
        public string Path;
        public string Message;
    }

    internal sealed class DirectoryNode
    {
        public int Id;
        public string Name;
        public string Path;
        public DirectoryNode Parent;
        public long Size;
        public long OwnFileSize;
        public long FileCount;
        public long DirectoryCount;
        public DateTime Modified;
        public readonly List<string> Errors = new List<string>();
        public readonly List<DirectoryNode> Children = new List<DirectoryNode>();
    }

    internal sealed class ScanResult
    {
        public DirectoryNode Root;
        public readonly Dictionary<int, DirectoryNode> Nodes = new Dictionary<int, DirectoryNode>();
        public readonly Dictionary<string, ExtensionStat> Extensions = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LargeFile> LargestFiles = new List<LargeFile>();
        public readonly List<ScanIssue> Issues = new List<ScanIssue>();
        public long EntriesSeen;
        public long SkippedCount;
        public TimeSpan Elapsed;
    }

    internal sealed class LargeFileComparer : IComparer<LargeFile>
    {
        public int Compare(LargeFile x, LargeFile y)
        {
            if (Object.ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            int bySize = x.Size.CompareTo(y.Size);
            if (bySize != 0)
            {
                return bySize;
            }

            int bySequence = x.Sequence.CompareTo(y.Sequence);
            if (bySequence != 0)
            {
                return bySequence;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path);
        }
    }

    internal sealed class AppDataScanner
    {
        private readonly int _largestLimit;
        private readonly int _issueLimit;
        private readonly SortedSet<LargeFile> _largestFiles = new SortedSet<LargeFile>(new LargeFileComparer());
        private readonly Dictionary<int, DirectoryNode> _nodes = new Dictionary<int, DirectoryNode>();
        private readonly Dictionary<string, ExtensionStat> _extensions = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScanIssue> _issues = new List<ScanIssue>();
        private int _nextId = 1;
        private long _sequence;
        private long _entriesSeen;
        private long _skippedCount;

        public AppDataScanner(int largestLimit, int issueLimit)
        {
            _largestLimit = largestLimit;
            _issueLimit = issueLimit;
        }

        public ScanResult Scan(string rootPath, CancellationToken token, Action<long, string> progress)
        {
            string fullPath = System.IO.Path.GetFullPath(rootPath);
            Stopwatch stopwatch = Stopwatch.StartNew();
            DirectoryNode root = ScanDirectory(fullPath, null, token, progress);
            stopwatch.Stop();

            ScanResult result = new ScanResult();
            result.Root = root;
            result.EntriesSeen = _entriesSeen;
            result.SkippedCount = _skippedCount;
            result.Elapsed = stopwatch.Elapsed;

            foreach (KeyValuePair<int, DirectoryNode> pair in _nodes)
            {
                result.Nodes[pair.Key] = pair.Value;
            }
            foreach (KeyValuePair<string, ExtensionStat> pair in _extensions)
            {
                result.Extensions[pair.Key] = pair.Value;
            }
            result.Issues.AddRange(_issues);
            result.LargestFiles.AddRange(_largestFiles.OrderByDescending(file => file.Size).ThenBy(file => file.Path));
            return result;
        }

        private DirectoryNode ScanDirectory(string path, DirectoryNode parent, CancellationToken token, Action<long, string> progress)
        {
            token.ThrowIfCancellationRequested();

            DirectoryNode node = NewNode(path, parent);
            DirectoryInfo directoryInfo = new DirectoryInfo(path);
            try
            {
                node.Modified = directoryInfo.LastWriteTime;
            }
            catch (Exception ex)
            {
                RecordIssue(path, ex.Message);
                node.Errors.Add(ex.Message);
                return node;
            }

            try
            {
                foreach (FileSystemInfo entry in directoryInfo.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    _entriesSeen++;

                    if (progress != null && _entriesSeen % 250 == 0)
                    {
                        progress(_entriesSeen, entry.FullName);
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception ex)
                    {
                        RecordIssue(entry.FullName, ex.Message);
                        node.Errors.Add(entry.Name + ": " + ex.Message);
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        RecordIssue(entry.FullName, "Skipped reparse point or symbolic link");
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        DirectoryNode child = ScanDirectory(entry.FullName, node, token, progress);
                        node.Children.Add(child);
                        node.Size += child.Size;
                        node.FileCount += child.FileCount;
                        node.DirectoryCount += child.DirectoryCount + 1;
                        if (child.Modified > node.Modified)
                        {
                            node.Modified = child.Modified;
                        }
                    }
                    else
                    {
                        AddFile(node, entry);
                    }
                }
            }
            catch (Exception ex)
            {
                RecordIssue(path, ex.Message);
                node.Errors.Add(ex.Message);
            }

            node.Children.Sort(delegate(DirectoryNode left, DirectoryNode right)
            {
                return right.Size.CompareTo(left.Size);
            });

            return node;
        }

        private DirectoryNode NewNode(string path, DirectoryNode parent)
        {
            DirectoryNode node = new DirectoryNode();
            node.Id = _nextId++;
            node.Path = path;
            node.Parent = parent;
            node.Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (String.IsNullOrEmpty(node.Name))
            {
                node.Name = path;
            }
            _nodes[node.Id] = node;
            return node;
        }

        private void AddFile(DirectoryNode node, FileSystemInfo entry)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(entry.FullName);
                long size = fileInfo.Length;
                DateTime modified = fileInfo.LastWriteTime;

                node.Size += size;
                node.OwnFileSize += size;
                node.FileCount++;
                if (modified > node.Modified)
                {
                    node.Modified = modified;
                }

                string extension = GetExtension(entry.Name);
                ExtensionStat stat;
                if (!_extensions.TryGetValue(extension, out stat))
                {
                    stat = new ExtensionStat();
                    _extensions[extension] = stat;
                }
                stat.Size += size;
                stat.Count++;

                LargeFile file = new LargeFile();
                file.Sequence = _sequence++;
                file.Path = entry.FullName;
                file.Name = entry.Name;
                file.Size = size;
                file.Modified = modified;
                file.Extension = extension;
                _largestFiles.Add(file);
                if (_largestFiles.Count > _largestLimit)
                {
                    _largestFiles.Remove(_largestFiles.Min);
                }
            }
            catch (Exception ex)
            {
                RecordIssue(entry.FullName, ex.Message);
                node.Errors.Add(entry.Name + ": " + ex.Message);
            }
        }

        private static string GetExtension(string name)
        {
            string extension = System.IO.Path.GetExtension(name);
            return String.IsNullOrEmpty(extension) ? "[no extension]" : extension.ToLowerInvariant();
        }

        private void RecordIssue(string path, string message)
        {
            _skippedCount++;
            if (_issues.Count >= _issueLimit)
            {
                return;
            }

            ScanIssue issue = new ScanIssue();
            issue.Path = path;
            issue.Message = message;
            _issues.Add(issue);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _pathBox = new TextBox();
        private readonly Button _appDataButton = new Button();
        private readonly Button _browseButton = new Button();
        private readonly Button _scanButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _exportButton = new Button();
        private readonly Button _openButton = new Button();
        private readonly Label _summaryLabel = new Label();
        private readonly Label _statusLabel = new Label();
        private readonly TreeView _directoryTree = new TreeView();
        private readonly TreemapControl _treemap = new TreemapControl();
        private readonly ListView _largestFiles = new ListView();
        private readonly ListView _extensions = new ListView();
        private readonly ListView _issues = new ListView();

        private ScanResult _result;
        private DirectoryNode _selectedNode;
        private CancellationTokenSource _cancelSource;
        private DateTime _lastProgress = DateTime.MinValue;

        public MainForm()
        {
            Text = "AppDataLens";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 650);
            Size = new Size(1220, 780);
            Font = new Font("Segoe UI", 9F);

            BuildUi();
            _pathBox.Text = DefaultAppDataPath();
            SetIdleState();
        }

        private void BuildUi()
        {
            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 46;
            toolbar.Padding = new Padding(10, 8, 10, 6);
            toolbar.ColumnCount = 8;
            toolbar.RowCount = 1;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));

            Label pathLabel = new Label();
            pathLabel.Text = "目录";
            pathLabel.AutoSize = true;
            pathLabel.Dock = DockStyle.Fill;
            pathLabel.TextAlign = ContentAlignment.MiddleLeft;
            toolbar.Controls.Add(pathLabel, 0, 0);

            _pathBox.Dock = DockStyle.Fill;
            toolbar.Controls.Add(_pathBox, 1, 0);

            SetupButton(_appDataButton, "AppData", AppDataButtonClick);
            SetupButton(_browseButton, "浏览", BrowseButtonClick);
            SetupButton(_scanButton, "扫描", ScanButtonClick);
            SetupButton(_stopButton, "停止", StopButtonClick);
            SetupButton(_exportButton, "导出 CSV", ExportButtonClick);
            SetupButton(_openButton, "打开位置", OpenButtonClick);
            toolbar.Controls.Add(_appDataButton, 2, 0);
            toolbar.Controls.Add(_browseButton, 3, 0);
            toolbar.Controls.Add(_scanButton, 4, 0);
            toolbar.Controls.Add(_stopButton, 5, 0);
            toolbar.Controls.Add(_exportButton, 6, 0);
            toolbar.Controls.Add(_openButton, 7, 0);
            Controls.Add(toolbar);

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Top;
            statusPanel.Height = 34;
            statusPanel.Padding = new Padding(10, 0, 10, 8);
            _summaryLabel.Dock = DockStyle.Fill;
            _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Dock = DockStyle.Right;
            _statusLabel.Width = 390;
            _statusLabel.TextAlign = ContentAlignment.MiddleRight;
            statusPanel.Controls.Add(_summaryLabel);
            statusPanel.Controls.Add(_statusLabel);
            Controls.Add(statusPanel);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 430;
            split.Panel1.Padding = new Padding(10, 0, 5, 10);
            split.Panel2.Padding = new Padding(5, 0, 10, 10);
            Controls.Add(split);

            _directoryTree.Dock = DockStyle.Fill;
            _directoryTree.HideSelection = false;
            _directoryTree.Font = new Font("Consolas", 9F);
            _directoryTree.AfterSelect += DirectoryTreeAfterSelect;
            split.Panel1.Controls.Add(_directoryTree);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(tabs);

            TabPage mapPage = new TabPage("占用图");
            _treemap.Dock = DockStyle.Fill;
            _treemap.NodeSelected += TreemapNodeSelected;
            mapPage.Controls.Add(_treemap);
            tabs.TabPages.Add(mapPage);

            TabPage largestPage = new TabPage("最大文件");
            SetupList(_largestFiles);
            _largestFiles.Columns.Add("大小", 110, HorizontalAlignment.Right);
            _largestFiles.Columns.Add("扩展名", 90, HorizontalAlignment.Left);
            _largestFiles.Columns.Add("最近修改", 140, HorizontalAlignment.Left);
            _largestFiles.Columns.Add("路径", 720, HorizontalAlignment.Left);
            _largestFiles.DoubleClick += LargestFilesDoubleClick;
            largestPage.Controls.Add(_largestFiles);
            tabs.TabPages.Add(largestPage);

            TabPage extensionPage = new TabPage("扩展名");
            SetupList(_extensions);
            _extensions.Columns.Add("扩展名", 150, HorizontalAlignment.Left);
            _extensions.Columns.Add("大小", 130, HorizontalAlignment.Right);
            _extensions.Columns.Add("文件数", 110, HorizontalAlignment.Right);
            _extensions.Columns.Add("占比", 80, HorizontalAlignment.Right);
            extensionPage.Controls.Add(_extensions);
            tabs.TabPages.Add(extensionPage);

            TabPage issuePage = new TabPage("跳过项");
            SetupList(_issues);
            _issues.Columns.Add("原因", 290, HorizontalAlignment.Left);
            _issues.Columns.Add("路径", 720, HorizontalAlignment.Left);
            issuePage.Controls.Add(_issues);
            tabs.TabPages.Add(issuePage);
        }

        private static void SetupButton(Button button, string text, EventHandler handler)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 0, 3, 0);
            button.Click += handler;
        }

        private static void SetupList(ListView list)
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
        }

        private void AppDataButtonClick(object sender, EventArgs e)
        {
            _pathBox.Text = DefaultAppDataPath();
        }

        private void BrowseButtonClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择要扫描的目录";
                dialog.SelectedPath = Directory.Exists(_pathBox.Text) ? _pathBox.Text : DefaultAppDataPath();
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private async void ScanButtonClick(object sender, EventArgs e)
        {
            string path = _pathBox.Text.Trim().Trim('"');
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "目录不存在：" + path, "AppDataLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ResetViews();
            SetScanningState();
            _cancelSource = new CancellationTokenSource();
            AppDataScanner scanner = new AppDataScanner(500, 3000);

            try
            {
                ScanResult result = await Task.Run(delegate
                {
                    return scanner.Scan(path, _cancelSource.Token, ProgressUpdate);
                });
                FinishScan(result);
            }
            catch (OperationCanceledException)
            {
                _summaryLabel.Text = "扫描已停止，没有保存不完整结果";
                _statusLabel.Text = "已停止";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "扫描失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _summaryLabel.Text = "扫描失败";
                _statusLabel.Text = "出错";
            }
            finally
            {
                _cancelSource = null;
                SetIdleState();
            }
        }

        private void StopButtonClick(object sender, EventArgs e)
        {
            if (_cancelSource != null)
            {
                _cancelSource.Cancel();
                _statusLabel.Text = "正在停止...";
            }
        }

        private void ExportButtonClick(object sender, EventArgs e)
        {
            if (_result == null)
            {
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出 CSV";
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dialog.FileName = "appdata_lens_report.csv";
                dialog.DefaultExt = "csv";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    ExportCsv(_result, dialog.FileName);
                    MessageBox.Show(this, "已导出：" + dialog.FileName, "AppDataLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OpenButtonClick(object sender, EventArgs e)
        {
            if (_selectedNode != null)
            {
                OpenPath(_selectedNode.Path);
            }
        }

        private void LargestFilesDoubleClick(object sender, EventArgs e)
        {
            if (_largestFiles.SelectedItems.Count == 0)
            {
                return;
            }

            string path = _largestFiles.SelectedItems[0].Tag as string;
            if (String.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch
            {
                OpenPath(System.IO.Path.GetDirectoryName(path));
            }
        }

        private void DirectoryTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            DirectoryNode node = e.Node.Tag as DirectoryNode;
            if (node == null)
            {
                return;
            }

            _selectedNode = node;
            _openButton.Enabled = true;
            _treemap.SetNode(_result, node);
        }

        private void TreemapNodeSelected(DirectoryNode node)
        {
            if (node == null)
            {
                return;
            }

            TreeNode treeNode = FindTreeNode(_directoryTree.Nodes, node);
            if (treeNode != null)
            {
                treeNode.EnsureVisible();
                _directoryTree.SelectedNode = treeNode;
            }
        }

        private void ProgressUpdate(long count, string currentPath)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastProgress).TotalMilliseconds < 250)
            {
                return;
            }
            _lastProgress = now;

            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(delegate
                {
                    string display = currentPath;
                    if (display.Length > 88)
                    {
                        display = "..." + display.Substring(display.Length - 85);
                    }
                    _statusLabel.Text = "已查看 " + count.ToString("N0") + " 项 | " + display;
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void FinishScan(ScanResult result)
        {
            _result = result;
            PopulateDirectoryTree();
            PopulateLargestFiles();
            PopulateExtensions();
            PopulateIssues();

            _summaryLabel.Text =
                "总大小 " + FormatSize(result.Root.Size) +
                " | 文件 " + result.Root.FileCount.ToString("N0") +
                " | 目录 " + result.Root.DirectoryCount.ToString("N0") +
                " | 跳过 " + result.SkippedCount.ToString("N0") +
                " | 耗时 " + result.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
            _statusLabel.Text = "完成，共查看 " + result.EntriesSeen.ToString("N0") + " 项";

            if (_directoryTree.Nodes.Count > 0)
            {
                _directoryTree.SelectedNode = _directoryTree.Nodes[0];
                _directoryTree.Nodes[0].Expand();
            }
        }

        private void PopulateDirectoryTree()
        {
            _directoryTree.BeginUpdate();
            try
            {
                _directoryTree.Nodes.Clear();
                if (_result == null || _result.Root == null)
                {
                    return;
                }

                TreeNode root = CreateTreeNode(_result.Root);
                _directoryTree.Nodes.Add(root);
            }
            finally
            {
                _directoryTree.EndUpdate();
            }
        }

        private TreeNode CreateTreeNode(DirectoryNode node)
        {
            TreeNode treeNode = new TreeNode(DirectoryLabel(node));
            treeNode.Tag = node;
            foreach (DirectoryNode child in node.Children)
            {
                treeNode.Nodes.Add(CreateTreeNode(child));
            }
            return treeNode;
        }

        private string DirectoryLabel(DirectoryNode node)
        {
            string percent = _result == null ? "0.0%" : Percent(node.Size, _result.Root.Size);
            string name = node.Parent == null ? node.Path : node.Name;
            return String.Format("{0,10}  {1,6}  {2}", FormatSize(node.Size), percent, name);
        }

        private void PopulateLargestFiles()
        {
            _largestFiles.BeginUpdate();
            try
            {
                _largestFiles.Items.Clear();
                foreach (LargeFile file in _result.LargestFiles)
                {
                    ListViewItem item = new ListViewItem(FormatSize(file.Size));
                    item.SubItems.Add(file.Extension);
                    item.SubItems.Add(FormatTime(file.Modified));
                    item.SubItems.Add(file.Path);
                    item.Tag = file.Path;
                    _largestFiles.Items.Add(item);
                }
            }
            finally
            {
                _largestFiles.EndUpdate();
            }
        }

        private void PopulateExtensions()
        {
            _extensions.BeginUpdate();
            try
            {
                _extensions.Items.Clear();
                IEnumerable<KeyValuePair<string, ExtensionStat>> rows =
                    _result.Extensions.OrderByDescending(pair => pair.Value.Size);
                foreach (KeyValuePair<string, ExtensionStat> pair in rows)
                {
                    ListViewItem item = new ListViewItem(pair.Key);
                    item.SubItems.Add(FormatSize(pair.Value.Size));
                    item.SubItems.Add(pair.Value.Count.ToString("N0"));
                    item.SubItems.Add(Percent(pair.Value.Size, _result.Root.Size));
                    _extensions.Items.Add(item);
                }
            }
            finally
            {
                _extensions.EndUpdate();
            }
        }

        private void PopulateIssues()
        {
            _issues.BeginUpdate();
            try
            {
                _issues.Items.Clear();
                foreach (ScanIssue issue in _result.Issues)
                {
                    ListViewItem item = new ListViewItem(issue.Message);
                    item.SubItems.Add(issue.Path);
                    _issues.Items.Add(item);
                }
            }
            finally
            {
                _issues.EndUpdate();
            }
        }

        private void ResetViews()
        {
            _result = null;
            _selectedNode = null;
            _directoryTree.Nodes.Clear();
            _largestFiles.Items.Clear();
            _extensions.Items.Clear();
            _issues.Items.Clear();
            _treemap.SetNode(null, null);
            _summaryLabel.Text = "扫描进行中，遇到无权限目录会自动跳过";
            _statusLabel.Text = "正在扫描...";
        }

        private void SetScanningState()
        {
            _scanButton.Enabled = false;
            _stopButton.Enabled = true;
            _exportButton.Enabled = false;
            _openButton.Enabled = false;
            _browseButton.Enabled = false;
            _appDataButton.Enabled = false;
        }

        private void SetIdleState()
        {
            _scanButton.Enabled = true;
            _stopButton.Enabled = false;
            _browseButton.Enabled = true;
            _appDataButton.Enabled = true;
            _exportButton.Enabled = _result != null;
            _openButton.Enabled = _selectedNode != null;
            if (_result == null && String.IsNullOrEmpty(_summaryLabel.Text))
            {
                _summaryLabel.Text = "选择 AppData 目录后开始扫描";
                _statusLabel.Text = "准备扫描";
            }
            else if (_result == null && _statusLabel.Text == "")
            {
                _statusLabel.Text = "准备扫描";
            }
        }

        private static string DefaultAppDataPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(local))
            {
                string parent = System.IO.Path.GetDirectoryName(local);
                if (!String.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    return parent;
                }
            }

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!String.IsNullOrEmpty(roaming))
            {
                string parent = System.IO.Path.GetDirectoryName(roaming);
                if (!String.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    return parent;
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = System.IO.Path.Combine(userProfile, "AppData");
            return Directory.Exists(appData) ? appData : userProfile;
        }

        private static void OpenPath(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开位置：" + ex.Message, "AppDataLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static TreeNode FindTreeNode(TreeNodeCollection nodes, DirectoryNode target)
        {
            foreach (TreeNode node in nodes)
            {
                if (Object.ReferenceEquals(node.Tag, target))
                {
                    return node;
                }

                TreeNode found = FindTreeNode(node.Nodes, target);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static IEnumerable<DirectoryNode> WalkDirectories(DirectoryNode root)
        {
            Stack<DirectoryNode> stack = new Stack<DirectoryNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DirectoryNode node = stack.Pop();
                yield return node;

                for (int index = node.Children.Count - 1; index >= 0; index--)
                {
                    stack.Push(node.Children[index]);
                }
            }
        }

        private static void ExportCsv(ScanResult result, string filename)
        {
            using (StreamWriter writer = new StreamWriter(filename, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("type,name,path,size_bytes,size,file_count,dir_count,percent,modified,message");
                foreach (DirectoryNode node in WalkDirectories(result.Root))
                {
                    WriteCsvRow(writer, new string[]
                    {
                        "directory",
                        node.Name,
                        node.Path,
                        node.Size.ToString(),
                        FormatSize(node.Size),
                        node.FileCount.ToString(),
                        node.DirectoryCount.ToString(),
                        Percent(node.Size, result.Root.Size),
                        FormatTime(node.Modified),
                        String.Join("; ", node.Errors.ToArray())
                    });
                }

                foreach (LargeFile file in result.LargestFiles)
                {
                    WriteCsvRow(writer, new string[]
                    {
                        "large_file",
                        file.Name,
                        file.Path,
                        file.Size.ToString(),
                        FormatSize(file.Size),
                        "",
                        "",
                        Percent(file.Size, result.Root.Size),
                        FormatTime(file.Modified),
                        file.Extension
                    });
                }

                foreach (KeyValuePair<string, ExtensionStat> pair in result.Extensions.OrderByDescending(item => item.Value.Size))
                {
                    WriteCsvRow(writer, new string[]
                    {
                        "extension",
                        pair.Key,
                        "",
                        pair.Value.Size.ToString(),
                        FormatSize(pair.Value.Size),
                        pair.Value.Count.ToString(),
                        "",
                        Percent(pair.Value.Size, result.Root.Size),
                        "",
                        ""
                    });
                }

                foreach (ScanIssue issue in result.Issues)
                {
                    WriteCsvRow(writer, new string[]
                    {
                        "issue",
                        "",
                        issue.Path,
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        issue.Message
                    });
                }
            }
        }

        private static void WriteCsvRow(StreamWriter writer, string[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    writer.Write(",");
                }
                writer.Write(EscapeCsv(fields[i]));
            }
            writer.WriteLine();
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
            {
                return "";
            }
            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        internal static string FormatSize(long size)
        {
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = size;
            int unitIndex = 0;
            while (value >= 1024D && unitIndex < units.Length - 1)
            {
                value /= 1024D;
                unitIndex++;
            }

            if (unitIndex == 0)
            {
                return ((long)value).ToString("N0") + " " + units[unitIndex];
            }
            return value.ToString("0.0") + " " + units[unitIndex];
        }

        internal static string FormatTime(DateTime value)
        {
            return value == DateTime.MinValue ? "" : value.ToString("yyyy-MM-dd HH:mm");
        }

        internal static string Percent(long part, long total)
        {
            if (total <= 0)
            {
                return "0.0%";
            }
            return ((double)part / total * 100D).ToString("0.0") + "%";
        }
    }

    internal sealed class TreemapControl : Control
    {
        private readonly Color[] _palette = new Color[]
        {
            Color.FromArgb(37, 99, 235),
            Color.FromArgb(5, 150, 105),
            Color.FromArgb(217, 119, 6),
            Color.FromArgb(220, 38, 38),
            Color.FromArgb(124, 58, 237),
            Color.FromArgb(8, 145, 178),
            Color.FromArgb(101, 163, 13),
            Color.FromArgb(190, 18, 60),
            Color.FromArgb(79, 70, 229),
            Color.FromArgb(15, 118, 110),
            Color.FromArgb(194, 65, 12),
            Color.FromArgb(147, 51, 234)
        };

        private readonly List<TreemapTile> _tiles = new List<TreemapTile>();
        private ScanResult _result;
        private DirectoryNode _node;

        public event Action<DirectoryNode> NodeSelected;

        public TreemapControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(248, 250, 252);
            ForeColor = Color.White;
        }

        public void SetNode(ScanResult result, DirectoryNode node)
        {
            _result = result;
            _node = node;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            _tiles.Clear();

            if (_result == null || _node == null)
            {
                DrawCenterText(e.Graphics, "扫描完成后会显示目录占用图");
                return;
            }

            if (_node.Size <= 0)
            {
                DrawCenterText(e.Graphics, "该目录没有可统计的文件");
                return;
            }

            List<TreemapEntry> entries = new List<TreemapEntry>();
            foreach (DirectoryNode child in _node.Children.OrderByDescending(child => child.Size))
            {
                if (child.Size > 0)
                {
                    entries.Add(new TreemapEntry(child.Name, child.Size, child));
                }
            }
            if (_node.OwnFileSize > 0)
            {
                entries.Add(new TreemapEntry("[当前目录文件]", _node.OwnFileSize, null));
            }

            entries = entries.OrderByDescending(entry => entry.Size).ToList();
            if (entries.Count > 48)
            {
                long otherSize = entries.Skip(48).Sum(entry => entry.Size);
                entries = entries.Take(48).ToList();
                entries.Add(new TreemapEntry("[其他]", otherSize, null));
            }

            Rectangle area = new Rectangle(6, 6, Math.Max(1, Width - 12), Math.Max(1, Height - 12));
            DrawTiles(e.Graphics, entries, area);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int index = _tiles.Count - 1; index >= 0; index--)
            {
                TreemapTile tile = _tiles[index];
                if (tile.Node != null && tile.Bounds.Contains(e.Location))
                {
                    Action<DirectoryNode> handler = NodeSelected;
                    if (handler != null)
                    {
                        handler(tile.Node);
                    }
                    return;
                }
            }
        }

        private void DrawTiles(Graphics graphics, List<TreemapEntry> entries, Rectangle area)
        {
            long total = entries.Sum(entry => entry.Size);
            if (total <= 0 || area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            bool horizontal = area.Width >= area.Height;
            float offset = 0F;

            using (Pen border = new Pen(Color.White, 2F))
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    TreemapEntry entry = entries[index];
                    float ratio = (float)((double)entry.Size / total);
                    Rectangle rectangle;
                    if (horizontal)
                    {
                        int tileWidth = index == entries.Count - 1
                            ? area.Width - (int)Math.Round(offset)
                            : Math.Max(1, (int)Math.Round(area.Width * ratio));
                        rectangle = new Rectangle(area.Left + (int)Math.Round(offset), area.Top, tileWidth, area.Height);
                        offset += tileWidth;
                    }
                    else
                    {
                        int tileHeight = index == entries.Count - 1
                            ? area.Height - (int)Math.Round(offset)
                            : Math.Max(1, (int)Math.Round(area.Height * ratio));
                        rectangle = new Rectangle(area.Left, area.Top + (int)Math.Round(offset), area.Width, tileHeight);
                        offset += tileHeight;
                    }

                    if (rectangle.Width < 2 || rectangle.Height < 2)
                    {
                        continue;
                    }

                    Color color = _palette[index % _palette.Length];
                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        graphics.FillRectangle(brush, rectangle);
                    }
                    graphics.DrawRectangle(border, rectangle);

                    TreemapTile tile = new TreemapTile();
                    tile.Bounds = rectangle;
                    tile.Node = entry.Node;
                    _tiles.Add(tile);

                    if (rectangle.Width >= 92 && rectangle.Height >= 44)
                    {
                        Rectangle labelBounds = new Rectangle(rectangle.Left + 8, rectangle.Top + 7, rectangle.Width - 14, rectangle.Height - 10);
                        string label = entry.Name + Environment.NewLine + MainForm.FormatSize(entry.Size);
                        TextRenderer.DrawText(
                            graphics,
                            label,
                            Font,
                            labelBounds,
                            Color.White,
                            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                    }
                }
            }
        }

        private void DrawCenterText(Graphics graphics, string text)
        {
            TextRenderer.DrawText(
                graphics,
                text,
                Font,
                ClientRectangle,
                Color.FromArgb(71, 85, 105),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private sealed class TreemapEntry
        {
            public readonly string Name;
            public readonly long Size;
            public readonly DirectoryNode Node;

            public TreemapEntry(string name, long size, DirectoryNode node)
            {
                Name = name;
                Size = size;
                Node = node;
            }
        }

        private sealed class TreemapTile
        {
            public Rectangle Bounds;
            public DirectoryNode Node;
        }
    }
}
