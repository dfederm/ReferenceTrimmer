using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ReferenceTrimmer.Loggers.MSVC;

/// <summary>
/// Central logger instance run in the main MSBuild process.
/// </summary>
public sealed class CentralLogger : Logger
{
    private readonly object _jsonLogWriteLock = new();
    private Lazy<StreamWriter>? _lazyJsonLogFileStreamWriter;
    private bool _firstEvent = true;
    private string? _jsonLogFilePath;

    internal const string JsonLogFileName = UnusedLibsLogger.HelpKeyword + ".json.log";

    /// <inheritdoc />
    public override void Initialize(IEventSource eventSource)
    {
        _jsonLogFilePath = Path.Combine(Directory.GetCurrentDirectory(), JsonLogFileName);
        if (File.Exists(_jsonLogFilePath))
        {
            File.Delete(_jsonLogFilePath);
        }

        _lazyJsonLogFileStreamWriter = new Lazy<StreamWriter>(() =>
        {
            try
            {
                var writer = new StreamWriter(_jsonLogFilePath);

                // Begin JSON array as text.
                writer.WriteLine("[");

                return writer;
            }
            catch (Exception ex)
            {
                throw new LoggerException($"Failed to create JSON log file for ReferenceTrimmer for MSVC: {ex.Message}");
            }
        });

        eventSource.CustomEventRaised += CustomEventHandler;
    }

    /// <inheritdoc />
    public override void Shutdown()
    {
        lock (_jsonLogWriteLock)
        {
            if (_lazyJsonLogFileStreamWriter is not null && _lazyJsonLogFileStreamWriter.IsValueCreated)
            {
                _lazyJsonLogFileStreamWriter.Value.WriteLine();
                _lazyJsonLogFileStreamWriter.Value.WriteLine("]");
                _lazyJsonLogFileStreamWriter.Value.Dispose();
            }
        }
    }

    private void CustomEventHandler(object sender, CustomBuildEventArgs e)
    {
        if (e is not UnusedLibsCustomBuildEventArgs unusedLibsEvent)
        {
            return;
        }

        Console.WriteLine(unusedLibsEvent.Message + Environment.NewLine + $"  * JSON version of this information added to {_jsonLogFilePath}");

        lock (_jsonLogWriteLock)
        {
            StreamWriter sw = _lazyJsonLogFileStreamWriter!.Value;
            if (_firstEvent)
            {
                _firstEvent = false;
            }
            else
            {
                // Separate JSON objects with a comma.
                sw.WriteLine(',');
            }

            sw.Write(unusedLibsEvent.UnusedLibraryPathsJson);
            sw.Flush();
        }
    }
}
