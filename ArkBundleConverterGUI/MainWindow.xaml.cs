using Microsoft.Win32; // For OpenFileDialog
using System;
using System.Collections.Generic;
using System.Diagnostics; // For Process
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private string _cliExecutablePath = "ArkAbConverter.exe";

        public MainWindow()
        {
            InitializeComponent();
            LocateCliExecutable(); // Try to find the CLI tool
            UpdateInputDisplay();

            // Ensure maximize/restore icon is updated when window state changes
            this.StateChanged += MainWindow_StateChanged;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // Maximize → show "还原" icon
            if (this.WindowState == WindowState.Maximized)
            {
                btnMaximize.Content = "🗗"; 
            }
            else
            {
                btnMaximize.Content = "🗖";  
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
                    LogMessage($"找到转换程序: {_cliExecutablePath}");
                    SetUIEnabled(true); // Enable UI if found
                }
                else
                {
                    string errorMsg = $"未能在 '{baseDir}' 找到转换程序 '{_cliExecutablePath}'。\n请确保它与 GUI 程序在同一目录，或手动修改路径。";
                    LogMessage(errorMsg, true);
                    System.Windows.MessageBox.Show(errorMsg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetUIEnabled(false); // Disable UI controls if not found
                    btnStart.IsEnabled = false;
                }
            }
            else
            {
                string errorMsg = $"无法确定应用程序基目录，无法定位 '{_cliExecutablePath}'。";
                LogMessage(errorMsg, true);
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
            txtLog.Clear();
            progressBar.Value = 0;
            txtProgress.Text = "0/0 (0%)";
            // Only disable the Start button to allow user to change selections during conversion
            btnStart.IsEnabled = false;
            btnOpenOutput.IsEnabled = false;
            LogMessage("开始执行转换...");
            LogMessage($"命令行: {_cliExecutablePath} {argsBuilder.ToString()}");

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
                            Dispatcher.InvokeAsync(() => 
                            {
                                LogMessage(args.Data);
                                UpdateProgressFromLog(args.Data);
                            });
                        }
                    };
                    process.ErrorDataReceived += (s, args) =>
                    {
                        if (args.Data != null)
                        {
                            Dispatcher.InvokeAsync(() => LogMessage($"错误: {args.Data}", true));
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
                        // Enable OpenOutput only if output dir exists and conversion succeeded
                        bool enableOpen = false;
                        try
                        {
                            enableOpen = process.ExitCode == 0 && !string.IsNullOrEmpty(_selectedOutputDir) && Directory.Exists(_selectedOutputDir);
                        }
                        catch { enableOpen = false; }
                        btnOpenOutput.IsEnabled = enableOpen;

                        // Ensure selection buttons remain enabled so user can change selection
                        btnSelectFiles.IsEnabled = true;
                        btnSelectInputDir.IsEnabled = true;
                        btnSelectOutputDir.IsEnabled = true;
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LogMessage($"启动或执行转换进程时出错: {ex.Message}", true);
                    // 发生错误时也允许重试
                    btnStart.IsEnabled = true;
                    btnSelectFiles.IsEnabled = true;
                    btnSelectInputDir.IsEnabled = true;
                    btnSelectOutputDir.IsEnabled = true;
                    btnOpenOutput.IsEnabled = false;
                });
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
                    progressBar.Maximum = total;
                    progressBar.Value = current;
                    txtProgress.Text = $"{current}/{total} ({percentage}%)";
                }
            }
        }

        // Log messages to the TextBox (must be called on UI thread or marshalled)
        private void LogMessage(string message, bool isError = false)
        {
            // This method assumes it's already called on the UI thread
            // Or that the caller used Dispatcher.InvokeAsync

            // Optional: Style error messages differently if using RichTextBox
            string prefix = isError ? "[错误] " : "";
            txtLog.AppendText($"{prefix}{message}{Environment.NewLine}");
            txtLog.ScrollToEnd(); // Keep the last line visible
        }

        // Enable/Disable UI controls
        private void SetUIEnabled(bool enabled)
        {
            // Ensure this runs on the UI thread if called from background
            btnSelectFiles.IsEnabled = enabled;
            btnSelectInputDir.IsEnabled = enabled;
            btnSelectOutputDir.IsEnabled = enabled;
            btnStart.IsEnabled = enabled;
        }

        private void txtInputPaths_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

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
                    // 如果拖动失败，忽略异常
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
                // 如果拖动失败，忽略异常
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
        /// 最大化/还原按钮
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