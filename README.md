# ReferenceTrimmer
[![NuGet Version](https://img.shields.io/nuget/v/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)

Easily identify which dependencies can be removed from an MSBuild project.

## How to use
Simply add a `PackageReference` to the [ReferenceTrimmer](https://www.nuget.org/packages/ReferenceTrimmer) package in your projects. You can add the package reference to your `Directory.Build.props` or `Directory.Build.targets` instead to apply to the entire repo.

The package contains build logic to emit warnings when unused dependencies are detected.

Note: to get better effects, enable [`IDE0005`](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0005) unnecessary code rule. See also https://github.com/dotnet/roslyn/issues/41640#issuecomment-985780130 for why this code analysis rule requires `<GenerateDocumentationFile>` property to be also enabled.

## Configuration
`$(EnableReferenceTrimmer)` - Controls whether the build logic should run for a given project. Defaults to `true`.
