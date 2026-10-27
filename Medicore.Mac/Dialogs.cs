using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Medicore.Mac;

/// <summary>رسائل التأكيد والتنبيه (بديل MessageBox في WPF) — من اليمين لليسار.</summary>
internal static class Dialogs
{
    public enum Kind { Info, Warning, Error, Question }

    public static Task Info(Window? owner, string message, string title = "Medicore Server") =>
        Show(owner, message, title, Kind.Info, "حسناً", null);

    public static Task Error(Window? owner, string message, string title = "خطأ") =>
        Show(owner, message, title, Kind.Error, "حسناً", null);

    /// <summary>true عند الضغط على "نعم".</summary>
    public static Task<bool> Confirm(Window? owner, string message, string title, Kind kind = Kind.Question) =>
        Show(owner, message, title, kind, "نعم", "لا");

    private static async Task<bool> Show(Window? owner, string message, string title, Kind kind, string ok, string? cancel)
    {
        var (glyph, color) = kind switch
        {
            Kind.Error => ("⛔", "#DC2626"),
            Kind.Warning => ("⚠️", "#D97706"),
            Kind.Question => ("❓", "#2563EB"),
            _ => ("ℹ️", "#059669"),
        };
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var okButton = new Button { Content = ok, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
        okButton.Click += (_, _) => { result = true; dialog.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        buttons.Children.Add(okButton);
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancelButton.Click += (_, _) => dialog.Close();
            buttons.Children.Add(cancelButton);
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 16) };
        body.Children.Add(new TextBlock { Text = glyph, FontSize = 26, Foreground = Brush.Parse(color), Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(12, 4, 0, 0) };
        Grid.SetColumn(text, 1);
        body.Children.Add(text);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(body);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.Opened += (_, _) => okButton.Focus();

        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }
        return result;
    }
}
