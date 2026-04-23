using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ReferenceTrimmer.Loggers.MSVC;

[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.MethodLevel)]

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class E2ETests
{
    private readonly record struct Warning(string Message, string Project, IEnumerable<string>? AltMessages = null);

    private static readonly (string ExePath, string Verb, string? VsInstallDir) MSBuild = GetMsBuildExeAndVerb();
    internal static readonly Lazy<Dictionary<string, string>?> VsDevEnvironment = new(GetVsDevEnvironment);

    private static readonly Regex WarningErrorRegex = new(
        @".+: (warning|error) (?<message>.+) \[(?<project>.+)\]",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public TestContext? TestContext { get; set; }

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        // Delete the package cache to avoid reusing old content
        if (Directory.Exists("Packages"))
        {
            Directory.Delete("Packages", recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedProjectReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedProjectReferenceProduceReferenceAssembly(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedProjectReferenceNoReferenceAssembly(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedProjectReferenceSwitchPattern(bool useSymbolAnalysis)
    {
        // Dependency type used only in switch expression type pattern and switch case clause pattern.
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    public Task UsedProjectReferenceNameof()
    {
        // Dependency type used only in nameof(). nameof is lowered to a string literal
        // in IOperation, so only the syntax-level handler catches it. Symbol-analysis only.
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedProjectReferenceCref(bool useSymbolAnalysis)
    {
        // Dependency type used only in XML doc <see cref="..."/>.
        // Both legacy (GetUsedAssemblyReferences with doc mode on) and symbol-based paths handle this.
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public Task UnusedProjectReference(bool enableReferenceTrimmerDiagnostics, bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed", "Library/Library.csproj"),
            },
            enableReferenceTrimmerDiagnostics: enableReferenceTrimmerDiagnostics,
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedProjectReferenceProduceReferenceAssembly(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedProjectReferenceNoReferenceAssembly(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedProjectReferenceNoWarn(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedProjectReferenceTreatAsUsed(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>(),
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedProjectReferenceSuppressed(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedTransitiveProjectReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // Only the Dependency gets the warning. Library doesn't get penalized for a transitive dependency.
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Dependency/Dependency.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedDirectAndTransitiveProjectReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // Both the Library and Dependency get the warning since both directly referenced it.
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Dependency/Dependency.csproj"),
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    public Task UnusedDirectReferenceUsedTransitively()
    {
        // Library directly references TransitiveDependency but doesn't use it.
        // Dependency uses TransitiveDependency. Symbol-based analysis is required to
        // reliably detect this as unused — the default approach may or may not depending
        // on whether reference assemblies strip the transitive metadata.
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UsedReferenceHintPath(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UsedReferenceItemSpec(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceHintPath(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0001: Reference Dependency can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceHintPathNoWarn(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceHintPathTreatAsUsed(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>(),
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>(),
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceItemSpec(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning(
                    OperatingSystem.IsWindows()
                        ? @"RT0001: Reference ..\Dependency\bin\x64\Debug\net472\\Dependency.dll can be removed"
                        : @"RT0001: Reference ../Dependency/bin/x64/Debug/net472/Dependency.dll can be removed",
                    "Library/Library.csproj",
                    [
                        // Alt: Can leave out 'x64' path segment on VS or Linux.
                        @"RT0001: Reference ..\Dependency\bin\Debug\net472\\Dependency.dll can be removed",
                        @"RT0001: Reference ../Dependency/bin/Debug/net472/Dependency.dll can be removed",
                    ]),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceItemSpecNoWarn(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusedReferenceItemSpecTreatAsUsed(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>(),
            useSymbolAnalysis: useSymbolAnalysis);

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>(),
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "The GAC is Windows-specific")]
    public async Task UnusedReferenceFromGac(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0001: Reference Microsoft.Office.Interop.Outlook can be removed", "Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "The GAC is Windows-specific")]
    public async Task UsedReferenceFromGac(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedPackageReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UsedIndirectPackageReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "WebHost/WebHost.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedPackageReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0003: PackageReference Newtonsoft.Json can be removed", "Library/Library.csproj")
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedPackageReferenceDocDisabled(bool useSymbolAnalysis)
    {
        // Default: RT0000 fires (doc generation disabled warning); the unused package is not detected
        //          because GetUsedAssemblyReferences is less accurate without doc generation.
        // Symbol analysis: RT0003 fires (unused package correctly detected regardless of doc mode).
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: useSymbolAnalysis
                ? new[] { new Warning("RT0003: PackageReference Newtonsoft.Json can be removed", "Library/Library.csproj") }
                : new[] { new Warning("RT0000: Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection", "Library/Library.csproj") },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedPackageReferenceNoWarn(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnusedPackageReferenceTreatAsUsed(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task BuildPackageReference(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MissingReferenceSourceTarget(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PlatformPackageConflictResolution(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // TODO: These "metapackages" should not be reported.
                new Warning("RT0003: PackageReference NETStandard.Library can be removed", "Library/Library.csproj"),
            },
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task NoTargets(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Project.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task TargetFrameworkWithOs(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AbsoluteIntermediateOutputPath(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task BuildExtensions(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReferenceInPackage(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Tests/Tests.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReferenceTrimmerDisabled(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "This test only applies to Windows")]
    public async Task LegacyStyleProject(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "This test only applies to Windows")]
    public async Task UnusedWinSdkImportLibrary()
    {
        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: [],
            expectedConsoleOutputs:
            [
                "Unused libraries:",
                @"\user32.lib",
                "Unused MSVC libraries detected in project",
                "  * Default Windows SDK import libraries:",
                "    - Libraries needed: ",
                "    - Unneeded: ",
            ],
            expectUnusedMsvcLibrariesLog: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "This test only applies to Windows")]
    public async Task UnusedCppLibrary()
    {
        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: [],
            expectedConsoleOutputs:
            [
                "Unused libraries:",
                "Unused MSVC libraries detected in project",
                "  * Other libraries - ",
                @"\Library.lib",
            ],
            expectUnusedMsvcLibrariesLog: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "This test only applies to Windows")]
    public async Task UnusedCppDelayLoadLibrary()
    {
        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: [],
            expectedConsoleOutputs:
            [
                "Unused libraries:",
                "Unused MSVC libraries detected in project",
                "  * Other libraries - ",
                @"\DLL.lib",
            ],
            expectUnusedMsvcLibrariesLog: true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task WpfApp(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "WpfApp/WpfApp.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PackageReferenceWithFakeBuildFile(bool useSymbolAnalysis)
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings:
            [
                new Warning("RT0003: PackageReference Microsoft.Extensions.Primitives can be removed", "Library/Library.csproj"),
            ],
            useSymbolAnalysis: useSymbolAnalysis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IgnorePackageBuildFiles(bool useSymbolAnalysis)
    {
        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: [],
            useSymbolAnalysis: useSymbolAnalysis,
            globalProperties: new Dictionary<string, string>
            {
                { "IgnorePackageBuildFiles", "false" },
            });

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings:
            [
                new Warning("RT0003: PackageReference Microsoft.Extensions.Logging can be removed", "Library/Library.csproj"),
            ],
            useSymbolAnalysis: useSymbolAnalysis,
            globalProperties: new Dictionary<string, string>
            {
                { "IgnorePackageBuildFiles", "true" },
            });
    }

    private static (string ExePath, string Verb, string? VsInstallDir) GetMsBuildExeAndVerb()
    {
        // On Windows, try to find Visual Studio using vswhere
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? vsInstallDir = FindVsInstallDir();

            if (!string.IsNullOrEmpty(vsInstallDir))
            {
                string msbuildExePath = Path.Combine(vsInstallDir, @"MSBuild\Current\Bin\amd64\MSBuild.exe");
                if (!File.Exists(msbuildExePath))
                {
                    throw new InvalidOperationException($"Could not find MSBuild.exe path for unit tests: {msbuildExePath}");
                }

                return (msbuildExePath, string.Empty, vsInstallDir);
            }
        }

        // Fall back to just using dotnet. Assume it's on the PATH
        return ("dotnet", "build", null);
    }

    private static string? FindVsInstallDir()
    {
        // When running from a developer command prompt, Visual Studio can be found under VSINSTALLDIR
        string? vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrEmpty(vsInstallDir))
        {
            return vsInstallDir;
        }

        // When running Visual Studio can be found under VSAPPIDDIR
        string? vsAppIdeDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
        if (!string.IsNullOrEmpty(vsAppIdeDir))
        {
            return Path.GetFullPath(Path.Combine(vsAppIdeDir, "..", ".."));
        }

        // Use vswhere to find Visual Studio
        string vswherePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswherePath))
        {
            return null;
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = vswherePath,
            Arguments = "-latest -property installationPath -requires Microsoft.Component.MSBuild",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });

        if (process is null)
        {
            return null;
        }

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
    }

    private static Dictionary<string, string>? GetVsDevEnvironment()
    {
        if (MSBuild.VsInstallDir is null)
        {
            return null;
        }

        string vsDevCmdPath = Path.Combine(MSBuild.VsInstallDir, @"Common7\Tools\VsDevCmd.bat");
        if (!File.Exists(vsDevCmdPath))
        {
            return null;
        }

        // Run VsDevCmd.bat and capture the resulting environment variables
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{vsDevCmdPath}\" -no_logo && set\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });

        if (process is null)
        {
            return null;
        }

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            return null;
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIndex = line.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = line[..eqIndex];
                string value = line[(eqIndex + 1)..].TrimEnd('\r');

                // Skip Platform to avoid interfering with dotnet build output paths
                if (!key.Equals("Platform", StringComparison.OrdinalIgnoreCase))
                {
                    env[key] = value;
                }
            }
        }

        return env;
    }

    private async Task RunMSBuildAsync(
        string projectFile,
        Warning[] expectedWarnings,
        string[]? expectedConsoleOutputs = null,
        bool expectUnusedMsvcLibrariesLog = false,
        bool enableReferenceTrimmerDiagnostics = false,
        bool useSymbolAnalysis = false,
        IReadOnlyDictionary<string, string>? globalProperties = null)
    {
        var testDataSourcePath = Path.GetFullPath(Path.Combine("TestData", TestContext?.TestName ?? string.Empty));

        string logDirBase = Path.Combine(testDataSourcePath, "Logs");
        string binlogFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".binlog");
        string warningsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".warnings.log");
        string errorsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".errors.log");

        TestContext?.WriteLine($"Log directory: {logDirBase}");

        string unusedLibraryLogPath = Path.Combine(testDataSourcePath, ForwardingLogger.HelpKeyword + ".json.log");
        if (File.Exists(unusedLibraryLogPath))
        {
            File.Delete(unusedLibraryLogPath);
        }

        string loggersAssemblyPath = Path.Combine(Environment.CurrentDirectory, "ReferenceTrimmer.Loggers.dll");

        string msbuildArgs = $"{MSBuild.Verb} \"{projectFile}\" " +
                             $"-m:1 -t:Rebuild -restore -nologo -nodeReuse:false -noAutoResponse " +
                             $"-bl:\"{binlogFilePath}\" " +
                             $"-flp1:logfile=\"{errorsFilePath}\";errorsonly " +
                             $"-flp2:logfile=\"{warningsFilePath}\";warningsonly " +
                             $"-distributedlogger:CentralLogger,\"{loggersAssemblyPath}\"*ForwardingLogger,\"{loggersAssemblyPath}\" " +
                             (enableReferenceTrimmerDiagnostics ? "-p:EnableReferenceTrimmerDiagnostics=true " : string.Empty) +
                             (useSymbolAnalysis ? "-p:ReferenceTrimmerUseSymbolAnalysis=true " : string.Empty);

        if (globalProperties is not null)
        {
            foreach ((string key, string value) in globalProperties)
            {
                msbuildArgs += $" -p:{key}={value}";
            }
        }

        Process? process = Process.Start(
            new ProcessStartInfo
            {
                FileName = MSBuild.ExePath,
                Arguments = msbuildArgs,
                WorkingDirectory = testDataSourcePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }.WithVsDevEnvironment());
        Assert.IsNotNull(process);

        string stdOut = await process.StandardOutput.ReadToEndAsync();
        string stdErr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, $"Build of {projectFile} was not successful.{Environment.NewLine}StandardError: {stdErr}{Environment.NewLine}StandardOutput: {stdOut}");
        Assert.AreEqual(File.Exists(unusedLibraryLogPath), expectUnusedMsvcLibrariesLog);

        string errors = await File.ReadAllTextAsync(errorsFilePath);
        Assert.AreEqual(0, errors.Length, $"Build of {projectFile} was not successful.{Environment.NewLine}Error log: {errors}");

        List<Warning> actualWarnings = new();
        foreach (string line in await File.ReadAllLinesAsync(warningsFilePath))
        {
            Match match = WarningErrorRegex.Match(line);
            if (match.Success)
            {
                string message = match.Groups["message"].Value;
                string projectFullPath = match.Groups["project"].Value;
                string projectRelativePath = projectFullPath[(testDataSourcePath.Length + 1)..];

                // Normalize slashes for the project paths
                projectRelativePath = projectRelativePath.Replace('\\', '/');

                actualWarnings.Add(new Warning(message, projectRelativePath));
            }
        }

        bool warningsMatched = expectedWarnings.Length == actualWarnings.Count;
        if (warningsMatched)
        {
            for (var i = 0; i < actualWarnings.Count; i++)
            {
                warningsMatched &=
                    expectedWarnings[i].Project == actualWarnings[i].Project &&
                    (expectedWarnings[i].Message == actualWarnings[i].Message ||
                     (expectedWarnings[i].AltMessages is not null &&
                     expectedWarnings[i].AltMessages!.Any(m => m == actualWarnings[i].Message)));
            }
        }

        Assert.IsTrue(
            warningsMatched,
            $@"
Expected warnings:
{(expectedWarnings.Length == 0 ? "<none>" : string.Join(Environment.NewLine, expectedWarnings))}

Actual warnings:
{(actualWarnings.Count == 0 ? "<none>" : string.Join(Environment.NewLine, actualWarnings))}");

        if (expectedConsoleOutputs is not null)
        {
            foreach (string expectedLogOutput in expectedConsoleOutputs)
            {
                Assert.IsTrue(stdOut.Contains(expectedLogOutput, StringComparison.OrdinalIgnoreCase), $"Expected log output '{expectedLogOutput}' was not found. Full console stdout: {stdOut}{Environment.NewLine}Console stderr: {stdErr}");
            }
        }

        // local tests run debug, CI builds run release, thus the assertion needs to look for the file
        var usedReferencesFiles = Directory.GetFiles(testDataSourcePath, "_ReferenceTrimmer_UsedReferences.log", SearchOption.AllDirectories);
        var unusedReferencesFiles = Directory.GetFiles(testDataSourcePath, "_ReferenceTrimmer_UnusedReferences.log", SearchOption.AllDirectories);
        Assert.AreEqual(enableReferenceTrimmerDiagnostics, usedReferencesFiles.Length > 0);
        Assert.AreEqual(enableReferenceTrimmerDiagnostics, unusedReferencesFiles.Length > 0);
    }
}

file static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithVsDevEnvironment(this ProcessStartInfo psi)
    {
        Dictionary<string, string>? vsEnv = E2ETests.VsDevEnvironment.Value;
        if (vsEnv is not null)
        {
            foreach (KeyValuePair<string, string> kvp in vsEnv)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        return psi;
    }
}
