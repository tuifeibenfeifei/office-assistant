/**
 * dsh-office-assistant — Client half.
 *
 * 侧边栏常驻一个小面板：内存占用、临时文件可清理量。
 * 组件是本地 markup，样式只引用 `--dsw-alias-*` 主题令牌，
 * 因此浅色/深色主题都能跟随宿主，也不依赖任何 Harness Client 包。
 */
window.__ModuleLoader__.load({
  id: 'dsh-office-assistant',
  factory(require) {
    const React = require('react');
    const h = React.createElement;
    const { useCallback, useEffect, useState } = React;

    const STATUS_PATH = '/dsh-office-assistant/status';

    const COPY = {
      zh: {
        'title': '办公助手',
        'memory': '内存',
        'free': '可用',
        'used': '使用率',
        'refresh': '刷新',
        'loading': '正在读取…',
        'error': '读取失败',
        'hint': '在对话里让助手调用 office_scan / office_clean / office_memory 即可扫描磁盘、清理临时文件、整理内存。',
      },
      en: {
        'title': 'Office Assistant',
        'memory': 'Memory',
        'free': 'Free',
        'used': 'Used',
        'refresh': 'Refresh',
        'loading': 'Loading…',
        'error': 'Failed to load',
        'hint': 'Ask the agent to call office_scan / office_clean / office_memory to scan disks, clean temp files, or trim memory.',
      },
    };

    function currentLang() {
      const tag = (document.documentElement.getAttribute('lang') || navigator.language || 'zh').toLowerCase();
      return tag.startsWith('zh') ? 'zh' : 'en';
    }

    function makeT() {
      return (key) => {
        const dict = COPY[currentLang()] ?? COPY.zh;
        return dict[key] ?? COPY.zh[key] ?? key;
      };
    }

    const TOKENS = {
      text: 'var(--dsw-alias-text-primary, currentColor)',
      textMuted: 'var(--dsw-alias-text-secondary, currentColor)',
      border: 'var(--dsw-alias-border-secondary, rgba(128,128,128,0.35))',
      surface: 'var(--dsw-alias-surface-secondary, transparent)',
      ok: 'var(--dsw-alias-state-success-primary, #389e0d)',
      warn: 'var(--dsw-alias-state-warning-primary, #d48806)',
      danger: 'var(--dsw-alias-state-error-primary, #d4380d)',
    };

    const BASE = {
      font: 'inherit',
      fontSize: '12px',
      lineHeight: 1.5,
      color: TOKENS.text,
      boxSizing: 'border-box',
    };

    const ROW = { display: 'flex', justifyContent: 'space-between', gap: '12px' };
    const KEY = { color: TOKENS.textMuted };

    /** 内存占用超过这两个阈值时换颜色，让状态一眼可见。 */
    function usageColor(percent) {
      if (percent >= 90) return TOKENS.danger;
      if (percent >= 75) return TOKENS.warn;
      return TOKENS.ok;
    }

    function Pill({ onClick, title, children }) {
      return h(
        'button',
        {
          type: 'button',
          onClick,
          title,
          style: {
            ...BASE,
            display: 'inline-flex',
            alignItems: 'center',
            gap: '6px',
            padding: '2px 8px',
            borderRadius: '999px',
            border: `1px solid ${TOKENS.border}`,
            background: TOKENS.surface,
            cursor: 'pointer',
            whiteSpace: 'nowrap',
          },
        },
        children,
      );
    }

    function useStatus(intervalMs) {
      const [state, setState] = useState({ loading: true, error: null, data: null });

      const load = useCallback(async () => {
        try {
          const response = await fetch(STATUS_PATH, { cache: 'no-store' });
          const payload = await response.json();
          if (!response.ok || payload?.ok !== true) throw new Error(payload?.message ?? `HTTP ${response.status}`);
          setState({ loading: false, error: null, data: payload });
        } catch (error) {
          setState((prev) => ({ ...prev, loading: false, error: String(error?.message ?? error) }));
        }
      }, []);

      useEffect(() => {
        let cancelled = false;
        const tick = () => { if (!cancelled) void load(); };
        tick();
        const timer = setInterval(tick, intervalMs);
        return () => { cancelled = true; clearInterval(timer); };
      }, [load, intervalMs]);

      return { ...state, reload: load };
    }

    function OfficePanel() {
      const t = makeT();
      const { loading, error, data, reload } = useStatus(30_000);
      const [open, setOpen] = useState(false);
      const memory = data?.memory;

      const pill = h(
        Pill,
        { onClick: () => setOpen((v) => !v), title: memory ? `${t('memory')} ${memory.usedPercent}%` : t('loading') },
        memory ? h('span', { style: { width: 8, height: 8, borderRadius: '50%', background: usageColor(memory.usedPercent), display: 'inline-block' } }) : null,
        h('span', null, memory ? `${t('memory')} ${memory.usedPercent}%` : t('loading')),
      );

      if (!open) return h('div', { style: { ...BASE, padding: '2px 0' } }, pill);

      return h(
        'div',
        { style: { ...BASE, display: 'grid', gap: '8px' } },
        pill,
        h(
          'div',
          {
            style: {
              padding: '8px 10px',
              border: `1px solid ${TOKENS.border}`,
              borderRadius: '8px',
              background: TOKENS.surface,
              display: 'grid',
              gap: '4px',
            },
          },
          loading && !memory ? h('div', { style: { color: TOKENS.textMuted } }, t('loading')) : null,
          error && !memory ? h('div', { style: { color: TOKENS.danger } }, `${t('error')}: ${error}`) : null,
          memory
            ? h(
                'div',
                { style: { display: 'grid', gap: '4px' } },
                h('div', { style: ROW }, h('span', { style: KEY }, t('used')), h('span', { style: { color: usageColor(memory.usedPercent) } }, `${memory.usedPercent}%`)),
                h('div', { style: ROW }, h('span', { style: KEY }, t('memory')), h('span', null, `${memory.usedText} / ${memory.totalText}`)),
                h('div', { style: ROW }, h('span', { style: KEY }, t('free')), h('span', null, memory.freeText)),
              )
            : null,
          h('div', { style: { color: TOKENS.textMuted, marginTop: '4px' } }, t('hint')),
          h(
            'div',
            { style: { display: 'flex', gap: '8px', marginTop: '6px' } },
            h(Pill, { onClick: () => void reload(), title: t('refresh') }, t('refresh')),
            h(Pill, { onClick: () => setOpen(false), title: '收起' }, '收起'),
          ),
        ),
      );
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        // 侧边栏常驻状态条目
        ctx.slots.inject('sidebar.panellist', () =>
          ctx.slots.register(
            {
              name: 'sidebar.panellist',
              id: 'office-assistant',
              order: 45,
              label: () => makeT()('title'),
              inject: () => ({}),
            },
            OfficePanel,
          ),
        );

        // 输入框下方的紧凑条
        ctx.slots.inject('conversation.composer.dock', () =>
          ctx.slots.register(
            { name: 'conversation.composer.dock', id: 'office-assistant-dock', order: 7 },
            function OfficeDock() {
              const t = makeT();
              const { data } = useStatus(60_000);
              const memory = data?.memory;
              return h(
                'div',
                { style: { ...BASE, display: 'flex', justifyContent: 'center', padding: '2px 0' } },
                h(
                  Pill,
                  { title: t('hint') },
                  h('span', { style: { width: 8, height: 8, borderRadius: '50%', background: memory ? usageColor(memory.usedPercent) : TOKENS.textMuted, display: 'inline-block' } }),
                  h('span', null, memory ? `${t('memory')} ${memory.usedPercent}%` : t('title')),
                ),
              );
            },
          ),
        );
      },
    };
  },
});
