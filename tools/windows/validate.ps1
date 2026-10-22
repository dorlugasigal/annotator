<#
.SYNOPSIS
  Local validation for the Windows edition. Runs on the owner's Windows machine; no hosted runners.
.DESCRIPTION
  core:      builds Annotator.Core and runs its tests (any OS with the .NET SDK).
  workspace: builds Annotator.Workspace and runs its tests (any OS with the .NET SDK).
  platform:  builds Annotator.Platform and runs its tests (Windows only; the tests create
             short-lived transparent windows and register an unused global shortcut).
  rendering: builds Annotator.Rendering and runs its tests (Windows only; pixel tests use the WARP device).
  conformance: builds Annotator.Conformance.Tests and compares the Windows port with the Mac conformance corpus in
             reference/conformance (any OS with the .NET SDK); every corpus file is checked against its manifest first.
  app:       builds Annotator.App and publishes it with Native AOT for win-x64 into apps/windows/out/app
             (Windows only); fails on any build or publish warning.
  all:       builds the whole Windows solution in Release, runs every test project (the conformance suite included),
             then runs the app suite.
  package:   builds the MSIX packages and bundle with Native AOT and checks their manifest and contents
             (tools/windows/package.ps1; x64, plus ARM64 when Visual Studio's ARM64 build tools are installed), after
             the tests of the owner-run Windows publisher (tools/windows/publish_windows.py; needs Python 3). Not part
             of all: it is a second Native AOT build for releases. Run app first to compare the payload with out/app.
  benchmarks: publishes apps/windows/benchmarks/Annotator.Benchmarks with Native AOT into apps/windows/out/benchmarks
             and runs the WIN-PERF-01 suite (the Mac's 34 workloads and the overlay presenter's frame work, offscreen)
             with -Samples and -Warmup into -Output (default build/performance/windows.json). Not part of all; run it
             on a quiet machine, because other builds and tests change the numbers.
  a11y:      builds the development app and runs the explicit accessibility checks of Annotator.App.Tests on every UX
             reference state: names and keyboard reach, a simulated contrast theme and 225% text (Windows only, needs
             a desktop session that renders; about ten minutes; the windows stay off screen and never activate).
  Reports toolchain differences from tools/windows/toolchain.json instead of installing anything.
.EXAMPLE
  pwsh tools/windows/validate.ps1 core
  pwsh tools/windows/validate.ps1 workspace
  pwsh tools/windows/validate.ps1 platform
  pwsh tools/windows/validate.ps1 rendering
  pwsh tools/windows/validate.ps1 conformance
  pwsh tools/windows/validate.ps1 app
  pwsh tools/windows/validate.ps1 all
  pwsh tools/windows/validate.ps1 package
  pwsh tools/windows/validate.ps1 benchmarks -Samples 21 -Warmup 3 -Output build/performance/windows.json
  pwsh tools/windows/validate.ps1 a11y
#>
param(
    [ValidateSet('core', 'workspace', 'platform', 'rendering', 'conformance', 'app', 'all', 'package', 'benchmarks', 'a11y')]
    [string]$Suite = 'all',
    [ValidateRange(3, 101)]
    [int]$Samples = 21,
    [ValidateRange(1, 20)]
    [int]$Warmup = 3,
    [string]$Output = 'build/performance/windows.json'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolchain = Get-Content (Join-Path $root 'tools\windows\toolchain.json') -Raw | ConvertFrom-Json

$sdk = (& dotnet --version).Trim()
if ($sdk -ne $toolchain.dotnetSdk) {
    Write-Warning "dotnet SDK $sdk differs from the pinned $($toolchain.dotnetSdk) (tools/windows/toolchain.json)."
}

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "== $name" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $action
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE"
    }
    Write-Host ("   {0} passed in {1:F1} s" -f $name, $clock.Elapsed.TotalSeconds)
}

# Visual Studio 2026's vcvarsall calls vswhere through PATH, so the installer folder must be on PATH for the Native AOT
# link step (Gate 0 S-12).
function Add-VisualStudioInstallerToPath {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
        $env:PATH = "$installer;$env:PATH"
    }
}

# Native AOT publishing of the app, failing on any warning.
function Invoke-AppPublish {
    Add-VisualStudioInstallerToPath
    Invoke-Step 'Build Annotator.App' { dotnet build src/Annotator.App/Annotator.App.csproj -c Release -p:Platform=x64 -nologo -v q -warnaserror }
    Invoke-Step 'Publish Annotator.App (Native AOT, win-x64)' {
        # Start from an empty folder so the checks below never see files of an earlier publish.
        if (Test-Path out/app) {
            Remove-Item out/app -Recurse -Force -ErrorAction Stop
        }
        $log = dotnet publish src/Annotator.App/Annotator.App.csproj -c Release -r win-x64 -o out/app -nologo -warnaserror 2>&1
        $exitCode = $LASTEXITCODE
        $log | ForEach-Object { Write-Host "   $_" }
        $warnings = @($log | Where-Object { "$_" -match ':\s+warning\s' })
        if ($warnings.Count -gt 0) {
            Write-Host ("   Native AOT publish reported {0} warning(s)." -f $warnings.Count) -ForegroundColor Red
            $global:LASTEXITCODE = 1
        }
        else {
            $global:LASTEXITCODE = $exitCode
        }
    }
    $exe = Join-Path (Get-Location) 'out\app\Annotator.exe'
    if (-not (Test-Path $exe)) {
        throw "Native AOT publish did not produce $exe"
    }
    # App.xaml lives in the app's resource index; without it the published app has no styles and stops at start.
    $pri = Join-Path (Get-Location) 'out\app\Annotator.pri'
    if (-not (Test-Path $pri)) {
        throw "Native AOT publish did not produce $pri"
    }
    Write-Host ("   {0} ({1:N2} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
}

# Tests use Microsoft.Testing.Platform, opted in by apps/windows/global.json, which dotnet test
# reads from the working directory.
Push-Location (Join-Path $root 'apps\windows')
try {

switch ($Suite) {
    'core' {
        Invoke-Step 'Build Annotator.Core' { dotnet build src/Annotator.Core/Annotator.Core.csproj -c Release -nologo -v q }
        Invoke-Step 'Test Annotator.Core' { dotnet test --project tests/Annotator.Core.Tests/Annotator.Core.Tests.csproj -c Release }
    }
    'workspace' {
        Invoke-Step 'Build Annotator.Workspace' { dotnet build src/Annotator.Workspace/Annotator.Workspace.csproj -c Release -nologo -v q }
        Invoke-Step 'Test Annotator.Workspace' { dotnet test --project tests/Annotator.Workspace.Tests/Annotator.Workspace.Tests.csproj -c Release }
    }
    'platform' {
        Invoke-Step 'Build Annotator.Platform' { dotnet build src/Annotator.Platform/Annotator.Platform.csproj -c Release -nologo -v q }
        Invoke-Step 'Test Annotator.Platform' { dotnet test --project tests/Annotator.Platform.Tests/Annotator.Platform.Tests.csproj -c Release }
    }
    'rendering' {
        Invoke-Step 'Build Annotator.Rendering' { dotnet build src/Annotator.Rendering/Annotator.Rendering.csproj -c Release -nologo -v q }
        Invoke-Step 'Test Annotator.Rendering' { dotnet test --project tests/Annotator.Rendering.Tests/Annotator.Rendering.Tests.csproj -c Release }
    }
    'conformance' {
        Invoke-Step 'Build Annotator.Conformance.Tests' { dotnet build tests/Annotator.Conformance.Tests/Annotator.Conformance.Tests.csproj -c Release -nologo -v q }
        Invoke-Step 'Test Annotator.Conformance.Tests' { dotnet test --project tests/Annotator.Conformance.Tests/Annotator.Conformance.Tests.csproj -c Release }
    }
    'app' {
        Invoke-AppPublish
    }
    'all' {
        Invoke-Step 'Build solution' { dotnet build Annotator.Windows.slnx -c Release -nologo -v q }
        Invoke-Step 'Test solution' { dotnet test --solution Annotator.Windows.slnx -c Release }
        Invoke-AppPublish
    }
    'package' {
        Invoke-Step 'Test the Windows publisher' { python (Join-Path $PSScriptRoot 'test_publish_windows.py') }
        & (Join-Path $PSScriptRoot 'package.ps1')
    }
    'benchmarks' {
        Add-VisualStudioInstallerToPath
        Invoke-Step 'Publish Annotator.Benchmarks (Native AOT, win-x64)' {
            if (Test-Path out/benchmarks) {
                Remove-Item out/benchmarks -Recurse -Force -ErrorAction Stop
            }
            $log = dotnet publish benchmarks/Annotator.Benchmarks/Annotator.Benchmarks.csproj -c Release -r win-x64 -o out/benchmarks -nologo -warnaserror 2>&1
            $exitCode = $LASTEXITCODE
            $warnings = @($log | Where-Object { "$_" -match ':\s+warning\s' })
            if ($exitCode -ne 0 -or $warnings.Count -gt 0) {
                $log | ForEach-Object { Write-Host "   $_" }
            }
            $global:LASTEXITCODE = if ($warnings.Count -gt 0) { 1 } else { $exitCode }
        }
        # The suite runs from the repository root, fingerprints the measured sources and starts one worker per scenario;
        # each worker has its own time limit, and so does the whole run.
        $benchmark = Join-Path (Get-Location) 'out\benchmarks\Annotator.Benchmarks.exe'
        Push-Location $root
        try {
            Invoke-Step 'Run the benchmark suite' {
                $run = Start-Process -FilePath $benchmark -ArgumentList '--samples', $Samples, '--warmup', $Warmup, '--output', $Output -NoNewWindow -PassThru
                if (-not $run.WaitForExit(3 * 60 * 60 * 1000)) {
                    $run.Kill($true)
                    throw 'The benchmark suite did not finish within 3 hours and was stopped.'
                }
                $global:LASTEXITCODE = $run.ExitCode
            }
        }
        finally {
            Pop-Location
        }
    }
    'a11y' {
        Invoke-Step 'Build Annotator.App (development)' { dotnet build src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64 -nologo -v q -warnaserror }
        Invoke-Step 'Build Annotator.App.Tests' { dotnet build tests/Annotator.App.Tests/Annotator.App.Tests.csproj -c Debug -nologo -v q }
        Invoke-Step 'Accessibility checks' { dotnet test --project tests/Annotator.App.Tests/Annotator.App.Tests.csproj -c Debug --no-build -- --explicit only }
    }
}
}
finally {
    Pop-Location
}
Write-Host "Validation suite '$Suite' passed." -ForegroundColor Green
