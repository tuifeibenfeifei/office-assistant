using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.Infrastructure;

/// <summary>
/// 系统托盘服务：常驻图标、右键菜单、气泡通知。
/// 图标全部由代码生成（不依赖外部 .ico 资源文件），便于离线构建与便携分发。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private System.Drawing.Icon? _currentIcon;
    private int _badge;

    public TrayService(Action showWindow, Action exitApplication, Action? showHelp = null, bool autoCleanupOn = false)
    {
        // 文档 3.6 规定的右键菜单：打开主界面 / 使用说明 / 退出
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => showWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("立即整理内存", null, (_, _) => MemoryRequested?.Invoke());
        if (showHelp is not null) menu.Items.Add("使用说明", null, (_, _) => showHelp());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApplication());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = AppPaths.ProductName,
        };
        // 文档 3.6：左键单击打开/唤起主窗口，双击同样唤起
        _notifyIcon.Click += (_, e) =>
        {
            if (e is System.Windows.Forms.MouseEventArgs { Button: System.Windows.Forms.MouseButtons.Left })
                showWindow();
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
        _currentIcon = IconFactory.CreateAppIcon(autoCleanupOn: autoCleanupOn);
        _notifyIcon.Icon = _currentIcon;
    }

    public event Action? MemoryRequested;

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 63 字符
        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    /// <summary>更新角标（0 表示不显示角标）。</summary>
    public void SetBadge(int count)
    {
        if (_badge == count) return;
        _badge = count;
        var next = IconFactory.CreateAppIcon(count);
        _notifyIcon.Icon = next;
        _currentIcon?.Dispose();
        _currentIcon = next;
    }

    /// <summary>切换图标形态（例如自动清理开启时加绿色圆点）。</summary>
    public void SetIcon(System.Drawing.Icon icon)
    {
        _notifyIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(10000);
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘通知失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _currentIcon?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>用 GDI+ 生成应用图标与托盘图标。</summary>
public static class IconFactory
{
    // 文档 3.6：16×16 深蓝圆角底 + 白色闪电与齿轮组合线标
    private static readonly System.Drawing.Color DeepBlue = System.Drawing.Color.FromArgb(0x0A, 0x16, 0x33);
    private static readonly System.Drawing.Color DeepBlueLight = System.Drawing.Color.FromArgb(0x12, 0x2B, 0x57);

    /// <summary>生成应用主图标（内存中位图，不落盘）。</summary>
    /// <param name="badge">角标数字，0 表示不显示。</param>
    /// <param name="autoCleanupOn">自动清理开启时在左上角加一个亮绿圆点，便于一眼看出状态。</param>
    public static System.Drawing.Icon CreateAppIcon(int badge = 0, bool autoCleanupOn = false)
    {
        using var bitmap = new System.Drawing.Bitmap(64, 64);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // 深蓝圆角底
            using var background = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(2, 2, 60, 60), DeepBlue, DeepBlueLight, System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
            using var path = RoundedRect(new Rectangle(2, 2, 60, 60), 14);
            g.FillPath(background, path);

            // 白色线性闪电（几何绘制，保证任意缩放都清晰，不依赖字体）
            using var bolt = new System.Drawing.Drawing2D.GraphicsPath();
            bolt.AddPolygon(
            [
                new System.Drawing.PointF(35, 8),
                new System.Drawing.PointF(18, 35),
                new System.Drawing.PointF(29, 35),
                new System.Drawing.PointF(25, 56),
                new System.Drawing.PointF(45, 27),
                new System.Drawing.PointF(33, 27),
            ]);
            using var boltPen = new System.Drawing.Pen(System.Drawing.Color.White, 4.2f)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            g.DrawPath(boltPen, bolt);

            // 右下角齿轮示意（线标组合，呼应"工具"属性）
            using var gearPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x9B, 0xB6, 0xE5), 3.0f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawEllipse(gearPen, 40, 40, 16, 16);
            g.DrawEllipse(gearPen, 46, 46, 4, 4);

            // 自动清理开启：左上角亮绿圆点（与界面 4 的"自动清理已开启"指示同一语义）
            if (autoCleanupOn)
            {
                using var dotBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x22, 0xE5, 0x84));
                g.FillEllipse(dotBrush, 4, 4, 14, 14);
            }

            if (badge > 0)
            {
                var text = badge > 99 ? "99+" : badge.ToString();
                using var badgeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xE0, 0x2B, 0x1C));
                g.FillEllipse(badgeBrush, 34, 34, 30, 30);
                var format = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center,
                };
                using var badgeFont = new System.Drawing.Font("Segoe UI", text.Length > 2 ? 13 : 17, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                using var badgeText = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                g.DrawString(text, badgeFont, badgeText, new System.Drawing.RectangleF(34, 34, 30, 30), format);
            }
        }

        return ToIcon(bitmap);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(System.Drawing.Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static System.Drawing.Icon ToIcon(System.Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            // 复制一份，避免句柄在 temporary 释放后失效
            return (System.Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}