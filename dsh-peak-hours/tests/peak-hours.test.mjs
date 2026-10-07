/**
 * dsh-peak-hours 核心逻辑测试（node --test，零依赖）。
 *
 * 覆盖：工作日峰谷边界、周末全天谷价、节假日覆盖与强制工作日、
 * 下一次切换计算、时区正确性（北京时间算 / 本地时间显示）、结论文案。
 */
import assert from 'node:assert/strict';

import { test } from './harness.mjs';

import {
  BEIJING_OFFSET_MINUTES,
  DEFAULT_PEAK_WINDOWS,
  OFF_PEAK_DISCOUNT,
  TIER,
  analyze,
  buildRules,
  dayKeyOf,
  findNext,
  formatDuration,
  isWeekend,
  normalizePeakWindows,
  parseClock,
  summarizeForAgent,
  tierAt,
  wallClockOf,
} from '../lib/peak-hours.js';

/** 由北京时间墙上时间构造绝对时刻。 */
const bj = (...args) => Date.UTC(...args) - BEIJING_OFFSET_MINUTES * 60_000;

// 2026-10-05 是周一，2026-10-10 是周六，2026-10-11 是周日
const MON = (h, m = 0) => bj(2026, 9, 5, h, m);
const TUE = (h, m = 0) => bj(2026, 9, 6, h, m);
const SAT = (h, m = 0) => bj(2026, 9, 10, h, m);
const SUN = (h, m = 0) => bj(2026, 9, 11, h, m);

test('北京时间换算不依赖运行环境时区', () => {
  const wall = wallClockOf(MON(9, 30), BEIJING_OFFSET_MINUTES);
  assert.equal(wall.hour, 9);
  assert.equal(wall.minute, 30);
  assert.equal(wall.weekday, 1, '应为周一');
  assert.equal(dayKeyOf(MON(9, 30)), '2026-10-05');
});

test('工作日：高峰窗口内为高峰，区间外为空闲（左闭右开）', () => {
  const rules = buildRules();
  assert.equal(tierAt(MON(8, 59), rules).tier, TIER.OFF_PEAK);
  assert.equal(tierAt(MON(9, 0), rules).tier, TIER.PEAK, '9:00 整点为高峰（左闭）');
  assert.equal(tierAt(MON(11, 59), rules).tier, TIER.PEAK);
  assert.equal(tierAt(MON(12, 0), rules).tier, TIER.OFF_PEAK, '12:00 整点已离开高峰（右开）');
  assert.equal(tierAt(MON(13, 59), rules).tier, TIER.OFF_PEAK, '午休属空闲');
  assert.equal(tierAt(MON(14, 0), rules).tier, TIER.PEAK);
  assert.equal(tierAt(MON(17, 59), rules).tier, TIER.PEAK);
  assert.equal(tierAt(MON(18, 0), rules).tier, TIER.OFF_PEAK);
  assert.equal(tierAt(MON(23, 59), rules).tier, TIER.OFF_PEAK);
  assert.equal(tierAt(bj(2026, 9, 6, 3, 0), rules).tier, TIER.OFF_PEAK, '凌晨属空闲');
});

test('周末：周六周日全天按低谷价（本次调价的关键规则）', () => {
  const rules = buildRules();
  for (const hour of [9, 10, 14, 15, 17]) {
    const sat = tierAt(SAT(hour, 30), rules);
    assert.equal(sat.tier, TIER.OFF_PEAK, `周六 ${hour}:30 应为空闲`);
    assert.equal(sat.reason, 'weekend');
    const sun = tierAt(SUN(hour, 30), rules);
    assert.equal(sun.tier, TIER.OFF_PEAK, `周日 ${hour}:30 应为空闲`);
  }
  assert.equal(isWeekend(SAT(12)), true);
  assert.equal(isWeekend(SUN(12)), true);
  assert.equal(isWeekend(MON(12)), false);
});

test('周末规则可关闭：关闭后周六按工作日峰谷计费', () => {
  const rules = buildRules({ weekendAlwaysOffPeak: false });
  assert.equal(tierAt(SAT(10, 0), rules).tier, TIER.PEAK);
  assert.equal(tierAt(SAT(13, 0), rules).tier, TIER.OFF_PEAK);
});

test('节假日整天按空闲价；workdayOverrides 可把某天强制当工作日', () => {
  const holiday = buildRules({ holidayDays: ['2026-10-06'] });
  assert.equal(tierAt(TUE(10, 0), holiday).tier, TIER.OFF_PEAK);
  assert.equal(tierAt(TUE(10, 0), holiday).reason, 'holiday');

  // 调休：某周六被强制当工作日 → 恢复峰谷
  const makeup = buildRules({ workdayOverrides: ['2026-10-10'] });
  assert.equal(tierAt(SAT(10, 0), makeup).tier, TIER.PEAK);
});

test('自定义高峰窗口生效，非法输入回落默认值', () => {
  const custom = buildRules({ peakWindows: [{ start: '08:00', end: '10:00' }] });
  assert.equal(tierAt(MON(8, 30), custom).tier, TIER.PEAK, '8:30 落在自定义窗口 08:00–10:00 内');
  assert.equal(tierAt(MON(9, 30), custom).tier, TIER.PEAK, '9:30 同样在窗口内');
  assert.equal(tierAt(MON(11, 0), custom).tier, TIER.OFF_PEAK, '11:00 已离开自定义窗口');
  assert.equal(tierAt(MON(13, 0), custom).tier, TIER.OFF_PEAK, '自定义后不再有默认的午后窗口');

  const broken = buildRules({ peakWindows: [{ start: '25:00', end: '10:00' }, { start: 'x' }] });
  assert.deepEqual(
    broken.peakWindows,
    DEFAULT_PEAK_WINDOWS.map((w) => ({ ...w })),
    '非法窗口应回落到官方默认值',
  );
  assert.equal(parseClock('9:05'), 545);
  assert.equal(parseClock('24:00'), null);
  assert.equal(normalizePeakWindows([]).length, 2);
});

test('下一次切换：长度正确且落点属于目标档期', () => {
  const rules = buildRules();

  const peakStart = analyze({ at: MON(9, 0), rules, forecastDays: 1 });
  assert.equal(peakStart.tier, TIER.PEAK);
  assert.equal(peakStart.nextChange.atMs, MON(12, 0), '上午高峰应于 12:00 结束');
  assert.equal(peakStart.currentPeriod.remainingMs, 3 * 60 * 60_000);
  assert.equal(peakStart.currentPeriod.remainingText, '3 小时');
  assert.equal(peakStart.nextChange.toTier, TIER.OFF_PEAK);
  assert.equal(tierAt(peakStart.nextChange.atMs, rules).tier, TIER.OFF_PEAK, '落点必须真的属于目标档期');

  const lunch = analyze({ at: MON(12, 30), rules, forecastDays: 1 });
  assert.equal(lunch.tier, TIER.OFF_PEAK);
  assert.equal(lunch.nextChange.atMs, MON(14, 0), '午休空闲应于 14:00 结束');
  assert.equal(lunch.nextChange.inText, '1 小时 30 分');
  assert.equal(lunch.nextChange.toTier, TIER.PEAK);

  const afternoon = analyze({ at: MON(14, 30), rules, forecastDays: 1 });
  assert.equal(afternoon.tier, TIER.PEAK);
  assert.equal(afternoon.nextChange.atMs, MON(18, 0), '下午高峰应于 18:00 结束');
  assert.equal(afternoon.currentPeriod.remainingMs, 3.5 * 60 * 60_000);
  assert.equal(tierAt(afternoon.nextChange.atMs, rules).tier, TIER.OFF_PEAK);

  const evening = analyze({ at: MON(20, 0), rules, forecastDays: 1 });
  assert.equal(evening.tier, TIER.OFF_PEAK);
  assert.equal(evening.nextChange.atMs, bj(2026, 9, 6, 9, 0), '晚间空闲应延续到次日 9:00');
  assert.equal(evening.nextChange.toTier, TIER.PEAK);
  assert.equal(evening.currentPeriod.remainingText, '13 小时');
  // 谷段自"上一个高峰窗口结束"起算：18:00–20:00 属于同一段夜间空闲
  assert.equal(evening.currentPeriod.startMs, MON(18, 0), '夜间谷段从 18:00（末个高峰结束）起算');
  assert.equal(evening.offPeakElapsedMs, 2 * 3_600_000, '18:00 到 20:00 共 2 小时');

  const weekend = analyze({ at: SAT(15, 0), rules, forecastDays: 1 });
  assert.equal(weekend.nextChange.atMs, bj(2026, 9, 12, 9, 0), '周末空闲应延续到周一 9:00');
  assert.equal(weekend.currentPeriod.remainingMs, 42 * 60 * 60_000, '周六 15:00 到周一 9:00 共 42 小时');
});

test('findNext 能找到下一次高峰/空闲，且从空闲进入高峰是下一个工作日 9:00', () => {
  const rules = buildRules();
  assert.equal(findNext(MON(12, 30), TIER.PEAK, rules), MON(14, 0));
  assert.equal(findNext(MON(20, 0), TIER.PEAK, rules), bj(2026, 9, 6, 9, 0));
  // 周五晚间 → 下一次高峰要跳过周末到周一 9:00
  const FRI = (h, m = 0) => bj(2026, 9, 9, h, m);
  assert.equal(findNext(FRI(20, 0), TIER.PEAK, rules), bj(2026, 9, 12, 9, 0));
  assert.equal(findNext(SAT(10, 0), TIER.PEAK, rules), bj(2026, 9, 12, 9, 0), '周六的下一次高峰在周一');
});

test('折扣与结论文案', () => {
  const rules = buildRules();

  const off = analyze({ at: MON(20, 0), rules, forecastDays: 2 });
  assert.equal(off.isOffPeak, true);
  assert.equal(off.discountRate, OFF_PEAK_DISCOUNT);
  assert.equal(off.discountRate, 0.5, '半价');
  assert.match(off.discountText, /一半/);

  const peak = analyze({ at: MON(10, 0), rules, forecastDays: 2 });
  assert.equal(peak.isPeak, true);
  assert.equal(peak.discountRate, 1);
  assert.equal(peak.offPeakElapsedMs, 0);

  const text = summarizeForAgent(peak);
  assert.match(text, /高峰时段（标准价）/);
  assert.match(text, /下一次空闲从北京时间/);
  assert.match(text, /今日高峰窗口/);

  const weekendText = summarizeForAgent(analyze({ at: SAT(10, 0), rules, forecastDays: 1 }));
  assert.match(weekendText, /全天按空闲价计费（周末）/);
});

test('时区：以北京时间计算，按本地偏移显示', () => {
  // 同一绝对时刻，在 UTC+7（曼谷）与 UTC+8（北京）下应得到不同本地墙上时间，但同一档期
  const at = MON(9, 30); // 北京 9:30 高峰
  const beijing = analyze({ at, rules: buildRules({ localOffsetMinutes: 480 }), forecastDays: 1 });
  const bangkok = analyze({ at, rules: buildRules({ localOffsetMinutes: 420 }), forecastDays: 1 });

  assert.equal(beijing.tier, TIER.PEAK);
  assert.equal(bangkok.tier, TIER.PEAK, '档期只由北京时间决定，与本地时区无关');
  assert.equal(beijing.sameAsBeijing, true);
  assert.equal(bangkok.sameAsBeijing, false);
  assert.match(beijing.localTimeText, /09:30/);
  assert.match(bangkok.localTimeText, /08:30/, 'UTC+7 本地时间应比北京早一小时');
  assert.match(bangkok.beijingTimeText, /09:30/, '北京时间轴不变');

  // 洛杉矶（UTC-7）下，北京 9:30 是前一天 18:30
  const la = analyze({ at, rules: buildRules({ localOffsetMinutes: -420 }), forecastDays: 1 });
  assert.match(la.localTimeText, /18:30/);
  assert.equal(la.tier, TIER.PEAK);
});

test('展望天数与每日安排', () => {
  const rules = buildRules({ holidayDays: ['2026-10-07'] });
  const result = analyze({ at: MON(10, 0), rules, forecastDays: 4 });
  assert.equal(result.forecast.length, 4);
  assert.deepEqual(result.forecast.map((d) => d.dayKey), [
    '2026-10-05', '2026-10-06', '2026-10-07', '2026-10-08',
  ]);
  assert.equal(result.forecast[0].peakWindows.length, 2);
  assert.equal(result.forecast[0].peakWindows[0].startText, '09:00');
  assert.equal(result.forecast[2].allOffPeak, true, '10-07 是配置的节假日');
  assert.equal(result.forecast[2].reason, 'holiday');

  const clamped = analyze({ at: MON(10, 0), rules, forecastDays: 99 });
  assert.ok(clamped.forecast.length <= 14, '展望天数应被限制');
});

test('工具函数与不变量', () => {
  assert.equal(formatDuration(0), '0 分');
  assert.equal(formatDuration(45 * 60_000), '45 分');
  assert.equal(formatDuration(2 * 3_600_000 + 13 * 60_000), '2 小时 13 分');
  assert.equal(formatDuration(26 * 3_600_000), '1 天 2 小时');

  assert.throws(() => analyze({ at: 'not-a-date' }), TypeError);

  // 结果必须可 JSON 序列化（Host 通过 HTTP 返回给 Client）
  const result = analyze({ at: MON(10, 0), forecastDays: 2 });
  assert.equal(JSON.parse(JSON.stringify(result)).tier, TIER.PEAK);

  // 传入的 at 不应被修改
  const probe = new Date(MON(10, 0));
  analyze({ at: probe });
  assert.equal(probe.getTime(), MON(10, 0));
});
