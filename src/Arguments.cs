// <copyright file="Arguments.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using CommandLine;

    public sealed class Arguments
    {
        [Option('r', "root", Required = false, HelpText = "Root to start searching for projects. Defaults to the current working directory.")]
        public string Root { get; set; }

        [Option("compile", Required = false, HelpText = "Compile a project if its intermediate assembly doesn't exist.")]
        public bool CompileIfNeeded { get; set; }

        [Option("restore", Required = false, HelpText = "Restore a project if its assets file doesn't exist and is needed to for PackageReference analysis.")]
        public bool RestoreIfNeeded { get; set; }

        [Option('d', "debug", Required = false, Hidden = true)]
        public bool Debug { get; set; }

        [Option('b', "binlog", Required = false, HelpText = "Creates a binlog if a Compile or Restore is needed. This can help with debugging failures.")]
        public bool UseBinaryLoogger { get; set; }

        [Option('t', "toolspath", Required = false, HelpText = "Overrides the MsBuild tools path")]
        public string ToolsPath { get; set; }

        [Option('e', "extensionspath", Required = false, HelpText = "Overrides the MsBuild extensions path. When provided, -toolspath must also be provided.")]
        public string ExtensionsPath { get; set; }

        [Option('s', "sdkspath", Required = false, HelpText = "Overrides the MsBuild sdks path. When provided, -toolspath must also be provided.")]
        public string SdksPath { get; set; }

        [Option('c', "roslyntargetspath", Required = false, HelpText = "Overrides the MsBuild roslyn targets path. When provided, -toolspath must also be provided.")]
        public string RoslynTargetsPath { get; set; }
    }
}
