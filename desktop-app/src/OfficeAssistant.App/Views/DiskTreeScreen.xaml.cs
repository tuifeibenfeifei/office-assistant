using System.Windows;
using System.Windows.Controls;
using OfficeAssistant.App.ViewModels;

namespace OfficeAssistant.App.Views;

/// <summary>
/// 磁盘管理（列表视图）：可勾选的目录树 + 一键清理 / 深度清理 / 批量删除。
///
/// 早期版本这里的无限画布与拖拽搬运已整体移除：拖拽卡顿、学习成本高，
/// 而用户真正需要的是"能一键把可以清理的东西清掉"。删除与清理仍走同一套安全流程。
/// </summary>
public partial class DiskTreeScreen : WpfUserControl
{
    public DiskTreeScreen()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = EnsureTreeLoadedAsync();
    }

    /// <summary>点击返回箭头。</summary>
    public event Action? BackRequested;

    private DiskPageViewModel? Page => DataContext as DiskPageViewModel;

    /// <summary>
    /// 树为空时自己加载一次。
    /// 不依赖"宿主一定先跑过初始化"——界面创建顺序（用户导航、自检、渲染取证）不受控，
    /// 画布时代就踩过这个坑（渲染出来是空的）。
    /// </summary>
    private async Task EnsureTreeLoadedAsync()
    {
        if (Page is null || Page.Roots.Count > 0) return;
        try
        {
            if (Page.Volumes.Count == 0) await Page.InitializeAsync().ConfigureAwait(true);
            else await Page.RefreshTreeAsync().ConfigureAwait(true);

            if (Page.Roots.Count == 0)
                Page.StatusText = "尚未建立本地索引。点上方「扫描」建立索引后，这里会显示各盘的文件树。";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"目录树初始化失败：{ex.Message}");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    /// <summary>勾选变化后刷新批量统计。</summary>
    private void OnCheckClick(object sender, RoutedEventArgs e) => Page?.SyncChecked();

    /// <summary>选中节点时在状态区显示它的详情（路径、大小、风险级别）。</summary>
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Page is null) return;
        if (e.NewValue is not FileNodeViewModel node || node.IsPlaceholder)
        {
            Page.SelectedDetailText = string.Empty;
            return;
        }

        Page.SelectedDetailText =
            $"当前选中：{node.Name}\n路径：{node.Path}\n大小：{node.SizeText}\n类型：{node.TypeText}";
    }

    private async void OnDeleteCheckedClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.DeleteCheckedAsync().ConfigureAwait(true);
    }

    /// <summary>全选"当前已经展开的"节点（含各级子节点）。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        foreach (var root in Page.Roots) CheckRecursive(root);
        Page.SyncChecked();
    }

    private static void CheckRecursive(FileNodeViewModel node)
    {
        if (node.IsPlaceholder) return;
        node.IsChecked = true;
        foreach (var child in node.Children) CheckRecursive(child);
    }

    private void OnClearCheckedClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        foreach (var root in Page.Roots) UncheckRecursive(root);
        Page.SyncChecked();
    }

    private static void UncheckRecursive(FileNodeViewModel node)
    {
        node.IsChecked = false;
        foreach (var child in node.Children) UncheckRecursive(child);
    }

    // ------------------------------------------------------------------ 清理

    private async void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.BuildCleanupPreviewAsync().ConfigureAwait(true);
    }

    private void OnCancelCleanupClick(object sender, RoutedEventArgs e)
    {
        if (Page is not null) Page.IsCleanupPreviewOpen = false;
    }

    private async void OnConfirmCleanupClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.ConfirmCleanupAsync().ConfigureAwait(true);
        await Page.RefreshTreeAsync().ConfigureAwait(true);
    }

    private async void OnTrustedCleanupClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.BuildTrustedPreviewAsync().ConfigureAwait(true);
    }

    private void OnCancelTrustedClick(object sender, RoutedEventArgs e)
    {
        if (Page is not null) Page.IsTrustedPreviewOpen = false;
    }

    private async void OnConfirmTrustedClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.ConfirmTrustedCleanupAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ 待判断文件

    private async void OnUnknownClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.OpenUnknownDrawerAsync().ConfigureAwait(true);
    }

    private void OnCloseDrawerClick(object sender, RoutedEventArgs e) => Page?.CloseUnknownDrawer();

    private async void OnUnknownKeepClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.ApplyUnknownActionAsync("keep").ConfigureAwait(true);
    }

    private async void OnUnknownDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Page is null) return;
        await Page.ApplyUnknownActionAsync("delete").ConfigureAwait(true);
    }
}
