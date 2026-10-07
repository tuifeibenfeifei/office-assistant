using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.Views;

/// <summary>
/// 设置（文档 3.4 界面 5）：左侧导航三项，右侧常规 / 使用说明 / 关于。
/// 使用说明是全软件唯一允许出现说明文字的地方，因此内容全部集中在这里。
/// </summary>
public partial class SettingsScreen : WpfUserControl
{
    private static readonly (string Title, string Body)[] HelpEntries =
    [
        ("点绿色圆圈展开盘里的文件夹",
         "磁盘管理里每个盘方块右边都有一个发光的小绿圈，点它就会展开这个盘里的文件夹；再点一次就收起来。"),
        ("拖动文件夹方块就能搬家",
         "按住文件夹小方块拖到别的盘或别的文件夹上，松手会问你确认，确认后文件才真正移动。"),
        ("拖到垃圾桶就是删除，还能找回来",
         "把文件夹方块拖到右下角的垃圾桶，确认后文件会进入系统回收站，随时可以从回收站恢复。"),
        ("一键清理只删安全的东西",
         "点右上角的一键清理，先给你看一张清单：临时文件、缓存、旧安装包、回收站内容、浏览器缓存，确认后才删除。"),
        ("看不懂的文件不会乱删",
         "左下角「待判断文件」里放的是程序认不出内容的文件，你可以自己决定保留、删除还是移动。"),
        ("点大圆按钮就能清理内存",
         "内存清理页中间那个大绿按钮，点一下就开始整理内存；它不会关掉你正在用的程序。"),
        ("加速按钮只管合法手段",
         "网络加速只做镜像切换、测速和配置建议，不改系统网络设置，也不会在后台常驻任何代理。"),
        ("关闭窗口不会退出程序",
         "点右上角关闭时程序会缩到系统托盘继续工作；要彻底退出，右键托盘图标选「退出」。"),
        ("所有数据都在你自己电脑上",
         "扫描结果、设置和日志只存在本机数据目录里，不会上传到任何服务器；设置页可以一键清除。"),
    ];

    public SettingsScreen()
    {
        InitializeComponent();
        BuildHelpEntries();
        Loaded += (_, _) => ShowPage("general");
    }

    /// <summary>点击返回箭头。</summary>
    public event Action? BackRequested;

    /// <summary>外部入口（托盘菜单"使用说明"）要求直接打开使用说明页。</summary>
    public void ShowHelpPage()
    {
        NavHelp.IsChecked = true;
        ShowPage("help");
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (sender is WpfRadioButton { Tag: string tag } && IsLoaded) ShowPage(tag);
    }

    /// <summary>切换右侧内容，按规格做 150ms 淡入。</summary>
    private void ShowPage(string tag)
    {
        var general = tag == "general";
        var help = tag == "help";
        var about = tag == "about";

        PageGeneral.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = help ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = about ? Visibility.Visible : Visibility.Collapsed;

        // 先归零再淡入：否则从 Collapsed 切回来时会保留上一次动画的终值，看起来像"没切换"
        var target = general ? PageGeneral : help ? (FrameworkElement)PageHelp : PageAbout;
        target.Opacity = 0;
        FadeIn(target);
    }

    private static void FadeIn(FrameworkElement element)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(System.Windows.UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// 生成"使用说明"条目：一行 16px 白色粗体小标题 + 一段 14px 正文 + 一张示意图占位，
    /// 条与条之间 24px 间距（文档界面 5）。
    /// </summary>
    private void BuildHelpEntries()
    {
        foreach (var (title, body) in HelpEntries)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };

            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text"),
                TextWrapping = TextWrapping.Wrap,
            });

            panel.Children.Add(new TextBlock
            {
                Text = body,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.UI"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            panel.Children.Add(BuildDiagram());
            HelpList.Children.Add(panel);
        }
    }

    /// <summary>
    /// 示意图：用统一线性图标与示意图形拼出的简图占位。
    /// 文档要求每条说明配一张示意图，这里先以矢量简图占位，后续可替换为界面截图。
    /// </summary>
    private Border BuildDiagram()
    {
        var canvas = new Grid { Height = 92, Width = 260, HorizontalAlignment = WpfHorizontalAlignment.Left };

        var frame = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Brush.CardFill"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };
        canvas.Children.Add(frame);

        var icon = new WpfPath
        {
            Data = (System.Windows.Media.Geometry)FindResource("Geo.Folder"),
            Style = (System.Windows.Style)FindResource("Icon.Base"),
            Width = 34,
            Height = 34,
            Stroke = (System.Windows.Media.Brush)FindResource("Brush.TextSoft"),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        canvas.Children.Add(icon);

        return new Border { Margin = new Thickness(0, 10, 0, 0), Child = canvas };
    }
}
