using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class E2ETests
{
    private static readonly (string ExePath, string Verb) MSBuild = GetMsBuildExeAndVerb();

    private static readonly Regex WarningErrorRegex = new(
        @".+: (warning|error) (?<message>.+) \[.+\]",
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
    public void UsedProjectReference()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void UnusedProjectReference()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                "RT0002: ProjectReference ../Dependency/Dependency.csproj can be removed",
            });
    }

    [TestMethod]
    public void UsedReferenceHintPath()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void UsedReferenceItemSpec()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void UnusedReferenceHintPath()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                "RT0001: Reference Dependency can be removed",
            });
    }

    [TestMethod]
    public void UnusedReferenceItemSpec()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: "Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                OperatingSystem.IsWindows()
                    ? @"RT0001: Reference ..\Dependency\bin\Debug\net472\\Dependency.dll can be removed"
                    : @"RT0001: Reference ../Dependency/bin/Debug/net472/Dependency.dll can be removed",
            });
    }

    [TestMethod]
    public void UsedPackageReference()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void UsedIndirectPackageReference()
    {
        RunMSBuild(
            projectFile: "WebHost/WebHost.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void UnusedPackageReference()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                "RT0003: PackageReference Newtonsoft.Json can be removed",
            });
    }

    [TestMethod]
    public void UnusedPackageReferenceDocDisabled()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                "RT0000: Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection",
            });
    }

    [TestMethod]
    public void BuildPackageReference()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void MissingReferenceSourceTarget()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void PlatformPackageConflictResolution()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: new[]
            {
                // TODO: These "metapackages" should not be reported.
                "RT0003: PackageReference NETStandard.Library can be removed",
            });
    }

    [TestMethod]
    public void NoTargets()
    {
        RunMSBuild(
            projectFile: "Project.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void TargetFrameworkWithOs()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void AbsoluteIntermediateOutputPath()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void BuildExtensions()
    {
        RunMSBuild(
            projectFile: "Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    public void ReferenceInPackage()
    {
        RunMSBuild(
            projectFile: "Tests/Tests.csproj",
            expectedWarnings: Array.Empty<string>());
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

    private void RunMSBuild(string projectFile, string[] expectedWarnings)
    {
        var testDataSourcePath = Path.GetFullPath(Path.Combine("TestData", TestContext?.TestName ?? string.Empty));

        string logDirBase = Path.Combine(testDataSourcePath, "Logs");
        string binlogFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".binlog");
        string warningsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".warnings.log");
        string errorsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".errors.log");

        TestContext?.WriteLine($"Log directory: {logDirBase}");

        Process? process = Process.Start(
            new ProcessStartInfo
            {
                FileName = MSBuild.ExePath,
                Arguments = $"{MSBuild.Verb} \"{projectFile}\" -restore -nologo -nodeReuse:false -noAutoResponse -bl:\"{binlogFilePath}\" -flp1:logfile=\"{errorsFilePath}\";errorsonly -flp2:logfile=\"{warningsFilePath}\";warningsonly",
                WorkingDirectory = testDataSourcePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        Assert.IsNotNull(process);

        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Assert.AreEqual(0, process.ExitCode, $"Build of {projectFile} was not successful.{Environment.NewLine}StandardError: {stdErr}{Environment.NewLine}StandardOutput: {stdOut}");

        string errors = File.ReadAllText(errorsFilePath);
        Assert.IsTrue(errors.Length == 0, $"Build of {projectFile} was not successful.{Environment.NewLine}Error log: {errors}");

        string[] actualWarnings = File.ReadAllLines(warningsFilePath)
            .Select(line =>
            {
                Match match = WarningErrorRegex.Match(line);
                return match.Success ? match.Groups["message"].Value : line;
            })
            .ToArray();

        bool warningsMatched = expectedWarnings.Length == actualWarnings.Length;
        if (warningsMatched)
        {
            for (var i = 0; i < actualWarnings.Length; i++)
            {
                warningsMatched &= expectedWarnings[i] == actualWarnings[i];
            }
        }

        Assert.IsTrue(
            warningsMatched,
            $@"
Expected warnings:
{(expectedWarnings.Length == 0 ? "<none>" : string.Join(Environment.NewLine, expectedWarnings))}

Actual warnings:
{(actualWarnings.Length == 0 ? "<none>" : string.Join(Environment.NewLine, actualWarnings))}");
    }
}
