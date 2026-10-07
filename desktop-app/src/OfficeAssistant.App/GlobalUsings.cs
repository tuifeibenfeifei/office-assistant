// ============================================================================
// 本项目的 using 策略
//
// 项目同时引用 WPF（界面）与 Windows Forms（系统托盘 NotifyIcon）。若按 SDK 默认方式
// 引入两边的隐式 using，MessageBox / Button / TextBox / Binding / Color / Font /
// HorizontalAlignment 等大量类型会二义，代码可读性与可维护性都会变差。
//
// 因此这里：关闭 ImplicitUsings，只全局引入 .NET 基础命名空间与 WPF 界面命名空间；
// GDI+（System.Drawing.*）与 Windows Forms（System.Windows.Forms）只在真正用到的
// 文件里显式书写完全限定名。这样"哪里用了桌面框架的哪一部分"一目了然。
//
// 注意：C# 要求所有 global using 必须位于非 global using 之前，本文件顺序不可调整。
// ============================================================================

// ---- .NET 基础命名空间（代替隐式 using）
global using global::System;
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Threading;
global using global::System.Threading.Tasks;

// ---- WPF 界面层
global using global::System.Windows;
global using global::System.Windows.Controls;
global using global::System.Windows.Data;
global using global::System.Windows.Input;
global using global::System.Windows.Media;

// ---- WPF 类型的固定别名（与 WinForms 同名时有歧义，统一用 Wpf 前缀）
global using WpfApplication = System.Windows.Application;
global using WpfUserControl = System.Windows.Controls.UserControl;
global using WpfWindow = System.Windows.Window;
global using WpfButton = System.Windows.Controls.Button;
global using WpfTextBox = System.Windows.Controls.TextBox;
global using WpfOrientation = System.Windows.Controls.Orientation;
global using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
global using WpfVerticalAlignment = System.Windows.VerticalAlignment;
global using WpfVisibility = System.Windows.Visibility;
global using WpfMessageBox = System.Windows.MessageBox;
global using WpfMessageBoxButton = System.Windows.MessageBoxButton;
global using WpfMessageBoxImage = System.Windows.MessageBoxImage;
global using WpfMessageBoxResult = System.Windows.MessageBoxResult;
global using WpfBinding = System.Windows.Data.Binding;
global using WpfBindingBase = System.Windows.Data.BindingBase;
global using WpfIValueConverter = System.Windows.Data.IValueConverter;
global using WpfColor = System.Windows.Media.Color;
global using WpfColorConverter = System.Windows.Media.ColorConverter;
global using WpfBrushes = System.Windows.Media.Brushes;
global using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
global using WpfDragEventArgs = System.Windows.DragEventArgs;
global using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
global using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
global using WpfRoutedEventArgs = System.Windows.RoutedEventArgs;
global using WpfStartupEventArgs = System.Windows.StartupEventArgs;
global using WpfDispatcherUnhandledExceptionEventArgs = System.Windows.Threading.DispatcherUnhandledExceptionEventArgs;
global using WpfDataObject = System.Windows.DataObject;
global using WpfDragDrop = System.Windows.DragDrop;
global using WpfDragDropEffects = System.Windows.DragDropEffects;
global using WpfMouseButton = System.Windows.Input.MouseButton;
global using WpfMouseButtonState = System.Windows.Input.MouseButtonState;
global using WpfPanel = System.Windows.Controls.Panel;
global using WpfClipboard = System.Windows.Clipboard;
global using WpfGrid = System.Windows.Controls.Grid;
global using WpfImage = System.Windows.Controls.Image;
global using WpfPath = System.Windows.Shapes.Path;
global using WpfRadioButton = System.Windows.Controls.RadioButton;
global using WpfPoint = System.Windows.Point;

// ---- 核心模型短别名（避免与 WinForms 类型重名）
global using FileTreeNode = OfficeAssistant.Core.Models.TreeNode;
global using FileEntryModel = OfficeAssistant.Core.Models.FileEntry;
global using HomeworkModel = OfficeAssistant.Core.Models.Homework;
