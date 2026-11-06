param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$TestDirectory)
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$copy = Join-Path $testRoot 'Star-Codex-Switcher.exe'
Copy-Item -LiteralPath $Executable -Destination $copy -Force
$resultPath = Join-Path $testRoot 'self-test.txt'
$start = [Diagnostics.ProcessStartInfo]::new($copy, '--self-test')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.WorkingDirectory = $testRoot
$start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
$start.Environment.Remove('DOTNET_ROOT') | Out-Null
$start.Environment.Remove('DOTNET_ROOT_X64') | Out-Null
$start.Environment.Remove('CODEX_SWITCHER_HOST_PATH') | Out-Null
$start.Environment['LOCALAPPDATA'] = Join-Path $testRoot '新用户 本地数据'
$start.Environment['CODEX_CLI_PATH'] = Join-Path $testRoot 'no-external-runtime.exe'
$start.Environment['CODEX_SWITCHER_SELF_TEST_RESULT'] = $resultPath
$process = [Diagnostics.Process]::Start($start)
if (!$process.WaitForExit(45000)) { throw 'Portable self-test timed out.' }
if ($process.ExitCode -ne 0) { throw "Portable self-test exited with $($process.ExitCode)" }
$result = Get-Content -LiteralPath $resultPath -Raw
if (!$result.StartsWith('PASS:')) { throw 'Portable self-test failed.' }
Write-Output $result
