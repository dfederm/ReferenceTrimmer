// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using CommandLine;
    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Execution;
    using Microsoft.Build.Locator;
    using Microsoft.Build.Logging;
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
                Debugger.Launch();
                Debugger.Break();
            }

            // Normalize the provided root param
            arguments.Path = arguments.Path == null
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(arguments.Path);

            // Load MSBuild
            if (MSBuildLocator.CanRegister)
            {
                if (string.IsNullOrEmpty(arguments.MSBuildPath))
                {
                    MSBuildLocator.RegisterDefaults();
                }
                else
                {
                    MSBuildLocator.RegisterMSBuildPath(arguments.MSBuildPath);
                }
            }

            var buildManager = BuildManager.DefaultBuildManager;
            var parameters = new BuildParameters(ProjectCollection.GlobalProjectCollection);

            if (arguments.UseBinaryLogger)
            {
                const string BinaryLoggerFilename = "msbuild.binlog";
                logger.LogInformation($"Binary logging enabled and will be written to {BinaryLoggerFilename}");
                parameters.Loggers = new[]
                {
                    new BinaryLogger { Parameters = BinaryLoggerFilename },
                };
            }

            buildManager.BeginBuild(parameters);

            var projectFiles = Directory.EnumerateFiles(arguments.Path, "*.*proj", SearchOption.AllDirectories);
            foreach (var projectFile in projectFiles)
            {
                var project = ParsedProject.Create(projectFile, arguments, buildManager, logger);
                if (project == null)
                {
                    continue;
                }

                var relativeProjectFile = projectFile.Substring(arguments.Path.Length + 1);

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

            buildManager.EndBuild();
        }

        private static void ConfigureNLog()
        {
            var config = new LoggingConfiguration();

            var consoleTarget = new ColoredConsoleTarget
            {
                Layout = @"[${date:format=HH\:mm\:ss.fff}] ${message}",
            };
            config.AddTarget("console", consoleTarget);
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, consoleTarget));

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
    }
}
