/**
 * Host 半侧的集成自测：用 mock ctx 驱动 apply()，验证
 *   · 服务注册与 rules 构建
 *   · HTTP 路由注册、请求解析与响应体
 *   · Agent 工具注册与工具返回值形状
 *   · 资源清理（effect 返回的 disposer 被调用）
 * 不需要真实 dsh 运行环境。
 */
import assert from 'node:assert/strict';

import { apply, inject, STATUS_PATH } from '../index.js';

const cases = [];
function test(name, fn) {
  cases.push({ name, fn });
}

function makeCtx({ withTools = true } = {}) {
  const disposers = [];
  const routes = [];
  const tools = [];
  const provided = new Map();
  const logs = [];

  const ctx = {
    logger: {
      info: (m) => logs.push(['info', m]),
      warn: (m) => logs.push(['warn', m]),
    },
    effect(fn, label) {
      const dispose = fn();
      disposers.push({ label, dispose });
      return dispose;
    },
    provide(name, value) {
      provided.set(name, value);
      return () => provided.delete(name);
    },
    inject(names, run) {
      if (withTools && names.includes('tools')) run({ ...ctx, tools: { register: (def) => tools.push(def) } });
    },
    webServer: {
      register({ kind, path, handler }) {
        routes.push({ kind, path, handler });
        return () => {
          const index = routes.findIndex((r) => r.path === path);
          if (index >= 0) routes.splice(index, 1);
        };
      },
    },
  };

  return { ctx, disposers, routes, tools, provided, logs };
}

/** 构造一个足以承载 handler 的假 request/response。 */
function callRoute(route, url) {
  const chunks = [];
  const response = {
    headersSent: false,
    writableEnded: false,
    statusCode: 0,
    headers: null,
    writeHead(status, headers) {
      this.statusCode = status;
      this.headers = headers;
    },
    end(text) {
      this.writableEnded = true;
      chunks.push(text);
    },
  };
  route.handler({ url }, response);
  const body = chunks.join('');
  return { status: response.statusCode, headers: response.headers, json: body ? JSON.parse(body) : null };
}

// 2026-10-05 是周一；北京时间 10:00 属于高峰
const BJ_OFFSET = 8 * 60;
const at = (y, m, d, hh, mm = 0) => Date.UTC(y, m - 1, d, hh, mm) - BJ_OFFSET * 60_000;

test('apply 注册服务、路由与工具，并记录日志', () => {
  const { ctx, routes, tools, provided, logs, disposers } = makeCtx();
  apply(ctx, {});

  assert.deepEqual(inject, ['webServer'], 'inject 只声明必需服务，tools 为可选注入');
  assert.equal(routes.length, 1, '应注册 1 条路由');
  assert.equal(routes[0].kind, 'exact');
  assert.equal(routes[0].path, STATUS_PATH);
  assert.equal(routes[0].path, '/dsh-peak-hours/status');
  assert.equal(tools.length, 1, '应注册 1 个工具');
  assert.equal(tools[0].name, 'dsh_peak_hours');
  assert.ok(provided.has('peakHours'), '应提供 peakHours 服务');
  assert.equal(typeof provided.get('peakHours').status, 'function');
  assert.equal(typeof provided.get('peakHours').summary, 'function');
  assert.ok(logs.some(([level, message]) => level === 'info' && message.includes('ready')), '应输出就绪日志');
  assert.ok(disposers.length >= 2, '路由与服务都应登记 effect，便于卸载清理');
  for (const { dispose } of disposers) {
    if (typeof dispose === 'function') dispose();
  }
});

test('只读接口：默认查询现在，返回完整结果与结论文案', () => {
  const { ctx, routes } = makeCtx();
  apply(ctx, {});
  const { status, headers, json } = callRoute(routes[0], STATUS_PATH);

  assert.equal(status, 200);
  assert.match(headers['content-type'], /application\/json/);
  assert.equal(headers['cache-control'], 'no-store');
  assert.equal(json.ok, true);
  assert.equal(json.plugin, 'dsh-peak-hours');
  assert.equal(json.result.rulesetVersion, '2026-08-23');
  assert.ok(['peak', 'off-peak'].includes(json.result.tier));
  assert.equal(typeof json.result.currentPeriod.remainingText, 'string');
  assert.equal(typeof json.agentSummary, 'string');
  // Client 需要这些字段渲染倒计时
  for (const key of ['beijingTimeText', 'localTimeText', 'sameAsBeijing', 'nextChange', 'forecast']) {
    assert.ok(key in json.result, `结果应包含 ${key}`);
  }
});

test('只读接口：?at= 与 ?days= 生效，非法参数回落默认值', () => {
  const { ctx, routes } = makeCtx();
  apply(ctx, {});

  const peak = callRoute(routes[0], `${STATUS_PATH}?at=${at(2026, 10, 5, 10)}&days=5`);
  assert.equal(peak.json.result.tier, 'peak');
  assert.equal(peak.json.result.forecast.length, 5, 'days 应生效');

  const off = callRoute(routes[0], `${STATUS_PATH}?at=${at(2026, 10, 5, 20)}`);
  assert.equal(off.json.result.tier, 'off-peak');

  const weekend = callRoute(routes[0], `${STATUS_PATH}?at=${at(2026, 10, 10, 10)}`);
  assert.equal(weekend.json.result.tier, 'off-peak', '周六应为空闲');
  assert.equal(weekend.json.result.tierReason, 'weekend');

  const bad = callRoute(routes[0], `${STATUS_PATH}?at=abc&days=xyz`);
  assert.equal(bad.status, 200);
  assert.equal(bad.json.ok, true, '非法参数应回落默认值而不是报错');
});

test('配置透传：节假日、强制工作日与自定义窗口', () => {
  const { ctx, routes } = makeCtx();
  apply(ctx, {
    holidayDays: ['2026-10-06'],
    workdayOverrides: ['2026-10-10'],
    weekendAlwaysOffPeak: true,
  });

  const holiday = callRoute(routes[0], `${STATUS_PATH}?at=${at(2026, 10, 6, 10)}`);
  assert.equal(holiday.json.result.tier, 'off-peak');
  assert.equal(holiday.json.result.tierReason, 'holiday');

  const makeup = callRoute(routes[0], `${STATUS_PATH}?at=${at(2026, 10, 10, 10)}`);
  assert.equal(makeup.json.result.tier, 'peak', '强制工作日应恢复峰谷计费');

  const custom = makeCtx();
  apply(custom.ctx, { peakWindows: [{ start: '08:00', end: '10:00' }] });
  const early = callRoute(custom.routes[0], `${STATUS_PATH}?at=${at(2026, 10, 5, 8, 30)}`);
  assert.equal(early.json.result.tier, 'peak');
  const late = callRoute(custom.routes[0], `${STATUS_PATH}?at=${at(2026, 10, 5, 10, 30)}`);
  assert.equal(late.json.result.tier, 'off-peak');
});

test('Agent 工具：status 与 forecast 两种动作', () => {
  const { ctx, tools } = makeCtx();
  apply(ctx, {});
  const tool = tools[0];

  const status = tool.execute({ action: 'status', at: at(2026, 10, 5, 10) });
  assert.equal(status.tier, 'peak');
  assert.equal(status.isPeak, true);
  assert.equal(status.discountRate, 1);
  assert.equal(status.nextChangeTo, 'off-peak');
  assert.equal(status.remainingText, '2 小时');
  assert.match(status.summary, /高峰时段（标准价）/);
  assert.equal(status.forecast, undefined, 'status 动作不返回 forecast');

  const forecast = tool.execute({ action: 'forecast', at: at(2026, 10, 5, 10), days: 3 });
  assert.equal(forecast.forecast.length, 3);
  assert.equal(forecast.forecast[0].dayKey, '2026-10-05');
  assert.equal(forecast.forecast[0].peakWindowsText, '09:00–12:00、14:00–18:00');
  assert.equal(forecast.forecast[0].allOffPeak, false);

  const weekend = tool.execute({ action: 'forecast', at: at(2026, 10, 10, 10), days: 3 });
  assert.equal(weekend.forecast[0].allOffPeak, true);
  assert.equal(weekend.forecast[0].peakWindowsText, '全天按空闲价');
  assert.equal(weekend.isOffPeak, true);
  assert.equal(weekend.discountRate, 0.5);
  assert.ok(weekend.nextPeakText.includes('09:00'), '周末的下一次高峰应指向工作日 09:00');

  // 参数可省略
  const bare = tool.execute({});
  assert.ok(['peak', 'off-peak'].includes(bare.tier));

  // 工具声明的参数与输出结构齐备
  assert.equal(tool.parameters.action.type, 'string');
  assert.deepEqual(tool.parameters.action.enum, ['status', 'forecast']);
  assert.equal(tool.output.schema.properties.tier.required, true);
  assert.equal(typeof tool.output.render, 'function');
  const rendered = tool.output.render({}, status);
  assert.equal(rendered[0].type, 'text');
  assert.equal(JSON.parse(rendered[0].text).tier, 'peak');
});

test('缺少 tools 服务时插件仍能工作', () => {
  const { ctx, routes, tools } = makeCtx({ withTools: false });
  apply(ctx, {});
  assert.equal(tools.length, 0, '没有 tools 服务时不注册工具');
  const { json } = callRoute(routes[0], STATUS_PATH);
  assert.equal(json.ok, true, '状态接口照常工作');
});

test('exposeTool: false 时不注册工具', () => {
  const { ctx, tools, routes } = makeCtx();
  apply(ctx, { exposeTool: false });
  assert.equal(tools.length, 0);
  assert.equal(routes.length, 1);
});

test('路由异常时不抛到调用方，返回 500', () => {
  const { ctx, routes } = makeCtx();
  apply(ctx, {});
  const broken = {
    headersSent: false,
    writableEnded: false,
    statusCode: 0,
    writeHead(status) {
      this.statusCode = status;
    },
    end() {
      this.writableEnded = true;
    },
  };
  // 制造异常：url 为不可解析对象
  assert.doesNotThrow(() => routes[0].handler({ get url() { throw new Error('boom') } }, broken));
  assert.equal(broken.statusCode, 500);
});

export { cases };
