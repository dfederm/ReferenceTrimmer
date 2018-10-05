// <copyright file="E2ETests.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Microsoft.Build.Locator;
    using Microsoft.Extensions.Logging;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public sealed class E2ETests
    {
        private static string msBuildPath;

        // ReSharper disable once MemberCanBePrivate.Global
        public TestContext TestContext { get; set; }

        [AssemblyInitialize]
#pragma warning disable CA1801 // Review unused parameters - [AssemblyInitialize] requires the param be there.
        public static void AssemblyInit(TestContext context)
#pragma warning restore CA1801 // Review unused parameters
        {
            var visualStudioInstance = MSBuildLocator.RegisterDefaults();
            msBuildPath = Path.Combine(visualStudioInstance.MSBuildPath, "MSBuild.exe");
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
            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Dependency\obj\Debug\net461\Dependency.dll does not exist. Compiling Dependency\Dependency.csproj...",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UnusedProjectReference()
        {
            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Dependency\obj\Debug\net461\Dependency.dll does not exist. Compiling Dependency\Dependency.csproj...",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
                @"ProjectReference ..\Dependency\Dependency.csproj can be removed from Library\Library.csproj",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UsedReference()
        {
            // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
            var buildFile = Path.GetFullPath(Path.Combine("TestData", this.TestContext.TestName, @"Dependency\Dependency.csproj"));
            RunMSBuild(buildFile);

            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UnusedReference()
        {
            // For direct references, MSBuild can't determine build order so we need to ensure the dependency is already built
            var buildFile = Path.GetFullPath(Path.Combine("TestData", this.TestContext.TestName, @"Dependency\Dependency.csproj"));
            RunMSBuild(buildFile);

            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
                @"Reference Dependency can be removed from Library\Library.csproj",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UsedPackageReference()
        {
            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UsedIndirectPackageReference()
        {
            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly WebHost\obj\Debug\netcoreapp2.1\WebHost.dll does not exist. Compiling WebHost\WebHost.csproj...",
            };
            AssertLogs(expectedLogs, actualLogs);
        }

        [TestMethod]
        public void UnusedPackageReference()
        {
            var actualLogs = this.RunTest();
            var expectedLogs = new[]
            {
                @"Binary logging enabled and will be written to msbuild.binlog",
                @"Assembly Library\obj\Debug\net461\Library.dll does not exist. Compiling Library\Library.csproj...",
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

        private static void RunMSBuild(string buildFile)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = msBuildPath,
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

        private string[] RunTest(Arguments arguments = null)
        {
            // Copy to the test run dir to avoid cross-test contamination
            var testPath = Path.GetFullPath(Path.Combine("TestData", this.TestContext.TestName));
            var root = Path.Combine(this.TestContext.TestRunDirectory, this.TestContext.TestName);
            DirectoryCopy(testPath, root);

            // Run ReferenceTrimmer and collect the log entries
            if (arguments == null)
            {
                arguments = new Arguments { CompileIfNeeded = true, RestoreIfNeeded = true };
            }

            arguments.Path = root;

            // To help with UT debugging
            arguments.UseBinaryLogger = true;

            var loggerFactory = new LoggerFactory();
            var mockLoggerProvider = new MockLoggerProvider();
            loggerFactory.AddProvider(mockLoggerProvider);

            // MSBuild sets the current working directory, so we need to be sure to restore it after each run.
            var currentWorkingDirectory = Directory.GetCurrentDirectory();
            try
            {
                ReferenceTrimmer.Run(arguments, loggerFactory.CreateLogger(this.TestContext.TestName));
            }
            finally
            {
                Directory.SetCurrentDirectory(currentWorkingDirectory);
            }

            return mockLoggerProvider.LogLines;
        }

        private sealed class MockLoggerProvider : ILoggerProvider
        {
            private readonly List<string> logLines = new List<string>();

            public string[] LogLines => this.logLines.ToArray();

            public void Dispose()
            {
            }

            public ILogger CreateLogger(string categoryName) => new MockLogger(this.logLines);
        }

        private sealed class MockLogger : ILogger
        {
            private readonly List<string> logLines;

            public MockLogger(List<string> logLines)
            {
                this.logLines = logLines;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
                this.logLines.Add(formatter(state, exception));

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
}
