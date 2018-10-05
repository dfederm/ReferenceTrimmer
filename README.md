# ReferenceTrimmer
[![NuGet Version](https://img.shields.io/nuget/v/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ReferenceTrimmer.svg)](https://www.nuget.org/packages/ReferenceTrimmer)

Easily identify which dependencies can be removed from an MSBuild project.

## How to install and run
1. `nuget install ReferenceTrimmer`
2. Note where NuGet specified it installed the package, which should match the location specified in your NuGet.config (it will look like "Installing package 'ReferenceTrimmer' to...")
3. Navigate to the root directory to use for analysis
4. Run `ReferenceTrimmer.exe` from the location noted in step 2. Within the package it's in the `tools` folder.

The program should then output which References, ProjectReferences, and PackageReferences should be removed from each project (if any).

## Command-line arguments
All arguments are optional.

```
  -p, --path           Path from which to start searching for projects. Defaults to the current working directory.
  -c, --compile        Compile a project if its intermediate assembly doesn't exist.
  -r, --restore        Restore a project if its assets file doesn't exist and is needed to for PackageReference analysis.
  -b, --binlog         Creates a binlog if a Compile or Restore is needed. This can help with debugging failures.
  -m, --msbuildpath    Overrides the MsBuild tools path
```