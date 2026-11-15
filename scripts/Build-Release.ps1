<#
.SYNOPSIS
  Builds the tool from a git tag or branch into release\<Name>\ (not committed).

.DESCRIPTION
  Checks the ref out into a temporary git worktree, so the working copy is untouched,
  builds the Release configuration there and copies DataTransferTool.exe and its .config
  into release\<Name>\, together with BUILD.txt (ref, commit, date).
  Keeps v1 and v2 side by side:
    release\v1\DataTransferTool.exe   (from tag v1.0.0, or branch v1 for fixes)
    release\v2\DataTransferTool.exe   (from the current v2 branch)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -Ref v1.0.0 -Name v1
  powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -Ref HEAD -Name v2
#>
param(
  [Parameter(Mandatory)][string]$Ref,
  [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$Name
)
$ErrorActionPreference = 'Stop'
$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$commit = (git -C $repo rev-parse --verify "$Ref^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or -not $commit) { throw "Unknown ref: $Ref" }

$work = Join-Path ([IO.Path]::GetTempPath()) "dtt_release_$Name`_$PID"
git -C $repo worktree add --detach $work $commit 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed" }
try {
  dotnet build (Join-Path $work 'src\DataTransferTool') -c Release -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "Build failed for $Ref" }
  $bin = Join-Path $work 'src\DataTransferTool\bin\Release'
  $out = Join-Path $repo "release\$Name"
  New-Item -ItemType Directory -Force $out | Out-Null
  Get-ChildItem $out -File | Remove-Item -Force
  Copy-Item (Join-Path $bin 'DataTransferTool.exe'), (Join-Path $bin 'DataTransferTool.exe.config') $out
  $ver = (Get-Item (Join-Path $out 'DataTransferTool.exe')).VersionInfo.FileVersion
  "Ref:     $Ref`r`nCommit:  $commit`r`nVersion: $ver`r`nBuilt:   $(Get-Date -Format 'yyyy-MM-dd HH:mm')" |
    Set-Content -Encoding ASCII (Join-Path $out 'BUILD.txt')
  "Built $Ref ($($commit.Substring(0,7)), version $ver) into $out"
} finally {
  git -C $repo worktree remove --force $work 2>&1 | Out-Null
}
