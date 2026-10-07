# dsh-office-assistant

> 办公助手（[DeepSeek Harness](https://github.com/topics/deepseek-harness) 插件）。
> 给 Agent 四个办公工具：**文本与 JSON 处理**、**只读磁盘占用扫描**、**系统临时目录清理**、**内存查看与整理**。
> 侧边栏常驻显示内存占用。

[English](README.md) | 中文

---

## 它解决什么问题

"我 C 盘满了，帮我看看什么占了空间"、"这段 JSON 格式一下"、"临时文件清一下" ——
这些是每天都在发生的小事，但每件都要离开对话、打开别的工具。

装上这个插件后，Agent 自己就能做：

```
你：我磁盘快满了，看看哪些文件夹最占地方
Agent：（调用 office_scan）扫描 C:\Users\你的名字：… 最大目录 …\AppData\Local\NVIDIA 4.0 GB
你：能清吗
Agent：（调用 office_clean，action=preview）共 310 个临时文件，可释放 4.1 GB …
你：清
Agent：（调用 office_clean，confirm=true）已把 310 个文件移入回收站 …
```

## 四个工具

| 工具 | 能做什么 | 安全边界 |
| --- | --- | --- |
| `office_text` | JSON 格式化/压缩、文本字数与阅读时长统计、Base64 与 URL 编解码、命名风格互转（camelCase / snake_case 等 5 种） | 纯文本处理，不碰文件系统。非法 JSON / 非法 Base64 会**报错**而不是静默"修好" |
| `office_scan` | 只读扫描目录，按体积给出文件夹排行与最大文件列表 | 只读；无权限的子目录跳过而不是整棵树失败；有文件数上限并明确标注结果不完整 |
| `office_clean` | 查看并清理系统公认的临时目录 | **只清理白名单内的位置**，不接受任意路径；必须先 `preview`，执行需 `confirm=true`；删除走**回收站**可恢复 |
| `office_memory` | 查看内存占用；整理非前台进程的工作集 | 不结束任何进程；只读动作无需确认，整理需 `confirm=true` |

**白名单包含**（全部限定在当前用户范围内）：用户临时文件、IE/系统网络缓存、Windows 错误报告、缩略图缓存、DirectX / NVIDIA 着色器缓存、CrashDumps 崩溃转储、漫游最近使用记录。

**刻意不包含**：文档、下载、桌面、浏览器配置与登录状态、聊天记录。这些地方删错了没有"重建"一说，所以不放进任何一键清理。

每个目标在预览里都会显示**为什么删了没事**，例如"着色器编译缓存，游戏首次运行会重新编译"。

## 侧边栏面板

常驻显示内存占用率（≥75% 变黄、≥90% 变红），点开可看总量 / 已用 / 可用。
输入框下方还有一条紧凑指示。数据来自插件自己的只读接口 `GET /dsh-office-assistant/status`。

## 安装

把本仓库放到任意目录，在 dsh 的插件管理器里以该**绝对路径**作为 target 安装。
**不要**手写 profile 的 `package.json` 或 `cordis.patch.yml`，也不要手动跑 pnpm —— 安装动作由插件管理器完成。

配置项（可省略）：

```yaml
- id: office-assistant
  name: 'dsh-office-assistant'
  config:
    exposeTools: true   # 设为 false 只保留侧边栏面板，不注册任何工具
```

## 开发

零依赖：不需要构建步骤，也没有 `npm install`。

```bash
node tests/run.js      # 25 个用例：核心逻辑 18 + Host 集成 7
node tests/smoke.mjs   # 真机冒烟：只读探测四个工具（不执行任何删除）
node --check index.js  # 语法检查
```

代码结构：

```
index.js               Host 半侧：四个工具 + 只读接口 + officeAssistant 服务
client.js              Client 半侧：侧边栏面板与输入框紧凑条
lib/office-core.js     纯函数核心：JSON、文本统计、编码、命名转换、目录体积归集
cordis.patch.yml       向 profile 插入 Host 行（含可调 config）
tests/                 用例（自带进程内运行器，避免子进程依赖）
```

### 为什么把逻辑放在纯函数层

真正容易出错的不是系统调用，而是**解析与统计的边界**：非法 JSON 的定位、
中英混排的字数口径、Base64 的截断识别、目录体积的逐级累加、盘符不该被当成文件夹。
这些全部放在 `lib/office-core.js`（不读全局状态、不碰文件系统），由 18 个用例覆盖；
Host 半侧只做系统调用与编排。

### 删除为什么走 PowerShell

Windows 下没有 Node 原生的回收站 API。这里通过 PowerShell 调用
`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin)`，
让删除可恢复。非 Windows 平台会**明确拒绝**执行清理，而不是退化成永久删除——
"删不掉"比"删错了找不回来"好得多。

## 与桌面版的关系

同一套能力的 C#/WPF 桌面版（含目录树、无虚拟化卡顿处理的完整磁盘界面）在
[全能办公助手](https://github.com/tuifeibenfeifei) 仓库里。
两者的白名单与安全判定口径一致；本插件是它在 dsh 里的对话式入口。

## 许可

[MIT](LICENSE)
