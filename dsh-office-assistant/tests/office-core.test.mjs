/**
 * dsh-office-assistant 核心逻辑测试。
 *
 * 覆盖：JSON 格式化与非法输入的行列定位、文本统计（中英混排）、
 * 编码往返与非法输入识别、命名风格转换、目录体积归集。
 */
import assert from 'node:assert/strict';

import { test } from './harness.mjs';

import {
  analyzeText,
  convertCase,
  decodeUrl,
  encodeUrl,
  formatBytes,
  formatJson,
  fromBase64,
  minifyJson,
  splitWords,
  summarizeFolders,
  toBase64,
} from '../lib/office-core.js';

// ---------------------------------------------------------------- JSON

test('JSON 格式化：对象、数组、嵌套都按缩进输出', () => {
  const result = formatJson('{"a":1,"b":[1,2],"c":{"d":"中文"}}', 2);
  assert.equal(result.ok, true);
  assert.equal(result.text, '{\n  "a": 1,\n  "b": [\n    1,\n    2\n  ],\n  "c": {\n    "d": "中文"\n  }\n}');
  // 中文按 UTF-8 计字节，不是按字符数
  assert.ok(result.bytes > result.text.length);
});

test('JSON 格式化：缩进可调且被夹取到 0–8', () => {
  assert.equal(formatJson('{"a":1}', 0).text, '{"a":1}');
  assert.equal(formatJson('{"a":1}', 4).text, '{\n    "a": 1\n}');
  assert.equal(formatJson('{"a":1}', 99).text, formatJson('{"a":1}', 8).text);
  assert.equal(formatJson('{"a":1}', -5).text, formatJson('{"a":1}', 0).text);
});

test('JSON 格式化：非法输入必须报错，并给出出错位置而不是静默修好', () => {
  const result = formatJson('{"a": 1,}');
  assert.equal(result.ok, false);
  assert.ok(result.error.length > 0);
  // 位置可能是 null（引擎没给），也可能带列号；不能编造行号
  assert.ok(result.line === null || Number.isInteger(result.line));
  assert.ok(result.column === null || Number.isInteger(result.column));
});

test('JSON 压缩：去掉全部可省略空白', () => {
  const result = minifyJson('{\n  "a": 1,\n  "b": [\n    1\n  ]\n}');
  assert.equal(result.ok, true);
  assert.equal(result.text, '{"a":1,"b":[1]}');
});

test('JSON：空输入与纯空白被明确拒绝', () => {
  assert.equal(formatJson('').ok, false);
  assert.equal(formatJson('   ').ok, false);
  assert.equal(formatJson(null).ok, false);
});

// ---------------------------------------------------------------- 文本统计

test('文本统计：中英混排的字数、词数、行数、段落数', () => {
  const text = '这是第一段，共十个汉字。\n\n第二段 has three words\n再来一行';
  const result = analyzeText(text);

  assert.equal(result.lines, 4);
  assert.equal(result.paragraphs, 2);
  // 汉字按字计词：中文不该因为没空格就只算一个词
  assert.equal(result.hanCharacters, 17);
  assert.equal(result.latinWords, 3);
  assert.equal(result.words, 20);
  assert.ok(result.bytes > result.characters);
});

test('文本统计：空文本全为零且不报错', () => {
  const result = analyzeText('');
  assert.equal(result.characters, 0);
  assert.equal(result.words, 0);
  assert.equal(result.lines, 0);
  assert.equal(result.paragraphs, 0);
  assert.equal(result.bytes, 0);
  assert.equal(result.readingText, '不到 1 分钟');
});

test('文本统计：全角字符被识别为宽字符', () => {
  const result = analyzeText('中文ＡＢＣ');
  assert.equal(result.wideCharacters, 5);
});

// ---------------------------------------------------------------- 编码

test('Base64 往返：含中文与特殊字符', () => {
  const source = '中文 + special/chars?&= 😀';
  const encoded = toBase64(source);
  assert.equal(encoded.ok, true);
  const decoded = fromBase64(encoded.text);
  assert.equal(decoded.ok, true);
  assert.equal(decoded.text, source);
});

test('Base64 解码：非法输入明确报错', () => {
  assert.equal(fromBase64('not base64!!').ok, false);
  assert.equal(fromBase64('abc').ok, false);        // 长度不是 4 的倍数
  assert.equal(fromBase64('').ok, false);
  assert.equal(fromBase64(null).ok, false);
});

test('URL 编码往返；非法转义解码报错而不是返回原文', () => {
  const source = '关键词 and spaces/plus+';
  const encoded = encodeUrl(source);
  assert.equal(encoded.ok, true);
  assert.equal(decodeUrl(encoded.text).text, source);
  assert.equal(decodeUrl('%E4%B8').ok, false);
});

// ---------------------------------------------------------------- 命名转换

test('命名风格：切词正确处理驼峰、下划线与连字符', () => {
  assert.deepEqual(splitWords('getUserName'), ['get', 'user', 'name']);
  assert.deepEqual(splitWords('HTTPResponse_code'), ['http', 'response', 'code']);
  assert.deepEqual(splitWords('user-profile name'), ['user', 'profile', 'name']);
});

test('命名风格：一次给出五种变体', () => {
  const result = convertCase('user profile name');
  assert.equal(result.ok, true);
  assert.equal(result.variants.camelCase, 'userProfileName');
  assert.equal(result.variants.PascalCase, 'UserProfileName');
  assert.equal(result.variants.snake_case, 'user_profile_name');
  assert.equal(result.variants['kebab-case'], 'user-profile-name');
  assert.equal(result.variants.CONSTANT_CASE, 'USER_PROFILE_NAME');
});

test('命名风格：没有可转换的词时明确失败', () => {
  assert.equal(convertCase('---').ok, false);
  assert.equal(convertCase('').ok, false);
});

// ---------------------------------------------------------------- 目录汇总

test('目录体积：每一级父目录都要累加，否则顶层体积失真', () => {
  const files = [
    { path: 'C:\\Users\\a\\doc.txt', bytes: 100 },
    { path: 'C:\\Users\\a\\img.png', bytes: 300 },
    { path: 'C:\\Users\\b\\big.bin', bytes: 600 },
  ];
  const result = summarizeFolders(files, { top: 10 });

  assert.equal(result.totalBytes, 1000);
  assert.equal(result.fileCount, 3);
  // C:\Users 必须等于三个文件之和，而不是只算直接子项
  assert.equal(result.folders.find((f) => f.path === 'C:\\Users').bytes, 1000);
  assert.equal(result.folders.find((f) => f.path === 'C:\\Users\\a').bytes, 400);
  // 排行按体积降序
  assert.equal(result.folders[0].path, 'C:\\Users');
});

test('目录体积：空输入与非法条目不崩', () => {
  assert.deepEqual(summarizeFolders([]), { totalBytes: 0, fileCount: 0, folders: [] });
  const result = summarizeFolders([{ path: 'no-separator', bytes: 5 }, { bytes: 7 }, null], { top: 5 });
  assert.equal(result.totalBytes, 12);
  // 没有分隔符的条目不会产生任何目录层级
  assert.equal(result.folders.length, 0);
});

test('目录体积：top 参数限制返回条数', () => {
  const files = Array.from({ length: 30 }, (_, i) => ({ path: `R:\\d${i}\\f.bin`, bytes: i + 1 }));
  assert.equal(summarizeFolders(files, { top: 5 }).folders.length, 5);
});

// ---------------------------------------------------------------- 字节格式化

test('字节格式化：单位与精度', () => {
  assert.equal(formatBytes(0), '0 B');
  assert.equal(formatBytes(512), '512 B');
  assert.equal(formatBytes(2048), '2.0 KB');
  assert.equal(formatBytes(5 * 1024 * 1024), '5.0 MB');
  assert.equal(formatBytes(3 * 1024 * 1024 * 1024), '3.0 GB');
});
