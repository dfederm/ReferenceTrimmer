using Microsoft.Build.Framework;

namespace ReferenceTrimmer.Loggers.MSVC;

/// <summary>
/// Passes the unused library information from <see cref="ForwardingLogger"/> to
/// <see cref="CentralLogger"/>. Note that the MSBuild binary logger will serialize this event in its
/// entirety, so to aid complete debugging fully analyzed information is passed here.
/// </summary>
[Serializable]
internal sealed class UnusedLibsCustomBuildEventArgs : CustomBuildEventArgs
{
    public UnusedLibsCustomBuildEventArgs()
    {
        ProjectPath = string.Empty;
        UnusedLibraryPathsJson = string.Empty;
    }

    public UnusedLibsCustomBuildEventArgs(
        string message,
        string projectPath,
        string unusedLibraryPathsJson)
        : base(message, ForwardingLogger.HelpKeyword, senderName: projectPath)
    {
        ProjectPath = projectPath;
        UnusedLibraryPathsJson = unusedLibraryPathsJson;
    }

    public string ProjectPath { get; set; }
    public string UnusedLibraryPathsJson { get; set; }
}
