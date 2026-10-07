/**
 * Host 半侧的集成自测：用 mock ctx 驱动 apply()，验证
 *   · 服务注册、HTTP 路由、四个工具的注册与返回值形状
 *   · 清理工具的二次确认与白名单边界（不接受任意路径）
 *   · 缺少 tools 服务或 exposeTools:false 时的降级
 * 不需要真实 dsh 运行环境。
 */
import assert from 'node:assert/strict';

import { apply, inject, STATUS_PATH } from '../index.js';

const cases = [];
function test(name, fn) { cases.push({ name, fn }); }

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

function callRoute(route) {
  const chunks = [];
  const response = {
    headersSent: false,
    writableEnded: false,
    statusCode: 0,
    headers: null,
    writeHead(status, headers) { this.statusCode = status; this.headers = headers; },
    end(text) { this.writableEnded = true; chunks.push(text); },
  };
  route.handler({ url: route.path }, response);
  const body = chunks.join('');
  return { status: response.statusCode, headers: response.headers, json: body ? JSON.parse(body) : null };
}

function toolByName(tools, name) {
  const tool = tools.find((t) => t.name === name);
  assert.ok(tool, `应注册工具 ${name}`);
  return tool;
}

test('apply 注册服务、只读路由与四个工具', () => {
  const { ctx, routes, tools, provided, logs, disposers } = makeCtx();
  apply(ctx, {});

  assert.deepEqual(inject, ['webServer'], 'inject 只声明必需服务');
  assert.equal(routes.length, 1);
  assert.equal(routes[0].path, STATUS_PATH);
  assert.ok(provided.has('officeAssistant'));
  assert.equal(typeof provided.get('officeAssistant').status, 'function');

  for (const name of ['office_text', 'office_scan', 'office_clean', 'office_memory']) {
    toolByName(tools, name);
  }
  assert.ok(logs.some(([level, message]) => level === 'info' && message.includes('ready')));
  for (const { dispose } of disposers) if (typeof dispose === 'function') dispose();
});

test('只读接口返回内存读数', () => {
  const { ctx, routes } = makeCtx();
  apply(ctx, {});
  const { status, json, headers } = callRoute(routes[0]);

  assert.equal(status, 200);
  assert.match(headers['content-type'], /application\/json/);
  assert.equal(json.ok, true);
  assert.equal(json.plugin, 'dsh-office-assistant');
  assert.ok(json.memory.totalBytes > 0);
  assert.ok(json.memory.usedPercent >= 0 && json.memory.usedPercent <= 100);
  assert.ok(json.memory.totalText.length > 0);
});

test('office_text：八种动作都能给出结果', async () => {
  const { ctx, tools } = makeCtx();
  apply(ctx, {});
  const tool = toolByName(tools, 'office_text');

  const formatted = await tool.execute({ action: 'format-json', text: '{"a":1}' });
  assert.equal(formatted.ok, true);
  assert.equal(formatted.text, '{\n  "a": 1\n}');

  const minified = await tool.execute({ action: 'minify-json', text: '{\n "a": 1\n}' });
  assert.equal(minified.text, '{"a":1}');

  const analyzed = await tool.execute({ action: 'analyze', text: '你好 world' });
  assert.equal(analyzed.ok, true);
  assert.equal(analyzed.stats.hanCharacters, 2);
  assert.match(analyzed.summary, /字符/);

  assert.equal((await tool.execute({ action: 'base64-encode', text: '中文' })).ok, true);
  assert.equal((await tool.execute({ action: 'base64-decode', text: '5Lit5paH' })).text, '中文');
  assert.equal((await tool.execute({ action: 'url-encode', text: 'a b' })).text, 'a%20b');
  assert.equal((await tool.execute({ action: 'url-decode', text: 'a%20b' })).text, 'a b');

  const cases = await tool.execute({ action: 'case-convert', text: 'user name' });
  assert.equal(cases.variants.camelCase, 'userName');

  // 非法输入与未知动作都要明确失败，不能静默返回空结果
  const bad = await tool.execute({ action: 'format-json', text: '{oops' });
  assert.equal(bad.ok, false);
  assert.ok(bad.error.length > 0);

  const unknown = await tool.execute({ action: 'nope' });
  assert.equal(unknown.ok, false);
  assert.match(unknown.summary, /action/);

  // 参数可省略时不崩
  assert.equal((await tool.execute({})).ok, false);
});

test('office_clean：预览不删除，执行必须显式确认，且只碰白名单路径', async () => {
  const { ctx, tools } = makeCtx();
  apply(ctx, {});
  const tool = toolByName(tools, 'office_clean');

  const preview = await tool.execute({ action: 'preview' });
  assert.equal(preview.ok, true);
  assert.equal(preview.action, 'preview');
  assert.ok(Array.isArray(preview.targets));
  assert.ok(preview.targets.length > 0, '应至少有一个可信目标');
  // 每个目标都必须带"为什么安全"，否则用户无法判断
  for (const target of preview.targets) {
    assert.ok(target.why.length > 0, `${target.name} 缺少安全性说明`);
    assert.ok(target.path.length > 0);
  }

  // 未显式确认时不得执行删除
  const unconfirmed = await tool.execute({ action: 'clean' });
  assert.equal(unconfirmed.ok, false);
  assert.equal(unconfirmed.needsConfirm, true);
  assert.match(unconfirmed.summary, /confirm=true/);

  // 该工具不接受任意路径参数：即使传了 path 也只处理白名单
  const withPath = await tool.execute({ action: 'preview', path: 'C:\\Windows\\System32' });
  assert.equal(withPath.ok, true);
  for (const target of withPath.targets) {
    assert.ok(!target.path.toLowerCase().includes('system32'), '白名单之外的路径不得出现在目标里');
  }
});

test('office_memory：状态可读，整理需显式确认', async () => {
  const { ctx, tools } = makeCtx();
  apply(ctx, {});
  const tool = toolByName(tools, 'office_memory');

  const status = await tool.execute({ action: 'status' });
  assert.equal(status.ok, true);
  assert.ok(status.memory.totalBytes > 0);
  assert.match(status.summary, /内存/);

  const unconfirmed = await tool.execute({ action: 'trim' });
  assert.equal(unconfirmed.ok, false);
  assert.equal(unconfirmed.needsConfirm, true);
});

test('office_scan：扫描临时目录并按体积排行', async () => {
  const { ctx, tools } = makeCtx();
  apply(ctx, {});
  const tool = toolByName(tools, 'office_scan');

  const result = await tool.execute({ top: 5, maxFiles: 200 });
  assert.equal(result.ok, true);
  assert.ok(result.fileCount >= 0);
  assert.ok(Array.isArray(result.folders));
  assert.ok(result.folders.length <= 5);
  assert.ok(Array.isArray(result.largest));
  assert.match(result.summary, /扫描/);

  // 目录排行必须按体积降序
  for (let i = 1; i < result.folders.length; i += 1) {
    assert.ok(result.folders[i - 1].bytes >= result.folders[i].bytes, '目录排行应按体积降序');
  }

  const missing = await tool.execute({ path: 'Z:\\definitely-not-here-12345' });
  assert.equal(missing.ok, false);
  assert.match(missing.error, /不存在/);
});

test('缺少 tools 服务或 exposeTools:false 时不注册工具但接口照常', async () => {
  const a = makeCtx({ withTools: false });
  apply(a.ctx, {});
  assert.equal(a.tools.length, 0);
  assert.equal(callRoute(a.routes[0]).json.ok, true);

  const b = makeCtx();
  apply(b.ctx, { exposeTools: false });
  assert.equal(b.tools.length, 0);
  assert.equal(callRoute(b.routes[0]).json.ok, true);
});

export { cases };
