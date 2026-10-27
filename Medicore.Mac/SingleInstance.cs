using System.IO.Pipes;
using Medicore.Server.Core;

namespace Medicore.Mac;

/// <summary>
/// نسخة واحدة من Medicore Server على الجهاز: قفل ملف في مجلد الدعم، وقناة محلية (Unix socket) تستقبل
/// من النسخة الثانية طلب "إظهار النافذة" أو "فتح النظام في المتصفح" ثم تخرج النسخة الثانية.
/// الماك يمنع عادةً فتح البرنامج مرتين من Finder، لكن التشغيل مع تسجيل الدخول واختصار سطح المكتب
/// يشغّلان الملف التنفيذي مباشرة.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ShowCommand = "show";
    public const string OpenCommand = "open";
    private static readonly string PipeName = "medicore-server-" + Environment.UserName;

    private readonly FileStream _lock;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(FileStream lockStream) => _lock = lockStream;

    public static SingleInstance? TryAcquire()
    {
        Directory.CreateDirectory(HostPaths.SupportDir);
        try
        {
            var fs = new FileStream(Path.Combine(HostPaths.SupportDir, "server.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(fs);
        }
        catch (IOException)
        {
            return null; // نسخة أخرى تعمل
        }
    }

    /// <summary>يرسل أمراً للنسخة العاملة (null = لا شيء، مثل التشغيل مع تسجيل الدخول والسيرفر يعمل بالفعل).</summary>
    public static void Signal(string? command)
    {
        if (command is null) return;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var w = new StreamWriter(client);
            w.WriteLine(command);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // النسخة الأخرى ما زالت تبدأ
        }
    }

    /// <summary>يستقبل الأوامر من النسخ الأخرى (على خيط خلفي) طوال عمر البرنامج.</summary>
    public void Listen(Action<string> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var r = new StreamReader(server);
                    var line = (await r.ReadLineAsync(_cts.Token))?.Trim();
                    if (!string.IsNullOrEmpty(line)) onCommand(line);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lock.Dispose();
    }
}
