/**
 * 真机冒烟：直接调用四个工具，验证在真实文件系统上能干活。
 * 只做只读动作（scan / clean preview / memory status / text），不执行任何删除。
 */
import { apply } from '../index.js';

const routes = [];
const tools = [];
const ctx = {
  logger: { info: () => {}, warn: (m) => console.log('[warn]', m) },
  effect: (fn) => fn(),
  provide: () => () => {},
  inject: (names, run) => run({ ...ctx, tools: { register: (def) => tools.push(def) } }),
  webServer: { register: (r) => { routes.push(r); return () => {}; } },
};

apply(ctx, {});
const byName = (name) => tools.find((t) => t.name === name);

console.log('=== office_text（JSON + 统计）===');
console.log(JSON.stringify(await byName('office_text').execute({
  action: 'analyze',
  text: '这是一段用于验证的中文文本 with some English words.',
}), null, 2).slice(0, 400));

console.log('\n=== office_memory（只读）===');
const memory = await byName('office_memory').execute({ action: 'status' });
console.log(memory.summary);

console.log('\n=== office_clean（仅预览）===');
const preview = await byName('office_clean').execute({ action: 'preview' });
console.log(preview.summary);
for (const target of preview.targets) {
  console.log(`  ${target.name.padEnd(22, ' ')} ${target.sizeText.padStart(10, ' ')}  ${target.fileCount} 个文件`);
}

console.log('\n=== office_scan（扫描用户主目录，限量）===');
const scan = await byName('office_scan').execute({ top: 5, maxFiles: 5000 });
console.log(scan.summary);
for (const folder of scan.folders) {
  console.log(`  ${folder.sizeText.padStart(10, ' ')}  ${folder.files} 个文件  ${folder.path}`);
}

console.log('\n=== 只读接口 ===');
const chunks = [];
routes[0].handler({ url: routes[0].path }, {
  headersSent: false,
  writeHead: (s, h) => { console.log('  HTTP', s, h['content-type']); },
  end: (t) => chunks.push(t),
});
console.log('  ' + chunks.join('').slice(0, 200));
