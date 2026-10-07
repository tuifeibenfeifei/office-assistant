/**
 * 进程内测试入口（核心逻辑 + Host 集成）。
 *
 * 为什么不用 `node --test <目录>`：那种方式会为每个测试文件派生子进程，
 * 在受限环境（沙箱禁止管道 stdio）下会直接 EPERM 失败。
 * 这里改用自带的极简用例运行器；同一批用例模块也可被 `node --test` 直接加载。
 *
 * 用法：node tests/run.js
 */
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { getCases, setReporter } from './harness.mjs';

const write = (line) => process.stdout.write(`${line}\n`);

setReporter(() => {});
await import('./office-core.test.mjs');
const coreCases = getCases();

const suites = [['核心逻辑', coreCases]];

// Host 集成用例可选：它需要 index.js 存在，便于先写核心再补宿主
const hostTestPath = fileURLToPath(new URL('./host.test.mjs', import.meta.url));
if (existsSync(hostTestPath)) {
  const hostModule = await import('./host.test.mjs');
  suites.push(['Host 集成', hostModule.cases]);
}

let passed = 0;
let total = 0;
const failures = [];

for (const [title, cases] of suites) {
  write(`\n${title}（${cases.length} 个用例）`);
  for (const { name, fn } of cases) {
    total += 1;
    try {
      await fn();
      passed += 1;
      write(`  ok   ${name}`);
    } catch (error) {
      failures.push({ title, name, error });
      write(`  FAIL ${name}`);
    }
  }
}

write(`\n共 ${total} 个用例：通过 ${passed}，失败 ${failures.length}`);

for (const { title, name, error } of failures) {
  write(`\n--- [${title}] ${name} ---\n${error?.stack ?? String(error)}`);
}

process.exitCode = failures.length === 0 && total > 0 ? 0 : 1;
