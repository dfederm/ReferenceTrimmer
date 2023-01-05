# ReferenceTrimmer
[![NuGet Version](https://img.shields.io/nuget/v/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)

Easily identify which dependencies can be removed from an MSBuild project.

## How to use
Simply add a `PackageReference` to the [ReferenceTrimmer](https://www.nuget.org/packages/ReferenceTrimmer) package in your projects. You can add the package reference to your `Directory.Build.props` or `Directory.Build.targets` instead to apply to the entire repo.

The package contains build logic to emit warnings when unused dependencies are detected. The logic relies on [`GetUsedAssemblyReferences`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.getusedassemblyreferences) analyzer API which is available starting with Roslyn compiler that shipped with Visual Studio 2019 version 16.10, .NET 5. (see https://github.com/dotnet/roslyn/blob/main/docs/wiki/NuGet-packages.md#versioning).

Note: to get better effects, enable [`IDE0005`](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0005) unnecessary code rule. See also the note for why IDE0005 code analysis rule requires `<GenerateDocumentationFile>` property to be enabled. Documentation generation is also required for accuracy of used references detection (based on https://github.com/dotnet/roslyn/issues/66188).

## Configuration
`$(EnableReferenceTrimmer)` - Controls whether the build logic should run for a given project. Defaults to `true`.

## Future development

The outcome of https://github.com/dotnet/sdk/issues/10414 may be of use for `ReferenceTrimmer` future updates.
