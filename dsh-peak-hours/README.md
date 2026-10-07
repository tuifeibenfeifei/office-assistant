# dsh-peak-hours

> A DeepSeek API peak/off-peak pricing assistant for [DeepSeek Harness](https://github.com/topics/deepseek-harness).
> A persistent sidebar indicator shows **whether the current tier is peak or off-peak and how long until it switches**.
> Beijing time is the authority and is converted to your local time; weekends and holidays are billed off-peak all day.
> The plugin also gives the agent a `dsh_peak_hours` tool so long, compute-heavy tasks can be scheduled into the cheaper window.

English | [中文](README.zh-CN.md)

---

## Why

DeepSeek API pricing is peak/off-peak: **the off-peak price is half of the peak price**.
The official rules are expressed in **Beijing time**, which is not where everyone runs dsh, and the weekend rule changed
once already — so guessing from memory easily lands a long task in the expensive window.

This plugin does three things:

1. **Visibility** — a persistent indicator in the sidebar and under the composer: peak (red) / off-peak half price (green),
   time left in the current tier, and when the next switch happens. It checks once when dsh opens, then refreshes on an interval.
2. **Correctness** — the tier is computed on the Beijing-time axis and displayed in both Beijing and your local time.
   Weekday peak is `09:00–12:00` and `14:00–18:00`; Saturday and Sunday are off-peak all day; holidays and makeup workdays are configurable.
3. **Action** — the `dsh_peak_hours` tool lets the agent itself decide whether to wait for the cheaper window.

## Official rules (sources)

| Item | Value |
| --- | --- |
| Weekday peak | 09:00–12:00 and 14:00–18:00 Beijing time (start inclusive, end exclusive) |
| Weekday off-peak | everything else |
| Weekend | **all day Saturday and Sunday** billed at the off-peak price |
| Price relation | off-peak price is half of the peak price (50%) |
| Effective | peak/off-peak pricing from 2026-08-17; weekend off-peak pricing from 2026-08-23 00:00 |

Sources: [Securities Times — DeepSeek raises prices again, weekends no longer split peak/off-peak](https://www.stcn.com/article/detail/4103775.html),
[Sina Finance — off-peak costs half of peak, effective August 17](https://finance.sina.cn/2026-08-13/detail-inineqhh4658.d.html?vt=4),
[DeepSeek API pricing docs](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/).

The ruleset version `2026-08-23` is shown in the UI; upstream changes will be tracked in the CHANGELOG.

## Features

- **Sidebar indicator** (`sidebar.panellist`): colour + tier + countdown, click to expand details.
- **Composer dock strip** (`conversation.composer.dock`): visible while you write a prompt.
- **Settings section** (`settings.section`): the rules, the live status, and a manual refresh.
- **Read-only HTTP endpoint** `GET /dsh-peak-hours/status` with `?at=<unix ms>` and `?days=<1–14>`.
- **Agent tool** `dsh_peak_hours`:
  - `action: 'status'` — tier, time left, next switch, next off-peak/peak moment, plus a quotable summary;
  - `action: 'forecast'` — also returns the per-day schedule for the next 1–14 days.
- **Config**: weekend rule toggle, holiday list, makeup-workday list, custom peak windows, local offset override.

## Install

### From a local directory (development)

Put this repository anywhere, then install it through the dsh plugin manager using that absolute directory as `target`.
Do **not** hand-write the profile's `package.json` or `cordis.patch.yml`, and do not run pnpm in the profile directory —
the install action owns those steps.

### From GitHub

Once pushed, install it from the plugin manager by giving it this repository's URL.

### Configure

After installing, edit the profile's `cordis.patch.yml` (or use the plugin manager's config UI). Every field is optional:

```yaml
- id: peak-hours
  name: 'dsh-peak-hours'
  config:
    exposeTool: true            # register the dsh_peak_hours tool (false keeps only the UI indicator)
    weekendAlwaysOffPeak: true  # weekends off-peak all day (current official rule)
    holidayDays:                # extra days billed off-peak (Beijing time)
      - '2026-10-01'
    workdayOverrides:           # weekends forced back to weekday peak/off-peak billing
      - '2026-10-10'
    peakWindows:                # override weekday peak windows (invalid values fall back to the official defaults)
      - start: '09:00'
        end: '12:00'
      - start: '14:00'
        end: '18:00'
    localOffsetMinutes: 480     # override the local UTC offset in minutes; defaults to the runtime time zone
```

## Development

Zero dependencies: no build step, no `npm install`.

```bash
node tests/run.js          # 20 cases: 12 core time logic + 8 host integration
node --check index.js      # syntax check
```

Layout:

```
index.js                Host half: read-only route, agent tool, peakHours service
client.js               Client half: sidebar indicator, composer strip, settings section
lib/peak-hours.js       Pure core: Beijing-time math, tier decision, segment bounds, forecast
cordis.patch.yml        Inserts this plugin's host row into the profile (with tunable config)
dsh.plugin.json         Plugin metadata
tests/                  Cases (small in-process runner, no subprocess dependency)
```

### Why the time logic is pure

The mistakes that actually happen here are about **day boundaries and time zones**, not about misremembering the rule:
an off-peak segment that spans days (night, weekend) collapses to a wrong conclusion such as
"switches on Sunday 24:00" if it is bounded by the calendar day. `lib/peak-hours.js` therefore reads no global state and
does not depend on the runtime time zone — every decision is a pure function of an absolute instant plus an explicit offset,
covered by cases for cross-day, cross-weekend, cross-holiday, and non-Beijing zones (UTC+7, UTC−8).

## How it differs from similar plugins

Several peak/off-peak indicators already exist ([dsh-peak](https://github.com/ibrhr/dsh-peak),
[dsh-peak-indicator](https://github.com/future007s/dsh-peak-indicator),
[dsh-peak-pricing-warning](https://github.com/Imnotndesh/dsh-peak-pricing-warning),
[dsh-pricing-badge](https://github.com/ZhangBo-cmd/dsh-pricing-badge)). This plugin chooses:

- **Agent-facing**: `dsh_peak_hours` lets the model decide whether to postpone long work, not just inform a human.
- **Time-zone correct**: computed on Beijing time, displayed in local time.
- **Complete weekend/holiday rule**: the 2026-08-23 all-day off-peak weekend rule plus holidays and makeup workdays,
  all inside a unit-tested rules module.

## License

[MIT](LICENSE)
