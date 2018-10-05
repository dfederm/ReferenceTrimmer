// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ReferenceTrimmer.Tests")]

namespace ReferenceTrimmer
{
    using System.Diagnostics;
    using CommandLine;
    using Microsoft.Build.Locator;
    using Microsoft.Extensions.Logging;
    using NLog;
    using NLog.Config;
    using NLog.Extensions.Logging;
    using NLog.Targets;
    using LogLevel = NLog.LogLevel;

    internal static class Program
    {
        private static void Main(string[] args)
        {
            var loggerFactory = new LoggerFactory()
                .AddNLog();
            ConfigureNLog();

            var logger = loggerFactory.CreateLogger("ReferenceTrimmer");

            Parser.Default.ParseArguments<Arguments>(args)
                .WithParsed(arguments =>
                {
                    logger.LogInformation("Full logs can be found in ReferenceTrimmer.log");

                    if (arguments.Debug)
                    {
                        Debugger.Launch();
                        Debugger.Break();
                    }

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

                    // ReferenceTrimmer needs to be a separate class to avoid the MSBuild assemblies getting loaded (and failing) before MSBuildLocator gets a chance to set things up.
                    ReferenceTrimmer.Run(arguments, logger);
                })
                .WithNotParsed(errors =>
                {
                    foreach (var error in errors)
                    {
                        logger.LogError(error.ToString());
                    }
                });
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
