// <copyright file="Options.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceReducer
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
    }
}
