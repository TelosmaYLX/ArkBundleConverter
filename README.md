感谢@MigitaRin 大佬在https://www.bilibili.com/opus/1053485538509586453 中分享的工具和开源

在舟引擎升级之后修改了assetBundle的压缩算法，具体可见https://www.bilibili.com/opus/1108601513797746721 这篇专栏的介绍。

本工具可以将明日方舟游戏资源的assetBundle文件转换成普通LZ4压缩的ab包文件，以便进一步使用assetStudio和assetRipper读取。

优化的地方:优化了GUI的表现、可以批量处理输入目录下内和其子文件内的所有文件、处理大量文件时不会卡死了、增加了进度条和计数、改进了日志打印信息、增加了快速打开输出目录和打印日志的功能。
<img width="908" height="909" alt="image" src="https://github.com/user-attachments/assets/5bfb04e1-4f75-4e00-900b-ee583f4d1f4d" />
如有问题请b站私信我https://space.bilibili.com/948134

## 导出 AB 包名与资源名(CAB)对应表

CLI 提供 `export-map` 子命令，仅读取 Bundle 元数据导出对应表，**不进行包体转换**：

```
ArkBundleConverterCLI.exe export-map -d <输入目录> -o <输出目录>          # 默认导出 CSV
ArkBundleConverterCLI.exe export-map -i a.ab b.ab -o <输出目录> -f json   # 导出 JSON
```

- `-i, --input-files` / `-d, --input-dir`：输入文件或目录（与其它子命令一致）
- `-o, --output-dir`：输出目录（默认当前目录），生成 `ab_cab_mapping.csv` 或 `ab_cab_mapping.json`
- `-f, --format`：`csv`（默认）或 `json`

GUI 点击「导出对应表」按钮即可执行相同功能。
