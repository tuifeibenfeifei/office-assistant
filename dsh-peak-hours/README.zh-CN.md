# dsh-peak-hours

> DeepSeek API 峰谷时段助手（[DeepSeek Harness](https://github.com/topics/deepseek-harness) 插件）。
> 侧边栏常驻显示**当前是高峰还是空闲、距离切换还有多久**；以北京时间为权威轴换算到你的本地时间；
> 周末与节假日按全天谷价处理；并给 Agent 一个 `dsh_peak_hours` 工具，让它能把算力密集的长任务挑到半价时段。

[English](README.md) | 中文

---

## 它解决什么问题

DeepSeek API 采用峰谷定价，**空闲时段价格是高峰时段的一半**。但官方规则以**北京时间**表达，
而你未必在 UTC+8；加上周末规则曾经调整过，凭记忆判断很容易踩在高峰上跑长任务。

这个插件做三件事：

1. **看得见**：侧边栏与输入框下方常驻一个档期指示 —— 高峰（红）/ 空闲半价（绿）、本段剩余时间、
   下一次切换的时刻。打开 dsh 时立即检测一次，之后按间隔刷新。
2. **算得准**：以北京时间为权威轴计算，同时显示你所在时区的本地时间；
   工作日高峰 `09:00–12:00`、`14:00–18:00`，周六周日全天谷价，支持配置节假日与调休。
3. **能行动**：`dsh_peak_hours` 工具让 Agent 自己判断"现在贵不贵、要不要等到空闲时段"，
   而不是只能给人看。

## 官方规则（依据）

| 项 | 内容 |
| --- | --- |
| 工作日高峰 | 北京时间 09:00–12:00、14:00–18:00（区间左闭右开） |
| 工作日空闲 | 上述之外的时间 |
| 周末 | **周六、周日全天**按空闲（低谷）价计费 |
| 价格关系 | 空闲时段价格为高峰时段的一半（50%） |
| 现行规则生效 | 2026-08-17 峰谷定价生效；2026-08-23 00:00 起周末全天谷价 |

来源：[证券时报《DeepSeek再度宣布调价 周末不再区分峰谷计费》](https://www.stcn.com/article/detail/4103775.html)、
[新浪财经《DeepSeek调价方案公布：空闲时段价格为高峰的一半，8月17日生效》](https://finance.sina.cn/2026-08-13/detail-inineqhh4658.d.html?vt=4)、
[DeepSeek API 定价文档](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)。

规则版本号 `2026-08-23` 会显示在界面上；官方调整后本插件会同步更新并在 CHANGELOG 记录。

## 功能

- **侧边栏常驻指示**（`sidebar.panellist`）：颜色 + 档期 + 倒计时，点开看完整详情。
- **输入框下方紧凑条**（`conversation.composer.dock`）：写提示词时顺手可见。
- **设置页说明区块**（`settings.section`）：规则原文、当前状态、手动刷新。
- **只读 HTTP 接口** `GET /dsh-peak-hours/status`：支持 `?at=<Unix 毫秒>` 与 `?days=<1–14>`，
  便于脚本或其它插件复用同一套判断。
- **Agent 工具** `dsh_peak_hours`：
  - `action: 'status'` → 当前档期、剩余时间、下一次切换、下一次空闲/高峰时刻，以及一句可直接引用的结论；
  - `action: 'forecast'` → 额外返回未来 1–14 天的每日时段安排。
- **配置项**：周末规则开关、节假日列表、调休（强制工作日）列表、自定义高峰窗口、本地时区覆盖。

## 安装

### 方式一：本地目录（开发调试）

把本仓库放到任意目录，然后在 dsh 里用插件管理器以该目录（绝对路径）作为 `target` 安装。
**不要**手写 profile 的 `package.json` 或 `cordis.patch.yml`，也不要手动跑 pnpm —— 安装动作由插件管理器完成。

### 方式二：从 GitHub

仓库推上去之后，在 dsh 的插件管理器里填本仓库地址安装。

### 配置

安装后编辑 profile 的 `cordis.patch.yml`（或在插件管理器的配置界面里改），本插件的行如下，
所有字段都可省略：

```yaml
- id: peak-hours
  name: 'dsh-peak-hours'
  config:
    exposeTool: true            # 是否注册 dsh_peak_hours 工具（设为 false 只保留界面指示）
    weekendAlwaysOffPeak: true  # 周末是否全天谷价（官方现行规则为 true）
    holidayDays:                # 额外按空闲价计费的自然日（北京时间）
      - '2026-10-01'
    workdayOverrides:           # 调休上班的周末，恢复峰谷计费
      - '2026-10-10'
    peakWindows:                # 覆盖工作日高峰窗口（留空/非法值回落官方默认）
      - start: '09:00'
        end: '12:00'
      - start: '14:00'
        end: '18:00'
    localOffsetMinutes: 480     # 覆盖本地时区偏移（分钟）；默认取运行环境
```

## 开发

零依赖：不需要构建步骤，也没有 `npm install`。

```bash
node tests/run.js          # 20 个用例：核心时段逻辑 12 + Host 集成 8
node --check index.js      # 语法检查
```

代码结构：

```
index.js                Host 半侧：只读接口 + Agent 工具 + peakHours 服务
client.js               Client 半侧：侧边栏指示、输入框紧凑条、设置区块
lib/peak-hours.js       纯函数核心：北京时间换算、档期判定、分段边界、未来安排
cordis.patch.yml        向 profile 插入 Host 行（含可调 config）
dsh.plugin.json         插件元信息
tests/                  用例（自研极简运行器，避免子进程依赖）
```

### 为什么时间计算是纯函数

时段判断最容易出的错不是"规则记错"，而是**日界与时区**：跨日的空闲段（夜间、周末）
如果按自然日收束，就会在周六显示"周日 24:00 切换"这类错误结论。
因此 `lib/peak-hours.js` 不读取任何全局状态、不依赖运行环境时区，
所有判断都走"绝对时刻 + 显式偏移"的纯函数，并由用例覆盖跨日、跨周末、跨节假日与
UTC+7 / UTC-8 等非北京时区场景。

## 与同类插件的区别

社区已有若干峰谷指示插件（[dsh-peak](https://github.com/ibrhr/dsh-peak)、
[dsh-peak-indicator](https://github.com/future007s/dsh-peak-indicator)、
[dsh-peak-pricing-warning](https://github.com/Imnotndesh/dsh-peak-pricing-warning)、
[dsh-pricing-badge](https://github.com/ZhangBo-cmd/dsh-pricing-badge) 等）。
本插件的取舍是：

- **给 Agent 用**：`dsh_peak_hours` 工具让模型自己决定"要不要等到半价时段再跑长任务"，而不只是给人看。
- **时区正确**：以北京时间算、按本地时间显示，非 UTC+8 用户不会看错。
- **周末/节假日完整**：2026-08-23 的周末全谷价规则与节假日、调休都在可自测的规则模块里。

## 许可

[MIT](LICENSE)
