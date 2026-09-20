using System.Diagnostics;
using System.Text;
using System.Windows;
using Serilog;

namespace PrintPacer.App.Platform;

/// <summary>
/// Routes WPF data-binding failures into the application log.
///
/// Binding errors are otherwise silent outside a debugger: the control simply renders
/// blank, which on this application's setup screen would look exactly like a field the user
/// had cleared. Surfacing them means a misconfigured screen is diagnosable from a log file
/// rather than needing a reproduction.
/// </summary>
internal sealed class BindingErrorListener : TraceListener
{
    private static readonly ILogger Log = Serilog.Log.ForContext<BindingErrorListener>();
    private readonly StringBuilder _pending = new();

    public static void Attach(SourceLevels level = SourceLevels.Warning)
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = level;
    }

    public override void Write(string? message) => _pending.Append(message);

    public override void WriteLine(string? message)
    {
        _pending.Append(message);
        var text = _pending.ToString().Trim();
        _pending.Clear();

        if (text.Length > 0) Log.Warning("Data binding: {Message}", text);
    }
}
