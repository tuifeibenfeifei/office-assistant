/**
 * dsh-office-assistant — Host half.
 *
 * 把"办公助手"里适合在 dsh 里用的能力做成 Agent 工具：
 *   · office_text   —— JSON 格式化/压缩、文本统计、编码转换、命名风格转换
 *   · office_scan   —— 只读扫描目录并给出体积排行（找"哪里占了空间"）
 *   · office_clean  —— 只清理系统公认的临时目录（每一项都做安全检查）
 *   · office_memory —— 内存读数，可选整理非前台进程的工作集
 *
 * 设计取舍：
 *   · 容易出错的解析与统计放在 lib/office-core.js（纯函数、已单测），这里只做系统调用与编排；
 *   · 删除一律**移入回收站**而不是永久删除；
 *   · 清理目标写死在白名单里，不接受任意路径入参——工具被模型调用，
 *     不能让它有机会删掉用户没打算删的东西。
 */

import { existsSync } from 'node:fs';
import { readdir, stat, rm, mkdtemp } from 'node:fs/promises';
import { homedir, tmpdir, totalmem, freemem } from 'node:os';
import { join, sep } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

import {
  analyzeText,
  convertCase,
  decodeUrl,
  encodeUrl,
  formatBytes,
  formatJson,
  fromBase64,
  minifyJson,
  summarizeFolders,
  toBase64,
} from './lib/office-core.js';

const run = promisify(execFile);

/** 依赖：仅 webServer（注册只读状态接口）。tools 放可选注入。 */
export const inject = ['webServer'];

/** 只读状态接口路径。Client 半侧用同一常量，改动需同步 client.js。 */
export const STATUS_PATH = '/dsh-office-assistant/status';

/**
 * 「百分百可清理」白名单。
 *
 * 全部是操作系统与主流软件公认的临时目录，且限定在当前用户范围内。
 * 刻意不包含：文档、下载、桌面、浏览器配置、聊天记录。
 */
function trustedTargets() {
  const home = homedir();
  const local = process.env.LOCALAPPDATA ?? join(home, 'AppData', 'Local');
  const roaming = process.env.APPDATA ?? join(home, 'AppData', 'Roaming');

  return [
    { name: '用户临时文件', path: tmpdir(), why: '程序运行时产生的中间文件，随时可重新生成' },
    { name: 'IE/系统网络缓存', path: join(local, 'Microsoft', 'Windows', 'INetCache'), why: '网页缓存副本，重新访问即可重新下载' },
    { name: 'Windows 错误报告', path: join(local, 'Microsoft', 'Windows', 'WER'), why: '崩溃报告，仅在排查问题时才有用' },
    { name: '缩略图缓存', path: join(local, 'Microsoft', 'Windows', 'Explorer'), why: '缩略图缓存，删除后自动重建' },
    { name: 'DirectX 着色器缓存', path: join(local, 'D3DSCache'), why: '着色器编译缓存，游戏首次运行会重新编译' },
    { name: 'NVIDIA 着色器缓存', path: join(local, 'NVIDIA', 'DXCache'), why: '着色器缓存，可安全重建' },
    { name: 'CrashDumps 崩溃转储', path: join(local, 'CrashDumps'), why: '崩溃转储文件，仅用于事后分析' },
    { name: '漫游最近使用记录', path: join(roaming, 'Microsoft', 'Windows', 'Recent'), why: '只影响快捷方式列表' },
  ];
}

/** 递归数出目录里的文件与总字节数；无权限的子目录跳过而不是整棵树失败。 */
async function measure(path, { maxFiles = 200000, files = [] } = {}) {
  let entries;
  try {
    entries = await readdir(path, { withFileTypes: true });
  } catch {
    return files;
  }

  for (const entry of entries) {
    if (files.length >= maxFiles) break;
    const full = join(path, entry.name);
    if (entry.isDirectory()) {
      await measure(full, { maxFiles, files });
    } else if (entry.isFile()) {
      try {
        const info = await stat(full);
        files.push({ path: full, bytes: info.size });
      } catch {
        // 单个文件读不到大小不影响整体统计
      }
    }
  }

  return files;
}

/**
 * 把文件移入回收站。
 *
 * Windows 下用 Shell 的回收站接口（通过 PowerShell 调用 Microsoft.VisualBasic），
 * 这样删除是可恢复的；不支持的平台退回临时目录内删除，其余情况明确拒绝。
 */
async function recycle(paths) {
  if (process.platform !== 'win32') {
    return { ok: false, message: '当前平台不支持回收站删除，已拒绝执行以免永久删除文件。' };
  }

  // 用 -LiteralPath 逐条处理：路径里可能有方括号等通配符字符
  const script = [
    'Add-Type -AssemblyName Microsoft.VisualBasic;',
    '$n = 0;',
    'foreach ($p in $env:DSH_RECYCLE_PATHS -split "\\n") {',
    '  if ([string]::IsNullOrWhiteSpace($p)) { continue }',
    '  try {',
    '    if (Test-Path -LiteralPath $p) {',
    '      [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p, "OnlyErrorDialogs", "SendToRecycleBin");',
    '      $n++',
    '    }',
    '  } catch { }',
    '}',
    'Write-Output $n',
  ].join(' ');

  try {
    const { stdout } = await run('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
      env: { ...process.env, DSH_RECYCLE_PATHS: paths.join('\n') },
      timeout: 120000,
      maxBuffer: 1024 * 1024,
    });
    const moved = Number.parseInt(String(stdout).trim(), 10) || 0;
    return { ok: true, moved };
  } catch (error) {
    return { ok: false, message: `回收站调用失败：${error?.message ?? error}` };
  }
}

/**
 * @param {object} ctx - Host Cordis context。
 * @param {object} [config]
 */
export function apply(ctx, config = {}) {
  const exposeTools = config.exposeTools !== false;

  /** 插件服务对象：Client 走 HTTP，工具走同一份实现。 */
  const service = {
    status() {
      const total = totalmem();
      const free = freemem();
      const used = total - free;
      return {
        ok: true,
        plugin: 'dsh-office-assistant',
        memory: {
          totalBytes: total,
          freeBytes: free,
          usedBytes: used,
          usedPercent: Math.round((used / total) * 1000) / 10,
          totalText: formatBytes(total),
          freeText: formatBytes(free),
          usedText: formatBytes(used),
        },
        platform: process.platform,
        tmpdir: tmpdir(),
      };
    },
    trustedTargets,
  };

  ctx.effect(() => ctx.provide('officeAssistant', service), 'dsh-office-assistant: service');

  // ---- 只读状态接口（Client 半侧的数据来源）
  const handler = (request, response) => {
    try {
      const text = JSON.stringify(service.status());
      response.writeHead(200, {
        'content-type': 'application/json; charset=utf-8',
        'cache-control': 'no-store',
        'content-length': Buffer.byteLength(text),
      });
      response.end(text);
    } catch (error) {
      ctx.logger?.warn?.(`[dsh-office-assistant] status route failed: ${error?.message ?? error}`);
      if (!response.headersSent) {
        response.writeHead(500, { 'content-type': 'application/json; charset=utf-8' });
        response.end(JSON.stringify({ ok: false, message: String(error?.message ?? error) }));
      }
    }
  };

  ctx.effect(
    () => ctx.webServer.register({ kind: 'exact', path: STATUS_PATH, handler }),
    'dsh-office-assistant: status route',
  );

  // ---- Agent 工具
  if (exposeTools) {
    ctx.inject?.(['tools'], (toolsCtx) => {
      try {
        registerTools(toolsCtx, service);
      } catch (error) {
        toolsCtx.logger?.warn?.(`[dsh-office-assistant] tool registration skipped: ${error?.message ?? error}`);
      }
    });
  }

  ctx.logger?.info?.(`[dsh-office-assistant] ready — status route ${STATUS_PATH}`);
}

/** 注册四个工具。用普通对象字面量声明，插件不依赖任何 dsh 内部包。 */
function registerTools(ctx, service) {
  // ---------------------------------------------------------------- 文本处理
  ctx.tools.register({
    name: 'office_text',
    description:
      '办公文本处理：格式化/压缩 JSON、统计文本字数与阅读时长、Base64 与 URL 编解码、' +
      '命名风格互转（camelCase / snake_case 等）。不适合处理超大文本（超过几万字符请分段）。',
    parameters: {
      action: {
        type: 'string',
        enum: ['format-json', 'minify-json', 'analyze', 'base64-encode', 'base64-decode', 'url-encode', 'url-decode', 'case-convert'],
        description: '要执行的动作。',
      },
      text: { type: 'string', description: '待处理的文本内容。' },
      indent: { type: 'integer', description: 'format-json 的缩进空格数（0–8，默认 2）。' },
    },
    execute(args = {}) {
      const text = typeof args.text === 'string' ? args.text : '';
      switch (args.action) {
        case 'format-json': {
          const result = formatJson(text, args.indent ?? 2);
          return result.ok
            ? { ok: true, action: 'format-json', text: result.text, bytes: result.bytes, summary: `已格式化，${formatBytes(result.bytes)}` }
            : { ok: false, action: 'format-json', error: result.error, summary: `JSON 非法：${result.error}` };
        }
        case 'minify-json': {
          const result = minifyJson(text);
          return result.ok
            ? { ok: true, action: 'minify-json', text: result.text, bytes: result.bytes, summary: `已压缩到 ${formatBytes(result.bytes)}` }
            : { ok: false, action: 'minify-json', error: result.error, summary: `JSON 非法：${result.error}` };
        }
        case 'analyze': {
          const stats = analyzeText(text);
          return {
            ok: true,
            action: 'analyze',
            stats,
            summary: `${stats.characters} 字符（汉字 ${stats.hanCharacters}、英文词 ${stats.latinWords}），` +
              `${stats.lines} 行 / ${stats.paragraphs} 段，${formatBytes(stats.bytes)}，阅读${stats.readingText}`,
          };
        }
        case 'base64-encode': {
          const result = toBase64(text);
          return result.ok ? { ok: true, action: 'base64-encode', text: result.text, summary: '已编码' } : { ok: false, error: result.error };
        }
        case 'base64-decode': {
          const result = fromBase64(text);
          return result.ok ? { ok: true, action: 'base64-decode', text: result.text, summary: '已解码' } : { ok: false, error: result.error, summary: result.error };
        }
        case 'url-encode': {
          const result = encodeUrl(text);
          return result.ok ? { ok: true, action: 'url-encode', text: result.text, summary: '已编码' } : { ok: false, error: result.error };
        }
        case 'url-decode': {
          const result = decodeUrl(text);
          return result.ok ? { ok: true, action: 'url-decode', text: result.text, summary: '已解码' } : { ok: false, error: result.error, summary: result.error };
        }
        case 'case-convert': {
          const result = convertCase(text);
          return result.ok
            ? { ok: true, action: 'case-convert', variants: result.variants, summary: Object.entries(result.variants).map(([k, v]) => `${k}: ${v}`).join('；') }
            : { ok: false, error: result.error, summary: result.error };
        }
        default:
          return { ok: false, error: `未知动作：${args.action ?? '(空)'}`, summary: '请提供 action 参数。' };
      }
    },
  });

  // ---------------------------------------------------------------- 目录扫描
  ctx.tools.register({
    name: 'office_scan',
    description:
      '只读扫描一个目录（默认当前用户主目录），按体积给出占空间最多的文件夹排行，并可列出最大的文件。' +
      '用于回答"空间被什么占满了"。不会修改任何文件。',
    parameters: {
      path: { type: 'string', description: '要扫描的目录，默认用户主目录。' },
      top: { type: 'integer', description: '返回前多少个目录/文件（1–50，默认 10）。' },
      maxFiles: { type: 'integer', description: '最多统计多少个文件后停止（默认 20000）。' },
    },
    async execute(args = {}) {
      const root = typeof args.path === 'string' && args.path.trim().length > 0 ? args.path : homedir();
      const top = Math.min(Math.max(Number.isFinite(args.top) ? Math.trunc(args.top) : 10, 1), 50);
      const maxFiles = Math.min(Math.max(Number.isFinite(args.maxFiles) ? Math.trunc(args.maxFiles) : 20000, 100), 200000);

      if (!existsSync(root)) {
        return { ok: false, error: `目录不存在：${root}`, summary: '路径不存在，请检查后重试。' };
      }

      const files = await measure(root, { maxFiles });
      const summary = summarizeFolders(files, { top, separator: sep });

      const largest = [...files]
        .sort((a, b) => b.bytes - a.bytes)
        .slice(0, top)
        .map((file) => ({ path: file.path, bytes: file.bytes, sizeText: formatBytes(file.bytes) }));

      const folders = summary.folders.map((folder) => ({
        path: folder.path,
        bytes: folder.bytes,
        files: folder.files,
        sizeText: formatBytes(folder.bytes),
      }));

      const truncated = files.length >= maxFiles;
      return {
        ok: true,
        path: root,
        totalBytes: summary.totalBytes,
        totalText: formatBytes(summary.totalBytes),
        fileCount: summary.fileCount,
        truncated,
        folders,
        largest,
        summary: `扫描 ${root}：${summary.fileCount} 个文件，合计 ${formatBytes(summary.totalBytes)}` +
          (truncated ? `（已达上限 ${maxFiles} 个文件，结果不完整）` : '') +
          (folders.length > 0 ? `；最大目录 ${folders[0].path} ${folders[0].sizeText}` : ''),
      };
    },
  });

  // ---------------------------------------------------------------- 清理
  ctx.tools.register({
    name: 'office_clean',
    description:
      '查看或清理系统公认的临时目录（用户临时文件、网络缓存、错误报告、缩略图缓存、着色器缓存、崩溃转储）。' +
      '这些内容删除后程序会自动重建。必须先用 action=preview 预览，再用 action=clean 并显式 confirm=true 才会删除；' +
      '删除走回收站，可恢复。不接受任意路径，只能清理白名单内的位置。',
    parameters: {
      action: { type: 'string', enum: ['preview', 'clean'], description: 'preview 只统计不删除；clean 真正执行。' },
      confirm: { type: 'boolean', description: 'action=clean 时必须显式传 true，作为二次确认。' },
    },
    async execute(args = {}) {
      const targets = service.trustedTargets();
      const measured = [];
      const allFiles = [];

      for (const target of targets) {
        if (!existsSync(target.path)) {
          measured.push({ ...target, exists: false, fileCount: 0, bytes: 0, sizeText: '0 B' });
          continue;
        }

        const files = await measure(target.path, { maxFiles: 50000 });
        const bytes = files.reduce((sum, file) => sum + file.bytes, 0);
        measured.push({ ...target, exists: true, fileCount: files.length, bytes, sizeText: formatBytes(bytes) });
        allFiles.push(...files);
      }

      const totalBytes = allFiles.reduce((sum, file) => sum + file.bytes, 0);
      const preview = {
        ok: true,
        action: 'preview',
        targets: measured.map((t) => ({
          name: t.name,
          path: t.path,
          why: t.why,
          exists: t.exists,
          fileCount: t.fileCount,
          bytes: t.bytes,
          sizeText: t.sizeText,
        })),
        totalBytes,
        totalText: formatBytes(totalBytes),
        summary: `共 ${allFiles.length} 个临时文件，可释放 ${formatBytes(totalBytes)}。` +
          '这些内容删除后由程序自动重建；删除走回收站，可恢复。',
      };

      if (args.action !== 'clean') return preview;

      if (args.confirm !== true) {
        return {
          ok: false,
          action: 'clean',
          needsConfirm: true,
          totalBytes,
          totalText: formatBytes(totalBytes),
          summary: `预览显示可释放 ${formatBytes(totalBytes)}。确认清理请重新调用并传 confirm=true。`,
        };
      }

      const moved = await recycle(allFiles.map((file) => file.path));
      if (!moved.ok) {
        return { ok: false, action: 'clean', error: moved.message, summary: moved.message };
      }

      return {
        ok: true,
        action: 'clean',
        movedFiles: moved.moved,
        totalBytes,
        totalText: formatBytes(totalBytes),
        targets: preview.targets,
        summary: `已把 ${moved.moved} 个文件移入回收站，释放约 ${formatBytes(totalBytes)}；如有误删可从回收站恢复。`,
      };
    },
  });

  // ---------------------------------------------------------------- 内存
  ctx.tools.register({
    name: 'office_memory',
    description:
      '查看当前内存占用；可选整理非前台进程的工作集以释放内存（不会结束任何进程）。' +
      '工作集压缩属于短期整理，系统按需会重新调入页面，因此不承诺固定的释放量。',
    parameters: {
      action: { type: 'string', enum: ['status', 'trim'], description: 'status 只读；trim 尝试整理内存。' },
      confirm: { type: 'boolean', description: 'action=trim 时必须显式传 true。' },
    },
    async execute(args = {}) {
      const status = service.status().memory;

      if (args.action !== 'trim') {
        return {
          ok: true,
          action: 'status',
          memory: status,
          summary: `内存 ${status.usedText} / ${status.totalText} 使用中（${status.usedPercent}%），可用 ${status.freeText}。`,
        };
      }

      if (args.confirm !== true) {
        return {
          ok: false,
          action: 'trim',
          needsConfirm: true,
          memory: status,
          summary: `当前内存占用 ${status.usedPercent}%。确认整理请重新调用并传 confirm=true（不会结束任何进程）。`,
        };
      }

      if (process.platform !== 'win32') {
        return { ok: false, action: 'trim', memory: status, summary: '工作集整理目前仅支持 Windows。' };
      }

      // 只压缩"非前台、非系统关键"的进程；实现交给 PowerShell 的 EmptyWorkingSet。
      // 刻意不在这里结束任何进程：结束用户程序的风险远大于释放的那点内存。
      const script = [
        'Add-Type -Namespace OA -Name Trim -MemberDefinition \'',
        '[DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);\' ;',
        '$before = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory;',
        '$n = 0;',
        'foreach ($p in Get-Process) {',
        '  try {',
        '    if ($p.Id -eq $PID) { continue }',
        '    if ($p.MainWindowHandle -ne 0) { continue }',
        '    if ($p.WorkingSet64 -lt 20MB) { continue }',
        '    if ([OA.Trim]::EmptyWorkingSet($p.Handle)) { $n++ }',
        '  } catch { }',
        '}',
        '$after = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory;',
        'Write-Output "$n $($after - $before)"',
      ].join(' ');

      try {
        const { stdout } = await run('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
          timeout: 120000,
          maxBuffer: 1024 * 1024,
        });
        const [countText, deltaText] = String(stdout).trim().split(/\s+/u);
        const processes = Number.parseInt(countText, 10) || 0;
        const releasedKb = Number.parseInt(deltaText, 10) || 0;
        const releasedBytes = Math.max(0, releasedKb * 1024);

        return {
          ok: true,
          action: 'trim',
          processes,
          releasedBytes,
          releasedText: formatBytes(releasedBytes),
          memoryBefore: status,
          memoryAfter: service.status().memory,
          summary: `已整理 ${processes} 个非前台进程，可用内存增加约 ${formatBytes(releasedBytes)}（短期效果，系统会按需重新调入）。`,
        };
      } catch (error) {
        return { ok: false, action: 'trim', error: String(error?.message ?? error), summary: '内存整理失败，请查看错误信息。' };
      }
    },
  });
}
