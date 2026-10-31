$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required. Install it, then rerun this launcher.'
}

$repositoryRoot = $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'tools/run-me/RunMe.csproj'
Push-Location $repositoryRoot
try {
    & dotnet run --project $projectPath -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}