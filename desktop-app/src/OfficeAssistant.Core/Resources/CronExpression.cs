namespace OfficeAssistant.Core.Resources;

/// <summary>
/// 轻量 cron 表达式（5 段：分 时 日 月 周）。
/// 支持 <c>*</c>、<c>*/n</c>、<c>a-b</c>、<c>a-b/n</c>、<c>a,b,c</c> 以及 <c>@hourly/@daily/@weekly/@monthly</c> 别名。
/// 用于「固定时间执行」策略；「周期执行」由 <see cref="IntervalSchedule"/> 承担。
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes = [];
    private readonly HashSet<int> _hours = [];
    private readonly HashSet<int> _days = [];
    private readonly HashSet<int> _months = [];
    private readonly HashSet<int> _weekdays = [];
    private readonly bool _dayWildcard;
    private readonly bool _weekdayWildcard;

    public string Expression { get; }

    public CronExpression(string expression)
    {
        var expr = (expression ?? string.Empty).Trim().ToLowerInvariant();
        expr = expr switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "" => "0 0 * * *",
            _ => expr,
        };

        Expression = expr;
        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new ArgumentException($"cron 表达式需要 5 段（分 时 日 月 周），当前为 {parts.Length} 段：{expression}");

        Fill(_minutes, parts[0], 0, 59);
        Fill(_hours, parts[1], 0, 23);
        Fill(_days, parts[2], 1, 31);
        Fill(_months, parts[3], 1, 12);
        Fill(_weekdays, parts[4], 0, 6);
        _dayWildcard = parts[2] == "*";
        _weekdayWildcard = parts[4] == "*";
    }

    public static bool TryParse(string expression, out CronExpression? cron, out string? error)
    {
        try
        {
            cron = new CronExpression(expression);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            cron = null;
            error = ex.Message;
            return false;
        }
    }

    private static void Fill(HashSet<int> set, string field, int min, int max)
    {
        foreach (var segment in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var body = segment;

            var slash = segment.IndexOf('/');
            if (slash >= 0)
            {
                body = segment[..slash];
                var stepText = segment[(slash + 1)..];
                if (!int.TryParse(stepText, out step) || step <= 0)
                    throw new ArgumentException($"步长非法：{segment}");
            }

            int from, to;
            if (body == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = body.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(body[..dash], out from) || !int.TryParse(body[(dash + 1)..], out to))
                        throw new ArgumentException($"区间非法：{segment}");
                }
                else
                {
                    if (!int.TryParse(body, out from)) throw new ArgumentException($"数值非法：{segment}");
                    to = from;
                }
            }

            // 周日既接受 0 也接受 7
            if (max == 6 && to == 7) to = 6;
            if (from < min || to > max || from > to)
                throw new ArgumentException($"超出取值范围 [{min},{max}]：{segment}");

            for (var v = from; v <= to; v += step) set.Add(v);
        }
    }

    /// <summary>判断某个时刻是否命中（精确到分钟）。</summary>
    public bool Matches(DateTime time)
    {
        if (!_minutes.Contains(time.Minute)) return false;
        if (!_hours.Contains(time.Hour)) return false;
        if (!_months.Contains(time.Month)) return false;

        var dayOk = _days.Contains(time.Day);
        var weekday = (int)time.DayOfWeek;
        var weekdayOk = _weekdays.Contains(weekday);

        // cron 惯例：日与周都非 * 时取"或"，否则取"与"
        if (!_dayWildcard && !_weekdayWildcard) return dayOk || weekdayOk;
        return dayOk && weekdayOk;
    }

    /// <summary>命中的分钟键，用于避免同一分钟内重复触发。</summary>
    public static string MinuteKey(DateTime time) => time.ToString("yyyy-MM-dd HH:mm");

    public string Describe() => $"cron: {Expression}";
}

/// <summary>周期调度：每 N 分钟 / 每 N 小时执行一次。</summary>
public sealed class IntervalSchedule
{
    public int IntervalMinutes { get; set; } = 30;

    public bool IsDue(DateTime lastRun, DateTime now)
        => lastRun == default || (now - lastRun).TotalMinutes >= Math.Max(1, IntervalMinutes);

    public string Describe() => IntervalMinutes % 60 == 0
        ? $"每 {IntervalMinutes / 60} 小时执行一次"
        : $"每 {IntervalMinutes} 分钟执行一次";
}
