param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$BaseUrl,
    [string]$Notes = '界面美化、额度和有效期配色、完整重置次数、导入成功自动关闭；保留切换二次确认。'
)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Executable
$version = $file.VersionInfo.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'EXE must have a four-part FileVersion' }
# Match the package's version instead of pinning future feeds to an old release.
# A zero revision uses the public three-part tag; nonzero revisions stay explicit.
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $parsedVersion = [Version]::Parse($version)
    $releaseVersion = if ($parsedVersion.Revision -eq 0) { $parsedVersion.ToString(3) } else { $parsedVersion.ToString() }
    $BaseUrl = "https://github.com/evenstar-2273/star-codex-switcher/releases/download/v$releaseVersion"
}
$uri = [Uri]$BaseUrl
if ($uri.Scheme -ne 'https') { throw 'HTTPS is required' }
$feed = [ordered]@{
    version = $version
    platform = 'windows-x64'
    package_type = 'portable'
    download_url = $BaseUrl.TrimEnd('/') + '/' + [Uri]::EscapeDataString($file.Name)
    sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    notes = $Notes
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output), ($feed | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $Output
