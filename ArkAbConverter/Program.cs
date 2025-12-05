using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 创建根命令
        var rootCommand = new RootCommand("Unity Bundle 文件转换工具");

        // 添加子命令
        rootCommand.AddCommand(CreateUncompressCommand());
        rootCommand.AddCommand(CreateArkLz4Command());

        // 解析参数并执行
        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// 创建 uncompress 子命令
    /// </summary>
    private static Command CreateUncompressCommand()
    {
        // 定义输入选项：可以接受多个文件或一个目录
        var inputFilesOption = new Option<FileInfo[]>(
            aliases: new[] { "-i", "--input-files" },
            description: "指定一个或多个输入的 Bundle 文件路径。不能与 --input-dir 同时使用。")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };

        var inputDirectoryOption = new Option<DirectoryInfo>(
            aliases: new[] { "-d", "--input-dir" },
            description: "指定包含 Bundle 文件的输入目录。不能与 --input-files 同时使用。");

        var outputDirectoryOption = new Option<DirectoryInfo>(
            aliases: new[] { "-o", "--output-dir" },
            description: "指定转换后文件存放的输出目录。")
        {
            IsRequired = true
        };

        // 输出目录验证
        outputDirectoryOption.AddValidator(result => {
            var dirInfo = result.GetValueForOption(outputDirectoryOption);
            if (dirInfo != null && !dirInfo.Exists)
            {
                result.ErrorMessage = $"指定的输出目录不存在: {dirInfo.FullName}";
            }
        });

        var uncompressCommand = new Command("uncompress", "将 UnityFS Bundle 文件转换为无压缩格式")
        {
            inputFilesOption,
            inputDirectoryOption,
            outputDirectoryOption
        };

        // 添加验证
        uncompressCommand.AddValidator(result => {
            var files = result.GetValueForOption(inputFilesOption);
            var dir = result.GetValueForOption(inputDirectoryOption);
            if ((files == null || files.Length == 0) && dir == null)
            {
                result.ErrorMessage = "必须提供 --input-files 或 --input-dir 中的一个。";
            }
            if (files != null && files.Length > 0 && dir != null)
            {
                result.ErrorMessage = "不能同时使用 --input-files 和 --input-dir。";
            }
        });

        // 设置命令处理程序
        uncompressCommand.SetHandler(async (InvocationContext context) =>
        {
            var inputFiles = context.ParseResult.GetValueForOption(inputFilesOption);
            var inputDir = context.ParseResult.GetValueForOption(inputDirectoryOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirectoryOption)!;

            int exitCode = await RunUncompressConversion(inputFiles, inputDir, outputDir);
            context.ExitCode = exitCode;
        });

        return uncompressCommand;
    }

    /// <summary>
    /// 创建 arklz4 子命令
    /// </summary>
    private static Command CreateArkLz4Command()
    {
        // 定义输入选项：可以接受多个文件或一个目录
        var inputFilesOption = new Option<FileInfo[]>(
            aliases: new[] { "-i", "--input-files" },
            description: "指定一个或多个输入的 Bundle 文件路径。不能与 --input-dir 同时使用。")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };

        var inputDirectoryOption = new Option<DirectoryInfo>(
            aliases: new[] { "-d", "--input-dir" },
            description: "指定包含 Bundle 文件的输入目录。不能与 --input-files 同时使用。");

        var outputDirectoryOption = new Option<DirectoryInfo>(
            aliases: new[] { "-o", "--output-dir" },
            description: "指定转换后文件存放的输出目录。")
        {
            IsRequired = true
        };

        // 输出目录验证
        outputDirectoryOption.AddValidator(result => {
            var dirInfo = result.GetValueForOption(outputDirectoryOption);
            if (dirInfo != null && !dirInfo.Exists)
            {
                result.ErrorMessage = $"指定的输出目录不存在: {dirInfo.FullName}";
            }
        });

        var arkLz4Command = new Command("arklz4", "将 Bundle 文件转换为 Ark LZ4 压缩格式")
        {
            inputFilesOption,
            inputDirectoryOption,
            outputDirectoryOption
        };

        // 添加验证
        arkLz4Command.AddValidator(result => {
            var files = result.GetValueForOption(inputFilesOption);
            var dir = result.GetValueForOption(inputDirectoryOption);
            if ((files == null || files.Length == 0) && dir == null)
            {
                result.ErrorMessage = "必须提供 --input-files 或 --input-dir 中的一个。";
            }
            if (files != null && files.Length > 0 && dir != null)
            {
                result.ErrorMessage = "不能同时使用 --input-files 和 --input-dir。";
            }
        });

        // 设置命令处理程序
        arkLz4Command.SetHandler(async (InvocationContext context) =>
        {
            var inputFiles = context.ParseResult.GetValueForOption(inputFilesOption);
            var inputDir = context.ParseResult.GetValueForOption(inputDirectoryOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirectoryOption)!;

            int exitCode = await RunArkLz4Conversion(inputFiles, inputDir, outputDir);
            context.ExitCode = exitCode;
        });

        return arkLz4Command;
    }

    /// <summary>
    /// 执行无压缩转换的逻辑
    /// </summary>
    static async Task<int> RunUncompressConversion(FileInfo[]? inputFiles, DirectoryInfo? inputDir, DirectoryInfo outputDir)
    {
        List<FileInfo> filesToProcess = CollectFilesToProcess(inputFiles, inputDir);

        if (filesToProcess.Count == 0)
        {
            Console.WriteLine("没有找到需要处理的文件。");
            return 0;
        }

        Console.WriteLine($"准备处理 {filesToProcess.Count} 个文件，输出到目录: {outputDir.FullName}");
        Console.WriteLine("转换模式: 无压缩格式");

        var converter = new BundleConverter();
        bool hadErrors = false;

        // Determine base input directory to preserve folder structure
        string? baseInputDir = null;
        if (inputDir != null)
        {
            baseInputDir = inputDir.FullName;
        }
        else if (filesToProcess.Count > 0)
        {
            baseInputDir = GetCommonDirectory(filesToProcess);
        }

        foreach (var inputFile in filesToProcess)
        {
            Console.WriteLine($"--- 开始处理: {inputFile.Name} ---");
            if (!inputFile.Exists)
            {
                WriteError($"错误: 输入文件不存在: {inputFile.FullName}");
                hadErrors = true;
                continue;
            }

            string relativePath;
            if (!string.IsNullOrEmpty(baseInputDir))
            {
                try
                {
                    relativePath = Path.GetRelativePath(baseInputDir, inputFile.FullName);
                }
                catch
                {
                    relativePath = inputFile.Name;
                }
            }
            else
            {
                relativePath = inputFile.Name;
            }

            string relativeDir = Path.GetDirectoryName(relativePath) ?? string.Empty;
            string outputDirForFile = string.IsNullOrEmpty(relativeDir) ? outputDir.FullName : Path.Combine(outputDir.FullName, relativeDir);

            try
            {
                // 确保输出文件夹存在
                if (!Directory.Exists(outputDirForFile)) Directory.CreateDirectory(outputDirForFile);

                string outputFileName = Path.ChangeExtension(Path.GetFileName(relativePath), ".uncompressed.ab");
                string outputPath = Path.Combine(outputDirForFile, outputFileName);

                converter.ConvertToUncompressed(inputFile.FullName, outputPath);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"成功转换: {inputFile.Name} -> {Path.GetRelativePath(outputDir.FullName, outputPath)}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                WriteError($"处理文件 '{inputFile.Name}' 时发生错误: {ex.Message}");
                hadErrors = true;
            }
            Console.WriteLine($"--- 完成处理: {inputFile.Name} ---");
            Console.WriteLine();
        }

        Console.WriteLine("所有文件处理完毕。");
        return hadErrors ? 1 : 0;
    }

    /// <summary>
    /// 执行 Ark LZ4 转换的逻辑
    /// </summary>
    static async Task<int> RunArkLz4Conversion(FileInfo[]? inputFiles, DirectoryInfo? inputDir, DirectoryInfo outputDir)
    {
        List<FileInfo> filesToProcess = CollectFilesToProcess(inputFiles, inputDir);

        if (filesToProcess.Count == 0)
        {
            Console.WriteLine("没有找到需要处理的文件。");
            return 0;
        }

        Console.WriteLine($"准备处理 {filesToProcess.Count} 个文件，输出到目录: {outputDir.FullName}");
        Console.WriteLine("转换模式: Ark LZ4 压缩格式");

        var converter = new BundleConverter();
        bool hadErrors = false;

        //确定输入文件夹结构
        string? baseInputDir = null;
        if (inputDir != null)
        {
            baseInputDir = inputDir.FullName;
        }
        else if (filesToProcess.Count > 0)
        {
            baseInputDir = GetCommonDirectory(filesToProcess);
        }

        foreach (var inputFile in filesToProcess)
        {
            Console.WriteLine($"--- 开始处理: {inputFile.Name} ---");
            if (!inputFile.Exists)
            {
                WriteError($"错误: 输入文件不存在: {inputFile.FullName}");
                hadErrors = true;
                continue;
            }

            string relativePath;
            if (!string.IsNullOrEmpty(baseInputDir))
            {
                try
                {
                    relativePath = Path.GetRelativePath(baseInputDir, inputFile.FullName);
                }
                catch
                {
                    relativePath = inputFile.Name;
                }
            }
            else
            {
                relativePath = inputFile.Name;
            }

            string relativeDir = Path.GetDirectoryName(relativePath) ?? string.Empty;
            string outputDirForFile = string.IsNullOrEmpty(relativeDir) ? outputDir.FullName : Path.Combine(outputDir.FullName, relativeDir);

            try
            {
                // Ensure output directory exists
                if (!Directory.Exists(outputDirForFile)) Directory.CreateDirectory(outputDirForFile);

                string outputFileName = Path.ChangeExtension(Path.GetFileName(relativePath), ".arklz4.ab");
                string outputPath = Path.Combine(outputDirForFile, outputFileName);

                converter.ConvertToArkLz4(inputFile.FullName, outputPath);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"成功转换: {inputFile.Name} -> {Path.GetRelativePath(outputDir.FullName, outputPath)}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                WriteError($"处理文件 '{inputFile.Name}' 时发生错误: {ex.Message}");
                hadErrors = true;
            }
            Console.WriteLine($"--- 完成处理: {inputFile.Name} ---");
            Console.WriteLine();
        }

        Console.WriteLine("所有文件处理完毕。");
        return hadErrors ? 1 : 0;
    }

    /// <summary>
    /// 收集需要处理的文件
    /// </summary>
    private static List<FileInfo> CollectFilesToProcess(FileInfo[]? inputFiles, DirectoryInfo? inputDir)
    {
        List<FileInfo> filesToProcess = new List<FileInfo>();

        if (inputFiles != null && inputFiles.Length > 0)
        {
            filesToProcess.AddRange(inputFiles);
        }
        else if (inputDir != null)
        {
            Console.WriteLine($"正在扫描输入目录: {inputDir.FullName}");
            try
            {
                // Changed to search all subdirectories instead of only top directory
                filesToProcess.AddRange(inputDir.GetFiles("*.*", SearchOption.AllDirectories)
                                                 .Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)));
            }
            catch (Exception ex)
            {
                WriteError($"扫描输入目录时出错: {ex.Message}");
            }
        }

        return filesToProcess;
    }

    /// <summary>
    /// 获取多个文件的共同父目录（如果存在）
    /// </summary>
    private static string? GetCommonDirectory(IEnumerable<FileInfo> files)
    {
        var fileList = files.ToList();
        if (!fileList.Any()) return null;

        string common = Path.GetFullPath(fileList[0].DirectoryName ?? string.Empty);
        if (string.IsNullOrEmpty(common)) return null;

        foreach (var fi in fileList.Skip(1))
        {
            var dir = Path.GetFullPath(fi.DirectoryName ?? string.Empty);
            if (string.Equals(common, dir, StringComparison.OrdinalIgnoreCase)) continue;

            // Reduce common until it is a prefix of dir
            while (!dir.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   !dir.Equals(common, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrEmpty(parent))
                {
                    common = string.Empty;
                    break;
                }
                common = parent;
            }

            if (string.IsNullOrEmpty(common)) break;
        }

        return string.IsNullOrEmpty(common) ? null : common;
    }

    /// <summary>
    /// 向控制台写入错误消息
    /// </summary>
    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }
}