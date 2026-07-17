[CmdletBinding()]
param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$testResults = Join-Path $repoRoot "TestResults"
$pythonResults = Join-Path $testResults "python"
$dotnetResults = Join-Path $testResults "dotnet"

function Invoke-GateCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host "`n==> $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

foreach ($resultDirectory in @($pythonResults, $dotnetResults)) {
    if (Test-Path -LiteralPath $resultDirectory) {
        Remove-Item -LiteralPath $resultDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resultDirectory | Out-Null
}

Push-Location $repoRoot
try {
    Invoke-GateCommand "Python dependency check" {
        & $Python -m pip check
    }
    Invoke-GateCommand "Python tests" {
        & $Python -m pytest -q --junitxml="$pythonResults/pytest.xml"
    }
    Invoke-GateCommand "NuGet restore and vulnerability audit" {
        dotnet restore GoalKeeper.sln --verbosity minimal
    }
    Invoke-GateCommand ".NET format verification" {
        dotnet format GoalKeeper.sln --verify-no-changes --no-restore --verbosity minimal
    }
    Invoke-GateCommand ".NET compiler and analyzer build" {
        dotnet build GoalKeeper.sln --configuration Release --no-restore --verbosity minimal
    }
    Invoke-GateCommand "Complete .NET solution test suite" {
        dotnet test GoalKeeper.sln `
            --configuration Release `
            --no-build `
            --maxcpucount:1 `
            --logger trx `
            --results-directory $dotnetResults
    }
    Invoke-GateCommand "Test-count regression check" {
        & $Python scripts/verify-test-counts.py `
            --python-results "$pythonResults/pytest.xml" `
            --dotnet-results $dotnetResults
    }
}
finally {
    Pop-Location
}
