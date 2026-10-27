using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Medicore.Licensing.Core;

namespace Medicore.Mac;

public partial class LicenseActivationWindow : Window
{
    public LicenseActivationWindow() : this(null)
    {
    }

    public LicenseActivationWindow(LicenseValidationResult? initialResult)
    {
        InitializeComponent();
        HardwareIdText.Text = App.CurrentHardwareId;

        // نعرض سبب الفشل فقط لو كان هناك ملف ترخيص أصلاً وفشل (منتهي/جهاز مختلف/تالف)
        if (initialResult is not null && initialResult.Status != LicenseStatus.FileNotFound)
        {
            StatusMessageText.Text = "⚠️ " + initialResult.Message;
            StatusMessageText.IsVisible = true;
        }
    }

    private async void CopyHardwareId_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(HardwareIdText.Text ?? "");
            CopiedText.IsVisible = true;
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, $"تعذّر النسخ: {ex.Message}");
        }
    }

    private async void BrowseAndInstall_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "اختر ملف الترخيص",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ملفات الترخيص (*.lic)") { Patterns = new[] { "*.lic" } },
                FilePickerFileTypes.All,
            },
        });
        var chosenPath = files.FirstOrDefault()?.TryGetLocalPath();
        if (chosenPath is null) return;

        try
        {
            // نتحقق من صحة الملف المختار قبل تثبيته نهائياً في مكانه الدائم،
            // حتى لا نستبدل ترخيصاً سارياً (إن وجد) بملف تالف أو خاص بجهاز آخر
            var checkResult = App.ValidateForServer(chosenPath);
            if (!checkResult.IsValid)
            {
                await Dialogs.Error(this, checkResult.Message, "ملف الترخيص غير صالح");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(App.LicenseFilePath)!);
            File.Copy(chosenPath, App.LicenseFilePath, overwrite: true);

            if (App.TryActivateAndLaunchServer())
            {
                Close();
            }
            else
            {
                await Dialogs.Error(this, "تم تثبيت الملف لكن حدث خطأ غير متوقع أثناء التفعيل. أعد تشغيل البرنامج.");
            }
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, $"تعذّر تثبيت ملف الترخيص: {ex.Message}");
        }
    }
}
