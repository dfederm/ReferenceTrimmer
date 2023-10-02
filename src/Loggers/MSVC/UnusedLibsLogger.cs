using System.Collections.Concurrent;
using System.Text;
using Microsoft.Build.Framework;

namespace ReferenceTrimmer.Loggers.MSVC;

/// <summary>
/// Logger that captures the list of unused libraries from the Link task.
/// Runs within the context of each MSBuild worker node and forwards events to the central logger.
/// </summary>
internal sealed class UnusedLibsLogger : IDisposable
{
    private const string LinkTaskName = "Link";

    // Default library list in $(CoreLibraryDependencies) used as a default list for $(AdditionalDependencies)
    // in the MSVC MSBuild SDK. To update with current lib list:
    //   findstr /s CoreLibraryDependencies "\Program Files"\*props
    // Ignores WindowsApp.lib for the WindowsAppContainer case.
    private static readonly HashSet<string> DefaultWin32DllImportLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "advapi32.lib",
        "comdlg32.lib",
        "gdi32.lib",
        "kernel32.lib",
        "odbc32.lib",
        "odbccp32.lib",
        "ole32.lib",
        "oleaut32.lib",
        "shell32.lib",
        "user32.lib",
        "uuid.lib",
        "winspool.lib",
    };

    private enum State
    {
        LinkStarted,
        UnusedLibsStarted,
        UnusedLibsEnded,
    }

    private enum LibType
    {
        DefaultImportLib,
        PackageOrDependencyLib,
    }

    private sealed class ProjectStateLibs
    {
        public State ProjectState { get; set; }
        public SortedSet<string> UnusedProjectLibPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly IEventSource _eventSource;
    private readonly IEventRedirector _buildEventRedirector;
    private readonly ConcurrentDictionary<string, ProjectStateLibs> _projects = new(StringComparer.OrdinalIgnoreCase);

    public const string HelpKeyword = "ReferenceTrimmerUnusedMSVCLibraries";

    public UnusedLibsLogger(IEventSource eventSource, IEventRedirector buildEventRedirector)
    {
        _eventSource = eventSource;
        _buildEventRedirector = buildEventRedirector;

        eventSource.TaskStarted += OnTaskStarted;
        eventSource.TaskFinished += OnTaskFinished;
        eventSource.MessageRaised += OnMessageRaised;
    }

    // Mainly for testing
    public void Dispose()
    {
        _eventSource.TaskStarted -= OnTaskStarted;
        _eventSource.TaskFinished -= OnTaskFinished;
        _eventSource.MessageRaised -= OnMessageRaised;
    }

    private void OnTaskStarted(object sender, TaskStartedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.ProjectFile) && e.TaskName.Equals(LinkTaskName, StringComparison.OrdinalIgnoreCase))
        {
            _projects[e.ProjectFile] = new ProjectStateLibs { ProjectState = State.LinkStarted };
        }
    }

    private void OnTaskFinished(object sender, TaskFinishedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.ProjectFile) || e.TaskName != LinkTaskName || !e.Succeeded)
        {
            return;
        }

        string projectFilePath = e.ProjectFile;

        // Project state present in map if the Link task was detected running in OnTaskStarted.
        if (!_projects.TryGetValue(projectFilePath, out ProjectStateLibs projState))
        {
            return;
        }

        if (projState.ProjectState is State.UnusedLibsStarted or State.UnusedLibsEnded &&
            projState.UnusedProjectLibPaths.Count > 0)
        {
            // Hack the output JSON format to avoid importing Newtonsoft or System.Text.Json that could collide with
            // versions in the current MSBuild process.
            var jsonSb = new StringBuilder(256);
            jsonSb.AppendLine("  {");
            jsonSb.AppendLine($"    \"{EscapeJsonChars(projectFilePath)}\": [");

            // Classify the libraries.
            var defaultImportLibraries = new List<string>();
            var otherLibraries = new List<string>();
            var implicitlyUsedImportLibraries = new HashSet<string>(DefaultWin32DllImportLibraries, StringComparer.OrdinalIgnoreCase);
            bool first = true;
            foreach (string libPath in projState.UnusedProjectLibPaths)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    jsonSb.AppendLine(",");
                }

                jsonSb.AppendLine("      {")
                      .AppendLine($"        \"LibPath\": \"{EscapeJsonChars(libPath)}\",");

                string libFileName = Path.GetFileName(libPath);
                LibType type;
                if (DefaultWin32DllImportLibraries.Contains(libFileName))
                {
                    defaultImportLibraries.Add(libPath);
                    implicitlyUsedImportLibraries.Remove(libFileName);
                    type = LibType.DefaultImportLib;
                }
                else
                {
                    otherLibraries.Add(libPath);
                    type = LibType.PackageOrDependencyLib;
                }

                jsonSb.AppendLine($"        \"LibType\": \"{type}\"")
                      .Append("      }");
            }

            jsonSb.AppendLine();
            jsonSb.AppendLine("    ]");
            jsonSb.Append("  }");

            var sb = new StringBuilder($"  Unused MSVC libraries detected in project {projectFilePath}:", 256);
            sb.AppendLine();

            if (defaultImportLibraries.Count > 0)
            {
                var implicitlyUsedImportLibrariesSorted = new List<string>(implicitlyUsedImportLibraries);
                implicitlyUsedImportLibrariesSorted.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append("  * Default Windows SDK import libraries:");
                if (implicitlyUsedImportLibrariesSorted.Count > 0)
                {
                    sb.AppendLine().Append($"    - Libraries needed: {string.Join(";", implicitlyUsedImportLibrariesSorted)} (set in $(AdditionalDependencies) without %(AdditionalDependencies))");
                }

                sb.AppendLine().Append($"    - Unneeded: {string.Join(", ", defaultImportLibraries.Select(Path.GetFileName))} (remove from $(AdditionalDependencies))");
            }

            if (otherLibraries.Count > 0)
            {
                sb.AppendLine().Append(
                    "  * Other libraries - if a lib from a project in this repo, remove the related ProjectReference to improve your build dependency graph; " +
                    "if a package lib, remove from $(AdditionalDependencies):");
                foreach (string lib in otherLibraries)
                {
                    sb.AppendLine().Append($"  - {lib}");
                }
            }

            _buildEventRedirector.ForwardEvent(
                new UnusedLibsCustomBuildEventArgs(
                    message: sb.ToString(),
                    projectFilePath,
                    jsonSb.ToString()));
        }

        _projects.TryRemove(projectFilePath, out _);
    }

    private static string EscapeJsonChars(string str)
    {
        return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void OnMessageRaised(object sender, BuildMessageEventArgs e)
    {
        if (string.IsNullOrEmpty(e.ProjectFile))
        {
            return;
        }

        string projectFilePath = e.ProjectFile;

        if (!_projects.TryGetValue(projectFilePath, out ProjectStateLibs? projState))
        {
            return;
        }

        // The 'Unused libraries' message is at the tail of Link's stdout.
        // Once found, assume the rest of the output is the list of unused libs.
        switch (projState.ProjectState)
        {
            case State.LinkStarted:
                if (e.Message.IndexOf("Unused libraries:", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    projState.ProjectState = State.UnusedLibsStarted;
                }
                break;

            case State.UnusedLibsStarted:
                string lib = e.Message.Trim();
                if (lib.Length > 0)
                {
                    try
                    {
                        lib = Path.GetFullPath(lib);
                    }
                    finally
                    {
                        projState.UnusedProjectLibPaths.Add(lib);
                    }
                }
                else
                {
                    projState.ProjectState = State.UnusedLibsEnded;
                }
                break;

            // Avoid parsing any other text after the usual empty line emitted after the unused libs list.
            // Allow to fall through.
            case State.UnusedLibsEnded:

            default:
                break;
        }
    }
}
