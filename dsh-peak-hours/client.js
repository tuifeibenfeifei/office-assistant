/**
 * dsh-peak-hours — Client half.
 *
 * 用户可见部分：
 *   · 侧边栏常驻状态指示（`sidebar.panellist`）：一眼看到当前是高峰还是空闲、
 *     距离切换还有多久；**挂载时立即请求一次**，之后按可配置间隔刷新。
 *   · 输入框下方的紧凑条（`conversation.composer.dock`）：写提示词时顺手能看到档期。
 *
 * 本模块不 import 任何 Harness Client 包：
 * 组件是本地 markup，样式只引用 `--dsw-alias-*` 主题令牌，因此浅色/深色主题都能跟随宿主，
 * 也不会因为上游 primitive 改名而导致整个 slot 渲染崩溃。
 */
window.__ModuleLoader__.load({
  id: 'dsh-peak-hours',
  factory(require) {
    const React = require('react')
    const h = React.createElement
    const { useCallback, useEffect, useRef, useState } = React

    const STATUS_PATH = '/dsh-peak-hours/status'

    const COPY = {
      zh: {
        'tier.peak': '高峰',
        'tier.offPeak': '空闲',
        'tier.peakFull': '高峰时段（标准价）',
        'tier.offPeakFull': '空闲时段（半价）',
        'badge.peak': '高峰',
        'badge.offPeak': '半价',
        'label.beijing': '北京时间',
        'label.local': '本地时间',
        'label.remaining': '本段剩余',
        'label.next': '下次切换',
        'label.today': '今日高峰窗口',
        'label.todayOffPeak': '今日全天按空闲价',
        'label.rules': '规则',
        'label.ruleset': '规则版本',
        'action.refresh': '刷新',
        'action.details': '详情',
        'action.collapse': '收起',
        'state.loading': '正在读取峰谷状态…',
        'state.error': '读取峰谷状态失败',
        'state.stale': '（数据可能已过期）',
        'note.halfPrice': '空闲时段价格为高峰时段的一半',
        'note.weekend': '周六、周日全天按空闲价计费',
        'note.peakWindows': '工作日高峰：北京时间 09:00–12:00、14:00–18:00',
        'note.schedule': '打开 dsh 时会自动检测一次，之后按设置间隔刷新。',
      },
      en: {
        'tier.peak': 'Peak',
        'tier.offPeak': 'Off-peak',
        'tier.peakFull': 'Peak hours (standard price)',
        'tier.offPeakFull': 'Off-peak hours (half price)',
        'badge.peak': 'Peak',
        'badge.offPeak': 'Half',
        'label.beijing': 'Beijing time',
        'label.local': 'Local time',
        'label.remaining': 'Time left in this tier',
        'label.next': 'Next switch',
        'label.today': "Today's peak windows",
        'label.todayOffPeak': 'Off-peak price all day',
        'label.rules': 'Rules',
        'label.ruleset': 'Ruleset',
        'action.refresh': 'Refresh',
        'action.details': 'Details',
        'action.collapse': 'Collapse',
        'state.loading': 'Reading peak/off-peak status…',
        'state.error': 'Failed to read peak/off-peak status',
        'state.stale': '(data may be stale)',
        'note.halfPrice': 'Off-peak price is half of the peak price',
        'note.weekend': 'Saturday and Sunday are billed at the off-peak price all day',
        'note.peakWindows': 'Weekday peak: 09:00–12:00 and 14:00–18:00 Beijing time',
        'note.schedule': 'Checked once when dsh opens, then refreshed on the configured interval.',
      },
    }

    /** 取当前界面语言：以 <html lang> 为准，回落到中文。 */
    function currentLang() {
      const tag = (document.documentElement.getAttribute('lang') || navigator.language || 'zh').toLowerCase()
      return tag.startsWith('zh') ? 'zh' : 'en'
    }

    /** 极简翻译：按语言查内建字典，缺项回落到键名。 */
    function makeT() {
      return (key) => {
        const dict = COPY[currentLang()] ?? COPY.zh
        return dict[key] ?? COPY.zh[key] ?? key
      }
    }

    /** 主题令牌包装：深浅色由宿主变量决定，插件不写死颜色。 */
    const TOKENS = {
      text: 'var(--dsw-alias-text-primary, currentColor)',
      textMuted: 'var(--dsw-alias-text-secondary, currentColor)',
      border: 'var(--dsw-alias-border-secondary, rgba(128,128,128,0.35))',
      surface: 'var(--dsw-alias-surface-secondary, transparent)',
      peak: 'var(--dsw-alias-state-error-primary, #d4380d)',
      offPeak: 'var(--dsw-alias-state-success-primary, #389e0d)',
      accent: 'var(--dsw-alias-text-link, #1677ff)',
    }

    const BASE_STYLE = {
      font: 'inherit',
      fontSize: '12px',
      lineHeight: 1.5,
      color: TOKENS.text,
      boxSizing: 'border-box',
    }

    const PILL_STYLE = {
      display: 'inline-flex',
      alignItems: 'center',
      gap: '6px',
      padding: '2px 8px',
      borderRadius: '999px',
      border: `1px solid ${TOKENS.border}`,
      background: TOKENS.surface,
      cursor: 'pointer',
      whiteSpace: 'nowrap',
    }

    const DOT_STYLE = (tier) => ({
      width: '8px',
      height: '8px',
      borderRadius: '50%',
      background: tier === 'peak' ? TOKENS.peak : TOKENS.offPeak,
      flex: '0 0 auto',
    })

    const ROW_STYLE = { display: 'flex', justifyContent: 'space-between', gap: '12px' }
    const KEY_STYLE = { color: TOKENS.textMuted, flex: '0 0 auto' }
    const VALUE_STYLE = { textAlign: 'right', wordBreak: 'break-word' }

    function useStatus(intervalMs) {
      const [state, setState] = useState({ loading: true, error: null, data: null, at: 0 })
      const timerRef = useRef(null)

      const load = useCallback(async () => {
        try {
          const response = await fetch(STATUS_PATH, { cache: 'no-store' })
          const payload = await response.json()
          if (!response.ok || payload?.ok !== true) throw new Error(payload?.message ?? `HTTP ${response.status}`)
          setState({ loading: false, error: null, data: payload, at: Date.now() })
        } catch (error) {
          setState((prev) => ({ ...prev, loading: false, error: String(error?.message ?? error) }))
        }
      }, [])

      useEffect(() => {
        let cancelled = false
        const tick = () => {
          if (!cancelled) void load()
        }
        tick() // 打开 dsh / 挂载本组件时立即检测一次
        timerRef.current = setInterval(tick, intervalMs)
        return () => {
          cancelled = true
          if (timerRef.current !== null) clearInterval(timerRef.current)
          timerRef.current = null
        }
      }, [load, intervalMs])

      return { ...state, reload: load }
    }

    /** 面板里展示的详情行。 */
    function DetailRows({ t, result }) {
      const today = result.forecast?.[0]
      const rows = [
        [t('label.beijing'), result.beijingTimeText],
        ...(result.sameAsBeijing ? [] : [[t('label.local'), result.localTimeText]]),
        [t('label.remaining'), result.currentPeriod.remainingText],
        [
          t('label.next'),
          `${result.nextChange.beijingTimeText} → ${
            result.nextChange.toTier === 'peak' ? t('tier.peak') : t('tier.offPeak')
          }`,
        ],
        ...(result.isPeak && result.nextOffPeakText ? [[t('label.next') + ' · ' + t('tier.offPeak'), result.nextOffPeakText]] : []),
        ...(today
          ? [
              [
                today.allOffPeak ? t('label.todayOffPeak') : t('label.today'),
                today.allOffPeak ? today.weekdayLabel : today.peakWindows.map((w) => `${w.startText}–${w.endText}`).join('、'),
              ],
            ]
          : []),
        [t('label.ruleset'), result.rulesetVersion],
      ]
      return h(
        'div',
        { style: { display: 'grid', gap: '4px', marginTop: '8px' } },
        rows.map(([key, value], index) =>
          h('div', { key: index, style: ROW_STYLE }, h('span', { style: KEY_STYLE }, key), h('span', { style: VALUE_STYLE }, String(value))),
        ),
        h(
          'div',
          { style: { marginTop: '8px', color: TOKENS.textMuted, display: 'grid', gap: '2px' } },
          h('div', null, `· ${t('note.halfPrice')}`),
          h('div', null, `· ${t('note.weekend')}`),
          h('div', null, `· ${t('note.peakWindows')}`),
          h('div', null, `· ${t('note.schedule')}`),
        ),
      )
    }

    /** 侧边栏 / 输入框下方共用的峰谷指示组件。 */
    function makeIndicator({ variant }) {
      return function PeakHoursIndicator(props) {
        const t = makeT()
        const intervalMs = Number.isFinite(props?.refreshSeconds) ? Math.max(10, props.refreshSeconds) * 1000 : 60_000
        const { loading, error, data, reload } = useStatus(intervalMs)
        const [open, setOpen] = useState(false)
        const result = data?.result

        const tier = result?.tier ?? 'off-peak'
        const isPeak = result?.isPeak === true
        const badge = !result ? t('tier.offPeak') : isPeak ? t('badge.peak') : t('badge.offPeak')
        const title = !result
          ? t('state.loading')
          : `${isPeak ? t('tier.peakFull') : t('tier.offPeakFull')} · ${result.currentPeriod.remainingText}`

        const compact = variant === 'dock'

        const button = h(
          'button',
          {
            type: 'button',
            onClick: () => setOpen((value) => !value),
            title,
            'aria-label': title,
            'aria-expanded': open,
            style: { ...BASE_STYLE, ...PILL_STYLE, ...(compact ? { fontSize: '11px' } : {}) },
          },
          h('span', { style: DOT_STYLE(tier) }),
          h('span', null, badge),
          result ? h('span', { style: { color: TOKENS.textMuted } }, result.currentPeriod.remainingText) : null,
        )

        if (compact && !open) {
          return h('div', { style: { ...BASE_STYLE, display: 'flex', justifyContent: 'center', padding: '2px 0' } }, button)
        }

        return h(
          'div',
          { style: { ...BASE_STYLE, padding: compact ? '2px 0' : '0' } },
          h('div', { style: { display: 'flex', justifyContent: compact ? 'center' : 'flex-start' } }, button),
          ...(open
            ? [
                h(
                  'div',
                  {
                    style: {
                      marginTop: '6px',
                      padding: '8px 10px',
                      border: `1px solid ${TOKENS.border}`,
                      borderRadius: '8px',
                      background: TOKENS.surface,
                    },
                  },
                  loading && !result ? h('div', { style: { color: TOKENS.textMuted } }, t('state.loading')) : null,
                  error && !result
                    ? h('div', { style: { color: TOKENS.peak } }, `${t('state.error')}: ${error}`)
                    : null,
                  result ? h(DetailRows, { t, result }) : null,
                  h(
                    'div',
                    { style: { display: 'flex', gap: '8px', marginTop: '8px' } },
                    h(
                      'button',
                      {
                        type: 'button',
                        onClick: (event) => {
                          event.stopPropagation()
                          void reload()
                        },
                        style: { ...BASE_STYLE, ...PILL_STYLE },
                      },
                      t('action.refresh'),
                    ),
                    h(
                      'button',
                      {
                        type: 'button',
                        onClick: (event) => {
                          event.stopPropagation()
                          setOpen(false)
                        },
                        style: { ...BASE_STYLE, ...PILL_STYLE },
                      },
                      t('action.collapse'),
                    ),
                  ),
                ),
              ]
            : []),
        )
      }
    }

    const SidebarIndicator = makeIndicator({ variant: 'sidebar' })
    const DockIndicator = makeIndicator({ variant: 'dock' })

    /** 设置页里的说明区块（`settings.section`）。 */
    function PeakHoursSettings() {
      const t = makeT()
      const { data, error, reload } = useStatus(120_000)
      const result = data?.result
      return h(
        'section',
        { style: { ...BASE_STYLE, display: 'grid', gap: '6px' } },
        h('h3', { style: { margin: 0, fontSize: '14px', fontWeight: 600 } }, 'DeepSeek 峰谷时段'),
        h('div', { style: { color: TOKENS.textMuted } }, t('note.halfPrice')),
        h('div', { style: { color: TOKENS.textMuted } }, t('note.weekend')),
        h('div', { style: { color: TOKENS.textMuted } }, t('note.peakWindows')),
        h('div', { style: { color: TOKENS.textMuted } }, t('note.schedule')),
        result ? h(DetailRows, { t, result }) : null,
        error ? h('div', { style: { color: TOKENS.peak } }, `${t('state.error')}: ${error}`) : null,
        h(
          'button',
          {
            type: 'button',
            onClick: () => void reload(),
            style: { ...BASE_STYLE, ...PILL_STYLE, justifySelf: 'start', marginTop: '4px' },
          },
          t('action.refresh'),
        ),
      )
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        // 侧边栏常驻指示：挂载即检测一次，之后每分钟刷新
        ctx.slots.inject('sidebar.panellist', () =>
          ctx.slots.register(
            {
              name: 'sidebar.panellist',
              id: 'peak-hours',
              order: 40,
              label: () => makeT()('badge.offPeak'),
              inject: () => ({}),
            },
            SidebarIndicator,
          ),
        )

        // 输入框下方的紧凑条：写提示词时顺手可见
        ctx.slots.inject('conversation.composer.dock', () =>
          ctx.slots.register(
            {
              name: 'conversation.composer.dock',
              id: 'peak-hours-dock',
              order: 6,
            },
            DockIndicator,
          ),
        )

        // 设置页说明
        ctx.slots.inject('settings.section', () =>
          ctx.slots.register(
            {
              name: 'settings.section',
              id: 'peak-hours-settings',
              order: 60,
              label: () => 'DeepSeek 峰谷时段',
            },
            PeakHoursSettings,
          ),
        )
      },
    }
  },
})
