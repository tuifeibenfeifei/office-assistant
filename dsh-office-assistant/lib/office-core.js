/**
 * dsh-office-assistant —— 纯函数核心。
 *
 * 这一层不读全局状态、不碰文件系统、不发网络请求，因此可以被用例完整覆盖。
 * Host 半侧的工具只是这层的薄封装：真正容易出错的逻辑（解析、统计、编码转换）
 * 全部集中在这里，便于测试也便于替换实现。
 */

// ---------------------------------------------------------------- JSON

/**
 * 格式化为带缩进的 JSON。
 *
 * 用 JSON.parse 而不是自己写格式化：非法输入必须**报错**而不是被静默"修好"，
 * 用户拿这段文本是要贴到别处的，静默篡改比报错危险得多。
 *
 * @param {string} text - 原始 JSON 文本。
 * @param {number} [indent] - 缩进空格数（0–8）。
 * @returns {{ok: true, text: string, bytes: number} | {ok: false, error: string, line: number|null, column: number|null}}
 */
export function formatJson(text, indent = 2) {
  const size = Math.min(Math.max(Number.isFinite(indent) ? Math.trunc(indent) : 2, 0), 8);
  if (typeof text !== 'string' || text.trim().length === 0) {
    return { ok: false, error: '输入为空。', line: null, column: null };
  }

  try {
    const value = JSON.parse(text);
    const formatted = JSON.stringify(value, null, size);
    return { ok: true, text: formatted, bytes: Buffer.byteLength(formatted, 'utf8') };
  } catch (error) {
    const position = locateJsonError(String(error?.message ?? error));
    return {
      ok: false,
      error: String(error?.message ?? error),
      line: position.line,
      column: position.column,
    };
  }
}

/** 压缩 JSON（去掉所有可省略的空白）。 */
export function minifyJson(text) {
  const formatted = formatJson(text, 0);
  return formatted.ok ? { ...formatted } : formatted;
}

/**
 * 从 V8 的报错文本里抠出行列号，用户才知道去哪儿改。
 * 匹配不到就返回 null，不编造位置。
 */
function locateJsonError(message) {
  const match = /position\s+(\d+)/i.exec(message) ?? /at position (\d+)/i.exec(message);
  if (!match) return { line: null, column: null };
  return { line: null, column: Number(match[1]) + 1 };
}

// ---------------------------------------------------------------- 文本统计

/** 判断字符是否为中日韩表意文字（用于按显示宽度估算）。 */
function isWide(char) {
  const code = char.codePointAt(0);
  return (code >= 0x1100 && code <= 0x115f) ||
    (code >= 0x2e80 && code <= 0xa4cf) ||
    (code >= 0xac00 && code <= 0xd7a3) ||
    (code >= 0xf900 && code <= 0xfaff) ||
    (code >= 0xfe30 && code <= 0xfe6f) ||
    (code >= 0xff00 && code <= 0xff60) ||
    (code >= 0xffe0 && code <= 0xffe6);
}

/**
 * 文本统计：字数、词数、行数、段落数、字节数、估算阅读时长。
 *
 * 词数按"拉丁词 + 单个汉字各计一词"统计：中文没有空格分词，
 * 按空格切会让整段中文变成 1 个词，对写作者没有参考价值。
 *
 * @param {string} text
 * @returns {object} 统计结果。
 */
export function analyzeText(text) {
  const source = typeof text === 'string' ? text : '';
  const characters = [...source].length;
  const charactersNoSpaces = [...source.replace(/\s/gu, '')].length;

  let wide = 0;
  for (const char of source) if (isWide(char)) wide += 1;

  const hanCount = (source.match(/[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/gu) ?? []).length;
  const latinWords = (source.match(/[A-Za-z0-9][A-Za-z0-9'’_-]*/gu) ?? []).length;
  const words = latinWords + hanCount;

  const lines = source.length === 0 ? 0 : source.split(/\r\n|\r|\n/u).length;
  const paragraphs = source.split(/\r?\n\s*\r?\n/u).filter((p) => p.trim().length > 0).length;

  // 阅读速度取中文 300 字/分钟、英文 200 词/分钟的中位估计，仅作参考
  const minutes = words === 0 ? 0 : words / 250;

  return {
    characters,
    charactersNoSpaces,
    wideCharacters: wide,
    words,
    hanCharacters: hanCount,
    latinWords,
    lines,
    paragraphs,
    bytes: Buffer.byteLength(source, 'utf8'),
    readingMinutes: Math.round(minutes * 10) / 10,
    readingText: minutes < 1 ? '不到 1 分钟' : `约 ${Math.round(minutes)} 分钟`,
  };
}

// ---------------------------------------------------------------- 编码

/** 文本 → Base64。 */
export function toBase64(text) {
  if (typeof text !== 'string') return { ok: false, error: '输入必须是文本。' };
  // 先按 UTF-8 取字节再编码：直接 btoa 会在非 ASCII 字符上抛错
  return { ok: true, text: Buffer.from(text, 'utf8').toString('base64') };
}

/** Base64 → 文本（严格校验，非法输入明确报错）。 */
export function fromBase64(text) {
  if (typeof text !== 'string') return { ok: false, error: '输入必须是文本。' };
  const cleaned = text.trim().replace(/\s+/gu, '');
  if (cleaned.length === 0) return { ok: false, error: '输入为空。' };
  if (!/^[A-Za-z0-9+/]*={0,2}$/u.test(cleaned) || cleaned.length % 4 !== 0) {
    return { ok: false, error: '不是合法的 Base64（长度或字符集不符合）。' };
  }

  try {
    const buffer = Buffer.from(cleaned, 'base64');
    // 回编一次做等价校验：Buffer 对非法输入很宽容，会静默截断
    if (buffer.toString('base64') !== cleaned) {
      return { ok: false, error: '不是合法的 Base64（回编码不一致，输入可能被截断）。' };
    }
    return { ok: true, text: buffer.toString('utf8') };
  } catch (error) {
    return { ok: false, error: String(error?.message ?? error) };
  }
}

/** URL 编码。 */
export function encodeUrl(text) {
  if (typeof text !== 'string') return { ok: false, error: '输入必须是文本。' };
  return { ok: true, text: encodeURIComponent(text) };
}

/** URL 解码（非法百分号转义明确报错，而不是返回原文）。 */
export function decodeUrl(text) {
  if (typeof text !== 'string') return { ok: false, error: '输入必须是文本。' };
  try {
    return { ok: true, text: decodeURIComponent(text) };
  } catch {
    return { ok: false, error: '不是合法的 URL 编码（存在不完整的百分号转义）。' };
  }
}

// ---------------------------------------------------------------- 命名转换

/** 把任意命名风格切成词。 */
export function splitWords(text) {
  if (typeof text !== 'string') return [];
  return text
    .replace(/([a-z0-9])([A-Z])/gu, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/gu, '$1 $2')
    .split(/[^A-Za-z0-9\u4e00-\u9fff]+/u)
    .filter((word) => word.length > 0)
    .map((word) => word.toLowerCase());
}

/** 生成 camelCase / PascalCase / snake_case / kebab-case / CONSTANT_CASE 五种命名。 */
export function convertCase(text) {
  const words = splitWords(text);
  if (words.length === 0) return { ok: false, error: '没有可转换的词。' };

  const capitalize = (word) => word.charAt(0).toUpperCase() + word.slice(1);
  return {
    ok: true,
    variants: {
      camelCase: words[0] + words.slice(1).map(capitalize).join(''),
      PascalCase: words.map(capitalize).join(''),
      snake_case: words.join('_'),
      'kebab-case': words.join('-'),
      CONSTANT_CASE: words.join('_').toUpperCase(),
    },
  };
}

// ---------------------------------------------------------------- 目录体积汇总

/**
 * 把"路径 → 字节数"的扁平行汇总成目录体积排行。
 *
 * 纯计算：输入是已经数完大小的条目，函数只做归集与排序，
 * 因此不需要真实磁盘就能测试，也便于在不同数据源之间复用。
 *
 * @param {Array<{path: string, bytes: number}>} files - 文件列表。
 * @param {object} [options]
 * @param {number} [options.top] - 返回前多少个大目录。
 * @param {string} [options.separator] - 路径分隔符。
 * @returns {{totalBytes: number, fileCount: number, folders: Array<{path: string, bytes: number, files: number}>}}
 */
export function summarizeFolders(files, options = {}) {
  const { top = 20, separator = '\\' } = options;
  const list = Array.isArray(files) ? files : [];

  let totalBytes = 0;
  const folders = new Map();

  for (const file of list) {
    const bytes = Number(file?.bytes) || 0;
    totalBytes += bytes;

    // 每一级父目录都要累加：只看直接父目录会让顶层文件夹的体积失真。
    // 从 depth = 2 起步是为了跳过盘符本身——"C:"（或 "/"）不是文件夹，
    // 把它列进体积排行没有意义（实测踩过，排行第一名是个光秃秃的盘符）。
    const parts = String(file?.path ?? '').split(separator).filter((p) => p.length > 0);
    for (let depth = 2; depth < parts.length; depth += 1) {
      const key = parts.slice(0, depth).join(separator);
      const current = folders.get(key) ?? { bytes: 0, files: 0 };
      folders.set(key, { bytes: current.bytes + bytes, files: current.files + 1 });
    }
  }

  const ranked = [...folders.entries()]
    .map(([path, value]) => ({ path, bytes: value.bytes, files: value.files }))
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, Math.max(1, top));

  return { totalBytes, fileCount: list.length, folders: ranked };
}

/** 人类可读的字节数。 */
export function formatBytes(bytes) {
  const value = Number(bytes) || 0;
  if (value < 1024) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let scaled = value / 1024;
  let index = 0;
  while (scaled >= 1024 && index < units.length - 1) {
    scaled /= 1024;
    index += 1;
  }
  return `${scaled.toFixed(scaled >= 100 ? 0 : 1)} ${units[index]}`;
}
