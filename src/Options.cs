using CommandLine;

namespace ReferenceReducer
{
    internal sealed class Options
    {
        [Option('r', "root", Required = false, HelpText = "Root to start searching for projects. Defaults to the current working directory.")]
        public string Root { get; set; }

        [Option('t', "msbuildtoolspath", Required = false, HelpText = "The MsBuild tools to use. If not provided, MsBuild will be searched on the PATH.")]
        public string MsBuildToolsPath { get; set; }

        [Option('d', "debug", Required = false, Hidden = true)]
        public bool Debug { get; set; }
    }
}
