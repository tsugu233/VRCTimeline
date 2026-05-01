using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace VRCTimeline.Services;

/// <summary>
/// MaterialDesign の DialogHost を使用した確認・情報ダイアログサービス。
/// ViewModel からダイアログを表示するために使用する。
/// </summary>
public class DialogService
{
    /// <summary>MainWindow に配置された DialogHost の識別子</summary>
    private const string DialogHostId = "RootDialogHost";

    /// <summary>はい/いいえの確認ダイアログを表示し、ユーザーの選択を返す</summary>
    public async Task<bool> ShowConfirmAsync(string message, string? title = null)
    {
        title ??= LocalizationService.GetString("Str_Confirm");
        var content = CreateDialogContent(title, message, isConfirm: true);
        var result = await DialogHost.Show(content, DialogHostId);
        return result is true;
    }

    /// <summary>OK ボタンのみの情報ダイアログを表示する</summary>
    public async Task ShowInfoAsync(string message, string? title = null)
    {
        title ??= LocalizationService.GetString("Str_Info");
        var content = CreateDialogContent(title, message, isConfirm: false);
        await DialogHost.Show(content, DialogHostId);
    }

    /// <summary>ダイアログの UI コンテンツをコードで構築する</summary>
    private static FrameworkElement CreateDialogContent(string title, string message, bool isConfirm)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Opacity = 0.85,
            MaxWidth = 350
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        if (isConfirm)
        {
            var noButton = new Button
            {
                Content = LocalizationService.GetString("Str_No"),
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0),
                Command = DialogHost.CloseDialogCommand,
                CommandParameter = false
            };
            noButton.SetResourceReference(FrameworkElement.StyleProperty, "MaterialDesignFlatMidBgButton");

            var yesButton = new Button
            {
                Content = LocalizationService.GetString("Str_Yes"),
                MinWidth = 80,
                Command = DialogHost.CloseDialogCommand,
                CommandParameter = true
            };
            yesButton.SetResourceReference(FrameworkElement.StyleProperty, "MaterialDesignRaisedButton");

            buttonPanel.Children.Add(noButton);
            buttonPanel.Children.Add(yesButton);
        }
        else
        {
            var okButton = new Button
            {
                Content = LocalizationService.GetString("Str_OK"),
                Command = DialogHost.CloseDialogCommand,
                CommandParameter = true
            };
            okButton.SetResourceReference(FrameworkElement.StyleProperty, "MaterialDesignRaisedButton");

            buttonPanel.Children.Add(okButton);
        }

        var stack = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            MinWidth = 280
        };
        stack.Children.Add(titleBlock);
        stack.Children.Add(messageBlock);
        stack.Children.Add(buttonPanel);

        return stack;
    }
}
