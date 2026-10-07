using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OfficeAssistant.App.Views;

/// <summary>
/// 主界面（文档 3.4 界面 1）。
///
/// 卡片的悬停上浮、边框辉光与按下缩放全部由 <c>Card.Clickable</c> 样式里的模板触发器完成
/// （见 Theme/DesignSystem.xaml 的实现注记：RenderTransform 内部对象不在模板命名域里，
/// 因此缩放/位移动画都放在模板内部，这里只负责导航事件）。
/// </summary>
public partial class HomeScreen : WpfUserControl
{
    public HomeScreen() => InitializeComponent();

    /// <summary>点击齿轮进入设置。</summary>
    public event Action? SettingsRequested;

    /// <summary>点击"网络加速"卡片。</summary>
    public event Action? NetworkRequested;

    /// <summary>点击"磁盘管理"卡片。</summary>
    public event Action? DiskRequested;

    /// <summary>点击"内存清理"卡片。</summary>
    public event Action? MemoryRequested;

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnNetworkClick(object sender, RoutedEventArgs e) => NetworkRequested?.Invoke();

    private void OnDiskClick(object sender, RoutedEventArgs e) => DiskRequested?.Invoke();

    private void OnMemoryClick(object sender, RoutedEventArgs e) => MemoryRequested?.Invoke();
}
