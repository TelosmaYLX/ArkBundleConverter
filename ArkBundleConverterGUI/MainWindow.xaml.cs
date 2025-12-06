using Microsoft.Win32; // For OpenFileDialog
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics; // For Process
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
// Required if using System.Windows.Forms.FolderBrowserDialog
using Forms = System.Windows.Forms;

namespace ArkBundleConverterGUI_WPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string? _selectedInputDir = null;
        private List<string> _selectedInputFiles = new List<string>();
        private string? _selectedOutputDir = null;
        // Adjust this path as needed, or add logic to find it
        private string _cliExecutablePath = "ArkBundleConverterCLI.exe";

        // New: queue and timer for batching UI log updates
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private readonly DispatcherTimer _logFlushTimer;
        private const int _maxBatchLines = 1000; // maximum lines to flush per tick
        private const int _logTrimLimit = 200_000; // maximum characters to keep in log TextBox

        // New: tracking failures for export
        private readonly List<(string FileName, string ResourceName, string Reason)> _failedFiles = new List<(string, string, string)>();
        private int _totalFiles = 0;
        private int _successFiles = 0;
        private int _failedFilesCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            LocateCliExecutable(); // Try to find the CLI tool
            UpdateInputDisplay();

            // Initialize and start timer to flush logs to UI at a controlled rate
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _logFlushTimer.Tick += LogFlushTimer_Tick;
            _logFlushTimer.Start();
        }

        private void LogFlushTimer_Tick(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            var sb = new StringBuilder();
            int lines = 0;

            while (lines < _maxBatchLines && _logQueue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
                // Also update progress if the line contains progress info
                UpdateProgressFromLog(line);
                lines++;
            }

            if (sb.Length > 0)
            {
                // Append once and scroll once to avoid per-line UI work
                txtLog.AppendText(sb.ToString());
                txtLog.ScrollToEnd();

                // Optionally trim the log if it grows too big to avoid memory/GC pressure
                if (txtLog.Text.Length > _logTrimLimit)
                {
                    // Keep only the last half
                    int startIndex = txtLog.Text.Length - (_logTrimLimit / 2);
                    txtLog.Text = txtLog.Text.Substring(startIndex);
                    txtLog.CaretIndex = txtLog.Text.Length;
                }
            }
        }

        private void LocateCliExecutable()
        {
            string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                string potentialPath = Path.Combine(baseDir, _cliExecutablePath);
                if (File.Exists(potentialPath))
                {
                    _cliExecutablePath = potentialPath;
                    EnqueueLogMessage($"找到转换程序: {_cliExecutablePath}");
                    SetUIEnabled(true); // Enable UI if found
                }
                else
                {
                    string errorMsg = $"未能在 '{baseDir}' 找到转换程序 '{_cliExecutablePath}'。\n请确保它与 GUI 程序在同一目录，或手动修改路径。";
                    EnqueueLogMessage(errorMsg, true);
                    System.Windows.MessageBox.Show(errorMsg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetUIEnabled(false); // Disable UI controls if not found
                    btnStart.IsEnabled = false;
                }
            }
            else
            {
                string errorMsg = $"无法确定应用程序基目录，无法定位 '{_cliExecutablePath}'。";
                EnqueueLogMessage(errorMsg, true);
                System.Windows.MessageBox.Show(errorMsg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                SetUIEnabled(false);
                btnStart.IsEnabled = false;
            }
        }

        private void BtnSelectFiles_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "AssetBundle Files (*.ab;*.bin)|*.ab;*.bin|All Files (*.*)|*.*",
                Title = "选择一个或多个输入文件"
            };

            if (ofd.ShowDialog() == true) // WPF uses bool?
            {
                _selectedInputFiles = ofd.FileNames.ToList();
                _selectedInputDir = null; // Clear directory selection
                UpdateInputDisplay();
            }
        }

        private void BtnSelectInputDir_Click(object sender, RoutedEventArgs e)
        {
            // Using System.Windows.Forms FolderBrowserDialog (requires reference)
            using (var fbd = new Forms.FolderBrowserDialog())
            {
                fbd.Description = "选择包含 Bundle 文件的输入目录";
                Forms.DialogResult result = fbd.ShowDialog(); // Returns Forms.DialogResult

                if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    _selectedInputDir = fbd.SelectedPath;
                    _selectedInputFiles.Clear(); // Clear file selection
                    UpdateInputDisplay();
                }
            }
        }

        private void BtnSelectOutputDir_Click(object sender, RoutedEventArgs e)
        {
            // Using System.Windows.Forms FolderBrowserDialog
            using (var fbd = new Forms.FolderBrowserDialog())
            {
                fbd.Description = "选择输出目录";
                Forms.DialogResult result = fbd.ShowDialog();

                if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    _selectedOutputDir = fbd.SelectedPath;
                    txtOutputDir.Text = _selectedOutputDir; // Update TextBox
                }
            }
        }

        private void UpdateInputDisplay()
        {
            if (_selectedInputDir != null)
            {
                txtInputPaths.Text = $"目录: {_selectedInputDir}";
            }
            else if (_selectedInputFiles.Count > 0)
            {
                // Display multiple files nicely
                txtInputPaths.Text = string.Join(Environment.NewLine, _selectedInputFiles);
            }
            else
            {
                txtInputPaths.Text = "(未选择)";
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            // Validation
            if (string.IsNullOrEmpty(_selectedInputDir) && _selectedInputFiles.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择输入文件或输入目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_selectedOutputDir))
            {
                System.Windows.MessageBox.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!File.Exists(_cliExecutablePath))
            {
                System.Windows.MessageBox.Show($"无法找到核心转换程序: {_cliExecutablePath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Reset tracking info
            _failedFiles.Clear();
            _totalFiles = 0;
            _successFiles = 0;
            _failedFilesCount = 0;

            // Determine subcommand to run. Default to 'uncompress'.
            // If you add UI to choose mode, replace this with selected mode.
            string selectedSubcommand = "uncompress";

            // Build arguments
            StringBuilder argsBuilder = new StringBuilder();
            argsBuilder.Append(selectedSubcommand).Append(' ');
            if (_selectedInputDir != null)
            {
                argsBuilder.Append($"--input-dir \"{_selectedInputDir}\" ");
            }
            else
            {
                // Handle multiple files for System.CommandLine
                argsBuilder.Append("--input-files ");
                foreach (string file in _selectedInputFiles)
                {
                    // Quote paths with spaces
                    argsBuilder.Append($"\"{file}\" ");
                }
            }
            argsBuilder.Append($"--output-dir \"{_selectedOutputDir}\"");

            // Prepare UI and start process
            // Clear log safely on UI thread
            Dispatcher.Invoke(() => {
                txtLog.Clear();
                progressBar.Value = 0;
                txtProgress.Text = "0/0 (0%)";
                // Only disable the Start button to allow user to change selections during conversion
                btnStart.IsEnabled = false;
                btnOpenOutput.IsEnabled = false;
                LogMessage("开始执行转换...");
                LogMessage($"命令行: {_cliExecutablePath} {argsBuilder.ToString()}");
            });

            // Run process asynchronously
            Task.Run(() => ExecuteCliProcess(argsBuilder.ToString()));
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedOutputDir) || !Directory.Exists(_selectedOutputDir))
            {
                System.Windows.MessageBox.Show("输出目录无效或不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _selectedOutputDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开文件夹: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // New: Export log and failures
        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog()
                {
                    FileName = "arkbundle_log",
                    DefaultExt = ".txt",
                    Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*"
                };

                bool? result = dlg.ShowDialog(this);
                if (result != true)
                    return;

                // Ensure we capture the full content of the log TextBox
                string allText = string.Empty;
                // If txtLog exists, prefer its Text. This returns the entire content irrespective of scroll/selection.
                if (txtLog != null)
                {
                    allText = txtLog.Text ?? string.Empty;
                }

                // Fallback: if there's any internal buffer field called _log or logBuilder, try to use it via reflection
                if (string.IsNullOrEmpty(allText))
                {
                    // Try common field names
                    var fieldNames = new[] { "_log", "logBuilder", "_logBuilder", "Log" };
                    foreach (var name in fieldNames)
                    {
                        var f = this.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (f != null)
                        {
                            var val = f.GetValue(this);
                            if (val is StringBuilder sb)
                            {
                                allText = sb.ToString();
                                break;
                            }
                            if (val is string s)
                            {
                                allText = s;
                                break;
                            }
                        }
                    }
                }

                // Write to file using UTF8
                File.WriteAllText(dlg.FileName, allText, Encoding.UTF8);

                System.Windows.MessageBox.Show(this, "日志已导出: " + dlg.FileName, "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "导出日志失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteCliProcess(string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _cliExecutablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using (Process process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (s, args) =>
                    {
                        if (args.Data != null)
                        {
                            // Enqueue raw log lines; flush timer running on UI thread will batch updates
                            _logQueue.Enqueue(args.Data);

                            // Try to parse progress and failure messages immediately on background thread
                            // Pattern for failure assumed: "处理文件 'name' 时发生错误: ..." 或类似
                            ParseCliLineForFailures(args.Data);
                        }
                    };
                    process.ErrorDataReceived += (s, args) =>
                    {
                        if (args.Data != null)
                        {
                            _logQueue.Enqueue($"[错误] {args.Data}");
                            ParseCliLineForFailures(args.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                    // Use Invoke to ensure UI updates happen on UI thread immediately
                    Dispatcher.Invoke(() =>
                    {
                        // 恢复 Start 按钮，允许重复转换
                        btnStart.IsEnabled = true;
                        // 恢复为之前的行为：始终允许点击“打开目录”按钮（与之前逻辑一致）
                        btnOpenOutput.IsEnabled = true;

                        // 确保选择按钮保持启用，以便用户可以更改选择
                        btnSelectFiles.IsEnabled = true;
                        btnSelectInputDir.IsEnabled = true;
                        btnSelectOutputDir.IsEnabled = true;

                        // Flush any remaining queued lines immediately
                        LogFlushTimer_Tick(null, EventArgs.Empty);

                        // After flush, append a detailed summary including failed file list
                        var summarySb = new StringBuilder();
                        summarySb.AppendLine();
                        summarySb.AppendLine($"转换完成: 总文件 {_totalFiles}, 成功 {_successFiles}, 失败 {_failedFilesCount}");
                        if (_failedFilesCount > 0)
                        {
                            summarySb.AppendLine();
                            summarySb.AppendLine("失败文件列表:");
                            lock (_failedFiles)
                            {
                                int idx = 1;
                                foreach (var f in _failedFiles)
                                {
                                    var fileLine = new StringBuilder();
                                    fileLine.AppendFormat("{0}. {1}", idx, string.IsNullOrEmpty(f.FileName) ? "(unknown)" : f.FileName);
                                    if (!string.IsNullOrEmpty(f.ResourceName) && f.ResourceName != "(unknown)")
                                    {
                                        fileLine.AppendFormat("  资源: {0}", f.ResourceName);
                                    }
                                    if (!string.IsNullOrEmpty(f.Reason))
                                    {
                                        fileLine.AppendFormat("  原因: {0}", f.Reason);
                                    }
                                    summarySb.AppendLine(fileLine.ToString());
                                    idx++;
                                }
                            }
                        }

                        txtLog.AppendText(summarySb.ToString());
                        txtLog.ScrollToEnd();
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    EnqueueLogMessage($"启动或执行转换进程时出错: {ex.Message}", true);
                    // 发生错误时也允许重试
                    btnStart.IsEnabled = true;
                    btnSelectFiles.IsEnabled = true;
                    btnSelectInputDir.IsEnabled = true;
                    btnSelectOutputDir.IsEnabled = true;
                    btnOpenOutput.IsEnabled = false;
                });
            }
        }

        private void ParseCliLineForFailures(string line)
        {
            // Try to extract failure information from CLI output lines.
            // This is heuristic and depends on CLI wording used in Program.cs.
            // Examples to handle:
            //  - "处理文件 'name' 时发生错误: <reason>"
            //  - "处理文件 'name' 时发生异常: <reason>"
            //  - "✓ 成功转换: filename -> output"

            try
            {
                if (line.Contains("进度:"))
                {
                    // Progress line, update totals
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"进度:\s*(\d+)/(\d+)\s*\((\d+)%\)");
                    if (match.Success)
                    {
                        if (int.TryParse(match.Groups[1].Value, out int current) &&
                            int.TryParse(match.Groups[2].Value, out int total))
                        {
                            // update total only if it's larger
                            _totalFiles = Math.Max(_totalFiles, total);
                        }
                    }
                }

                if (line.Contains("成功转换"))
                {
                    // increment success count
                    System.Threading.Interlocked.Increment(ref _successFiles);
                }

                if (line.Contains("发生错误") || line.Contains("发生异常"))
                {
                    // crude parse: extract file name between single quotes if present
                    string fileName = "(unknown)";
                    string resourceName = "(unknown)";
                    string reason = line;

                    var m = System.Text.RegularExpressions.Regex.Match(line, @"文件\s*'(?<f>[^']+)'\s*时");
                    if (m.Success) fileName = m.Groups["f"].Value;

                    // Try extract reason after colon
                    var idx = line.IndexOf(":");
                    if (idx >= 0 && idx + 1 < line.Length) reason = line.Substring(idx + 1).Trim();

                    lock (_failedFiles)
                    {
                        _failedFiles.Add((fileName, resourceName, reason));
                        _failedFilesCount = _failedFiles.Count;
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        private void UpdateProgressFromLog(string logLine)
        {
            // Pattern: "进度: X/Y (Z%)"
            var match = System.Text.RegularExpressions.Regex.Match(logLine, @"进度:\s*(\d+)/(\d+)\s*\((\d+)%\)");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int current) &&
                    int.TryParse(match.Groups[2].Value, out int total) &&
                    int.TryParse(match.Groups[3].Value, out int percentage))
                {
                    // This method is run on UI thread by the flush timer
                    progressBar.Maximum = total;
                    progressBar.Value = current;
                    txtProgress.Text = $"{current}/{total} ({percentage}%)";
                }
            }
        }

        // Log messages to the TextBox (must be called on UI thread or marshalled)
        private void LogMessage(string message, bool isError = false)
        {
            // Keep legacy direct logging for immediate messages; uses UI thread
            string prefix = isError ? "[错误] " : "";
            txtLog.AppendText($"{prefix}{message}{Environment.NewLine}");
            txtLog.ScrollToEnd(); // Keep the last line visible
        }

        // Enqueue messages from non-UI threads
        private void EnqueueLogMessage(string message, bool isError = false)
        {
            if (isError) message = "[错误] " + message;
            _logQueue.Enqueue(message);
        }

        // Enable/Disable UI controls
        private void SetUIEnabled(bool enabled)
        {
            btnSelectFiles.IsEnabled = enabled;
            btnSelectInputDir.IsEnabled = enabled;
            btnSelectOutputDir.IsEnabled = enabled;
            btnStart.IsEnabled = enabled;
        }

        private void txtInputPaths_TextChanged(object sender, TextChangedEventArgs e)
        { }

        /// <summary>
        /// 拖动标题栏来移动窗口
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.Source == this)
            {
                try
                {
                    this.DragMove();
                }
                catch
                {
                    // 忽略拖动失败异常
                }
            }
        }

        /// <summary>
        /// 拖动标题栏来移动窗口
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                this.DragMove();
            }
            catch
            {
                // 忽略拖动失败异常
            }
        }

        /// <summary>
        /// 最小化按钮
        /// </summary>
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.MinimizeWindow(this);
        }

        /// <summary>
        /// 最大化/还原按钮（仅保留窗口状态切换逻辑，删除 Content 修改）
        /// </summary>
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == System.Windows.WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }

           
        }

        /// <summary>
        /// 关闭按钮
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.CloseWindow(this);
        }

        #region Thumb handlers for inner rounded rectangle resizing
        private void Thumb_TopLeft_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;
            double newHeight = this.Height - e.VerticalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; this.Left += e.HorizontalChange; }
            if (newHeight >= this.MinHeight) { this.Height = newHeight; this.Top += e.VerticalChange; }
        }

        private void Thumb_TopRight_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;
            double newHeight = this.Height - e.VerticalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; }
            if (newHeight >= this.MinHeight) { this.Height = newHeight; this.Top += e.VerticalChange; }
        }

        private void Thumb_BottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;
            double newHeight = this.Height + e.VerticalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; this.Left += e.HorizontalChange; }
            if (newHeight >= this.MinHeight) { this.Height = newHeight; }
        }

        private void Thumb_BottomRight_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;
            double newHeight = this.Height + e.VerticalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; }
            if (newHeight >= this.MinHeight) { this.Height = newHeight; }
        }

        private void Thumb_Left_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; this.Left += e.HorizontalChange; }
        }

        private void Thumb_Right_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;
            if (newWidth >= this.MinWidth) { this.Width = newWidth; }
        }

        private void Thumb_Top_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height - e.VerticalChange;
            if (newHeight >= this.MinHeight) { this.Height = newHeight; this.Top += e.VerticalChange; }
        }

        private void Thumb_Bottom_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height + e.VerticalChange;
            if (newHeight >= this.MinHeight) { this.Height = newHeight; }
        }
        #endregion
    }
}