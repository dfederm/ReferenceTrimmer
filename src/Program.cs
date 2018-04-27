// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Buildalyzer;
    using Buildalyzer.Environment;
    using CommandLine;
    using Microsoft.Extensions.Logging;
    using NLog;
    using NLog.Config;
    using NLog.Extensions.Logging;
    using NLog.Targets;
    using ILogger = Microsoft.Extensions.Logging.ILogger;
    using LogLevel = NLog.LogLevel;

    public static class Program
    {
        public static void Main(string[] args)
        {
            var loggerFactory = new LoggerFactory()
                .AddNLog();
            ConfigureNLog();

            var logger = loggerFactory.CreateLogger("ReferenceTrimmer");

            Parser.Default.ParseArguments<Arguments>(args)
                .WithParsed(options =>
                {
                    logger.LogInformation("Full logs can be found in ReferenceTrimmer.log");
                    Run(options, logger);
                })
                .WithNotParsed(errors =>
                {
                    foreach (var error in errors)
                    {
                        logger.LogError(error.ToString());
                    }
                });
        }

        public static void Run(Arguments arguments, ILogger logger)
        {
            if (arguments.Debug)
            {
                Console.WriteLine($"Waiting for a debugger to attach (PID {Process.GetCurrentProcess().Id})");
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1000);
                }

                Debugger.Break();
            }

            // MsBuild will end up using the current working directory at time, so set it to the root.
            if (!string.IsNullOrEmpty(arguments.Root))
            {
                Directory.SetCurrentDirectory(arguments.Root);
            }

            var workingDirectory = Directory.GetCurrentDirectory();
            var projectFiles = Directory.EnumerateFiles(workingDirectory, "*.*proj", SearchOption.AllDirectories);
            var manager = new AnalyzerManager(new AnalyzerManagerOptions { CleanBeforeCompile = false });
            var buildEnvironment = CreateBuildEnvironment(arguments);

            foreach (var projectFile in projectFiles)
            {
                var project = Project.GetProject(manager, buildEnvironment, logger, projectFile, arguments.MsBuildBinlog);
                if (project == null)
                {
                    continue;
                }

                var relativeProjectFile = projectFile.Substring(workingDirectory.Length + 1);

                foreach (var reference in project.References)
                {
                    if (!project.AssemblyReferences.Contains(reference))
                    {
                        logger.LogInformation($"Reference {reference} can be removed from {relativeProjectFile}");
                    }
                }

                foreach (var projectReference in project.ProjectReferences)
                {
                    var projectReferenceAssemblyName = projectReference.Project.AssemblyName;
                    if (!project.AssemblyReferences.Contains(projectReferenceAssemblyName))
                    {
                        logger.LogInformation($"ProjectReference {projectReference.UnevaluatedInclude} can be removed from {relativeProjectFile}");
                    }
                }

                foreach (var packageReference in project.PackageReferences)
                {
                    if (!project.PackageAssemblies.TryGetValue(packageReference, out var packageAssemblies))
                    {
                        // These are likely Analyzers, tools, etc.
                        continue;
                    }

                    if (!packageAssemblies.Any(packageAssembly => project.AssemblyReferences.Contains(packageAssembly)))
                    {
                        logger.LogInformation($"PackageReference {packageReference} can be removed from {relativeProjectFile}");
                    }
                }
            }
        }

        private static void ConfigureNLog()
        {
            var config = new LoggingConfiguration();

            var consoleTarget = new ColoredConsoleTarget
            {
                Layout = @"[${date:format=HH\:mm\:ss.fff}] ${message}",
            };
            config.AddTarget("console", consoleTarget);
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, consoleTarget));

            var fileTarget = new FileTarget
            {
                FileName = "${logger}.log",
                Layout = @"[${date:format=HH\:mm\:ss.fff}] ${level:uppercase=true} ${message}",
                DeleteOldFileOnStartup = true,
            };
            config.AddTarget("file", fileTarget);
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));

            LogManager.Configuration = config;
        }

        private static BuildEnvironment CreateBuildEnvironment(Arguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.ToolsPath)
                && string.IsNullOrEmpty(arguments.ExtensionsPath)
                && string.IsNullOrEmpty(arguments.SdksPath)
                && string.IsNullOrEmpty(arguments.RoslynTargetsPath))
            {
                return null;
            }

            if (string.IsNullOrEmpty(arguments.ToolsPath))
            {
                throw new ArgumentException("ToolsPath must be provided when ExtensionsPath, SdksPath, or RoslynTargetsPath are provided");
            }

            var toolsPath = arguments.ToolsPath;
            var msBuildExePath = Path.Combine(toolsPath, "MSBuild.exe");
            var extensionsPath = !string.IsNullOrEmpty(arguments.ExtensionsPath)
                ? arguments.ExtensionsPath
                : Path.GetFullPath(Path.Combine(toolsPath, @"..\..\"));
            var sdksPath = !string.IsNullOrEmpty(arguments.SdksPath)
                ? arguments.SdksPath
                : Path.Combine(extensionsPath, "Sdks");
            var roslynTargetsPath = !string.IsNullOrEmpty(arguments.RoslynTargetsPath)
                ? arguments.RoslynTargetsPath
                : Path.Combine(toolsPath, "Roslyn");
            return new BuildEnvironment(msBuildExePath, extensionsPath, sdksPath, roslynTargetsPath);
        }
    }
}
