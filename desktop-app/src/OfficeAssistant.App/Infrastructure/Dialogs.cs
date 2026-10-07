using Microsoft.Win32;

namespace OfficeAssistant.App.Infrastructure;

/// <summary>窗口级对话框服务：把 MessageBox / 文件对话框收口，便于页面视图模型与 UI 框架解耦。</summary>
public static class Dialogs
{
    public static bool Confirm(WpfWindow? owner, string title, string message)
        => Show(owner, title, message, WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question) == WpfMessageBoxResult.Yes;

    public static void ShowText(WpfWindow? owner, string title, string message)
        => Show(owner, title, message, WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);

    public static void ShowError(WpfWindow? owner, string title, string message)
        => Show(owner, title, message, WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);

    private static WpfMessageBoxResult Show(WpfWindow? owner, string title, string message,
        WpfMessageBoxButton button, WpfMessageBoxImage icon)
    {
        var fullTitle = $"全能办公助手 · {title}";
        return owner is { IsVisible: true }
            ? WpfMessageBox.Show(owner, message, fullTitle, button, icon)
            : WpfMessageBox.Show(message, fullTitle, button, icon);
    }

    public static string? OpenFile(WpfWindow? owner, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public static string? SaveFile(WpfWindow? owner, string defaultName, string filter = "CSV 文件 (*.csv)|*.csv|所有文件|*.*")
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = $"{defaultName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>选择目录（用于"移动到…"）。使用 WinForms 的 FolderBrowserDialog，支持长路径与新建文件夹。</summary>
    public static string? PickFolder(WpfWindow? owner, string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            AutoUpgradeEnabled = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    /// <summary>需要用户输入确认词的高危操作对话框。</summary>
    public static string? PromptForText(WpfWindow? owner, string title, string message)
    {
        var window = new WpfWindow
        {
            Title = $"全能办公助手 · {title}",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner is { IsVisible: true } ? owner : null,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var input = new WpfTextBox { Padding = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new WpfButton { Content = "确认执行", MinWidth = 90, IsDefault = true };
        var cancel = new WpfButton { Content = "取消", MinWidth = 70, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        ok.Click += (_, _) => { window.DialogResult = true; };
        input.Focus();

        return window.ShowDialog() == true ? input.Text : null;
    }
}