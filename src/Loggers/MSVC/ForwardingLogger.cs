using Microsoft.Build.Framework;

namespace ReferenceTrimmer.Loggers.MSVC;

/// <summary>
/// Forwarding logger that runs in each MSBuild worker node outside of the primary node.
/// </summary>
public sealed class ForwardingLogger : IForwardingLogger
{
    private UnusedLibsLogger? _logger;

    /// <summary>
    /// Gets or sets the level of detail to show in the event log.
    /// </summary>
    public LoggerVerbosity Verbosity { get; set; }

    /// <summary>
    /// The logger takes a single parameter to suppress the output of the errors
    /// and warnings summary at the end of a build.
    /// </summary>
    public string? Parameters { get; set; }

    /// <summary>
    /// This property is set by the build engine to allow node loggers to forward messages to the
    /// central logger.
    /// </summary>
    public IEventRedirector? BuildEventRedirector { get; set; }

    /// <summary>
    /// The identifier of the node.
    /// </summary>
    public int NodeId { get; set; }

    /// <summary>
    /// Signs up the logger for all build events.
    /// </summary>
    public void Initialize(IEventSource eventSource, int nodeCount)
    {
        Initialize(eventSource);
    }

    /// <summary>
    /// Signs up the logger for all build events.
    /// </summary>
    public void Initialize(IEventSource eventSource)
    {
        _logger = new UnusedLibsLogger(eventSource, BuildEventRedirector!);
    }

    /// <summary>
    /// Called when Engine is done with this logger
    /// </summary>
    public void Shutdown()
    {
        _logger?.Dispose();
    }
}
