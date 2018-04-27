Param(
  [string]$configuration = 'Release',
  [string]$runtime = 'win10-x64',
  [string[]]$frameworks = @("netcoreapp2.0","net461"),
  [string]$output = (Join-Path (Get-Location) 'Publish'),
  [string]$version = '1.2.0'
)

Remove-Item -Path $output -Recurse -Force -ErrorAction SilentlyContinue

foreach ($framework in $frameworks) {
  Write-Host "Publishing for $framework"
  dotnet publish src\ReferenceTrimmer.csproj -c $configuration -r $runtime -f $framework -o $output\$framework -p:AssemblyVersion=$version
  Compress-Archive -Path $output\$framework\* -DestinationPath $output\ReferenceTrimmer-$version-$framework.zip
}
