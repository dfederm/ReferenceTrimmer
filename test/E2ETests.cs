using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReferenceTrimmer.Tests;

[TestClass]
[DeploymentItem("TestData/Directory.Build.props")]
[DeploymentItem("TestData/Directory.Build.targets")]
public sealed class E2ETests
{
    private static readonly (string ExePath, string Verb) MSBuild = GetMsBuildExeAndVerb();

    private static readonly Regex WarningErrorRegex = new(
        @".+: (warning|error) [\w]*: (?<message>.+) \[.+\]",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public TestContext TestContext { get; set; }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UsedProjectReference)}", nameof(UsedProjectReference))]
    public void UsedProjectReference()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UnusedProjectReference)}", nameof(UnusedProjectReference))]
    public void UnusedProjectReference()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: new[]
            {
                @"ProjectReference ../Dependency/Dependency.csproj can be removed",
            });
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UsedReference)}", nameof(UsedReference))]
    public void UsedReference()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: @"Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UnusedReference)}", nameof(UnusedReference))]
    public void UnusedReference()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        RunMSBuild(
            projectFile: @"Dependency/Dependency.csproj",
            expectedWarnings: Array.Empty<string>());

        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: new[]
            {
                @"Reference Dependency can be removed",
            });
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UsedPackageReference)}", nameof(UsedPackageReference))]
    public void UsedPackageReference()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UsedIndirectPackageReference)}", nameof(UsedIndirectPackageReference))]
    public void UsedIndirectPackageReference()
    {
        RunMSBuild(
            projectFile: @"WebHost/WebHost.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UnusedPackageReference)}", nameof(UnusedPackageReference))]
    public void UnusedPackageReference()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: new[]
            {
                @"PackageReference Newtonsoft.Json can be removed",
            });
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(UnusedPackageReferenceDocDisabled)}", nameof(UnusedPackageReferenceDocDisabled))]
    public void UnusedPackageReferenceDocDisabled()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: new[]
            {
                @"Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection",
            });
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(MissingReferenceSourceTarget)}", nameof(MissingReferenceSourceTarget))]
    public void MissingReferenceSourceTarget()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: Array.Empty<string>());
    }

    [TestMethod]
    [DeploymentItem($"TestData/{nameof(PlatformPackageConflictResolution)}", nameof(PlatformPackageConflictResolution))]
    public void PlatformPackageConflictResolution()
    {
        RunMSBuild(
            projectFile: @"Library/Library.csproj",
            expectedWarnings: new[]
            {
                // TODO: These "metapackages" should not be reported.
                "PackageReference NETStandard.Library can be removed",
            });
    }

    private static (string ExePath, string Verb) GetMsBuildExeAndVerb()
    {
        // On Windows, try to find Visual Studio
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // When running from a developer command prompt, Visual Studio can be found under VSINSTALLDIR
            string vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (string.IsNullOrEmpty(vsInstallDir))
            {
                // When running Visual Studio can be found under VSAPPIDDIR
                string vsAppIdeDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
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

    private void RunMSBuild(string projectFile, string[] expectedWarnings)
    {
        // Copy to the test run dir to avoid cross-test contamination
        var testDataExecPath = Path.Combine(TestContext.TestRunDirectory, "Out", TestContext.TestName);
        string logDirBase = Path.Combine(testDataExecPath, "Logs");
        string binlogFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".binlog");
        string warningsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".warnings.log");
        string errorsFilePath = Path.Combine(logDirBase, Path.GetFileName(projectFile) + ".errors.log");
        string testOutputDir = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

        Process process = Process.Start(
            new ProcessStartInfo
            {
                FileName = MSBuild.ExePath,
                Arguments = $"{MSBuild.Verb} \"{projectFile}\" -restore -nologo -nodeReuse:false -noAutoResponse -bl:\"{binlogFilePath}\" -flp1:logfile=\"{errorsFilePath}\";errorsonly -flp2:logfile=\"{warningsFilePath}\";warningsonly -p:TestOutputDir={testOutputDir}",
                WorkingDirectory = testDataExecPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

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
