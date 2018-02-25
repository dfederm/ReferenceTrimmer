# ReferenceTrimmer
Easily identify which dependencies can be removed from a project.

## How to use
1. Download the latest release or build the project yourself
2. Navigate to the root directory to use for analysis
3. Run `ReferenceTrimmer`

The program should output which References, ProjectReferences, and PackageReferences should be removed from each project (if any).

### Versions
If you're using Windows, you'll probably always want to use the binaries built against net461. Even if you're analyzing projects using netcore and netstandard, it will choose the right installation of MsBuild to analyze with.

If you're using a non-Windows OS, you'll need to use the binaries built against netcoreapp2.0.