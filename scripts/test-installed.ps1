param(
 [Parameter(Mandatory=$true)][string]$AppExecutable,
 [Parameter(Mandatory=$true)][string]$PreviousExecutable,
 [Parameter(Mandatory=$true)][string]$Makensis,
 [Parameter(Mandatory=$true)][string]$Dotnet,
 [Parameter(Mandatory=$true)][string]$TestDirectory
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$testRoot=[IO.Path]::GetFullPath($TestDirectory)
if(Test-Path -LiteralPath $testRoot){throw 'TestDirectory must not already exist; each run is isolated.'}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$id=[Guid]::NewGuid().ToString('N')
$registryKey='Software\Microsoft\Windows\CurrentVersion\Uninstall\StarCodexInstallerTest-'+$id
$registryPath='HKCU:\'+$registryKey
$shortcut='Star Codex Installer Test '+$id
$legacyShortcut='Star Codex Legacy Installer Test '+$id
$setup=Join-Path $testRoot 'test-setup.exe'
$version=(Get-Item -LiteralPath $AppExecutable).VersionInfo.FileVersion
& $Makensis /INPUTCHARSET UTF8 /V2 /DTEST_MODE=1 "/DREGKEY=$registryKey" "/DSHORTCUT=$shortcut" "/DLEGACY_SHORTCUT=$legacyShortcut" "/DAPP_VERSION=$version" "/DAPP_EXE=$([IO.Path]::GetFullPath($AppExecutable))" "/DOUTPUT_FILE=$setup" (Join-Path $projectRoot 'windows\installer\setup.nsi')
if($LASTEXITCODE -ne 0){throw 'NSIS test build failed'}
$priorEnvironment=@{}
foreach($name in @('CODEX_SWITCHER_DATA_HOME','CODEX_HOME','CODEX_CLI_PATH')){$priorEnvironment[$name]=[Environment]::GetEnvironmentVariable($name)}
$env:CODEX_SWITCHER_DATA_HOME=Join-Path $testRoot '账号数据'
$env:CODEX_HOME=Join-Path $testRoot 'codex'
$env:CODEX_CLI_PATH=Join-Path $testRoot 'no-cli.exe'
New-Item -ItemType Directory -Path $env:CODEX_SWITCHER_DATA_HOME,$env:CODEX_HOME | Out-Null
$sentinel=Join-Path $env:CODEX_SWITCHER_DATA_HOME 'preserve.txt'
[IO.File]::WriteAllText($sentinel,'do not delete user accounts')
$codexSentinel=Join-Path $env:CODEX_HOME 'auth.json'
[IO.File]::WriteAllText($codexSentinel,'{"test_only":true}')
$install=Join-Path $testRoot '安装 程序'
$app=Join-Path $install 'StarCodexSwitcher.exe'
$uninstaller=Join-Path $install 'Uninstall.exe'
$desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) ($shortcut+'.lnk')
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) ($shortcut+'.lnk')
function Run-Process([string]$exe,[string]$arguments,[int]$expected=0){
 $si=[Diagnostics.ProcessStartInfo]::new($exe,$arguments)
 $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.WindowStyle='Hidden'
 $p=[Diagnostics.Process]::Start($si)
 if(!$p.WaitForExit(120000)){throw ('Test process timed out: '+$exe)}
 if($p.ExitCode -ne $expected){throw ('Test process exit '+$p.ExitCode+', expected '+$expected+': '+$exe)}
 $p.Dispose()
}
function Assert([bool]$ok,[string]$message){if(!$ok){throw $message}}
try {
 Run-Process $setup ('/S /D='+$install)
 Assert ((Test-Path -LiteralPath $app) -and (Test-Path -LiteralPath $uninstaller)) 'Missing installed files'
 Assert ((Get-ItemProperty -LiteralPath $registryPath).DisplayVersion -eq $version) 'Missing installed version'
 Assert ((Test-Path -LiteralPath $desktop) -and (Test-Path -LiteralPath $menu)) 'Missing shortcuts'
 Remove-Item -LiteralPath $desktop
 Run-Process $setup ('/S /UPDATE /D='+$install)
 Assert (!(Test-Path -LiteralPath $desktop)) 'Update recreated deleted desktop shortcut'
 Assert (Test-Path -LiteralPath $menu) 'Update removed existing menu shortcut'
 # Only this process's synthetic account directory participates in the IPC handshake.
 $start=[Diagnostics.ProcessStartInfo]::new($app,'--background')
 $start.UseShellExecute=$false;$start.WindowStyle='Hidden'
 $running=[Diagnostics.Process]::Start($start)
 Start-Sleep -Seconds 4
 Run-Process $app '--prepare-install'
 Assert ($running.WaitForExit(10000)) 'Graceful installer handoff left app running'
 $running.Dispose()
 $before=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
 Set-ItemProperty -LiteralPath $registryPath -Name DisplayVersion -Value '99.0.0.0'
 Run-Process $setup ('/S /UPDATE /D='+$install) 15
 Assert ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -eq $before) 'Downgrade guard modified app'
 Set-ItemProperty -LiteralPath $registryPath -Name DisplayVersion -Value $version
 & $Dotnet run --project (Join-Path $projectRoot 'windows\CodexAccountSwitcher.InstallerChecks\CodexAccountSwitcher.InstallerChecks.csproj') -c Release --artifacts-path (Join-Path $testRoot 'check-build') -- $setup $app $testRoot $registryKey ([IO.Path]::GetFullPath($PreviousExecutable))
 if($LASTEXITCODE -ne 0){throw 'Installed updater checks failed'}
 # An unrelated same-name shortcut must survive uninstall.
 $shell=New-Object -ComObject WScript.Shell
 $link=$shell.CreateShortcut($desktop)
 $link.TargetPath=Join-Path $env:SystemRoot 'System32\notepad.exe'
 $link.Save()
 [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
 [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
 $foreignShortcutHash=(Get-FileHash -LiteralPath $desktop -Algorithm SHA256).Hash
 $unknown=Join-Path $install 'user-owned-file.txt'
 [IO.File]::WriteAllText($unknown,'keep unknown files')
 Run-Process $uninstaller '/S'
 # NSIS launches a temporary uninstaller, so wait for its child to finish too.
 for($attempt=0;$attempt -lt 100 -and ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $registryPath));$attempt++){Start-Sleep -Milliseconds 100}
 Assert (!(Test-Path -LiteralPath $app)) 'Uninstall retained app'
 Assert (!(Test-Path -LiteralPath $uninstaller)) 'Uninstall retained uninstaller'
 Assert (!(Test-Path -LiteralPath $registryPath)) 'Uninstall retained registration'
 Assert (!(Test-Path -LiteralPath $menu)) 'Uninstall retained owned shortcut'
 Assert ((Test-Path -LiteralPath $desktop) -and (Get-FileHash -LiteralPath $desktop -Algorithm SHA256).Hash -eq $foreignShortcutHash) 'Uninstall changed unrelated same-name shortcut'
 Remove-Item -LiteralPath $desktop
 Assert ([IO.File]::ReadAllText($unknown) -eq 'keep unknown files') 'Uninstall removed unknown file'
 Assert ([IO.File]::ReadAllText($sentinel) -eq 'do not delete user accounts') 'Account data changed'
 Assert ([IO.File]::ReadAllText($codexSentinel) -eq '{"test_only":true}') 'Codex authentication data changed'
 $result='PASS: current-user install; Chinese/space path; shortcuts; update preserves deleted shortcuts; graceful exit; downgrade refusal; installed update/restart/rollback; uninstall preserves accounts, Codex auth, and unknown files.'
 [IO.File]::WriteAllText((Join-Path $testRoot 'result.txt'),$result)
 Write-Output $result
} finally {
 foreach($name in $priorEnvironment.Keys){[Environment]::SetEnvironmentVariable($name,$priorEnvironment[$name])}
}
