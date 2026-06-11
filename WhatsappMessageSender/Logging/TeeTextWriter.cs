using System.Text;

namespace WhatsappMessageSender.Logging;

/// <summary>
/// Writes to multiple <see cref="TextWriter"/> targets (e.g. daily file + console).
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter[] _targets;

    public TeeTextWriter(params TextWriter[] targets) =>
        _targets = targets;

    public override Encoding Encoding => _targets[0].Encoding;

    public override void Write(char value)
    {
        foreach (var target in _targets)
            target.Write(value);
    }

    public override void Write(string? value)
    {
        foreach (var target in _targets)
            target.Write(value);
    }

    public override void WriteLine(string? value)
    {
        foreach (var target in _targets)
            target.WriteLine(value);
    }

    public override void Flush()
    {
        foreach (var target in _targets)
            target.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var target in _targets)
                target.Dispose();
        }

        base.Dispose(disposing);
    }
}
