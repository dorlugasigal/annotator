<#
.SYNOPSIS
  Builds Annotator's MSIX packages and bundle (WIN-DST-01, WIN-DST-04) and checks their manifest and contents.
.DESCRIPTION
  Builds the single-project MSIX of Annotator.App with Native AOT for each architecture (-p:AnnotatorPackage=true), then
  bundles them with MakeAppx. MakeAppx validates every manifest against the schema while it packs. The script then checks
  each package: the Identity version is (Major + 1).Minor.Build.0 of the shared version in apps/macos/project.yml, the
  architecture, the capabilities, the startup task and the .annotator file type, the native executable and resource
  index, every logo, and no symbols or .NET host files. When apps/windows/out/app exists (validate.ps1 app), the payload
  must equal that validated publish, apart from the package's own files.

  ARM64 needs Visual Studio's ARM64 build tools (Microsoft.VisualStudio.Component.VC.Tools.ARM64). Without them the
  script builds x64 only and says so, unless -RequireArm64 asks for both (the release publisher does).

  -MetricsUrl gives the build the usage-count service's origin, as the app build takes it (-p:AnnotatorMetricsUrl, the
  Mac's ANNOTATOR_METRICS_URL): an HTTPS root such as https://annotator.example, checked with the app's own rule
  (UsageMetrics.ValidatedEndpoint), recorded in package-report.json and found in each package's Annotator.exe. Without it
  the build has none. Either way the app sends nothing while UsageMetrics.ServiceAcceptsWindows is off.

  Output, in <Output>\<package version>\:
    Annotator_<version>_x64.msix, Annotator_<version>_arm64.msix (when built), Annotator_<version>.msixbundle,
    layout-x64\ (the unpacked x64 package, for Add-AppxPackage -Register in Developer Mode) and package-report.json.
  The packages are unsigned: the Store signs what it publishes. Nothing is installed, signed or uploaded.

  -Portable also builds the portable release for people without the Microsoft Store (WIN-DST-09): the Native AOT app
  of each architecture, published with -p:AnnotatorDistribution=portable, zipped as
  Annotator-<version>-windows-<architecture>.zip with Annotator.exe at the root, the file winget installs as a portable
  app (tools/windows/winget.py). Each ZIP's SHA-256 is in package-report.json.

  -UpdateSite gives the portable app the website that publishes its update feed (WIN-SYS-03), as the Mac's
  ANNOTATOR_UPDATE_FEED_URL: an HTTPS root such as https://getannotator.app. The portable app then reads
  <site>/updates/windows-stable.json and only says where to get a newer release. Without it the portable app can't check
  and says how to update with winget.
.EXAMPLE
  pwsh tools/windows/package.ps1
  pwsh tools/windows/package.ps1 -Portable
  pwsh tools/windows/package.ps1 -Portable -UpdateSite https://getannotator.app
  pwsh tools/windows/package.ps1 -RequireArm64 -BuildStamp "Release 0123abc" -MetricsUrl https://annotator.example
#>
param(
    [string]$Output,
    [string]$BuildStamp = '',
    [string]$MetricsUrl = '',
    [string]$UpdateSite = '',
    [switch]$RequireArm64,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windows = Join-Path $root 'apps\windows'
$project = Join-Path $windows 'src\Annotator.App\Annotator.App.csproj'
if (-not $Output) {
    $Output = Join-Path $windows 'out\package'
}

# The shared version, read like tools/release/release.py (a second, independent reading of SharedVersion.props): a plain
# X.Y.Z, its build derived as major * 10000 + minor * 100 + patch, and the package version (major + 1).minor.patch.0.
function Get-SharedVersion {
    $text = Get-Content (Join-Path $root 'apps\macos\project.yml') -Raw
    if ($text -match '(?m)^ *ANNOTATOR_PRERELEASE:') {
        throw 'Remove ANNOTATOR_PRERELEASE from apps/macos/project.yml: releases have no pre-release labels.'
    }
    $values = @{}
    foreach ($name in 'MARKETING_VERSION', 'CURRENT_PROJECT_VERSION') {
        $found = [regex]::Matches($text, "(?m)^    ${name}: *(.*?) *\r?$")
        if ($found.Count -ne 1) {
            throw "Expected one base $name setting in apps/macos/project.yml."
        }
        $values[$name] = $found[0].Groups[1].Value.Trim('"', "'")
    }
    $version = [regex]::Match($values.MARKETING_VERSION, '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)$')
    if (-not $version.Success) {
        throw "The shared version $($values.MARKETING_VERSION) in apps/macos/project.yml is not a plain X.Y.Z with minor and patch at 99 or lower."
    }
    $major, $minor, $patch = [int]$version.Groups[1].Value, [int]$version.Groups[2].Value, [int]$version.Groups[3].Value
    $build = $major * 10000 + $minor * 100 + $patch
    if ($values.CURRENT_PROJECT_VERSION -ne "$build") {
        throw "CURRENT_PROJECT_VERSION in apps/macos/project.yml must be $build, derived from $($values.MARKETING_VERSION)."
    }
    [pscustomobject]@{
        Label = $values.MARKETING_VERSION
        Msix = '{0}.{1}.{2}.0' -f ($major + 1), $minor, $patch
    }
}

# The portable app's update feed for -UpdateSite: <site>/updates/windows-stable.json, an HTTPS root without credentials,
# port, query or fragment (the app's own rule, ReleaseFeed.Address, checks it again).
function Get-UpdateFeed([string]$site) {
    $trimmed = $site.TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($trimmed, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or
        -not $uri.IsDefaultPort -or $uri.Query -or $uri.Fragment) {
        throw "-UpdateSite must be an HTTPS website root such as https://getannotator.app, not '$site'."
    }
    "$trimmed/updates/windows-stable.json"
}

function Get-MakeAppx {
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    # The Windows SDK build tools that the MSIX tooling itself uses (a dependency of the Windows App SDK packages).
    $candidate = Get-ChildItem (Join-Path $packages 'microsoft.windows.sdk.buildtools') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'bin') -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue } |
        Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $candidate) {
        throw 'MakeAppx.exe was not found in the Microsoft.Windows.SDK.BuildTools package. Run validate.ps1 app first so NuGet restores it.'
    }
    $candidate.FullName
}

function Test-Arm64Tools {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        return $false
    }
    $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath
    [bool]$path
}

function Invoke-PackageBuild([string]$architecture, [string]$folder) {
    $platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    $scratch = Join-Path $folder "msbuild-$architecture"
    $arguments = @('build', $project, '-c', 'Release', '-r', "win-$architecture", "-p:Platform=$platform", '-nologo', '-warnaserror',
        '-p:AnnotatorPackage=true', "-p:AppxPackageDir=$scratch\",
        # A separate intermediate folder, so package builds and development builds never share generated files.
        "-p:IntermediateOutputPath=obj\package\$architecture\", "-p:OutputPath=bin\package\$architecture\")
    if ($BuildStamp) {
        $arguments += "-p:AnnotatorBuildStamp=$BuildStamp"
    }
    if ($MetricsUrl) {
        $arguments += "-p:AnnotatorMetricsUrl=$MetricsUrl"
    }
    Write-Host "== Build the $architecture package (Native AOT)" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $log = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $warnings = @($log | Where-Object { "$_" -match ':\s+warning\s' })
    if ($exitCode -ne 0 -or $warnings.Count -gt 0) {
        $log | ForEach-Object { Write-Host "   $_" }
        throw "The $architecture package build failed (exit code $exitCode, $($warnings.Count) warning(s))."
    }
    $msix = Get-ChildItem $scratch -Recurse -Filter *.msix | Select-Object -First 1
    if (-not $msix) {
        throw "The $architecture package build produced no .msix in $scratch."
    }
    Write-Host ('   built in {0:F1} s' -f $clock.Elapsed.TotalSeconds)
    $msix.FullName
}

function Invoke-PortableBuild([string]$architecture, [string]$folder, [string]$label, [string]$feed) {
    $platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publish = Join-Path $folder "portable-$architecture"
    $arguments = @('publish', $project, '-c', 'Release', '-r', "win-$architecture", "-p:Platform=$platform", '-o', $publish, '-nologo',
        '-warnaserror', '-p:AnnotatorDistribution=portable',
        "-p:IntermediateOutputPath=obj\portable\$architecture\", "-p:OutputPath=bin\portable\$architecture\")
    if ($BuildStamp) {
        $arguments += "-p:AnnotatorBuildStamp=$BuildStamp"
    }
    if ($MetricsUrl) {
        $arguments += "-p:AnnotatorMetricsUrl=$MetricsUrl"
    }
    if ($feed) {
        $arguments += "-p:AnnotatorUpdateFeedUrl=$feed"
    }
    Write-Host "== Build the portable $architecture app (Native AOT)" -ForegroundColor Cyan
    $log = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $warnings = @($log | Where-Object { "$_" -match ':\s+warning\s' })
    if ($exitCode -ne 0 -or $warnings.Count -gt 0) {
        $log | ForEach-Object { Write-Host "   $_" }
        throw "The portable $architecture build failed (exit code $exitCode, $($warnings.Count) warning(s))."
    }
    Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force
    foreach ($required in 'Annotator.exe', 'Annotator.pri', 'notes.json', 'annotator\SKILL.md') {
        Assert-That (Test-Path (Join-Path $publish $required)) "The portable $architecture app lacks $required."
    }
    $exeText = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes((Join-Path $publish 'Annotator.exe')))
    Assert-That ($exeText.Contains('AnnotatorDistribution', [StringComparison]::Ordinal) -and $exeText.Contains('portable', [StringComparison]::Ordinal)) "The portable $architecture Annotator.exe is not marked portable."
    if ($feed) {
        Assert-That ($exeText.Contains($feed, [StringComparison]::Ordinal)) "The portable $architecture Annotator.exe lacks its update feed $feed."
    }
    $zip = Join-Path $folder "Annotator-$label-windows-$architecture.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $publish -Recurse -Force
    [ordered]@{ architecture = $architecture; file = (Split-Path $zip -Leaf); bytes = (Get-Item $zip).Length; sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Read-Package([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $files = @($zip.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName).Replace('/', '\') })
        $reader = New-Object IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        $manifest = [xml]$reader.ReadToEnd()
        $reader.Dispose()
    }
    finally {
        $zip.Dispose()
    }
    [pscustomobject]@{ Files = $files; Manifest = $manifest }
}

# Whether a file in the package holds $text in UTF-8, the way Native AOT keeps assembly metadata such as AnnotatorMetricsURL.
function Test-PackageText([string]$path, [string]$entryName, [string]$text) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.GetEntry($entryName)
        if (-not $entry) {
            return $false
        }
        $memory = New-Object IO.MemoryStream
        $stream = $entry.Open()
        try {
            $stream.CopyTo($memory)
        }
        finally {
            $stream.Dispose()
        }
        $latin = [Text.Encoding]::Latin1
        $latin.GetString($memory.ToArray()).Contains($latin.GetString([Text.Encoding]::UTF8.GetBytes($text)), [StringComparison]::Ordinal)
    }
    finally {
        $zip.Dispose()
    }
}

# Each check that fails is collected, so one run lists every problem.
$problems = [Collections.Generic.List[string]]::new()
function Assert-That([bool]$condition, [string]$message) {
    if (-not $condition) {
        $problems.Add($message)
        Write-Host "   FAIL $message" -ForegroundColor Red
    }
}

function Test-Package([string]$path, [string]$architecture, [string]$version) {
    $package = Read-Package $path
    $m = New-Object Xml.XmlNamespaceManager($package.Manifest.NameTable)
    $m.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $m.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $m.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $m.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
    $m.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
    $m.AddNamespace('uap11', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/11')
    $m.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $xml = $package.Manifest
    $identity = $xml.SelectSingleNode('/m:Package/m:Identity', $m)
    Assert-That ($identity.Version -eq $version) "$architecture package version is $($identity.Version), expected $version."
    Assert-That ($identity.ProcessorArchitecture -eq $architecture) "$architecture package says architecture $($identity.ProcessorArchitecture)."
    $application = $xml.SelectSingleNode('/m:Package/m:Applications/m:Application', $m)
    Assert-That ($application.Executable -eq 'Annotator.exe' -and $application.EntryPoint -eq 'Windows.FullTrustApplication') 'The application is not the full-trust Annotator.exe.'
    $minimum = $xml.SelectSingleNode('/m:Package/m:Dependencies/m:TargetDeviceFamily', $m)
    Assert-That ($minimum.Name -eq 'Windows.Desktop' -and $minimum.MinVersion -eq '10.0.22621.0') 'The package must target Windows.Desktop 10.0.22621.0 (Windows 11 22H2) or later.'
    $capabilities = @($xml.SelectNodes('/m:Package/m:Capabilities/*', $m) | ForEach-Object { $_.Name }) | Sort-Object
    Assert-That (($capabilities -join ',') -eq 'graphicsCaptureProgrammatic,graphicsCaptureWithoutBorder,runFullTrust') "Unexpected capabilities: $($capabilities -join ', ')."
    $task = $xml.SelectSingleNode('//uap5:Extension[@Category="windows.startupTask"]', $m)
    Assert-That ($null -ne $task -and $task.StartupTask.TaskId -eq 'AnnotatorStartup' -and $task.StartupTask.Enabled -eq 'false' -and
        $task.GetAttribute('Parameters', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -eq '--startup-task') 'The startup task must be AnnotatorStartup, off, with --startup-task.'
    $association = $xml.SelectSingleNode('//uap3:FileTypeAssociation', $m)
    Assert-That ($null -ne $association -and $association.SupportedFileTypes.FileType -eq '.annotator' -and $association.Parameters -eq '"%1"') 'The .annotator file type must pass the file path as "%1".'
    $files = $package.Files
    foreach ($required in 'Annotator.exe', 'resources.pri', 'AppxManifest.xml', 'AppxBlockMap.xml', 'Microsoft.ui.xaml.dll', 'notes.json', 'annotator\SKILL.md',
        'Assets\Lucide-LICENSE.txt', 'RoughJS-LICENSE.txt', 'Text\Unicode-LICENSE.txt') {
        Assert-That ($files -contains $required) "$architecture package lacks $required."
    }
    foreach ($forbidden in 'Annotator.dll', 'Annotator.pri', 'Annotator.deps.json', 'Annotator.runtimeconfig.json', 'Assets\icon-map.json', 'Package.appxmanifest') {
        Assert-That ($files -notcontains $forbidden) "$architecture package carries $forbidden."
    }
    Assert-That (@($files | Where-Object { $_ -like '*.pdb' }).Count -eq 0) "$architecture package carries symbols."
    if ($MetricsUrl) {
        Assert-That (Test-PackageText $path 'Annotator.exe' $MetricsUrl) "$architecture package's Annotator.exe lacks the usage-count service $MetricsUrl."
    }
    $logos = @(Get-ChildItem (Join-Path $windows 'src\Annotator.App\Assets\Package') -Filter *.png | ForEach-Object { "Assets\Package\$($_.Name)" })
    $missing = @($logos | Where-Object { $files -notcontains $_ })
    Assert-That ($logos.Count -gt 0 -and $missing.Count -eq 0) "$architecture package lacks logos: $($missing -join ', ')."
    $published = Join-Path $windows 'out\app'
    $comparison = 'not compared (run validate.ps1 app first)'
    if ($architecture -eq 'x64' -and (Test-Path (Join-Path $published 'Annotator.exe'))) {
        $own = @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri') + $logos
        $payload = @($files | Where-Object { $own -notcontains $_ })
        $development = @(Get-ChildItem $published -Recurse -File | ForEach-Object { $_.FullName.Substring($published.Length + 1) } |
            Where-Object { $_ -notlike '*.pdb' -and $_ -ne 'Annotator.pri' })
        $extra = @($payload | Where-Object { $development -notcontains $_ })
        $absent = @($development | Where-Object { $payload -notcontains $_ })
        Assert-That ($extra.Count -eq 0 -and $absent.Count -eq 0) "x64 payload differs from out\app: extra [$($extra -join ', ')], missing [$($absent -join ', ')]."
        $comparison = if ($extra.Count -eq 0 -and $absent.Count -eq 0) { "same $($payload.Count) files as out\app" } else { 'differs from out\app' }
    }
    [pscustomobject]@{ Files = $files.Count; Comparison = $comparison; Placeholder = ($identity.Name -like '*PLACEHOLDER*' -or $identity.Publisher -like '*PLACEHOLDER*') }
}

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
    $env:PATH = "$installer;$env:PATH"
}

# The app's rule for the usage-count service (UsageMetrics.ValidatedEndpoint): an HTTPS origin with nothing after it but "/".
# The app ignores any other value, so the build stops instead of shipping one that sends nothing.
if ($MetricsUrl -and $MetricsUrl -cnotmatch '^https://[A-Za-z0-9.-]+/?$') {
    throw "-MetricsUrl must be an HTTPS root such as https://annotator.example, with no port, user, path, query or fragment; '$MetricsUrl' is not."
}

$shared = Get-SharedVersion
$version = $shared.Msix
$feed = if ($UpdateSite) { Get-UpdateFeed $UpdateSite } else { '' }
if ($feed -and -not $Portable) {
    Write-Host '-UpdateSite only changes the portable app (-Portable); the Store packages get their updates from the Store.' -ForegroundColor Yellow
}
$folder = Join-Path $Output $version
if (Test-Path $folder) {
    Remove-Item $folder -Recurse -Force
}
New-Item -ItemType Directory -Force $folder | Out-Null
$makeappx = Get-MakeAppx
$architectures = @('x64')
if (Test-Arm64Tools) {
    $architectures += 'arm64'
}
elseif ($RequireArm64) {
    throw 'ARM64 packages need Visual Studio''s ARM64 build tools. In the Visual Studio Installer, modify Visual Studio and add "MSVC ... ARM64/ARM64EC build tools" (Microsoft.VisualStudio.Component.VC.Tools.ARM64), then run this again.'
}
else {
    Write-Host 'ARM64 skipped: Visual Studio''s ARM64 build tools are not installed (Microsoft.VisualStudio.Component.VC.Tools.ARM64).' -ForegroundColor Yellow
}

Push-Location $windows
try {
    $bundleInput = Join-Path $folder 'bundle-input'
    New-Item -ItemType Directory -Force $bundleInput | Out-Null
    $report = [ordered]@{ version = $version; label = $shared.Label; buildStamp = $BuildStamp; metricsUrl = $MetricsUrl; updateFeed = $feed; packages = @() }
    foreach ($architecture in $architectures) {
        $built = Invoke-PackageBuild $architecture $folder
        $name = "Annotator_${version}_$architecture.msix"
        $target = Join-Path $folder $name
        Copy-Item $built $target
        Copy-Item $built (Join-Path $bundleInput $name)
        $checks = Test-Package $target $architecture $version
        $report.packages += [ordered]@{
            architecture = $architecture; file = $name; bytes = (Get-Item $target).Length
            sha256 = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant(); files = $checks.Files; payload = $checks.Comparison
        }
        $report.placeholderIdentity = $checks.Placeholder
        Remove-Item (Join-Path $folder "msbuild-$architecture") -Recurse -Force
    }
    Write-Host '== Bundle the packages (MakeAppx)' -ForegroundColor Cyan
    $bundle = Join-Path $folder "Annotator_$version.msixbundle"
    $bundleLog = & $makeappx bundle /d $bundleInput /p $bundle /bv $version /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $bundleLog | ForEach-Object { Write-Host "   $_" }
        throw "MakeAppx could not bundle the packages (exit code $LASTEXITCODE)."
    }
    Remove-Item $bundleInput -Recurse -Force
    $report.bundle = [ordered]@{ file = (Split-Path $bundle -Leaf); bytes = (Get-Item $bundle).Length; sha256 = (Get-FileHash $bundle -Algorithm SHA256).Hash.ToLowerInvariant(); architectures = $architectures }
    Write-Host '== Unpack the x64 package for a Developer Mode test' -ForegroundColor Cyan
    $layout = Join-Path $folder 'layout-x64'
    $unpackLog = & $makeappx unpack /p (Join-Path $folder "Annotator_${version}_x64.msix") /d $layout /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $unpackLog | ForEach-Object { Write-Host "   $_" }
        throw "MakeAppx could not unpack the x64 package (exit code $LASTEXITCODE)."
    }
    if ($Portable) {
        $report.portable = @(foreach ($architecture in $architectures) { Invoke-PortableBuild $architecture $folder $shared.Label $feed })
    }
    $report.problems = @($problems)
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $folder 'package-report.json') -Encoding utf8NoBOM
}
finally {
    Pop-Location
}

foreach ($package in $report.packages) {
    Write-Host ('   {0}: {1:N2} MB, {2} files, payload {3}' -f $package.file, ($package.bytes / 1MB), $package.files, $package.payload)
}
foreach ($zip in @($report.portable)) {
    if ($zip) {
        Write-Host ('   {0}: {1:N2} MB, SHA-256 {2}' -f $zip.file, ($zip.bytes / 1MB), $zip.sha256)
    }
}
Write-Host ('   {0}: {1:N2} MB ({2})' -f $report.bundle.file, ($report.bundle.bytes / 1MB), ($architectures -join ', '))
if ($report.placeholderIdentity) {
    Write-Host '   The Store identity is still the PLACEHOLDER of Package.appxmanifest: fine for a local test, not for the Store.' -ForegroundColor Yellow
}
if ($problems.Count -gt 0) {
    throw "$($problems.Count) package check(s) failed; see package-report.json in $folder."
}
Write-Host "Packages in $folder" -ForegroundColor Green
