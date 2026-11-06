param(
 [Parameter(Mandatory=$true)][string]$AppExecutable,
 [Parameter(Mandatory=$true)][string]$PreviousExecutable,
 [Parameter(Mandatory=$true)][string]$Makensis,
 [Parameter(Mandatory=$true)][string]$TestDirectory,
 [ValidateSet('all','success','rollback')][string]$Scenario = 'all'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
$currentSource = [IO.Path]::GetFullPath($AppExecutable)
$previousSource = [IO.Path]::GetFullPath($PreviousExecutable)
$productionKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexAccountSwitcher'
# The unchanged 1.0.0 helper opens its hard-coded production registration. It may
# only participate when that key is absent, so it cannot alter a real install.
foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
 $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
 try { $existing = $baseKey.OpenSubKey($productionKey); if ($existing) { $existing.Dispose(); throw 'A real installed registration exists; legacy helper test is refused.' } }
 finally { $baseKey.Dispose() }
}
if (Test-Path -LiteralPath $testRoot) { throw 'TestDirectory must not exist; each run is isolated.' }
$oldVersion = (Get-Item -LiteralPath $previousSource).VersionInfo.FileVersion
$newVersion = (Get-Item -LiteralPath $currentSource).VersionInfo.FileVersion
if ($oldVersion -ne '1.0.0.0' -or [Version]$newVersion -le [Version]$oldVersion) { throw 'Use the authentic 1.0.0 installed executable and a newer installed executable.' }
function Hash([string]$path) {
 # Polling must not lock the executable while the unchanged helper restores it.
 $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
 try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
 finally { $stream.Dispose() }
}
function Assert([bool]$value, [string]$message) { if (!$value) { throw $message } }
function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$message) {
 $watch = [Diagnostics.Stopwatch]::StartNew()
 while ($watch.Elapsed.TotalSeconds -lt $seconds) {
  try { if (& $condition) { return } } catch [IO.IOException] { }
  Start-Sleep -Milliseconds 100
 }
 throw $message
}
function Start-Hidden([string]$path, [string[]]$arguments) {
 $start = [Diagnostics.ProcessStartInfo]::new($path)
 $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
 foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
 [Diagnostics.Process]::Start($start)
}
function Run-Setup([string]$path, [string]$directory) {
 # NSIS requires /D to be the last argument and unquoted, including spaces.
 $start = [Diagnostics.ProcessStartInfo]::new($path, '/S /D=' + $directory)
 $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
 $process = [Diagnostics.Process]::Start($start)
 try { if (!$process.WaitForExit(120000)) { $process.Kill($true); throw 'Fixture setup timed out.' }; Assert ($process.ExitCode -eq 0) 'Fixture setup failed.' }
 finally { $process.Dispose() }
}
function Matching-Processes([string]$path) {
 foreach ($process in [Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($path))) {
  try { if (!$process.HasExited -and [string]::Equals($process.MainModule.FileName, $path, [StringComparison]::OrdinalIgnoreCase)) { $process } else { $process.Dispose() } }
  catch { $process.Dispose() }
 }
}
function Stop-FixtureProcess([string]$path) {
 foreach ($process in @(Matching-Processes $path)) { try { $process.Kill(); $null = $process.WaitForExit(10000) } finally { $process.Dispose() } }
}
function Write-Link([string]$path, [string]$target) {
 $shell = New-Object -ComObject WScript.Shell
 try { $link = $shell.CreateShortcut($path); $link.TargetPath = $target; $link.WorkingDirectory = [IO.Path]::GetDirectoryName($target); $link.Save(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
 finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Link-Target([string]$path) {
 $shell = New-Object -ComObject WScript.Shell
 try { $link = $shell.CreateShortcut($path); $target = $link.TargetPath; $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link); return $target }
 finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
if (!('LegacyMigrationFailureProbe' -as [type])) {
 Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
public sealed class LegacyMigrationFailureProbe : IDisposable {
 readonly CancellationTokenSource stop = new CancellationTokenSource();
 readonly Task worker;
 public int KilledProcessId { get; private set; }
 public LegacyMigrationFailureProbe(string target, string root) {
  if (!System.IO.Path.GetFullPath(target).StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fixture process must remain in the test directory.");
  worker = Task.Run(() => {
   while (!stop.IsCancellationRequested) {
    foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(target))) using (process) {
     try {
      if (!process.HasExited && string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) {
       process.Kill(); process.WaitForExit(10000); KilledProcessId = process.Id; return;
      }
     } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    Thread.Sleep(5);
   }
  });
 }
 public void Dispose() { stop.Cancel(); worker.GetAwaiter().GetResult(); stop.Dispose(); }
}
'@
}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$id = [Guid]::NewGuid().ToString('N')
$registryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\StarCodexInstallerTest-' + $id
$registryPath = 'HKCU:\' + $registryKey
$shortcut = 'Star Codex Legacy Test ' + $id
$legacyShortcut = 'Codex Legacy Test ' + $id
$shortcutFolders = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
$links = @($shortcutFolders | ForEach-Object { Join-Path $_ ($shortcut + '.lnk'); Join-Path $_ ($legacyShortcut + '.lnk') })
foreach ($link in $links) { Assert (!(Test-Path -LiteralPath $link)) 'Unique fixture shortcut already exists.' }
$setup = Join-Path $testRoot 'test-setup.exe'
& $Makensis /INPUTCHARSET UTF8 /V2 /DTEST_MODE=1 "/DREGKEY=$registryKey" "/DSHORTCUT=$shortcut" "/DLEGACY_SHORTCUT=$legacyShortcut" "/DAPP_VERSION=$newVersion" "/DAPP_EXE=$currentSource" "/DOUTPUT_FILE=$setup" (Join-Path $projectRoot 'windows/installer/setup.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Legacy migration fixture setup failed to compile.' }
$oldHash = Hash $previousSource; $newHash = Hash $currentSource
$cacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Codex Account Switcher Updates'
$environmentNames = @('CODEX_SWITCHER_DATA_HOME','CODEX_HOME','CODEX_CLI_PATH','CODEX_SWITCHER_HOST_PATH')
$priorEnvironment = @{}; foreach ($name in $environmentNames) { $priorEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$requestedScenario = $Scenario
try {
 foreach ($caseName in $(if ($requestedScenario -eq 'all') { @('success','rollback') } else { @($requestedScenario) })) {
  $scenario = $caseName
  $caseRoot = Join-Path $testRoot $scenario
  $install = Join-Path $caseRoot '安装 程序'
  $legacy = Join-Path $install 'CodexAccountSwitcher.exe'
  $current = Join-Path $install 'StarCodexSwitcher.exe'
  $uninstaller = Join-Path $install 'Uninstall.exe'
  $data = Join-Path $caseRoot '账号数据'; $codex = Join-Path $caseRoot 'codex'
  New-Item -ItemType Directory -Path $caseRoot,$data,$codex -Force | Out-Null
  $env:CODEX_SWITCHER_DATA_HOME = $data; $env:CODEX_HOME = $codex; $env:CODEX_CLI_PATH = Join-Path $caseRoot 'no-cli.exe'
  [Environment]::SetEnvironmentVariable('CODEX_SWITCHER_HOST_PATH', $null)
  $sentinel = Join-Path $data 'preserve.txt'; $authSentinel = Join-Path $codex 'auth.json'
  [IO.File]::WriteAllText($sentinel, 'fixture accounts must survive'); [IO.File]::WriteAllText($authSentinel, '{"test_only":true}')
  Run-Setup $setup $install
  Assert ((Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $uninstaller)) 'Fixture install is incomplete.'
  $oldUninstallerHash = Hash $uninstaller
  Copy-Item -LiteralPath $previousSource -Destination $legacy
  Remove-Item -LiteralPath $current
  Set-ItemProperty -LiteralPath $registryPath -Name DisplayVersion -Value $oldVersion
  Set-ItemProperty -LiteralPath $registryPath -Name DisplayName -Value 'Codex Account Switcher'
  Set-ItemProperty -LiteralPath $registryPath -Name DisplayIcon -Value $legacy
  foreach ($folder in $shortcutFolders) {
   $newLink = Join-Path $folder ($shortcut + '.lnk'); if (Test-Path -LiteralPath $newLink) { Remove-Item -LiteralPath $newLink }
   Write-Link (Join-Path $folder ($legacyShortcut + '.lnk')) $legacy
  }
  $jobFolder = Join-Path $cacheRoot ([Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $jobFolder -Force | Out-Null
  $payload = Join-Path $jobFolder 'download.exe'; $helperPath = Join-Path $jobFolder 'update-helper.exe'
  Copy-Item -LiteralPath $setup -Destination $payload; Copy-Item -LiteralPath $previousSource -Destination $helperPath
  Assert ((Hash $helperPath) -eq $oldHash) 'The helper must be the unmodified published 1.0.0 application.'
  $parent = $null; $helper = $null; $killer = $null
  try {
   $parent = Start-Hidden $legacy @('--background')
   Start-Sleep -Seconds 3
   Assert (!$parent.HasExited) 'The authentic 1.0.0 fixture did not start.'
   $job = [ordered]@{ Target=$legacy; Payload=$payload; Sha256=(Hash $payload); OldSha256=$oldHash; Version=$newVersion; ParentId=$parent.Id; ParentStartTicks=$parent.StartTime.ToUniversalTime().Ticks; PackageType='installer'; AppSha256=$newHash }
   $jobPath = Join-Path $jobFolder 'job.json'
   [IO.File]::WriteAllText($jobPath, ($job | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
   Assert (!(Test-Path -LiteralPath ('HKCU:\' + $productionKey))) 'A real install appeared before the legacy helper test; stop without launching the helper.'
   if ($scenario -eq 'rollback') { $killer = [LegacyMigrationFailureProbe]::new($current, $testRoot) }
   $helper = Start-Hidden $helperPath @('--apply-update', $jobPath)
   Wait-Until { Test-Path -LiteralPath (Join-Path $jobFolder 'helper-ready') } 15 'Original helper did not acknowledge the update job.'
   $quit = Start-Hidden $legacy @('--prepare-install')
   try { Assert ($quit.WaitForExit(15000) -and $quit.ExitCode -eq 0) 'The original application did not hand off cleanly.' } finally { $quit.Dispose() }
   Assert ($parent.WaitForExit(10000)) 'The original parent application remained active.'
   if ($scenario -eq 'success') {
    Wait-Until { Test-Path -LiteralPath (Join-Path $jobFolder 'result.txt') } 100 'The original helper did not report successful migration.'
    Assert ([IO.File]::ReadAllText((Join-Path $jobFolder 'result.txt')).StartsWith('PASS:')) 'Original helper update did not pass.'
    Wait-Until { (Test-Path -LiteralPath $current) -and !(Test-Path -LiteralPath $legacy) -and !(Test-Path -LiteralPath (Join-Path $install '.star-migration.ini')) } 20 'Legacy alias or migration marker was not removed.'
    Assert ((Hash $current) -eq $newHash) 'The new named executable checksum differs from the release.'
    $running = @(Matching-Processes $current); Assert ($running.Count -ge 1) 'The new named application is not running.'; $running | ForEach-Object { $_.Dispose() }
    $registration = Get-ItemProperty -LiteralPath $registryPath
    Assert ($registration.DisplayVersion -eq $newVersion -and $registration.DisplayName -eq 'Star Codex Switcher' -and $registration.DisplayIcon -eq $current) 'Registration did not migrate to the Star product.'
    foreach ($folder in $shortcutFolders) {
     $newLink = Join-Path $folder ($shortcut + '.lnk'); $oldLink = Join-Path $folder ($legacyShortcut + '.lnk')
     Assert ((Test-Path -LiteralPath $newLink) -and !(Test-Path -LiteralPath $oldLink) -and (Link-Target $newLink) -eq $current) 'Existing shortcut was not renamed and retargeted.'
    }
   } else {
    Wait-Until { $killer.KilledProcessId -gt 0 } 100 'The new fixture process was not intercepted for startup failure.'
    Wait-Until { (Test-Path -LiteralPath $legacy) -and (Hash $legacy) -eq $oldHash -and !(Test-Path -LiteralPath $current) } 70 'Original helper did not restore the authentic old application.'
    Assert (!(Test-Path -LiteralPath (Join-Path $jobFolder 'result.txt'))) 'The failure fixture ran too late and must not be counted as a rollback.'
    Assert ((Hash $uninstaller) -eq $oldUninstallerHash) 'Rollback did not preserve the original uninstaller.'
    $registration = Get-ItemProperty -LiteralPath $registryPath
    Assert ($registration.DisplayVersion -eq $oldVersion -and $registration.DisplayName -eq 'Codex Account Switcher' -and $registration.DisplayIcon -eq $legacy) 'Rollback did not preserve the old registration.'
    foreach ($folder in $shortcutFolders) { $oldLink = Join-Path $folder ($legacyShortcut + '.lnk'); Assert ((Test-Path -LiteralPath $oldLink) -and (Link-Target $oldLink) -eq $legacy) 'Rollback changed an old shortcut.' }
   }
   Assert ([IO.File]::ReadAllText($sentinel) -eq 'fixture accounts must survive') 'Account sentinel changed.'
   Assert ([IO.File]::ReadAllText($authSentinel) -eq '{"test_only":true}') 'Codex authentication sentinel changed.'
   Assert (!(Test-Path -LiteralPath ('HKCU:\' + $productionKey))) 'A production registration was unexpectedly created.'
   [IO.File]::WriteAllText((Join-Path $caseRoot 'result.txt'), "PASS: authentic 1.0.0 helper $scenario; preserved credentials and isolated registration.")
   Write-Output "PASS: authentic 1.0.0 helper $scenario."
  } finally {
   $probeFailure = $null
   if ($killer) { try { $killer.Dispose() } catch { $probeFailure = $_ } }
   # Close only processes whose executable is the exact isolated fixture path.
   if ($helper -and !$helper.HasExited) { $null = $helper.CloseMainWindow(); if (!$helper.WaitForExit(1000)) { $helper.Kill(); $null = $helper.WaitForExit(10000) } }
   Stop-FixtureProcess $current; Stop-FixtureProcess $legacy
   if ($parent) { $parent.Dispose() }; if ($helper) { $helper.Dispose() }
   foreach ($link in $links) { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link } }
   if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath }
   if ($probeFailure) { throw $probeFailure }
  }
 }
 Assert ((Hash $previousSource) -eq $oldHash) 'The authentic source application was modified.'
 Write-Output "PASS: unmodified 1.0.0 helper $requestedScenario scenario(s). No real accounts or installation registration were used."
} finally {
 foreach ($name in $priorEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $priorEnvironment[$name]) }
}
