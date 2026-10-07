# 变更记录

本项目遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] — 2026-10-07

首个版本。

### 新增

- 侧边栏常驻档期指示（`sidebar.panellist`）：高峰/空闲配色、本段剩余时间、点击展开详情。
- 输入框下方紧凑条（`conversation.composer.dock`）与设置页说明区块（`settings.section`）。
- 只读接口 `GET /dsh-peak-hours/status`，支持 `?at=<Unix 毫秒>`、`?days=<1–14>`。
- Agent 工具 `dsh_peak_hours`（`status` / `forecast` 两种动作），让模型自己把长任务安排到空闲时段。
- 纯函数时段核心 `lib/peak-hours.js`：北京时间权威轴、跨日/跨周末/跨节假日的谷段边界、
  非北京时区的本地时间换算。
- 配置项：`exposeTool`、`weekendAlwaysOffPeak`、`holidayDays`、`workdayOverrides`、
  `peakWindows`、`localOffsetMinutes`。
- 20 个自测用例（核心 12 + Host 集成 8），零依赖、无需构建。

### 规则依据

- 工作日高峰：北京时间 09:00–12:00、14:00–18:00；其余为空闲。
- 空闲时段价格为高峰时段的一半（50%）。
- 2026-08-17：峰谷定价生效。
- 2026-08-23 00:00 起：周六、周日全天按空闲价计费（本版本采用该规则，规则版本号 `2026-08-23`）。
