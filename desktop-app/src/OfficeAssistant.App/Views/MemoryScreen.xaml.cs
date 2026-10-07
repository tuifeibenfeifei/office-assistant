using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OfficeAssistant.App.ViewModels;

namespace OfficeAssistant.App.Views;

/// <summary>
/// 内存清理（文档 3.4 界面 4）。
/// 动效按规格：清理执行中圆环以 1 秒一圈匀速旋转并带绿色流光，完成后恢复静态。
/// </summary>
public partial class MemoryScreen : WpfUserControl
{
    private ResourcePageViewModel? _watched;

    public MemoryScreen()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => { HookViewModel(); SyncAutoState(); };
    }

    /// <summary>点击返回箭头。</summary>
    public event Action? BackRequested;

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void OnCleanClick(object sender, RoutedEventArgs e) => StartSpinning();

    private void HookViewModel()
    {
        var vm = DataContext as ResourcePageViewModel;
        if (ReferenceEquals(_watched, vm)) return;

        if (_watched is not null) _watched.PropertyChanged -= OnViewModelPropertyChanged;
        _watched = vm;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnViewModelPropertyChanged;
            SyncFromViewModel();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ResourcePageViewModel.IsRunning):
                if (_watched?.IsRunning == true) StartSpinning();
                else StopSpinning();
                break;
            case nameof(ResourcePageViewModel.Enabled):
                SyncAutoState();
                break;
        }
    }

    /// <summary>圆环旋转：1 秒一圈、无限循环，直到清理结束。</summary>
    private void StartSpinning()
    {
        RingFlow.Opacity = 1;
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RingSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        CleanButton.Content = "清理中…";
    }

    private void StopSpinning()
    {
        RingSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        RingFlow.Opacity = 0;
        CleanButton.Content = "立即清理";
        RollNumber();
    }

    /// <summary>完成后数字滚动更新到新值（文档：数字以滚动动画更新）。</summary>
    private void RollNumber()
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ReleasedValue.BeginAnimation(System.Windows.UIElement.OpacityProperty, animation);
    }

    /// <summary>同步右上角"自动清理"文案与圆点颜色。</summary>
    private void SyncAutoState()
    {
        var enabled = _watched?.Enabled ?? false;
        AutoText.Text = enabled ? "自动清理已开启" : "自动清理已关闭";
        AutoDot.Fill = enabled
            ? (System.Windows.Media.Brush)FindResource("Brush.Green")
            : (System.Windows.Media.Brush)FindResource("Brush.TextFaint");
        AutoDot.Effect = enabled
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Color = System.Windows.Media.Color.FromRgb(0x22, 0xE5, 0x84),
                Opacity = 0.8,
            }
            : null;
    }

    private void SyncFromViewModel()
    {
        if (_watched is null) return;
        if (_watched.IsRunning) StartSpinning();
        SyncAutoState();
    }
}
