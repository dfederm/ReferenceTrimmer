// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Buildalyzer;
    using Buildalyzer.Environment;
    using CommandLine;

    internal static class Program
    {
        private static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run)
                .WithNotParsed(WriteErrors);
        }

        private static void Run(Options options)
        {
            if (options.Debug)
            {
                Console.WriteLine($"Waiting for a debugger to attach (PID {Process.GetCurrentProcess().Id})");
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1000);
                }

                Debugger.Break();
            }

            // MsBuild will end up using the current working directory at time, so set it to the root.
            if (!string.IsNullOrEmpty(options.Root))
            {
                Directory.SetCurrentDirectory(options.Root);
            }

            var projectFiles = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.*proj", SearchOption.AllDirectories);
            var manager = new AnalyzerManager(new AnalyzerManagerOptions { CleanBeforeCompile = false });

            BuildEnvironment buildEnvironment = null;
            if (!string.IsNullOrEmpty(options.ToolsPath)
                || !string.IsNullOrEmpty(options.ExtensionsPath)
                || !string.IsNullOrEmpty(options.SdksPath)
                || !string.IsNullOrEmpty(options.RoslynTargetsPath))
            {
                if (string.IsNullOrEmpty(options.ToolsPath))
                {
                    Console.WriteLine("ToolsPath must be provided when ExtensionsPath, SdksPath, or RoslynTargetsPath are provided");
                    return;
                }

                var toolsPath = options.ToolsPath;
                var extensionsPath = !string.IsNullOrEmpty(options.ExtensionsPath)
                    ? options.ExtensionsPath
                    : Path.GetFullPath(Path.Combine(toolsPath, @"..\..\"));
                buildEnvironment = new BuildEnvironment
                {
                    ToolsPath = toolsPath,
                    ExtensionsPath = extensionsPath,
                    SDKsPath = !string.IsNullOrEmpty(options.SdksPath)
                        ? options.SdksPath
                        : Path.Combine(extensionsPath, "Sdks"),
                    RoslynTargetsPath = !string.IsNullOrEmpty(options.RoslynTargetsPath)
                        ? options.RoslynTargetsPath
                        : Path.Combine(toolsPath, "Roslyn"),
                };
            }

            foreach (var projectFile in projectFiles)
            {
                var project = Project.GetProject(manager, buildEnvironment, projectFile, options.MsBuildBinlog);
                if (project == null)
                {
                    continue;
                }

                foreach (var reference in project.References)
                {
                    if (!project.AssemblyReferences.Contains(reference))
                    {
                        Console.WriteLine($"Reference {reference} can be removed from {projectFile}");
                    }
                }

                foreach (var projectReference in project.ProjectReferences)
                {
                    var projectReferenceAssemblyName = projectReference.AssemblyName;
                    if (!project.AssemblyReferences.Contains(projectReferenceAssemblyName))
                    {
                        Console.WriteLine($"ProjectReference {projectReference.Name} can be removed from {projectFile}");
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
                        Console.WriteLine($"PackageReference {packageReference} can be removed from {projectFile}");
                    }
                }
            }
        }

        private static void WriteErrors(IEnumerable<Error> errors)
        {
            foreach (var error in errors)
            {
                Console.WriteLine(error.ToString());
            }
        }
    }
}
