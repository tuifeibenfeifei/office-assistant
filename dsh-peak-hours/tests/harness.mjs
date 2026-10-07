/**
 * 极简测试宿主：让同一份用例既能被 `node tests/run.js` 在进程内跑，
 * 也能被 `node --test tests/` 识别（后者仅在允许派生子进程的环境可用）。
 */
const cases = [];
let reporter = null;

/** 注册一个用例。fn 可为同步或异步。 */
export function test(name, fn) {
  cases.push({ name, fn });
}

/** 设置进度输出回调；未设置时不输出（供 node --test 静默收集）。 */
export function setReporter(fn) {
  reporter = fn;
}

/** 在用例内部输出一行进度信息。 */
export function note(line) {
  if (typeof reporter === 'function') reporter(`       ${line}`);
}

/** 取回已注册的用例（导入测试模块后调用）。 */
export function getCases() {
  return cases;
}
