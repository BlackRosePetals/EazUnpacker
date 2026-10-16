# EazUnpacker

![Supported Eazfuscator.NET versions: 2021.1–2026.2](https://img.shields.io/badge/Eazfuscator.NET-2021.1%E2%80%932026.2-2563eb)

A command-line unpacker and deobfuscator for Eazfuscator.NET assemblies, with IL recovery and C# source export.

面向 Eazfuscator.NET 程序集的命令行解包与去混淆工具，支持 IL 恢复和 C# 源码导出。

[English](#english) · [简体中文](#简体中文) · [Before / After · 效果对比](#before--after--效果对比)

## Before / After · 效果对比

### 1. Entry-point cleanup · 入口方法整理

**Before · 之前（图 1）**

![Entry point before processing: obfuscated identifiers and constant conditional branches / 处理前的入口方法：混淆标识符和恒定条件分支](src/assets/screenshots/entry-point-before.png)

**After · 之后（图 2）**

![Entry point after processing: readable identifiers, decoded strings and simplified branches / 处理后的入口方法：可读标识符、还原字符串和简化分支](src/assets/screenshots/entry-point-after.png)

### 2. String recovery and control-flow cleanup · 字符串恢复与控制流整理

**Before · 之前（图 3）**

![Method before processing: string decoder calls and redundant conditional branches / 处理前的方法：字符串解码调用和冗余条件分支](src/assets/screenshots/method-before.png)

**After · 之后（图 4）**

![Method after processing: visible string literals and simplified control flow / 处理后的方法：可见的字符串字面量和简化的控制流](src/assets/screenshots/method-after.png)

## English

### Supported versions

**Eazfuscator.NET 2021.1–2026.2**, inclusive.

### Features

- Assembly unpacking and recovery of strings, resources and embedded assemblies.
- Control-flow cleanup and devirtualization of supported VM methods.
- Readable identifier generation and C# project export with a rebuild step.
- JSON analysis output and recovery progress in the CLI.

Generated names improve readability; they do not recover the author's original names when original symbols are unavailable. Support depends on the protections and assembly being processed.

### Build

Requires Windows, .NET Framework 4.8, and a .NET SDK with `.slnx` support (9.0.200 or newer). The first build restores NuGet dependencies.

```powershell
.\src\build.ps1
# Also build the optional modern .NET backend
.\src\build.ps1 -WithModernWorker
```

You can also open `Unpacker.slnx` or run `dotnet build Unpacker.slnx -c Release`. Output is written to `src\bin\Release\net48`; distribute the entire output directory, including its worker processes. The optional modern backend requires the .NET 8 Desktop Runtime.

### Usage

```powershell
.\src\bin\Release\net48\Unpacker.exe unpack "C:\input.exe" "C:\output.exe"
.\src\bin\Release\net48\Unpacker.exe analyzer "C:\input.exe"
```

For .NET Framework EXE output, deploy the generated `.config` alongside the executable. Exported source, logs and diagnostics are stored in `%LOCALAPPDATA%\Unpacker\work`. The `analyzer` command returns JSON that includes the generated project location when available.

Exit codes: `0` for completion of the identified stages, `2` for incomplete recovery or source rebuilding, and `1` for input, command or processing errors.

### Source layout

| Path | Purpose |
| --- | --- |
| `src/` | Main program, project file and build script |
| `src/ThirdParty/` | Required EazFixer, EazyDevirt and Echo source dependencies |
| `src/WorkerHost/` | Required .NET Framework worker process |
| `src/ModernWorker/` | Optional .NET 8 worker for modern .NET assemblies |
| `src/tests/` | Regression scripts and behavior validation tools |
| `src/assets/screenshots/` | Before-and-after screenshots used in this README |

### Testing

```powershell
# Built-in tests; no external samples required
.\src\bin\Release\net48\worker\Unpacker.Worker.exe --self-test
# Regression tests using a prepared sample directory
.\src\tests\Regression.ps1 -SampleRoot "C:\samples"
```

### License

The combined project is licensed under GPL-3.0; see [LICENSE](LICENSE). Third-party components retain their own licenses. Sources and attribution are listed in [NOTICE](NOTICE).

## 简体中文

### 支持版本

**Eazfuscator.NET 2021.1 至 2026.2**，包含起止版本。

### 功能

- 程序集解包，恢复字符串、资源及内嵌程序集。
- 整理控制流，对受支持的 VM 方法进行反虚拟化。
- 生成可读标识符，导出 C# 工程并执行重构建。
- 通过命令行显示处理进度，支持 JSON 分析输出。

缺少原始符号时，生成的名称用于改善可读性，不等于恢复了原作者命名。具体支持情况取决于目标程序集及其保护方式。

### 构建

需要 Windows、.NET Framework 4.8 和支持 `.slnx` 的 .NET SDK（9.0.200 或更新版本）。首次构建会还原 NuGet 依赖。

```powershell
.\src\build.ps1
# 同时构建可选的现代 .NET 后端
.\src\build.ps1 -WithModernWorker
```

也可打开 `Unpacker.slnx`，或运行 `dotnet build Unpacker.slnx -c Release`。输出位于 `src\bin\Release\net48`，分发时保留整个输出目录及工作进程。可选现代后端需要 .NET 8 桌面运行时。

### 使用

```powershell
.\src\bin\Release\net48\Unpacker.exe unpack "C:\input.exe" "C:\output.exe"
.\src\bin\Release\net48\Unpacker.exe analyzer "C:\input.exe"
```

传统 .NET Framework EXE 输出包含必需的 `.config`，应与程序一起部署。导出的源码、日志与诊断文件保存在 `%LOCALAPPDATA%\Unpacker\work`。`analyzer` 输出 JSON，在生成工程可用时提供其位置。

退出码：`0` 表示已识别阶段完成，`2` 表示恢复或源码重建不完整，`1` 表示输入、命令或处理错误。

### 源码结构

| 路径 | 用途 |
| --- | --- |
| `src/` | 主程序源码、项目文件和构建脚本 |
| `src/ThirdParty/` | 编译必需的 EazFixer、EazyDevirt 与 Echo 源码依赖 |
| `src/WorkerHost/` | 主程序必需的 .NET Framework 工作进程 |
| `src/ModernWorker/` | 处理现代 .NET 程序集的可选 .NET 8 工作进程 |
| `src/tests/` | 回归脚本和行为验证工具 |
| `src/assets/screenshots/` | README 使用的处理前后效果图 |

### 测试

```powershell
# 无需外部样本的内置测试
.\src\bin\Release\net48\worker\Unpacker.Worker.exe --self-test
# 使用准备好的样本目录运行回归测试
.\src\tests\Regression.ps1 -SampleRoot "C:\samples"
```

### 许可证

组合项目采用 GPL-3.0，见 [LICENSE](LICENSE)。第三方源码保留各自许可证，来源和署名见 [NOTICE](NOTICE)。
