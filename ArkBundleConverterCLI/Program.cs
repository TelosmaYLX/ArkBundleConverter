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
        rootCommand.AddCommand(CreateExportMapCommand());
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
    /// 创建 export-map 子命令
    /// </summary>
    private static Command CreateExportMapCommand()
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
            description: "指定对应表文件的输出目录（默认：当前目录）。");

        var formatOption = new Option<string>(
            aliases: new[] { "-f", "--format" },
            description: "对应表导出格式：csv 或 json（默认：csv）。");

        // 输出目录验证（可选，仅在提供时检查）
        outputDirectoryOption.AddValidator(result => {
            var dirInfo = result.GetValueForOption(outputDirectoryOption);
            if (dirInfo != null && !dirInfo.Exists)
            {
                result.ErrorMessage = $"指定的输出目录不存在: {dirInfo.FullName}";
            }
        });

        var exportMapCommand = new Command("export-map", "导出 AB 包文件名与资源名(CAB)的对应表，不进行包体转换")
        {
            inputFilesOption,
            inputDirectoryOption,
            outputDirectoryOption,
            formatOption
        };

        // 添加验证
        exportMapCommand.AddValidator(result => {
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
            var format = result.GetValueForOption(formatOption);
            if (!string.IsNullOrEmpty(format) && format != "csv" && format != "json")
            {
                result.ErrorMessage = "--format 仅支持 csv 或 json。";
            }
        });

        // 设置命令处理程序
        exportMapCommand.SetHandler(async (InvocationContext context) =>
        {
            var inputFiles = context.ParseResult.GetValueForOption(inputFilesOption);
            var inputDir = context.ParseResult.GetValueForOption(inputDirectoryOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirectoryOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "csv";

            int exitCode = await RunExportMapConversion(inputFiles, inputDir, outputDir, format);
            context.ExitCode = exitCode;
        });

        return exportMapCommand;
    }

    /// <summary>
    /// 执行导出对应表的逻辑（仅读取元数据，不转换包体）
    /// </summary>
    static async Task<int> RunExportMapConversion(FileInfo[]? inputFiles, DirectoryInfo? inputDir, DirectoryInfo? outputDir, string format)
    {
        List<FileInfo> filesToProcess = CollectFilesToProcess(inputFiles, inputDir);

        if (filesToProcess.Count == 0)
        {
            Console.WriteLine("没有找到需要处理的文件。");
            return 0;
        }

        Console.WriteLine($"准备处理 {filesToProcess.Count} 个文件，提取 AB 包名与资源名(CAB)对应表...");
        Console.WriteLine("模式: 仅导出对应表（不进行包体转换）");
        Console.WriteLine();

        var converter = new BundleConverter();
        bool hadErrors = false;

        string? baseInputDir = null;
        if (inputDir != null)
        {
            baseInputDir = inputDir.FullName;
        }
        else if (filesToProcess.Count > 0)
        {
            baseInputDir = GetCommonDirectory(filesToProcess);
        }

        var mappings = new List<BundleMapping>();

        int processedCount = 0;
        int totalCount = filesToProcess.Count;
        int successCount = 0;
        int failedCount = 0;

        foreach (var inputFile in filesToProcess)
        {
            try
            {
                if (!inputFile.Exists)
                {
                    WriteError($"错误: 输入文件不存在: {inputFile.FullName}");
                    hadErrors = true;
                    failedCount++;
                    continue; // 进度由 finally 统一更新
                }

                if (!BundleConverter.IsUnityBundle(inputFile.FullName))
                {
                    Console.WriteLine($"跳过: {inputFile.Name}（不是 UnityFS Bundle）");
                    continue; // 进度由 finally 统一更新
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

                var metadata = converter.ExtractBundleMetadata(inputFile.FullName);
                mappings.Add(new BundleMapping(relativePath, metadata.Cabs, metadata.Dependencies));

                string cabInfo = metadata.Cabs.Count > 0
                    ? string.Join(", ", metadata.Cabs)
                    : "(无CAB)";
                Console.WriteLine($"✓ 成功处理: {inputFile.Name} -> {cabInfo}");
                successCount++;
            }
            catch (Exception ex)
            {
                WriteError($"处理文件 '{inputFile.Name}' 时发生错误: {ex.Message}");
                hadErrors = true;
                failedCount++;
            }
            finally
            {
                processedCount++;
                PrintProgress(processedCount, totalCount);
            }
        }

        // 写入对应表文件
        if (mappings.Count > 0)
        {
            try
            {
                string outputFilePath = WriteMappingFile(mappings, outputDir, format);
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ 对应表已导出: {outputFilePath}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                WriteError($"写入对应表文件时发生错误: {ex.Message}");
                hadErrors = true;
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("没有提取到任何可用的 Bundle 信息，未生成对应表文件。");
        }

        // Summary
        Console.WriteLine($"共处理{totalCount}个文件，成功提取{successCount}个，失败{failedCount}个");
        return hadErrors ? 1 : 0;
    }

    /// <summary>
    /// 将提取结果写入对应表文件（csv / json）
    /// </summary>
    private static string WriteMappingFile(List<BundleMapping> mappings, DirectoryInfo? outputDir, string format)
    {
        string directory = outputDir?.FullName ?? Directory.GetCurrentDirectory();

        string extension = format == "json" ? "json" : "csv";
        string outputPath = Path.Combine(directory, $"ab_cab_mapping.{extension}");

        if (format == "json")
        {
            var root = new
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                totalBundles = mappings.Count,
                bundles = mappings.Select(m => new
                {
                    abName = m.AbName,
                    cabs = m.Cabs,
                    dependencies = m.Dependencies
                }).ToList()
            };
            string json = System.Text.Json.JsonSerializer.Serialize(root, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB包文件名,CAB资源名,依赖");
            foreach (var m in mappings)
            {
                string deps = string.Join(";", m.Dependencies);
                if (m.Cabs.Count == 0)
                {
                    sb.AppendLine($"{CsvEscape(m.AbName)},(无CAB),{CsvEscape(deps)}");
                }
                else
                {
                    foreach (var cab in m.Cabs)
                    {
                        sb.AppendLine($"{CsvEscape(m.AbName)},{CsvEscape(cab)},{CsvEscape(deps)}");
                    }
                }
            }
            // 带 BOM 的 UTF-8，便于 Excel 直接打开中文表头
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(true));
        }

        return outputPath;
    }

    /// <summary>
    /// CSV 字段转义（含逗号/分号/引号/换行时用双引号包裹）
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains(';') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
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
        Console.WriteLine();

        var converter = new BundleConverter();
        bool hadErrors = false;

        string? baseInputDir = null;
        if (inputDir != null)
        {
            baseInputDir = inputDir.FullName;
        }
        else if (filesToProcess.Count > 0)
        {
            baseInputDir = GetCommonDirectory(filesToProcess);
        }

        int processedCount = 0;
        int totalCount = filesToProcess.Count;
        int successCount = 0;
        int failedCount = 0;

        foreach (var inputFile in filesToProcess)
        {
            try
            {
                // 提取元数据
                var metadata = converter.ExtractBundleMetadata(inputFile.FullName);
                string resourceInfo = string.IsNullOrEmpty(metadata.ResourceName) ? "未知资源" : metadata.ResourceName;
                
                Console.WriteLine($"已找到: {inputFile.Name}");
                Console.WriteLine($"资源名: {resourceInfo}");

                if (!inputFile.Exists)
                {
                    WriteError($"错误: 输入文件不存在: {inputFile.FullName}");
                    hadErrors = true;
                    failedCount++;
                    processedCount++;
                    PrintProgress(processedCount, totalCount);
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

                    string outputFileName = Path.ChangeExtension(Path.GetFileName(relativePath), ".ab");
                    string outputPath = Path.Combine(outputDirForFile, outputFileName);

                    converter.ConvertToUncompressed(inputFile.FullName, outputPath);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ 成功转换: {inputFile.Name} -> {outputFileName}");
                    Console.ResetColor();
                    successCount++;
                }
                catch (Exception ex)
                {
                    WriteError($"处理文件 '{inputFile.Name}' 时发生错误: {ex.Message}");
                    hadErrors = true;
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                WriteError($"处理文件 '{inputFile.Name}' 时发生异常: {ex.Message}");
                hadErrors = true;
                failedCount++;
            }
            finally
            {
                processedCount++;
                PrintProgress(processedCount, totalCount);
                Console.WriteLine();
            }
        }

        // Summary
        Console.WriteLine($"共发现{totalCount}个可转换文件，{successCount}个文件转换成功，{failedCount}个文件转换失败");
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
        Console.WriteLine();

        var converter = new BundleConverter();
        bool hadErrors = false;

        string? baseInputDir = null;
        if (inputDir != null)
        {
            baseInputDir = inputDir.FullName;
        }
        else if (filesToProcess.Count > 0)
        {
            baseInputDir = GetCommonDirectory(filesToProcess);
        }

        int processedCount = 0;
        int totalCount = filesToProcess.Count;
        int successCount = 0;
        int failedCount = 0;

        foreach (var inputFile in filesToProcess)
        {
            try
            {
                // 提取元数据
                var metadata = converter.ExtractBundleMetadata(inputFile.FullName);
                string resourceInfo = string.IsNullOrEmpty(metadata.ResourceName) ? "未知资源" : metadata.ResourceName;
                
                Console.WriteLine($"已找到: {inputFile.Name}");
                Console.WriteLine($"  资源名: {resourceInfo}");

                if (!inputFile.Exists)
                {
                    WriteError($"错误: 输入文件不存在: {inputFile.FullName}");
                    hadErrors = true;
                    failedCount++;
                    processedCount++;
                    PrintProgress(processedCount, totalCount);
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
                    Console.WriteLine($"✓ 成功转换: {inputFile.Name} -> {outputFileName}");
                    Console.ResetColor();
                    successCount++;
                }
                catch (Exception ex)
                {
                    WriteError($"处理文件 '{inputFile.Name}' 时发生错误: {ex.Message}");
                    hadErrors = true;
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                WriteError($"处理文件 '{inputFile.Name}' 时发生异常: {ex.Message}");
                hadErrors = true;
                failedCount++;
            }
            finally
            {
                processedCount++;
                PrintProgress(processedCount, totalCount);
                Console.WriteLine();
            }
        }

        // Summary
        Console.WriteLine($"共发现{totalCount}个可转换文件，{successCount}个文件转换成功，{failedCount}个文件转换失败");
        return hadErrors ? 1 : 0;
    }

    /// <summary>
    /// 打印进度信息
    /// </summary>
    private static void PrintProgress(int completed, int total)
    {
        int percentage = total > 0 ? (completed * 100) / total : 0;
        Console.WriteLine($"进度: {completed}/{total} ({percentage}%)");
        Console.Out.Flush();
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

/// <summary>
/// 单个 Bundle 的 AB 包文件名与资源名(CAB)对应关系
/// </summary>
public record BundleMapping(string AbName, List<string> Cabs, List<string> Dependencies);