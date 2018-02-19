Param(
  [string]$configuration = 'Release',
  [string]$runtime = 'win10-x64',
  [string]$output = (Join-Path (Get-Location) 'Publish')
)

Remove-Item -Path $output -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish src\ReferenceTrimmer.csproj -c $configuration -r $runtime -o $output

Compress-Archive -Path $output\* -DestinationPath $output\ReferenceTrimmer.zip
