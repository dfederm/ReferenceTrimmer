using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReferenceTrimmer.Loggers.MSVC;

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class E2ETests
{
    private readonly record struct Warning(string Message, string Project, IEnumerable<string>? AltMessages = null);

    private static readonly (string ExePath, string Verb) MSBuild = GetMsBuildExeAndVerb();

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
    public Task UsedProjectReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UsedProjectReferenceProduceReferenceAssembly()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UnusedProjectReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed", "Library/Library.csproj"),
            });
    }

    [TestMethod]
    public Task UnusedProjectReferenceProduceReferenceAssembly()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed", "Library/Library.csproj"),
            });
    }

    [TestMethod]
    public Task UnusedProjectReferenceNoWarn()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UnusedTransitiveProjectReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // Only the Dependency gets the warning. Library doesn't get penalized for a transitive dependency.
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Dependency/Dependency.csproj"),
            });
    }

    [TestMethod]
    public Task UnusedDirectAndTransitiveProjectReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // Both the Library and Dependency get the warning since both directly referenced it.
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Dependency/Dependency.csproj"),
                new Warning("RT0002: ProjectReference ../TransitiveDependency/TransitiveDependency.csproj can be removed", "Library/Library.csproj"),
            });
    }

    [TestMethod]
    public async Task UsedReferenceHintPath()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task UsedReferenceItemSpec()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task UnusedReferenceHintPath()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0001: Reference Dependency can be removed", "Library/Library.csproj"),
            });
    }

    [TestMethod]
    public async Task UnusedReferenceHintPathNoWarn()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task UnusedReferenceItemSpec()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning(
                    OperatingSystem.IsWindows()
                        ? @"RT0001: Reference ..\Dependency\bin\x64\Debug\net472\\Dependency.dll can be removed"
                        : @"RT0001: Reference ../Dependency/bin/x64/Debug/net472/Dependency.dll can be removed",
                    "Library/Library.csproj",
                    new[]
                    {
                        // Alt: Can leave out 'x64' path segment on VS or Linux.
                        @"RT0001: Reference ..\Dependency\bin\Debug\net472\\Dependency.dll can be removed",
                        @"RT0001: Reference ../Dependency/bin/Debug/net472/Dependency.dll can be removed",
                    }),
            });
    }

    [TestMethod]
    public async Task UnusedReferenceItemSpecNoWarn()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        await RunMSBuildAsync(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<Warning>());

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task UnusedReferenceFromGac()
    {
        // The GAC is Windows-specific
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive();
        }

        await RunMSBuildAsync(
            projectFile: "Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0001: Reference Microsoft.Office.Interop.Outlook can be removed", "Library.csproj"),
            });
    }

    [TestMethod]
    public async Task UsedReferenceFromGac()
    {
        // The GAC is Windows-specific
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive();
        }

        await RunMSBuildAsync(
            projectFile: "Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UsedPackageReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UsedIndirectPackageReference()
    {
        return RunMSBuildAsync(
            projectFile: "WebHost/WebHost.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UnusedPackageReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0003: PackageReference Newtonsoft.Json can be removed", "Library/Library.csproj")
            });
    }

    [TestMethod]
    public Task UnusedPackageReferenceNoWarn()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task UnusedPackageReferenceDocDisabled()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                new Warning("RT0000: Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection", "Library/Library.csproj")
            });
    }

    [TestMethod]
    public Task BuildPackageReference()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task MissingReferenceSourceTarget()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task PlatformPackageConflictResolution()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // TODO: These "metapackages" should not be reported.
                new Warning("RT0003: PackageReference NETStandard.Library can be removed", "Library/Library.csproj"),
            });
    }

    [TestMethod]
    public Task NoTargets()
    {
        return RunMSBuildAsync(
            projectFile: "Project.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task TargetFrameworkWithOs()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task AbsoluteIntermediateOutputPath()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task BuildExtensions()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task ReferenceInPackage()
    {
        return RunMSBuildAsync(
            projectFile: "Tests/Tests.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public Task ReferenceTrimmerDisabled()
    {
        return RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task LegacyStyleProject()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("This test only applies to Windows");
            return;
        }

        await RunMSBuildAsync(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<Warning>());
    }

    [TestMethod]
    public async Task UnusedWinSdkImportLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("This test only applies to Windows");
            return;
        }

        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: Array.Empty<Warning>(),
            expectedConsoleOutputs: new[]
            {
                "Unused libraries:",   // Ensure link.exe unused lib flags are active
                @"\user32.lib",  // Tail of variable unused lib paths like "C:\Program Files (x86)\Windows Kits\10\lib\10.0.19041.0\um\x86\user32.lib"
                "Unused MSVC libraries detected in project",
                "  * Default Windows SDK import libraries:",
                "    - Libraries needed: ",
                "    - Unneeded: ",
            },
            expectUnusedMsvcLibrariesLog: true);
    }

    [TestMethod]
    public async Task UnusedCppLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("This test only applies to Windows");
            return;
        }

        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: Array.Empty<Warning>(),
            expectedConsoleOutputs: new[]
            {
                "Unused libraries:",   // Ensure link.exe unused lib flags are active
                "Unused MSVC libraries detected in project",
                "  * Other libraries - ",
                @"\Library.lib",  // Tail of variable unused lib paths like "C:\Program Files (x86)\Windows Kits\10\lib\10.0.19041.0\um\x86\user32.lib"
            },
            expectUnusedMsvcLibrariesLog: true);
    }

    [TestMethod]
    public async Task UnusedCppDelayLoadLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("This test only applies to Windows");
            return;
        }

        await RunMSBuildAsync(
            projectFile: "App/App.vcxproj",
            expectedWarnings: Array.Empty<Warning>(),
            expectedConsoleOutputs: new[]
            {
                "Unused libraries:",   // Ensure link.exe unused lib flags are active
                "Unused MSVC libraries detected in project",
                "  * Other libraries - ",
                @"\DLL.lib",  // Tail of variable unused lib paths like "C:\Program Files (x86)\Windows Kits\10\lib\10.0.19041.0\um\x86\user32.lib"
            },
            expectUnusedMsvcLibrariesLog: true);
    }

    private static (string ExePath, string Verb) GetMsBuildExeAndVerb()
    {
        // On Windows, try to find Visual Studio
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // When running from a developer command prompt, Visual Studio can be found under VSINSTALLDIR
            string? vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (string.IsNullOrEmpty(vsInstallDir))
            {
                // When running Visual Studio can be found under VSAPPIDDIR
                string? vsAppIdeDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
                if (!string.IsNullOrEmpty(vsAppIdeDir))
                {
                    vsInstallDir = Path.Combine(vsAppIdeDir, "..", "..");
                }
            }

            if (!string.IsNullOrEmpty(vsInstallDir))
            {
                string msbuildExePath = Path.Combine(vsInstallDir, @"MSBuild\Current\Bin\MSBuild.exe");
                if (!File.Exists(msbuildExePath))
                {
                    throw new InvalidOperationException($"Could not find MSBuild.exe path for unit tests: {msbuildExePath}");
                }

                return (msbuildExePath, string.Empty);
            }
        }

        // Fall back to just using dotnet. Assume it's on the PATH
        return ("dotnet", "build");
    }

    // From: https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-copy-directories
    private static void DirectoryCopy(string sourceDirName, string destDirName)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(sourceDirName);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory does not exist or could not be found: {sourceDirName}");
        }

        var subdirs = dir.GetDirectories();

        // If the destination directory doesn't exist, create it.
        if (!Directory.Exists(destDirName))
        {
            Directory.CreateDirectory(destDirName);
        }

        // Get the files in the directory and copy them to the new location.
        var files = dir.GetFiles();
        foreach (var file in files)
        {
            var destFile = Path.Combine(destDirName, file.Name);
            file.CopyTo(destFile, false);
        }

        // Copy subdirectories and their contents to new location.
        foreach (var subdir in subdirs)
        {
            var destSubdirName = Path.Combine(destDirName, subdir.Name);
            DirectoryCopy(subdir.FullName, destSubdirName);
        }
    }

    private async Task RunMSBuildAsync(string projectFile, Warning[] expectedWarnings, string[]? expectedConsoleOutputs = null, bool expectUnusedMsvcLibrariesLog = false)
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
                             $"-distributedlogger:CentralLogger,\"{loggersAssemblyPath}\"*ForwardingLogger,\"{loggersAssemblyPath}\"";

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
            });
        Assert.IsNotNull(process);

        string stdOut = await process.StandardOutput.ReadToEndAsync();
        string stdErr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, $"Build of {projectFile} was not successful.{Environment.NewLine}StandardError: {stdErr}{Environment.NewLine}StandardOutput: {stdOut}");
        Assert.AreEqual(File.Exists(unusedLibraryLogPath), expectUnusedMsvcLibrariesLog);

        string errors = await File.ReadAllTextAsync(errorsFilePath);
        Assert.IsTrue(errors.Length == 0, $"Build of {projectFile} was not successful.{Environment.NewLine}Error log: {errors}");

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
    }
}
