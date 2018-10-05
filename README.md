# ReferenceTrimmer
Easily identify which dependencies can be removed from a project.

## How to install
1. `nuget install ReferenceTrimmer`
2. Note where NuGet specified it installed the package, which should match the location specified in your NuGet.config (it will look like "Installing package 'ReferenceTrimmer' to...")
3. Navigate to the root directory to use for analysis
4. Run `ReferenceTrimmer.exe` from the location noted in step 2 above.

The program should output which References, ProjectReferences, and PackageReferences should be removed from each project (if any).
