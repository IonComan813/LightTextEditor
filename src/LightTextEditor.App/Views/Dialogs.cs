using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace LightTextEditor.App.Views;

public enum SaveChoice { Save, DontSave, Cancel }

/// <summary>Fluent 风格的对话框。</summary>
internal static class Dialogs
{
    /// <summary>当前是否有对话框打开（打开时屏蔽全局快捷键）。</summary>
    public static bool IsOpen { get; private set; }

    private static async Task<FAContentDialogResult> ShowAsync(FAContentDialog dialog, TopLevel owner)
    {
        IsOpen = true;
        try { return await dialog.ShowAsync(owner); }
        finally { IsOpen = false; }
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

    /// <summary>输入文本的对话框。<paramref name="validate"/> 返回错误信息或 null。</summary>
    public static async Task<string?> PromptAsync(TopLevel owner, string title, string label, string initialText,
        Func<string, string?> validate, int selectionLength = -1)
    {
        var box = new TextBox { Text = initialText, MinWidth = 360 };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            FontSize = 12,
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
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = selectionLength >= 0 ? Math.Min(selectionLength, box.Text?.Length ?? 0) : box.Text?.Length ?? 0;
        };

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
