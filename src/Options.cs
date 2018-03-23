// <copyright file="Options.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System.Diagnostics.CodeAnalysis;
    using CommandLine;

    [SuppressMessage("Microsoft.Performance", "CA1812", Justification = "This class is created by late-bound reflection")]
    internal sealed class Options
    {
        [Option('r', "root", Required = false, HelpText = "Root to start searching for projects. Defaults to the current working directory.")]
        public string Root { get; set; }

        [Option('d', "debug", Required = false, Hidden = true)]
        public bool Debug { get; set; }

        [Option('b', "binlog", Required = false, Hidden = true)]
        public bool MsBuildBinlog { get; set; }

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
