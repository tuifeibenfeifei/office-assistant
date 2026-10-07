using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using OfficeAssistant.App.ViewModels;

namespace OfficeAssistant.App.Views;

/// <summary>
/// 网络加速（文档 3.4 界面 2）。
/// 动效按界面 2 规格：执行期间按钮做轻量呼吸（透明度 0.85↔1，周期 1.2s），完成后恢复常态。
/// </summary>
public partial class NetworkScreen : WpfUserControl
{
    private NetworkPageViewModel? _watched;

    public NetworkScreen()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();
    }

    /// <summary>点击返回箭头。</summary>
    public event Action? BackRequested;

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void OnAccelerateClick(object sender, RoutedEventArgs e) => StartBreathing();

    private void HookViewModel()
    {
        var vm = DataContext as NetworkPageViewModel;
        if (ReferenceEquals(_watched, vm)) return;

        if (_watched is not null) _watched.PropertyChanged -= OnViewModelPropertyChanged;
        _watched = vm;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NetworkPageViewModel.IsProbing)) return;

        if (_watched?.IsProbing == true) StartBreathing();
        else StopBreathing();
    }

    /// <summary>执行期间的呼吸效果：透明度 0.85↔1，周期 1.2s，无限循环直到结束。</summary>
    private void StartBreathing()
    {
        var animation = new DoubleAnimation
        {
            From = 0.85,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(1200),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        AccelerateButton.BeginAnimation(System.Windows.UIElement.OpacityProperty, animation);
    }

    private void StopBreathing()
    {
        AccelerateButton.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
        AccelerateButton.Opacity = 1.0;
    }
}
