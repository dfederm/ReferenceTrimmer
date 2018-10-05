// <copyright file="Arguments.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using CommandLine;

    internal sealed class Arguments
    {
        [Option('p', "path", Required = false, HelpText = "Path from which to start searching for projects. Defaults to the current working directory.")]
        public string Path { get; set; }

        [Option('c', "compile", Required = false, HelpText = "Compile a project if its intermediate assembly doesn't exist.")]
        public bool CompileIfNeeded { get; set; }

        [Option('r', "restore", Required = false, HelpText = "Restore a project if its assets file doesn't exist and is needed to for PackageReference analysis.")]
        public bool RestoreIfNeeded { get; set; }

        [Option('d', "debug", Required = false, Hidden = true)]
        public bool Debug { get; set; }

        [Option('b', "binlog", Required = false, HelpText = "Creates a binlog if a Compile or Restore is needed. This can help with debugging failures.")]
        public bool UseBinaryLogger { get; set; }

        [Option('m', "msbuildpath", Required = false, HelpText = "Overrides the MsBuild tools path")]
        public string MSBuildPath { get; set; }
    }
}
