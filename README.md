# 全能办公助手

Windows 10/11（64 位）桌面办公辅助工具，托盘常驻。**界面只有四个入口，功能只有一个目标：把日常琐事一键做完。**

| 界面 | 做什么 | 状态 |
| --- | --- | --- |
| **主界面** | 三张卡片进入下面三个功能，右上角齿轮进设置 | 可用 |
| **网络加速** | GitHub / Steam 常用域名三段测速（DNS → TCP → HTTPS）、失败分段诊断、镜像源一键切换与还原、Git 配置优化向导 | 可用（合规版：**不改 hosts、不写系统代理、不建隧道**） |
| **磁盘清理** | 只读扫描建立索引、目录树浏览、一键清理（按规则匹配）、深度清理（系统公认临时目录）、批量删除、待判断文件 | 可用 |
| **内存清理** | 累计已释放内存圆环、一键整理工作集、非前台进程豁免 | 可用（**不结束任何进程**） |
| **设置** | 常规三项开关（开机启动 / 自动清理内存 / 常驻托盘）、使用说明、关于 | 可用 |

所有数据仅存本机，不上传任何文件路径、内容或日志。

> 修订说明：早期规格里的「学习通作业管理」已按规格变更删除；磁盘管理曾做成无限画布 + 拖拽搬运，
> 因拖拽卡顿、学习成本高而**整体移除**，改为目录树 + 一键清理这条更短的路径。

---

## 一、仓库结构

```
全能办公助手/
├── desktop-app/                    C# WPF / .NET 8 桌面程序（本仓库主体）
│   ├── 全能办公助手.sln
│   ├── build.ps1                   构建 + 自测 + 打包
│   ├── NuGet.config                已清空包源（离线构建，零第三方依赖）
│   ├── packaging/安装.ps1           快捷方式创建 / 卸载 / 便携模式切换
│   └── src/
│       ├── OfficeAssistant.Core/   引擎：扫描、识别、清理与安全、资源管家、网络、合规审计
│       ├── OfficeAssistant.App/    WPF 界面：设计系统、四个界面、托盘
│       └── OfficeAssistant.Tests/  13 项引擎自测
├── dsh-office-assistant/           dsh 插件：给 Agent 四个办公工具
├── dsh-peak-hours/                 dsh 插件：DeepSeek 峰谷时段助手
├── docs/                           架构设计 / 使用说明 / 验收与自查报告 / 界面截图
└── specs/                          功能说明与项目计划书（需求来源，保留存档）
```

`dist/`（便携版产物）与运行期数据不入库，见 `.gitignore`。

三个部分放在同一个仓库里（而不是子模块），克隆一次就能拿到全部内容，不需要 `--recurse-submodules`。
每个子目录都能独立使用：`desktop-app/` 是完整的 .NET 解决方案，两个 dsh 插件各自零依赖、可直接安装。

---

## 二、快速开始

### 1. 运行环境

- Windows 10 21H2 / Windows 11（64 位）
- .NET 8 桌面运行时（`Microsoft.WindowsDesktop.App 8.x`）
  未安装时先装：<https://dotnet.microsoft.com/download/dotnet/8.0> → "Desktop Runtime x64"
- 无需管理员权限（仅"待机列表清理"需要）

### 2. 构建

```powershell
cd desktop-app
powershell -ExecutionPolicy Bypass -File .\build.ps1              # 构建 + 自测 + UI 自检 + 打包
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests   # 只构建与打包
powershell -ExecutionPolicy Bypass -File .\build.ps1 -NoPublish   # 只构建与自测
```

> `-ExecutionPolicy Bypass` 不能省：`.ps1` 脚本里的中文在 Windows PowerShell 5.1 下按 ANSI 代码页读取，
> 脚本已带 UTF-8 BOM；执行策略默认禁止运行脚本。

也可以逐步手动执行：

```powershell
cd desktop-app
dotnet build .\src\OfficeAssistant.Core\OfficeAssistant.Core.csproj  -c Release
dotnet build .\src\OfficeAssistant.App\OfficeAssistant.App.csproj    -c Release
dotnet publish .\src\OfficeAssistant.App\OfficeAssistant.App.csproj -c Release -r win-x64 --self-contained false -o .\dist\全能办公助手-便携版
```

> 注：解决方案级 `dotnet build 全能办公助手.sln` 在部分机器上会因 SDK 缺少 workload locator 目录报 `MSB4276`，
> 因此 `build.ps1` 逐个项目构建；两者效果等价。
> 自包含发布需要联网下载运行时包，离线环境无法完成，故只提供框架依赖版本。

### 3. 自测

```powershell
# 引擎自测（13 项）
.\desktop-app\src\OfficeAssistant.Tests\bin\Release\net8.0-windows\OfficeAssistant.Tests.exe

# UI 自检：真实 WPF 栈上加载全部 XAML、实例化四个界面、捕获数据绑定错误
.\dist\全能办公助手-便携版\全能办公助手.exe --smoke-test

# 启动路径取证：完整走一遍真实启动（含托盘、窗口、初始化、后台服务）
.\dist\全能办公助手-便携版\全能办公助手.exe --startup-check --full

# 界面渲染取证：把真实窗口渲染成 PNG，用于人工核对布局
.\dist\全能办公助手-便携版\全能办公助手.exe --render
```

`--render` 会把截图写到 `%TEMP%\OfficeAssistant-render-*`。这个模式是排查界面问题最有效的手段：
早先"按钮黑字看不见""画布渲染成空白"都是靠它一眼看出来的。

### 4. 安装（便携版）

```powershell
cd dist\全能办公助手-便携版
powershell -ExecutionPolicy Bypass -File .\安装.ps1              # 创建快捷方式
powershell -ExecutionPolicy Bypass -File .\安装.ps1 -Portable    # 便携模式：数据写在程序目录旁的 data 子目录
powershell -ExecutionPolicy Bypass -File .\安装.ps1 -Uninstall   # 卸载：删快捷方式与本地数据
```

直接双击 `全能办公助手.exe` 也能用，不安装即可运行。

**数据目录**：默认 `%LOCALAPPDATA%\全能办公助手`。若该位置不可写（企业策略、权限残缺等），
程序会依次尝试 `%APPDATA%` → 程序目录旁的 `data\` → `%TEMP%`，并把每次尝试记录在
`%TEMP%\OfficeAssistant-startup-trace.log`，不会因为一个目录写不进去就起不来。

### 5. 首次使用建议

1. 进「磁盘清理」→ 点「扫描」建立索引（只读，不修改任何文件）。
2. 想快速释放空间：点「深度清理」——只覆盖系统公认的临时目录（着色器缓存、错误报告、崩溃转储、缩略图缓存等），
   预览里会逐项写明"为什么删了没事"。
3. 想按规则清理：点「一键清理」，先看分类清单再确认。删除统一进回收站，可恢复。
4. 需要长时间不动手时：设置里打开「自动清理内存」。

---

## 三、安全边界（重要）

这几条是硬约束，代码里有对应实现与自测，不会因为后续改动而放松：

| 约束 | 实现 |
| --- | --- |
| **误删防护** | 系统目录、程序目录、用户白名单命中即拦截；12 项高危路径 100% 拦截（含绕过界面直接调用） |
| **删除可恢复** | 统一走回收站（`IFileOperation`），不做物理删除；回收站不可用时明确报错，**不静默降级为永久删除** |
| **操作可审计** | 每次写入进 `operation_log`，可回滚 |
| **合规红线** | 不修改 hosts、不写系统代理、不安装驱动、不驻留代理进程；源码 + 二进制双向回归审计 |
| **不结束进程** | 资源管家只压缩工作集与调优先级；反射断言全模块无 `Kill` / `TerminateProcess` |
| **扫描只读** | 扫描与识别全程不打开文件写句柄 |

---

## 四、文档索引

| 文档 | 内容 |
| --- | --- |
| [架构设计](docs/架构设计.md) | 分层、模块职责、数据库表结构、关键决策与理由 |
| [使用说明](docs/使用说明.md) | 面向使用者的逐步指引 |
| [验收与自查报告](docs/验收与自查报告.md) | 逐条验收对照、实测数据、缺陷清单（如实列出未验证项） |
| [界面截图](docs/界面截图/) | 五个界面的真实渲染图 |
| [功能说明](specs/全能办公助手插件_功能说明.md) | 需求规格（含已知问题与修复要求） |

dsh 插件有各自的说明：[办公助手](dsh-office-assistant/README.zh-CN.md)、[峰谷时段](dsh-peak-hours/README.zh-CN.md)。

---

## 五、许可

[MIT](LICENSE)。程序内不含任何第三方运行时依赖，图标与图标集均为本项目自绘矢量。
