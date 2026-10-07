/**
 * dsh-peak-hours — Host half.
 *
 * 提供两件事：
 *   1. 一个 HTTP 只读接口，供本插件的 Client 半侧取当前峰谷状态；
 *   2. 一个 Agent 工具 `dsh_peak_hours`，让模型自己能问"现在是不是高峰、
 *      下一个空闲时段什么时候开始"，从而把算力密集的长任务挑到半价时段执行。
 *
 * 所有时间判断都委托给 lib/peak-hours.js（纯函数、已单测覆盖）。
 * 本模块不写任何文件、不访问网络，只读系统时钟。
 */

import { analyze, buildRules, summarizeForAgent, RULESET_VERSION } from './lib/peak-hours.js'

/** 依赖：仅 webServer（注册只读接口）。tools 放可选注入，缺少时插件照常工作。 */
export const inject = ['webServer']

/** 只读接口路径。Client 半侧用同一个常量，改动时需要同步 client.js。 */
export const STATUS_PATH = '/dsh-peak-hours/status'

const JSON_HEADERS = {
  'content-type': 'application/json; charset=utf-8',
  'cache-control': 'no-store',
}

/** 把分析结果包成接口响应。 */
function payloadOf(ctx, request) {
  const url = new URL(request.url ?? STATUS_PATH, 'http://localhost')
  const atText = url.searchParams.get('at')
  const at = atText === null ? undefined : Number(atText)
  const daysText = url.searchParams.get('days')
  const forecastDays = daysText === null ? undefined : Number(daysText)

  const result = analyze({
    at: Number.isFinite(at) ? at : undefined,
    forecastDays: Number.isFinite(forecastDays) ? forecastDays : undefined,
    rules: ctx.peakHours.rules,
  })

  return {
    ok: true,
    plugin: 'dsh-peak-hours',
    result,
    agentSummary: summarizeForAgent(result),
  }
}

/** 写 JSON 响应；响应已发出时不重复写。 */
function send(response, status, body) {
  if (response.headersSent || response.writableEnded) return
  const text = JSON.stringify(body)
  response.writeHead(status, { ...JSON_HEADERS, 'content-length': Buffer.byteLength(text) })
  response.end(text)
}

/**
 * @param {object} ctx - Host Cordis context。
 * @param {object} [config] - profile 里本插件行的 config（见 README 的配置表）。
 */
export function apply(ctx, config = {}) {
  const rules = buildRules({
    peakWindows: config.peakWindows,
    weekendAlwaysOffPeak: config.weekendAlwaysOffPeak,
    holidayDays: config.holidayDays,
    workdayOverrides: config.workdayOverrides,
    localOffsetMinutes: config.localOffsetMinutes,
    rulesetVersion: config.rulesetVersion,
  })

  /**
   * 插件自己的服务对象：Client 走 HTTP，Agent 工具与后续扩展走这个对象，
   * 两条路径共用同一份规则与分析实现，不重复实现业务逻辑。
   */
  const service = {
    rules,
    rulesetVersion: rules.rulesetVersion || RULESET_VERSION,
    /** 当前状态快照。 */
    status(options = {}) {
      return analyze({ at: options.at, forecastDays: options.forecastDays, rules })
    },
    /** 给模型看的一句话结论。 */
    summary(options = {}) {
      return summarizeForAgent(analyze({ at: options.at, forecastDays: options.forecastDays ?? 4, rules }))
    },
  }

  ctx.effect(() => ctx.provide('peakHours', service), 'dsh-peak-hours: peakHours service')

  // ---- 只读 HTTP 接口（Client 半侧的数据来源）
  const handler = (request, response) => {
    try {
      send(response, 200, payloadOf({ peakHours: service }, request))
    } catch (error) {
      ctx.logger?.warn?.(`[dsh-peak-hours] status route failed: ${error?.message ?? error}`)
      send(response, 500, { ok: false, message: String(error?.message ?? error) })
    }
  }
  ctx.effect(
    () => ctx.webServer.register({ kind: 'exact', path: STATUS_PATH, handler }),
    'dsh-peak-hours: status route',
  )

  // ---- Agent 工具（可选：profile 未启用 tools 时静默跳过，不影响插件其余部分）
  if (config.exposeTool !== false) {
    ctx.inject?.(['tools'], (toolsCtx) => {
      try {
        registerTool(toolsCtx, service)
      } catch (error) {
        toolsCtx.logger?.warn?.(`[dsh-peak-hours] tool registration skipped: ${error?.message ?? error}`)
      }
    })
  }

  ctx.logger?.info?.(
    `[dsh-peak-hours] ready — ruleset ${service.rulesetVersion}, status route ${STATUS_PATH}`,
  )
}

/**
 * 注册 `dsh_peak_hours` 工具。
 *
 * 用普通对象字面量声明（而非 import defineTool），这样插件不依赖任何 dsh 内部包：
 * 只要 profile 启用了 tools 服务就能注册，dsh 内部结构变化也不会让插件加载失败。
 *
 * @param {object} ctx - 提供了 tools 服务的 context。
 * @param {object} service - 本插件的服务对象。
 */
function registerTool(ctx, service) {
  ctx.tools.register({
    name: 'dsh_peak_hours',
    description:
      '查询 DeepSeek API 当前处于高峰时段还是空闲时段（空闲价为高峰价的一半），并给出下一次切换、' +
      '下一次空闲时段与未来几天的时段安排。当你要执行耗时较长、算力密集的任务时，可以用它判断是否值得等到空闲时段再做。',
    parameters: {
      action: {
        type: 'string',
        enum: ['status', 'forecast'],
        description: 'status 返回当前档期与切换时间；forecast 额外返回未来几天的每日时段安排。',
      },
      at: {
        type: 'integer',
        description: '要查询的绝对时刻（Unix 毫秒）。省略表示查询现在。',
      },
      days: {
        type: 'integer',
        description: 'forecast 需要展望的天数（1–14，默认 3）。',
      },
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          tier: { type: 'string', required: true, description: 'peak 或 off-peak。' },
          isPeak: { type: 'boolean', required: true },
          isOffPeak: { type: 'boolean', required: true },
          discountRate: { type: 'number', required: true, description: '空档折扣系数，0.5 表示半价。' },
          beijingTimeText: { type: 'string', required: true, description: '当前北京时间。' },
          localTimeText: { type: 'string', required: true, description: '当前本地时间。' },
          remainingText: { type: 'string', required: true, description: '当前档期剩余时长。' },
          nextChangeText: { type: 'string', required: true, description: '下一次切换的北京时间。' },
          nextChangeTo: { type: 'string', required: true, description: '切换后的档期。' },
          nextOffPeakText: { type: 'string', description: '下一次空闲时段的开始时间。' },
          nextPeakText: { type: 'string', description: '下一次高峰时段的开始时间。' },
          summary: { type: 'string', required: true, description: '面向读者的结论，可直接引用。' },
          forecast: {
            type: 'array',
            description: '每日时段安排（仅 forecast 动作返回）。',
            items: {
              type: 'object',
              additionalProperties: false,
              properties: {
                dayKey: { type: 'string', required: true },
                weekdayLabel: { type: 'string', required: true },
                allOffPeak: { type: 'boolean', required: true },
                peakWindowsText: { type: 'string', required: true },
              },
            },
          },
        },
      },
      render: (_args, value) => [{ type: 'text', text: JSON.stringify(value) }],
    },
    execute(args = {}) {
      const forecastDays = args.action === 'forecast' ? args.days : 1
      const result = service.status({ at: args.at, forecastDays })
      return {
        tier: result.tier,
        isPeak: result.isPeak,
        isOffPeak: result.isOffPeak,
        discountRate: result.discountRate,
        beijingTimeText: result.beijingTimeText,
        localTimeText: result.localTimeText,
        remainingText: result.currentPeriod.remainingText,
        nextChangeText: result.nextChange.beijingTimeText,
        nextChangeTo: result.nextChange.toTier,
        ...(result.nextOffPeakText === null ? {} : { nextOffPeakText: result.nextOffPeakText }),
        ...(result.nextPeakText === null ? {} : { nextPeakText: result.nextPeakText }),
        summary: summarizeForAgent(result),
        ...(args.action === 'forecast'
          ? {
              forecast: result.forecast.map((day) => ({
                dayKey: day.dayKey,
                weekdayLabel: day.weekdayLabel,
                allOffPeak: day.allOffPeak,
                peakWindowsText: day.allOffPeak
                  ? '全天按空闲价'
                  : day.peakWindows.map((w) => `${w.startText}–${w.endText}`).join('、'),
              })),
            }
          : {}),
      }
    },
  })
}
