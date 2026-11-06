param(
    [Parameter(Mandatory = $true)][string]$SwiftRuntimeDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'windows/artifacts/guarded' }
$artifactRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
# Package the v0.1.27 host and its runtime dependencies.
# Only allow the runtime files and notices into the archive, never accounts or user settings.
foreach ($required in @('SwitcherHost.exe','swiftCore.dll','Foundation.dll','vcruntime140.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $SwiftRuntimeDirectory $required))) { throw "Missing runtime: $required" }
}
$buildRoot = Join-Path $projectRoot 'windows/artifacts/guarded-builds'
$stage = Join-Path $buildRoot ([Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Get-ChildItem -LiteralPath $SwiftRuntimeDirectory -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -eq 'SwitcherHost.exe' -or $_.Name -like '*LICENSE*' -or $_.Name -like '*NOTICES*'
} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtime }
$archive = Join-Path $stage 'SwiftRuntime.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($runtime, $archive)
$publish = Join-Path $stage 'publish'
& $Dotnet publish (Join-Path $projectRoot 'windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj') `
    -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    "-p:SwiftRuntimeArchive=$archive" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Guarded build failed' }
$app = Join-Path $publish 'Star-Codex-Switcher-windows-x64.exe'
$version = [Version]::Parse((Get-Item -LiteralPath $app).VersionInfo.FileVersion)
$releaseVersion = if ($version.Revision -eq 0) { $version.ToString(3) } else { $version.ToString() }
$name = "Star-Codex-Switcher-$releaseVersion-Portable-x64.exe"
$destination = Join-Path $artifactRoot $name
Copy-Item -LiteralPath $app -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($destination + '.sha256', "$hash  $name`n", [Text.UTF8Encoding]::new($false))
# Keep private build intermediates under the source tree, never in the deliverables folder.
Write-Output $destination
