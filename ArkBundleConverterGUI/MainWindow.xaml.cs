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
                // ShowDialog needs a HWND owner in WPF, but passing null often works,
                // or create a temporary hidden WinForms window as owner if needed.
                // For simplicity, let's try without explicit owner first.
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
            SetUIEnabled(false);
            LogMessage("开始执行转换...");
            LogMessage($"命令行: {_cliExecutablePath} {argsBuilder.ToString()}");

            // Run process asynchronously
            Task.Run(() => ExecuteCliProcess(argsBuilder.ToString()));
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
                StandardOutputEncoding = Encoding.UTF8, // Match CLI output encoding
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using (Process process = new Process { StartInfo = startInfo })
                {
                    // Use lambda expressions to capture context for Dispatcher
                    process.OutputDataReceived += (s, args) =>
                    {
                        if (args.Data != null)
                        {
                            // Marshal call back to UI thread
                            Dispatcher.InvokeAsync(() => LogMessage(args.Data));
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

                    process.WaitForExit(); // Wait for the process to complete

                    // Log exit code on UI thread
                    Dispatcher.InvokeAsync(() =>
                    {
                        LogMessage($"转换进程已退出，退出码: {process.ExitCode}", process.ExitCode != 0);
                        SetUIEnabled(true); // Re-enable UI after process finishes
                    });
                }
            }
            catch (Exception ex)
            {
                // Log exception on UI thread
                Dispatcher.InvokeAsync(() =>
                {
                    LogMessage($"启动或执行转换进程时出错: {ex.Message}", true);
                    SetUIEnabled(true); // Ensure UI is re-enabled on error
                });
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

        }
    }
}