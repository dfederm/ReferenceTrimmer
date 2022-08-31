using System.CommandLine;
using System.CommandLine.Binding;

namespace ReferenceTrimmer;

internal record Arguments(bool Debug, DirectoryInfo Path, bool CompileIfNeeded, bool RestoreIfNeeded, bool UseBinaryLogger, string? MSBuildPath)
{
    internal static BinderBase<Arguments> AddOptionsAndGetBinder(Command command)
    {
        Option<bool>? debugOption =
#if DEBUG
    new Option<bool>(
        aliases: new[] { "--debug" },
        description: "Path from which to start searching for projects. Defaults to the current working directory.");
#else
    null;
#endif

        var pathOption = new Option<DirectoryInfo?>(
            aliases: new[] { "--path", "-p" },
            description: "Path from which to start searching for projects. Defaults to the current working directory.");

        var compileOption = new Option<bool>(
            aliases: new[] { "--compile", "-c" },
            description: "Compile a project if its intermediate assembly doesn't exist.");

        var restoreOption = new Option<bool>(
            aliases: new[] { "--restore", "-r" },
            description: "Restore a project if its assets file doesn't exist and is needed to for PackageReference analysis.");

        var binlogOption = new Option<bool>(
            aliases: new[] { "--binlog", "-bl" },
            description: "Creates a binlog if a Compile or Restore is needed. This can help with debugging failures.");

        var msbuildPathOption = new Option<string?>(
            aliases: new[] { "--msbuildpath" },
            description: "Overrides the MsBuild tools path.");

        if (debugOption != null)
        {
            command.AddOption(debugOption);
        }

        command.AddOption(pathOption);
        command.AddOption(compileOption);
        command.AddOption(restoreOption);
        command.AddOption(binlogOption);

        return new ArgumentsBinder(debugOption, pathOption, compileOption, restoreOption, binlogOption, msbuildPathOption);
    }

    private sealed class ArgumentsBinder : BinderBase<Arguments>
    {
        private readonly Option<bool>? _debugOption;
        private readonly Option<DirectoryInfo?> _pathOption;
        private readonly Option<bool> _compileOption;
        private readonly Option<bool> _restoreOption;
        private readonly Option<bool> _binlogOption;
        private readonly Option<string?> _msbuildPathOption;

        public ArgumentsBinder(
            Option<bool>? debugOption,
            Option<DirectoryInfo?> pathOption,
            Option<bool> compileOption,
            Option<bool> restoreOption,
            Option<bool> binlogOption,
            Option<string?> msbuildPathOption)
        {
            _debugOption = debugOption;
            _pathOption = pathOption;
            _compileOption = compileOption;
            _restoreOption = restoreOption;
            _binlogOption = binlogOption;
            _msbuildPathOption = msbuildPathOption;
        }

        protected override Arguments GetBoundValue(BindingContext bindingContext) =>
            new Arguments(
                _debugOption == null ? false : bindingContext.ParseResult.GetValueForOption(_debugOption),
                bindingContext.ParseResult.GetValueForOption(_pathOption) ?? new DirectoryInfo(Directory.GetCurrentDirectory()),
                bindingContext.ParseResult.GetValueForOption(_compileOption),
                bindingContext.ParseResult.GetValueForOption(_restoreOption),
                bindingContext.ParseResult.GetValueForOption(_binlogOption),
                bindingContext.ParseResult.GetValueForOption(_msbuildPathOption));
    }

}
