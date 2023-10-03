@echo off
@rem Builds with ReferenceTrimmer C# and C++ modes
@rem Assumes RefTrim is wired into Directory.Build.props or Directory.Packages.props as a global package reference.
@rem Usage: BuildWithReferenceTrimmer [msbuild args]
setlocal EnableDelayedExpansion

@rem Explicitly turn on in case the repo is defaulting to false.
set EnableReferenceTrimmer=true

@echo Restoring with ReferenceTrimmer enabled
call msbuild /t:Restore

@rem Find the RefTrim latest package in the package store.
set NuGetPkgBase=%NUGET_PACKAGES%
if "%NuGetPkgBase%"=="" set NuGetPkgBase=%USERPROFILE%\.nuget\packages
set RefTrimPkgBase=%NuGetPkgBase%\ReferenceTrimmer
for /d %%d in (%RefTrimPkgBase%\*) do set RefTrimPkg=%%d
set MSBuildLoggerParam=-distributedlogger:CentralLogger,%RefTrimPkg%\build\ReferenceTrimmer.Loggers.dll*ForwardingLogger,%RefTrimPkg%\build\ReferenceTrimmer.Loggers.dll

@echo Building with ReferenceTrimmer C# and C++ logger.
set cmd=msbuild %MSBuildLoggerParam% %*
echo %cmd%
call %cmd%
