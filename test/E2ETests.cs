using System.Diagnostics;
using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class E2ETests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private static string MsBuildExePath;

    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    [AssemblyInitialize]
    public static void AssemblyInit(TestContext _)
    {
        string msbuildPath = GetMsBuildPath();
        MsBuildExePath = Path.Combine(msbuildPath, "MSBuild.exe");

        MSBuildLocator.RegisterMSBuildPath(msbuildPath);

        // Try resolving any other missing assembly from the MSBuild directory
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            AssemblyName requestedAssemblyName = new(eventArgs.Name);
            FileInfo candidateAssemblyFileInfo = new FileInfo(Path.Combine(msbuildPath, $"{requestedAssemblyName.Name}.dll"));
            if (!candidateAssemblyFileInfo.Exists)
            {
                return null;
            }

            return Assembly.LoadFrom(candidateAssemblyFileInfo.FullName);
        };
    }

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        // Write some Directory.Build.(props|targets) to avoid unexpected inheritance
        const string Contents = "<Project></Project>";
        File.WriteAllText(Path.Combine(testContext.TestRunDirectory, "Directory.Build.props"), Contents);
        File.WriteAllText(Path.Combine(testContext.TestRunDirectory, "Directory.Build.targets"), Contents);
    }

    [TestMethod]
    public void UsedProjectReference()
    {
        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Dependency\obj\Debug\net472\Dependency.dll does not exist. Compiling Dependency\Dependency.csproj...",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UnusedProjectReference()
    {
        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Dependency\obj\Debug\net472\Dependency.dll does not exist. Compiling Dependency\Dependency.csproj...",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
            @"ProjectReference ..\Dependency\Dependency.csproj can be removed from Library\Library.csproj",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UsedReference()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        var buildFile = Path.GetFullPath(Path.Combine("TestData", TestContext.TestName, @"Dependency\Dependency.csproj"));
        RunMSBuild(buildFile);

        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UnusedReference()
    {
        // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
        var buildFile = Path.GetFullPath(Path.Combine("TestData", TestContext.TestName, @"Dependency\Dependency.csproj"));
        RunMSBuild(buildFile);

        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
            @"Reference Dependency can be removed from Library\Library.csproj",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UsedPackageReference()
    {
        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UsedIndirectPackageReference()
    {
        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly WebHost\obj\Debug\netcoreapp2.2\WebHost.dll does not exist. Compiling WebHost\WebHost.csproj...",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    [TestMethod]
    public void UnusedPackageReference()
    {
        var actualLogs = RunTest();
        var expectedLogs = new[]
        {
            @"Binary logging enabled and will be written to msbuild.binlog",
            @"Assembly Library\obj\Debug\net472\Library.dll does not exist. Compiling Library\Library.csproj...",
            @"PackageReference Newtonsoft.Json can be removed from Library\Library.csproj",
        };
        AssertLogs(expectedLogs, actualLogs);
    }

    private static void AssertLogs(string[] expectedLogs, string[] actualLogs)
    {
        var errorMessage = $@"
Expected Logs:
{(expectedLogs.Length == 0 ? "<none>" : string.Join(Environment.NewLine, expectedLogs))}

Actual Logs:
{(actualLogs.Length == 0 ? "<none>" : string.Join(Environment.NewLine, actualLogs))}";
        Assert.AreEqual(expectedLogs.Length, actualLogs.Length, errorMessage);
        for (var i = 0; i < actualLogs.Length; i++)
        {
            Assert.AreEqual(expectedLogs[i], actualLogs[i]);
        }
    }

    private static string GetMsBuildPath()
    {
        // When running in CloudBuild, "Visual Studio" can be found under TOOLPATH_MSBUILD
        string? vsInstallDir;

        // When running from a developer command prompt, Visual Studio can be found under VSINSTALLDIR
        vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (string.IsNullOrEmpty(vsInstallDir))
        {
            // When running Visual Studio can be found under VSAPPIDDIR
            string? vsAppIdeDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
            if (!string.IsNullOrEmpty(vsAppIdeDir))
            {
                vsInstallDir = Path.Combine(vsAppIdeDir, @"..\..");
            }
        }

        if (string.IsNullOrEmpty(vsInstallDir) || !Directory.Exists(vsInstallDir))
        {
            throw new InvalidOperationException($"Could not find Visual Studio path for unit tests: {vsInstallDir}");
        }

        string msbuildPath = Path.Combine(vsInstallDir, @"MSBuild\Current\Bin");
        if (!Directory.Exists(msbuildPath))
        {
            throw new InvalidOperationException($"Could not find MSBuild path for unit tests: {msbuildPath}");
        }

        return msbuildPath;
    }

    private static void RunMSBuild(string buildFile)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = MsBuildExePath,
                Arguments = Path.GetFileName(buildFile),
                WorkingDirectory = Path.GetDirectoryName(buildFile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        process.WaitForExit();

        Assert.AreEqual(0, process.ExitCode, $"Build of {buildFile} was not successful.\r\nStandardError: {process.StandardError.ReadToEnd()},\r\nStandardOutput: {process.StandardOutput.ReadToEnd()}");
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

    private string[] RunTest()
    {
        // Copy to the test run dir to avoid cross-test contamination
        var testPath = Path.GetFullPath(Path.Combine("TestData", TestContext.TestName));
        var root = Path.Combine(TestContext.TestRunDirectory, TestContext.TestName);
        DirectoryCopy(testPath, root);

        // Run ReferenceTrimmer and collect the log entries
        var arguments = new Arguments(
            Debug: false,
            Path: new DirectoryInfo(root),
            CompileIfNeeded: true,
            RestoreIfNeeded: true,
            UseBinaryLogger: true, // To help with UT debugging
            MSBuildPath: null);

        var loggerFactory = new LoggerFactory();
        var mockLoggerProvider = new MockLoggerProvider();
        loggerFactory.AddProvider(mockLoggerProvider);

        // MSBuild sets the current working directory, so we need to be sure to restore it after each run.
        var currentWorkingDirectory = Directory.GetCurrentDirectory();
        try
        {
            ReferenceTrimmerRunner.Run(arguments, loggerFactory.CreateLogger(TestContext.TestName));
        }
        finally
        {
            Directory.SetCurrentDirectory(currentWorkingDirectory);
        }

        return mockLoggerProvider.LogLines;
    }

    private sealed class MockLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _logLines = new List<string>();

        public string[] LogLines => _logLines.ToArray();

        public void Dispose()
        {
        }

        public ILogger CreateLogger(string categoryName) => new MockLogger(_logLines);
    }

    private sealed class MockLogger : ILogger
    {
        private readonly List<string> _logLines;

        public MockLogger(List<string> logLines)
        {
            _logLines = logLines;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _logLines.Add(formatter(state, exception));

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable BeginScope<TState>(TState state) => new MockDisposable();
    }

    private sealed class MockDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
