using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using ReferenceTrimmer;
using LogLevel = NLog.LogLevel;

var loggerFactory = LoggerFactory.Create(
    builder => builder.AddNLog(GetLoggingConfiguration()));
ILogger logger = loggerFactory.CreateLogger("ReferenceTrimmer");

var rootCommand = new RootCommand();

var argumentBinder = Arguments.AddOptionsAndGetBinder(rootCommand);

rootCommand.SetHandler(
    (Arguments arguments) =>
    {
        logger.LogInformation("Full logs can be found in ReferenceTrimmer.log");

        if (arguments.Debug)
        {
            Debugger.Launch();
        }

        // Load MSBuild
        string? msbuildBinPath = null;
        if (MSBuildLocator.CanRegister)
        {
            if (string.IsNullOrEmpty(arguments.MSBuildPath))
            {
                VisualStudioInstance vsInstance = MSBuildLocator.RegisterDefaults();
                msbuildBinPath = vsInstance.MSBuildPath;
            }
            else
            {
                MSBuildLocator.RegisterMSBuildPath(arguments.MSBuildPath);
                msbuildBinPath = arguments.MSBuildPath;
            }
        }
        else
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "Microsoft.Build", StringComparison.OrdinalIgnoreCase))
                {
                    msbuildBinPath = Path.GetDirectoryName(assembly.Location)!;
                    break;
                }
            }
        }

        if (msbuildBinPath == null)
        {
            logger.LogError("Unable to locate MSBuild");
            return;
        }

        // Try resolving any other missing assembly from the MSBuild directory
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            AssemblyName requestedAssemblyName = new (eventArgs.Name);
            FileInfo candidateAssemblyFileInfo = new FileInfo(Path.Combine(msbuildBinPath, $"{requestedAssemblyName.Name}.dll"));
            if (!candidateAssemblyFileInfo.Exists)
            {
                return null;
            }

            return Assembly.LoadFrom(candidateAssemblyFileInfo.FullName);
        };

        // ReferenceTrimmerRunner needs to be a separate class to avoid the MSBuild assemblies getting loaded (and failing) before MSBuildLocator gets a chance to set things up.
        ReferenceTrimmerRunner.Run(arguments, logger);
    },
    argumentBinder);

return await rootCommand.InvokeAsync(args);

static LoggingConfiguration GetLoggingConfiguration()
{
    var config = new LoggingConfiguration();

    static string OptionalLayout(string layout, string prefix, string suffix)
        => $@"${{when:when=length('{layout}') > 0:inner={prefix}{layout}{suffix}:else=}}";

    const string DateLayout = @"${date:format=HH\:mm\:ss.fff}";
    const string LoggerNameLayout = @"${logger:shortName=true}";
    const string LevelLayout = @"${pad:padding=5:inner=${level:uppercase=true}}";
    const string ExceptionLayout = @"${exception:format=tostring}";
    const string MessageLayout = @"${message}";
    const string ScopeLayout = @"${ndlc}";

    var consoleTarget = new ColoredConsoleTarget
    {
        Layout = @$"{DateLayout} [{LoggerNameLayout}]{OptionalLayout(ScopeLayout, "[", "]")} {MessageLayout}{OptionalLayout(ExceptionLayout, " ", string.Empty)}",
    };

    // Override the default color for error (yellow) and warn (magenta)
    consoleTarget.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(condition: "level >= LogLevel.Error", foregroundColor: ConsoleOutputColor.Red, backgroundColor: ConsoleOutputColor.NoChange));
    consoleTarget.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(condition: "level == LogLevel.Warn", foregroundColor: ConsoleOutputColor.Yellow, backgroundColor: ConsoleOutputColor.NoChange));
    config.AddTarget("console", consoleTarget);
    config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, consoleTarget));

    var fileTarget = new FileTarget
    {
        FileName = "${logger}.log",
        Layout = @$"{DateLayout} {LevelLayout} [{LoggerNameLayout}]{OptionalLayout(ScopeLayout, "[", "]")} {MessageLayout}{OptionalLayout(ExceptionLayout, " ", string.Empty)}",
        DeleteOldFileOnStartup = true,
    };
    config.AddTarget("file", fileTarget);
    config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));

    return config;
}