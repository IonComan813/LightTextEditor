using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using FluentAvalonia.UI.Controls;
using LightTextEditor.Core.Library;

namespace LightTextEditor.App.Views;

public enum SaveChoice { Save, DontSave, Cancel }

/// <summary>文件名对话框的参数。</summary>
public sealed record FileNamePrompt(
    string Title,
    string BaseName,
    string Extension,
    string PrimaryText,
    Func<string, string?> Validate)
{
    /// <summary>对话框顶部的说明文字。</summary>
    public string? Message { get; init; }

    /// <summary>显示在输入框下方的保存位置（只读）。</summary>
    public string? Location { get; init; }

    /// <summary>第二个按钮（如“不保存”）。为空时不显示。</summary>
    public string? SecondaryText { get; init; }
}

public sealed record FileNamePromptResult(SaveChoice Choice, string? FileName);

/// <summary>Fluent 风格的对话框。</summary>
internal static class Dialogs
{
    /// <summary>当前打开着对话框的窗口（每个窗口单独记录）。</summary>
    private static readonly HashSet<TopLevel> OwnersWithOpenDialog = [];

    /// <summary>这个窗口当前是否有对话框打开（打开时屏蔽快捷键、关闭标签等操作）。</summary>
    public static bool IsOpenFor(TopLevel owner) => OwnersWithOpenDialog.Contains(owner);

    private static async Task<FAContentDialogResult> ShowAsync(FAContentDialog dialog, TopLevel owner)
    {
        bool added = OwnersWithOpenDialog.Add(owner);
        try { return await dialog.ShowAsync(owner); }
        finally { if (added) OwnersWithOpenDialog.Remove(owner); }
    }

    public static async Task<SaveChoice> ConfirmSaveAsync(TopLevel owner, string documentTitle)
    {
        var dialog = new FAContentDialog
        {
            Title = "LightTextEditor",
            Content = $"是否将更改保存到“{documentTitle}”？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        return await ShowAsync(dialog, owner) switch
        {
            FAContentDialogResult.Primary => SaveChoice.Save,
            FAContentDialogResult.Secondary => SaveChoice.DontSave,
            _ => SaveChoice.Cancel,
        };
    }

    public static async Task<bool> ConfirmAsync(TopLevel owner, string title, string message, string confirmText, string cancelText = "取消")
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = FAContentDialogButton.Primary,
        };
        return await ShowAsync(dialog, owner) == FAContentDialogResult.Primary;
    }

    /// <summary>
    /// 安装后第一次启动时的“安装完成”对话框。返回关闭时“打开默认应用设置”选框是否勾选。
    /// （Velopack 的安装程序没有可定制的界面，所以放在第一次启动时。）
    /// </summary>
    public static async Task<bool> ShowInstalledAsync(TopLevel owner)
    {
        var openSettings = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "关闭后打开 Windows“默认应用”设置，把 LightTextEditor 设为这些文件的默认程序",
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = true,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel { MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = "LightTextEditor 已安装。.txt、.md 和常见代码文件的右键“打开方式”菜单中已经加入了本程序，" +
                   "原来的默认程序不变。\n\n以后也可以在“文件 → 设为默认程序”中更改。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(openSettings);

        var dialog = new FAContentDialog
        {
            Title = "安装完成",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = FAContentDialogButton.Close,
        };
        await ShowAsync(dialog, owner);
        return openSettings.IsChecked == true;
    }

    public static async Task ShowErrorAsync(TopLevel owner, string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "确定",
            DefaultButton = FAContentDialogButton.Close,
        };
        await ShowAsync(dialog, owner);
    }

    /// <summary>
    /// 输入文件名和后缀的对话框（不询问保存位置）。
    /// <paramref name="validate"/> 接收完整文件名（含后缀），返回错误信息或 null。
    /// 设置了 <see cref="FileNamePrompt.SecondaryText"/>（如“不保存”）时会多一个按钮。
    /// </summary>
    public static async Task<FileNamePromptResult> PromptFileNameAsync(TopLevel owner, FileNamePrompt prompt)
    {
        var nameBox = new TextBox { Text = prompt.BaseName };
        var extBox = new ComboBox
        {
            IsEditable = true,
            Width = 120,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            ItemsSource = FileTypes.Common.Select(t => t.Extension).ToList(),
            Text = prompt.Extension,
            MaxDropDownHeight = 320,
        };
        ToolTip.SetTip(extBox, "后缀，可从列表选择或直接输入");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(nameBox);
        Grid.SetColumn(extBox, 1);
        row.Children.Add(extBox);

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            FontSize = 12,
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 420, MaxWidth = 520 };
        if (prompt.Message is not null)
            panel.Children.Add(new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "文件名" });
        panel.Children.Add(row);
        if (prompt.Location is not null)
        {
            var location = new TextBlock
            {
                Text = "位置：" + prompt.Location,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            };
            panel.Children.Add(location);
        }
        panel.Children.Add(error);

        var dialog = new FAContentDialog
        {
            Title = prompt.Title,
            Content = panel,
            PrimaryButtonText = prompt.PrimaryText,
            SecondaryButtonText = prompt.SecondaryText ?? "",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };

        string CurrentFileName()
        {
            var ext = FileTypes.NormalizeExtension(extBox.Text ?? extBox.SelectedItem as string);
            return (nameBox.Text ?? "").Trim() + ext;
        }

        void Revalidate()
        {
            var msg = prompt.Validate(CurrentFileName());
            error.Text = msg ?? "";
            error.IsVisible = msg is not null;
            dialog.IsPrimaryButtonEnabled = msg is null;
        }

        nameBox.TextChanged += (_, _) => Revalidate();
        extBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty || e.Property == SelectingItemsControl.SelectedItemProperty)
                Revalidate();
        };
        Revalidate();
        dialog.Opened += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        var result = await ShowAsync(dialog, owner);
        return result switch
        {
            FAContentDialogResult.Primary => new FileNamePromptResult(SaveChoice.Save, CurrentFileName()),
            FAContentDialogResult.Secondary => new FileNamePromptResult(SaveChoice.DontSave, null),
            _ => new FileNamePromptResult(SaveChoice.Cancel, null),
        };
    }

    /// <summary>输入单行文本的简单对话框。</summary>
    public static async Task<string?> PromptTextAsync(TopLevel owner, string title, string label, string initialText,
        Func<string, string?> validate)
    {
        var box = new TextBox { Text = initialText, MinWidth = 300 };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            FontSize = 12,
            IsVisible = false,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(error);
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        void Revalidate()
        {
            var msg = validate(box.Text ?? "");
            error.Text = msg ?? "";
            error.IsVisible = msg is not null;
            dialog.IsPrimaryButtonEnabled = msg is null;
        }
        box.TextChanged += (_, _) => Revalidate();
        Revalidate();
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await ShowAsync(dialog, owner) == FAContentDialogResult.Primary ? box.Text : null;
    }

    public static async Task<int?> PromptLineNumberAsync(TopLevel owner, int current, int max)
    {
        var box = new FANumberBox
        {
            Minimum = 1,
            Maximum = max,
            Value = current,
            SpinButtonPlacementMode = FANumberBoxSpinButtonPlacementMode.Hidden,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"行号（1 - {max}）" });
        panel.Children.Add(box);
        var dialog = new FAContentDialog
        {
            Title = "转到行",
            Content = panel,
            PrimaryButtonText = "转到",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus();
        if (await ShowAsync(dialog, owner) != FAContentDialogResult.Primary) return null;
        return double.IsNaN(box.Value) ? null : (int)Math.Clamp(box.Value, 1, max);
    }
}
