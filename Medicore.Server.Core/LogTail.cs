using System.Text;

namespace Medicore.Server.Core;

/// <summary>
/// يتابع سطور سجل أخطاء PHP الجديدة (php_errors.log) لعرضها مباشرة في نافذة السيرفر —
/// فيظهر سبب أي "HTTP ERROR 500" فوراً بدل البحث عن الملف. يتجاهل Deprecated/Notice (ضجيج).
/// </summary>
public sealed class LogTail
{
    private readonly string _file;
    private long _position;

    public LogTail(string file)
    {
        _file = file;
        _position = File.Exists(file) ? new FileInfo(file).Length : 0; // ما قبل التشغيل لا يُعرض
    }

    public IReadOnlyList<string> ReadNew()
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(_file)) return lines;
            using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _position) _position = 0; // الملف أُعيد إنشاؤه
            if (fs.Length == _position) return lines;
            fs.Seek(_position, SeekOrigin.Begin);
            var toRead = (int)Math.Min(fs.Length - _position, 256 * 1024);
            var buffer = new byte[toRead];
            var read = fs.Read(buffer, 0, toRead);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return lines; // سطر لم يكتمل بعد
            _position += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);
            foreach (var raw in text[..lastNewline].Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.Contains("Deprecated:") || line.Contains("PHP Notice:")) continue;
                lines.Add(line.Length > 600 ? line[..600] + "…" : line);
            }
        }
        catch (IOException)
        {
            // مقفل مؤقتاً — المحاولة التالية
        }
        return lines;
    }
}
