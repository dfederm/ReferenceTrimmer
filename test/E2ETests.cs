// <copyright file="E2ETests.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer.Tests
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using Buildalyzer.Logging;
    using Microsoft.Extensions.Logging;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class E2ETests
    {
        private static readonly char[] NewLineCharacters = Environment.NewLine.ToCharArray();

        // ReSharper disable once MemberCanBePrivate.Global
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void UsedProjectReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(0, logs.Length);
        }

        [TestMethod]
        public void UnusedProjectReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(1, logs.Length);
            Assert.AreEqual(@"ProjectReference ..\Dependency\Dependency.csproj can be removed from Library\Library.csproj", logs[0]);
        }

        [TestMethod]
        public void UsedReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(0, logs.Length);
        }

        [TestMethod]
        public void UnusedReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(1, logs.Length);
            Assert.AreEqual(@"Reference Dependency can be removed from Library\Library.csproj", logs[0]);
        }

        [TestMethod]
        public void UsedPackageReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(0, logs.Length);
        }

        [TestMethod]
        public void UnusedPackageReference()
        {
            var logs = this.RunTest();
            Assert.AreEqual(1, logs.Length);
            Assert.AreEqual(@"PackageReference Newtonsoft.Json can be removed from Library\Library.csproj", logs[0]);
        }

        private string[] RunTest()
        {
            var testPath = Path.GetFullPath(Path.Combine("TestData", this.TestContext.TestName));

            // First, build the projects
            foreach (var buildFile in Directory.EnumerateFiles(testPath, "*.*proj", SearchOption.AllDirectories))
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        // Using dotnet since it's easier to find than msbuild
                        FileName = "dotnet",
                        Arguments = $"build {Path.GetFileName(buildFile)}",
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

            // Next, run ReferenceTrimmer and collect the log entries
            var logs = new StringBuilder();
            using (var writer = new StringWriter(logs))
            {
                var arguments = new Arguments { Root = testPath };
                var loggerFactory = new LoggerFactory();
                loggerFactory.AddProvider(new TextWriterLoggerProvider(writer));

                // Providing a Root messes with the current working directory, so we need to stash the old one and restore it later.
                var oldCurrentDirectory = Directory.GetCurrentDirectory();
                try
                {
                    Program.Run(arguments, loggerFactory.CreateLogger(this.TestContext.TestName));
                }
                finally
                {
                    Directory.SetCurrentDirectory(oldCurrentDirectory);
                }
            }

            return logs.ToString().Split(NewLineCharacters, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
