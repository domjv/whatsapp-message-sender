using System.Text;

namespace WhatsappMessageSender.Logging;

/// <summary>
/// Writes log lines to a daily file: <c>{LogDirectory}/whatsapp-sender-yyyy-MM-dd.log</c>.
/// </summary>
public sealed class DailyRollingTextWriter : TextWriter
{
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly int _retainedFileCountLimit;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _currentDateKey;

    public DailyRollingTextWriter(string directory, string filePrefix = "whatsapp-sender", int retainedFileCountLimit = 31)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        _retainedFileCountLimit = Math.Max(1, retainedFileCountLimit);
        Directory.CreateDirectory(_directory);
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            EnsureWriter();
            _writer!.Write(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
            return;

        lock (_lock)
        {
            EnsureWriter();
            _writer!.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            EnsureWriter();
            _writer!.WriteLine(FormatLine(value ?? string.Empty));
            _writer.Flush();
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            _writer?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureWriter()
    {
        var dateKey = DateTime.Now.ToString("yyyy-MM-dd");
        if (_writer != null && dateKey == _currentDateKey)
            return;

        _writer?.Dispose();
        _currentDateKey = dateKey;

        var path = Path.Combine(_directory, $"{_filePrefix}-{dateKey}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false
        };

        PurgeOldFiles();
    }

    private void PurgeOldFiles()
    {
        if (_retainedFileCountLimit <= 0)
            return;

        var files = Directory
            .EnumerateFiles(_directory, $"{_filePrefix}-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.Name)
            .Skip(_retainedFileCountLimit)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // Best-effort retention; ignore files locked by log viewers.
            }
        }
    }

    private static string FormatLine(string message) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
}
