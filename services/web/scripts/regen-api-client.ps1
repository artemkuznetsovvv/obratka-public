# Regenerates the TypeScript OpenAPI client from the running Web API.
#
# Usage:
#   1. Start the backend: dotnet run --project backend/src/Obratka.WebApi
#   2. Run this script from any directory.
#
# The generated client lands in frontend/src/api/generated and is committed
# to git (we want the diff visible in PRs).

param(
    [string]$ApiUrl = 'http://localhost:5100/openapi/v1.json'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$frontendDir = Join-Path $repoRoot 'frontend'
$outputDir = Join-Path $frontendDir 'src/api/generated'

Write-Host "Generating TS client from $ApiUrl"
Write-Host "Output: $outputDir"

Push-Location $frontendDir
try {
    npm exec --no -- openapi-typescript-codegen `
        --input $ApiUrl `
        --output src/api/generated `
        --client axios `
        --useOptions `
        --useUnionTypes
}
finally {
    Pop-Location
}

Write-Host "Done. Review the diff and commit." -ForegroundColor Green
