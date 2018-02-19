// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceReducer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Buildalyzer;
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
            var manager = new AnalyzerManager();

            var projects = new Dictionary<string, Project>();
            foreach (var projectFile in projectFiles)
            {
                var project = Project.GetProject(manager, options, projectFile);
                if (project == null)
                {
                    continue;
                }

                var assemblyReferences = project.AssemblyReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var reference in project.References)
                {
                    if (!assemblyReferences.Contains(reference))
                    {
                        Console.WriteLine($"Reference {reference} can be removed from {projectFile}");
                    }
                }

                foreach (var projectReference in project.ProjectReferences)
                {
                    var projectReferenceAssemblyName = projectReference.AssemblyName;
                    if (!assemblyReferences.Contains(projectReferenceAssemblyName))
                    {
                        Console.WriteLine($"ProjectReference {projectReference} can be removed from {projectFile}");
                    }
                }

                // TODO: PackageReferences
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
