<#
  Builds and tests the solution under SonarScanner for .NET and sends the analysis, with test
  results and coverage, to the SonarQube server. The same script runs in CI
  (.github/workflows/sonarqube.yml) and on a developer's machine.

  Reads SONAR_HOST_URL and SONAR_TOKEN from the environment. Locally they come from 1Password:
    op run --env-file sonar.env -- pwsh tools/sonar.ps1
#>
$ErrorActionPreference = 'Stop'

foreach ($name in 'SONAR_HOST_URL', 'SONAR_TOKEN') {
  if (-not [Environment]::GetEnvironmentVariable($name)) {
    throw "$name is not set. Run this through 'op run --env-file sonar.env -- ...'."
  }
}

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
  # The scanner fetches a Java runtime from the server it reports to (SonarQube 10.6 and later).
  # 'update' installs it when missing and leaves a matching version alone.
  dotnet tool update --global dotnet-sonarscanner --version 11.3.0
  if ($LASTEXITCODE) { throw 'Installing SonarScanner for .NET failed.' }

  # Stale results from an earlier run would be reported as this one's.
  $results = 'tests/PPObjectSearch.Tests/TestResults'
  if (Test-Path $results) { Remove-Item $results -Recurse -Force }

  # Everything built between begin and end is analysed. The views' code-behind only wires
  # controls to their view models and needs a live window, so it is left out of coverage;
  # it is still analysed for issues.
  dotnet sonarscanner begin `
    /k:"tmnrtn_PPObjectSearch" `
    /d:sonar.host.url="$env:SONAR_HOST_URL" `
    /d:sonar.token="$env:SONAR_TOKEN" `
    /d:sonar.cs.opencover.reportsPaths="$results/**/coverage.opencover.xml" `
    /d:sonar.cs.vstest.reportsPaths="$results/*.trx" `
    /d:sonar.coverage.exclusions="Views/**,**/*.xaml.cs"
  if ($LASTEXITCODE) { throw 'SonarScanner begin failed.' }

  dotnet restore PPObjectSearch.slnx --locked-mode
  if ($LASTEXITCODE) { throw 'Restore failed.' }

  dotnet build PPObjectSearch.slnx -c Release --no-restore
  if ($LASTEXITCODE) { throw 'Build failed.' }

  # OpenCover, the coverage format SonarQube reads for C#. A failing test still lets the
  # analysis through, so the server sees the build's issues; the script fails afterwards.
  dotnet test tests/PPObjectSearch.Tests -c Release --no-build `
    --logger "trx;LogFileName=results.trx" `
    --collect:"XPlat Code Coverage;Format=opencover" `
    --results-directory $results
  $testExit = $LASTEXITCODE

  dotnet sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"
  if ($LASTEXITCODE) { throw 'SonarScanner end failed.' }

  if ($testExit) { throw 'Tests failed.' }
}
finally {
  Pop-Location
}
