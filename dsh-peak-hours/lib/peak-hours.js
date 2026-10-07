/**
 * dsh-peak-hours — 峰谷时段计算核心（纯函数，零依赖，可独立测试）。
 *
 * 权威时区是**北京时间（UTC+8）**：官方规则以北京时间为准，与运行 dsh 的机器所在时区无关。
 * 本地时间只用于显示，因此这里同时给出北京时间的墙上时间与本地墙上时间。
 *
 * 规则（依据 DeepSeek 官方公告，2026-08-23 00:00 起生效）：
 *   1. 周六、周日全天按**低谷**价计费（周末不再区分峰谷）。
 *   2. 工作日高峰时段为北京时间 9:00–12:00 与 14:00–18:00（区间左闭右开）。
 *   3. 其余时间均为空闲时段。
 *   4. 低谷价格为高峰价格的一半。
 *
 * 本模块不做系统时钟读表以外的任何 IO，也不修改调用方传入的对象。
 */

/** 北京时间固定偏移（分钟）。中国大陆不实行夏令时，固定 +8 即可。 */
export const BEIJING_OFFSET_MINUTES = 8 * 60;

/** 权威规则版本；规则变化时必须同步更新，界面上会展示它。 */
export const RULESET_VERSION = '2026-08-23';

/** 生效日期（北京时间当日 00:00 起）。此前的时间按旧规则，本插件不覆盖旧规则。 */
export const RULESET_EFFECTIVE_FROM = '2026-08-23';

/** 工作日高峰时段（北京时间，左闭右开，分钟计）。 */
export const DEFAULT_PEAK_WINDOWS = Object.freeze([
  Object.freeze({ startMinute: 9 * 60, endMinute: 12 * 60 }),
  Object.freeze({ startMinute: 14 * 60, endMinute: 18 * 60 }),
]);

/** 计费档期。 */
export const TIER = Object.freeze({ PEAK: 'peak', OFF_PEAK: 'off-peak' });

/** 空闲时段价格相对高峰的折扣（0.5 = 半价）。 */
export const OFF_PEAK_DISCOUNT = 0.5;

const MINUTE_MS = 60_000;
const DAY_MS = 86_400_000;
const WEEKDAY_LABELS_ZH = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

/** 把任意输入解析成可用的 Date；非法输入抛错，避免静默给出错误结论。 */
function toDate(value) {
  const date = value instanceof Date ? new Date(value.getTime()) : new Date(value ?? Date.now());
  if (Number.isNaN(date.getTime())) throw new TypeError('dsh-peak-hours: 时间参数无法解析为有效日期');
  return date;
}

function pad2(value) {
  return value < 10 ? `0${value}` : String(value);
}

/**
 * 把某个绝对时刻在指定 UTC 偏移下的墙上字段读出来。
 * 用 Date 的 UTC 字段读取"偏移后的时间戳"，因此结果与运行环境的 TZ 无关。
 * @param {number} epochMs - 绝对时刻。
 * @param {number} offsetMinutes - 目标时区相对 UTC 的分钟偏移。
 */
export function wallClockOf(epochMs, offsetMinutes) {
  const shiftedMs = epochMs + offsetMinutes * MINUTE_MS;
  const shifted = new Date(shiftedMs);
  const year = shifted.getUTCFullYear();
  const month = shifted.getUTCMonth() + 1;
  const day = shifted.getUTCDate();
  const hour = shifted.getUTCHours();
  const minute = shifted.getUTCMinutes();
  return {
    year,
    month,
    day,
    hour,
    minute,
    weekday: shifted.getUTCDay(),
    minuteOfDay: hour * 60 + minute,
    /**
     * 该墙上日期 00:00 对应的绝对时刻。
     *
     * 用"墙上日期 → UTC 刻度 → 减偏移"三步写清楚，不使用取模技巧：
     * UTC(year, month-1, day) 得到的是"把墙上时间当作 UTC"的刻度，
     * 减去该时区偏移就回到真正的绝对时刻。这样日界不会整体平移一个时区。
     */
    dayStartMs: Date.UTC(year, month - 1, day, 0, 0, 0, 0) - offsetMinutes * MINUTE_MS,
  };
}

/** 墙上日期键，形如 `2026-10-07`。 */
export function dayKeyOf(epochMs, offsetMinutes = BEIJING_OFFSET_MINUTES) {
  const wall = wallClockOf(epochMs, offsetMinutes);
  return `${wall.year}-${pad2(wall.month)}-${pad2(wall.day)}`;
}

/** 把墙上时间格式化，形如 `2026-10-07 14:23`。 */
export function formatWallClock(wall, { withWeekday = false, withSeconds = false } = {}) {
  const base = `${wall.year}-${pad2(wall.month)}-${pad2(wall.day)} ${pad2(wall.hour)}:${pad2(wall.minute)}`;
  const seconds = withSeconds ? `:${pad2(wall.second ?? 0)}` : '';
  const weekday = withWeekday ? ` ${WEEKDAY_LABELS_ZH[wall.weekday]}` : '';
  return `${base}${seconds}${weekday}`;
}

/** 把分钟数格式化为 `HH:mm`。 */
export function formatMinuteOfDay(minuteOfDay) {
  const normalized = ((minuteOfDay % 1440) + 1440) % 1440;
  return `${pad2(Math.floor(normalized / 60))}:${pad2(normalized % 60)}`;
}

/** 把毫秒时长格式化为紧凑中文文案，例如 `2 小时 13 分`。 */
export function formatDuration(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return '0 分';
  const totalMinutes = Math.floor(ms / MINUTE_MS);
  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;
  const parts = [];
  if (days > 0) parts.push(`${days} 天`);
  if (hours > 0) parts.push(`${hours} 小时`);
  if (minutes > 0 || parts.length === 0) parts.push(`${minutes} 分`);
  return parts.join(' ');
}

/** 把任意时刻对齐到北京时间的当日 00:00。 */
export function beijingDayStart(epochMs) {
  const wall = wallClockOf(epochMs, BEIJING_OFFSET_MINUTES);
  return wall.dayStartMs;
}

/** 是否周末（北京时间）。 */
export function isWeekend(epochMs) {
  const weekday = wallClockOf(epochMs, BEIJING_OFFSET_MINUTES).weekday;
  return weekday === 0 || weekday === 6;
}

/** 解析 `HH:mm` 或 `H:mm` 成当日分钟数；失败返回 null。 */
export function parseClock(text) {
  if (typeof text !== 'string') return null;
  const match = /^\s*(\d{1,2})\s*:\s*(\d{2})\s*$/.exec(text);
  if (!match) return null;
  const hour = Number(match[1]);
  const minute = Number(match[2]);
  if (hour > 23 || minute > 59) return null;
  return hour * 60 + minute;
}

/**
 * 规范化配置里的高峰窗口；非法项被丢弃，空结果回落到官方默认值。
 * @param {unknown} windows - `[{ start: 'HH:mm', end: 'HH:mm' }]` 或 `[{ startMinute, endMinute }]`。
 */
export function normalizePeakWindows(windows) {
  const list = [];
  if (Array.isArray(windows)) {
    for (const entry of windows) {
      if (entry === null || typeof entry !== 'object') continue;
      const start = Number.isInteger(entry.startMinute)
        ? entry.startMinute
        : parseClock(entry.start);
      const end = Number.isInteger(entry.endMinute)
        ? entry.endMinute
        : parseClock(entry.end);
      if (!Number.isInteger(start) || !Number.isInteger(end)) continue;
      if (start < 0 || end > 1440 || start >= end) continue;
      list.push({ startMinute: start, endMinute: end });
    }
  }
  if (list.length === 0) return DEFAULT_PEAK_WINDOWS.map((w) => ({ ...w }));
  list.sort((a, b) => a.startMinute - b.startMinute);
  return list;
}

/**
 * 构建一次分析所需的规则对象。
 * @param {object} [config]
 * @param {Array} [config.peakWindows] - 工作日高峰窗口覆盖。
 * @param {boolean} [config.weekendAlwaysOffPeak] - 周末是否全天空闲（默认 true）。
 * @param {string[]} [config.holidayDays] - 额外按空闲价计费的自然日（北京时间）。
 * @param {string[]} [config.workdayOverrides] - 被强制当作工作日处理、仍按峰谷计费的自然日。
 * @param {number} [config.localOffsetMinutes] - 显示用本地偏移；未给则用运行环境默认时区。
 */
export function buildRules(config = {}) {
  const localOffsetMinutes = Number.isInteger(config.localOffsetMinutes)
    ? config.localOffsetMinutes
    : -new Date().getTimezoneOffset();
  return {
    peakWindows: normalizePeakWindows(config.peakWindows),
    weekendAlwaysOffPeak: config.weekendAlwaysOffPeak !== false,
    holidayDays: new Set(Array.isArray(config.holidayDays) ? config.holidayDays.filter((d) => typeof d === 'string') : []),
    workdayOverrides: new Set(Array.isArray(config.workdayOverrides) ? config.workdayOverrides.filter((d) => typeof d === 'string') : []),
    localOffsetMinutes,
    rulesetVersion: typeof config.rulesetVersion === 'string' && config.rulesetVersion ? config.rulesetVersion : RULESET_VERSION,
  };
}

/** 某个自然日（北京时间）按其日期键应遵守的档期安排。 */
function dayPlanOf(epochMs, rules) {
  const wall = wallClockOf(epochMs, BEIJING_OFFSET_MINUTES);
  const key = `${wall.year}-${pad2(wall.month)}-${pad2(wall.day)}`;
  const isHoliday = rules.holidayDays.has(key);
  const forcedWorkday = rules.workdayOverrides.has(key);
  const weekend = wall.weekday === 0 || wall.weekday === 6;

  let allOffPeak = false;
  let reason = 'weekday';
  if (isHoliday) {
    allOffPeak = true;
    reason = 'holiday';
  } else if (weekend && rules.weekendAlwaysOffPeak && !forcedWorkday) {
    allOffPeak = true;
    reason = 'weekend';
  }

  return {
    dayKey: key,
    weekday: wall.weekday,
    weekdayLabel: WEEKDAY_LABELS_ZH[wall.weekday],
    dayStartMs: wall.dayStartMs,
    allOffPeak,
    reason,
    windows: allOffPeak ? [] : rules.peakWindows.map((w) => ({ ...w })),
  };
}

/**
 * 判断某个绝对时刻属于哪一档，并给出该档的起止边界。
 * @param {number} epochMs - 绝对时刻。
 * @param {object} rules - 由 {@link buildRules} 构建。
 */
export function tierAt(epochMs, rules) {
  const plan = dayPlanOf(epochMs, rules);
  const minuteOfDay = wallClockOf(epochMs, BEIJING_OFFSET_MINUTES).minuteOfDay;

  // 谷段（含全天谷价日、午休、夜间）统一按"上一高峰结束 → 下一高峰开始"成段。
  // 这样周末与相邻的谷价日会连成一段，跨日切换点自然落在下一个高峰窗口。
  const segmentStartMs = previousPeakWindowEnd(plan, rules, minuteOfDay);
  const segmentEndMs = nextPeakWindowStart(plan, rules, minuteOfDay);

  if (plan.allOffPeak) {
    return {
      tier: TIER.OFF_PEAK,
      reason: plan.reason,
      dayKey: plan.dayKey,
      windowStartMs: segmentStartMs,
      windowEndMs: segmentEndMs,
      peakWindows: plan.windows,
    };
  }

  for (const window of plan.windows) {
    if (minuteOfDay >= window.startMinute && minuteOfDay < window.endMinute) {
      return {
        tier: TIER.PEAK,
        reason: 'peak-window',
        dayKey: plan.dayKey,
        windowStartMs: plan.dayStartMs + window.startMinute * MINUTE_MS,
        windowEndMs: plan.dayStartMs + window.endMinute * MINUTE_MS,
        peakWindows: plan.windows,
      };
    }
  }

  return {
    tier: TIER.OFF_PEAK,
    reason: 'between-windows',
    dayKey: plan.dayKey,
    windowStartMs: segmentStartMs,
    windowEndMs: segmentEndMs,
    peakWindows: plan.windows,
  };
}

/**
 * 从某个时刻出发，向后找到下一个高峰窗口的绝对开始时刻。
 * 先看当日尚未开始的窗口，再以自然日为步长向后查（最多 8 天）。
 *
 * @param {{dayStartMs: number, windows: Array<{startMinute: number, endMinute: number}>}} plan - 当日安排。
 * @param {object} rules - 规则。
 * @param {number} minuteOfDay - 当前北京时间轴的当日分钟数。
 * @returns {number} 下一个高峰窗口开始时刻。
 */
function nextPeakWindowStart(plan, rules, minuteOfDay) {
  const upcomingToday = plan.windows.find((w) => w.startMinute > minuteOfDay);
  if (upcomingToday) return plan.dayStartMs + upcomingToday.startMinute * MINUTE_MS;

  for (let offset = 1; offset <= 8; offset += 1) {
    // 用当日正午取样，避免日界附近因偏移取整带来的歧义
    const probeMs = plan.dayStartMs + offset * DAY_MS + 12 * 60 * MINUTE_MS;
    const dayPlan = dayPlanOf(probeMs, rules);
    if (dayPlan.windows.length > 0) {
      return dayPlan.dayStartMs + dayPlan.windows[0].startMinute * MINUTE_MS;
    }
  }
  // 规则里完全没有高峰窗口：按一个自然日收束
  return plan.dayStartMs + DAY_MS;
}

/**
 * 某时刻之前、最近一个高峰窗口的结束时刻；之前若一直没有高峰窗口，
 * 则回落到当日 00:00。用于确定谷段的起点。
 *
 * @param {{dayStartMs: number, windows: Array<{startMinute: number, endMinute: number}>}} plan - 当日安排。
 * @param {object} rules - 规则。
 * @param {number} minuteOfDay - 当前北京时间轴的当日分钟数。
 * @returns {number} 谷段起点（绝对时刻）。
 */
function previousPeakWindowEnd(plan, rules, minuteOfDay) {
  const passedToday = plan.windows.filter((w) => w.endMinute <= minuteOfDay).pop();
  if (passedToday) return plan.dayStartMs + passedToday.endMinute * MINUTE_MS;

  for (let offset = 1; offset <= 8; offset += 1) {
    const probeMs = plan.dayStartMs - offset * DAY_MS + 12 * 60 * MINUTE_MS;
    const dayPlan = dayPlanOf(probeMs, rules);
    if (dayPlan.windows.length > 0) {
      const last = dayPlan.windows[dayPlan.windows.length - 1];
      return dayPlan.dayStartMs + last.endMinute * MINUTE_MS;
    }
  }
  return plan.dayStartMs;
}

/**
 * 完整分析：当前档期、本段剩余时间、下一次切换、未来若干天的每日安排。
 *
 * @param {object} [options]
 * @param {number|Date} [options.at] - 分析时刻，默认现在。
 * @param {object} [options.rules] - 由 {@link buildRules} 构建；未给则用默认规则。
 * @param {number} [options.forecastDays] - 需要展望的天数（1–14，默认 3）。
 * @returns {object} 纯 JSON 可序列化的分析结果。
 */
export function analyze(options = {}) {
  const at = toDate(options.at);
  const nowMs = at.getTime();
  const rules = options.rules ?? buildRules();
  const forecastDays = Math.min(14, Math.max(1, Number.isInteger(options.forecastDays) ? options.forecastDays : 3));

  const current = tierAt(nowMs, rules);
  const nextTier = current.tier === TIER.PEAK ? TIER.OFF_PEAK : TIER.PEAK;

  // 下一次切换就是本段边界；再往后找下一次进入 peak / off-peak 的时刻
  const boundaryMs = current.windowEndMs;
  const nextPeakMs = findNext(nowMs + 1, TIER.PEAK, rules);
  const nextOffPeakMs = findNext(nowMs + 1, TIER.OFF_PEAK, rules);

  const forecast = [];
  for (let index = 0; index < forecastDays; index += 1) {
    const dayMs = beijingDayStart(nowMs) + index * DAY_MS;
    const plan = dayPlanOf(dayMs, rules);
    forecast.push({
      dayKey: plan.dayKey,
      weekday: plan.weekday,
      weekdayLabel: plan.weekdayLabel,
      allOffPeak: plan.allOffPeak,
      reason: plan.reason,
      peakWindows: plan.windows.map((w) => ({
        startMinute: w.startMinute,
        endMinute: w.endMinute,
        startText: formatMinuteOfDay(w.startMinute),
        endText: formatMinuteOfDay(w.endMinute),
      })),
    });
  }

  const beijingWall = wallClockOf(nowMs, BEIJING_OFFSET_MINUTES);
  const localWall = wallClockOf(nowMs, rules.localOffsetMinutes);
  const boundaryWallBejing = wallClockOf(boundaryMs, BEIJING_OFFSET_MINUTES);
  const boundaryWallLocal = wallClockOf(boundaryMs, rules.localOffsetMinutes);

  return {
    ok: true,
    rulesetVersion: rules.rulesetVersion,
    effectiveFrom: RULESET_EFFECTIVE_FROM,
    tier: current.tier,
    isPeak: current.tier === TIER.PEAK,
    isOffPeak: current.tier === TIER.OFF_PEAK,
    tierReason: current.reason,
    discountRate: current.tier === TIER.OFF_PEAK ? OFF_PEAK_DISCOUNT : 1,
    discountText: current.tier === TIER.OFF_PEAK
      ? `空闲时段价格为高峰时段的一半（${OFF_PEAK_DISCOUNT * 100}%）`
      : '高峰时段按标准价格计费',
    atMs: nowMs,
    atIso: new Date(nowMs).toISOString(),
    beijingTimeText: formatWallClock(beijingWall, { withWeekday: true }),
    localTimeText: formatWallClock(localWall, { withWeekday: true }),
    /** 本地时区相对 UTC 的偏移（分钟），正值表示东区。 */
    localOffsetMinutes: rules.localOffsetMinutes,
    /** 本地时区是否与北京时间一致；不一致时界面上会同时显示两个时间。 */
    sameAsBeijing: rules.localOffsetMinutes === BEIJING_OFFSET_MINUTES,
    currentPeriod: {
      startMs: current.windowStartMs,
      endMs: current.windowEndMs,
      remainingMs: Math.max(0, boundaryMs - nowMs),
      remainingText: formatDuration(Math.max(0, boundaryMs - nowMs)),
      startText: formatWallClock(wallClockOf(current.windowStartMs, BEIJING_OFFSET_MINUTES)),
      endText: formatWallClock(boundaryWallBejing),
      endTextLocal: formatWallClock(boundaryWallLocal),
    },
    nextChange: {
      atMs: boundaryMs,
      toTier: nextTier,
      inMs: Math.max(0, boundaryMs - nowMs),
      inText: formatDuration(Math.max(0, boundaryMs - nowMs)),
      beijingTimeText: formatWallClock(boundaryWallBejing, { withWeekday: true }),
      localTimeText: formatWallClock(boundaryWallLocal, { withWeekday: true }),
    },
    nextPeakAtMs: nextPeakMs,
    nextPeakText: nextPeakMs === null ? null : formatWallClock(wallClockOf(nextPeakMs, BEIJING_OFFSET_MINUTES), { withWeekday: true }),
    nextOffPeakAtMs: nextOffPeakMs,
    nextOffPeakText: nextOffPeakMs === null ? null : formatWallClock(wallClockOf(nextOffPeakMs, BEIJING_OFFSET_MINUTES), { withWeekday: true }),
    /** 从空闲时段开始到此刻已过的时间；高峰时为 0。 */
    offPeakElapsedMs: current.tier === TIER.OFF_PEAK ? Math.max(0, nowMs - current.windowStartMs) : 0,
    forecast,
  };
}

/**
 * 从某个时刻起，下一次进入目标档期的绝对时刻。
 * 以 1 分钟为步长向前扫描，最多 8 天；找不到返回 null。
 */
export function findNext(fromMs, targetTier, rules) {
  const limit = fromMs + 8 * DAY_MS;
  for (let ms = fromMs; ms <= limit; ms += MINUTE_MS) {
    if (tierAt(ms, rules).tier === targetTier) return ms;
  }
  return null;
}

/**
 * 给 Agent 用的一句话结论：现在是什么档期、还剩多久、下一次切换在什么时候。
 * @param {object} result - {@link analyze} 的返回值。
 */
export function summarizeForAgent(result) {
  const tierText = result.isPeak ? '高峰时段（标准价）' : '空闲时段（半价）';
  const lines = [
    `当前：${tierText}，北京时间 ${result.beijingTimeText}（本地 ${result.localTimeText}）`,
    `本段剩余 ${result.currentPeriod.remainingText}，将在北京时间 ${result.nextChange.beijingTimeText} 切换为${result.nextChange.toTier === TIER.PEAK ? '高峰' : '空闲'}`,
  ];
  if (result.isPeak && result.nextOffPeakText) {
    lines.push(`若要把长任务放到半价时段，下一次空闲从北京时间 ${result.nextOffPeakText} 开始`);
  } else if (!result.isPeak && result.nextPeakText) {
    lines.push(`下一次高峰从北京时间 ${result.nextPeakText} 开始，可在此之前安排算力密集任务`);
  }
  const today = result.forecast[0];
  if (today) {
    lines.push(today.allOffPeak
      ? `今日（${today.dayKey} ${today.weekdayLabel}）全天按空闲价计费（${today.reason === 'weekend' ? '周末' : '节假日'}）`
      : `今日高峰窗口（北京时间）：${today.peakWindows.map((w) => `${w.startText}–${w.endText}`).join('、')}`);
  }
  return lines.join('\n');
}
