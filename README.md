# ReferenceTrimmer
[![NuGet Version](https://img.shields.io/nuget/v/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)

Easily identify which dependencies can be removed from an MSBuild project.

## How to use
Add a package reference to the [ReferenceTrimmer](https://www.nuget.org/packages/ReferenceTrimmer) package in your projects. The package contains build logic to emit warnings when unused dependencies are detected.

If you're using [Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/Central-Package-Management), you can it as a `GlobalPackageReference` in your `Directory.Packages.props` to apply it to the entire repo.

```xml
  <ItemGroup>
    <GlobalPackageReference Include="ReferenceTrimmer" Version="{SomeVersion}" />
  </ItemGroup>
```

Alternately, you can add a `PackageReference` to your `Directory.Build.props` or `Directory.Build.targets` to apply to the entire repo.

You'll need to enable C# documentation XML generation to ensure good analysis results. If your repo is not already using docxml globally, this can introduce a large number of errors and warnings specific to docxml. Additionally, turning on docxml adds additional output I/O that can slow down large repos. You can turn off specific docxml related warnings and errors while defaulting ReferenceTrimmer to off using a block of code like this in your `Directory.Build.props`. Turn on the ReferenceTrimmer build by setting `/p:EnableReferenceTrimmer=true` on the MSBuild command line or setting the same property value as an environment variable. You could create a separate build pipeline for your repo to run ReferenceTrimmer builds.

```xml
  <!-- ReferenceTrimmer - run build with /p:EnableReferenceTrimmer=true to enable -->
  <PropertyGroup Label="ReferenceTrimmer">
    <EnableReferenceTrimmer Condition=" '$(EnableReferenceTrimmer)' == '' ">false</EnableReferenceTrimmer>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(EnableReferenceTrimmer)' == 'true' and '$(GenerateDocumentationFile)' != 'true' " Label="ReferenceTrimmer">
    <!-- Documentation file generation is required for more accurate C# detection. -->
    <GenerateDocumentationFile>true</GenerateDocumentationFile>

    <!-- Suppress XML doc comment issues to avoid errors during ReferenceTrimmer:
         - CS0419: Ambiguous reference in cref attribute
         - CS1570: XML comment has badly formed XML
         - CS1573: Parameter has no matching param tag in the XML comment
         - CS1574: XML comment has cref attribute that could not be resolved
         - CS1584: XML comment has syntactically incorrect cref attribute
         - CS1591: Missing XML comment for publicly visible type or member
         - SA1602: Enumeration items should be documented
    -->
    <NoWarn>$(NoWarn);419;1570;1573;1574;1584;1591;SA1602</NoWarn>
  </PropertyGroup>
```

Note: To get better results, enable the [`IDE0005`](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0005) unnecessary `using` rule. This avoids the C# compiler seeing a false positive assembly usage from unneeded `using` directives causing it to miss a removable dependency. See also the note for why IDE0005 code analysis rule requires `<GenerateDocumentationFile>` property to be enabled. Documentation generation is also required for accuracy of used references detection (based on https://github.com/dotnet/roslyn/issues/66188).

## Configuration
`$(EnableReferenceTrimmer)` - Controls whether the build logic should run for a given project. Defaults to `true`.

## Rules
| Id     | Description |
|--------|-------------|
| RT0000 | Enable documentation generation for accuracy of used references detection |
| RT0001 | Unnecessary reference  |
| RT0002 | Unnecessary project reference |
| RT0003 | Unnecessary package reference |

## How does it work?
There are two main pieces to the package. First there is an MSBuild task which collects all refernces passed to the compiler. There is also a Roslyn Analyzer which uses the [`GetUsedAssemblyReferences`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.getusedassemblyreferences) analyzer API which is available starting with Roslyn compiler that shipped with Visual Studio 2019 version 16.10, .NET 5. (see https://github.com/dotnet/roslyn/blob/main/docs/wiki/NuGet-packages.md#versioning). This is the compiler telling us exactly what references were needed as part of compilation. The analyzer then compares the set of references the Task gathered with the references the compiler says were used.

## Future development
The outcome of https://github.com/dotnet/sdk/issues/10414 may be of use for `ReferenceTrimmer` future updates.
