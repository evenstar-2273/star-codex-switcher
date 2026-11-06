param(
 [Parameter(Mandatory=$true)][string]$SwiftRuntimeDirectory,
 [Parameter(Mandatory=$true)][string]$Dotnet,
 [Parameter(Mandatory=$true)][string]$Makensis,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [string]$GitHubRepository = 'evenstar-2273/star-codex-switcher',
 [string]$ReleaseTag,
 [string]$BaseUrl
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$stage=Join-Path $projectRoot ('windows\artifacts\installed-builds\'+[Guid]::NewGuid().ToString('N'))
$runtime=Join-Path $stage 'runtime'
New-Item -ItemType Directory -Path $runtime,$OutputDirectory -Force | Out-Null
foreach($required in @('SwitcherHost.exe','swiftCore.dll','Foundation.dll','vcruntime140.dll')){if(!(Test-Path -LiteralPath (Join-Path $SwiftRuntimeDirectory $required))){throw "Missing runtime $required"}}
Get-ChildItem -LiteralPath $SwiftRuntimeDirectory -File | Where-Object {$_.Extension -eq '.dll' -or $_.Name -eq 'SwitcherHost.exe' -or $_.Name -like '*LICENSE*' -or $_.Name -like '*NOTICES*'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $runtime}
$archive=Join-Path $stage 'SwiftRuntime.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($runtime,$archive)
$publish=Join-Path $stage 'publish'
& $Dotnet publish (Join-Path $projectRoot 'windows\CodexAccountSwitcher\CodexAccountSwitcher.csproj') -c Release -r win-x64 --self-contained true -o $publish -p:InstalledEdition=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:SwiftRuntimeArchive=$archive" --nologo
if($LASTEXITCODE -ne 0){throw 'Installed build failed'}
$app=Join-Path $publish 'Star-Codex-Switcher-windows-x64.exe'
$fileVersion=(Get-Item -LiteralPath $app).VersionInfo.FileVersion
$parsedVersion=[Version]::Parse($fileVersion)
$releaseVersion=if($parsedVersion.Revision -eq 0){$parsedVersion.ToString(3)}else{$parsedVersion.ToString()}
if (!$ReleaseTag) { $ReleaseTag = 'v' + $releaseVersion }
if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $ReleaseTag -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository or release tag' }
if (!$BaseUrl) { $BaseUrl = "https://github.com/$GitHubRepository/releases/download/$ReleaseTag" }
$baseUri = [Uri]$BaseUrl
if (!$baseUri.IsAbsoluteUri -or $baseUri.Scheme -ne 'https' -or $baseUri.Query -or $baseUri.Fragment -or $baseUri.UserInfo) { throw 'BaseUrl must be an absolute HTTPS download directory' }
$BaseUrl = $BaseUrl.TrimEnd('/')
$setup=Join-Path $OutputDirectory ("Star-Codex-Switcher-$releaseVersion-Setup-x64.exe")
& $Makensis /INPUTCHARSET UTF8 /V2 "/DAPP_VERSION=$fileVersion" "/DAPP_EXE=$app" "/DOUTPUT_FILE=$setup" (Join-Path $projectRoot 'windows\installer\setup.nsi')
if($LASTEXITCODE -ne 0){throw 'NSIS build failed'}
$hash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($setup+'.sha256',"$hash  $([IO.Path]::GetFileName($setup))`n",[Text.UTF8Encoding]::new($false))
$feed=[ordered]@{version=$fileVersion;platform='windows-x64';package_type='installer';download_url=$BaseUrl+'/'+[IO.Path]::GetFileName($setup);sha256=$hash;app_sha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant();notes="${releaseVersion}：当前使用账号自动置顶，其余账号保持原顺序。新增额度自动刷新设置，默认5分钟，可选禁用、2/5/10/15分钟或自定义1–1440分钟；自定义整数按回车或失焦保存，非法输入不保存。设置即时生效、重启保留，托盘驻留时继续执行；禁用只停止定时刷新，首次加载、手动刷新和自动重置仍保留。刷新失败保留上次额度，卡片直接区分网络或代理故障、超时、登录失效和限流。"}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'update-installed.json'),($feed|ConvertTo-Json)+"`n",[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $projectRoot 'windows\artifacts\latest-installed-build.txt'),$publish)
Write-Output $setup
