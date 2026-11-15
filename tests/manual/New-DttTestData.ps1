<#
.SYNOPSIS
  Generates test folders for the manual tests in MANUAL_TESTS.md. Never touches existing files.

.DESCRIPTION
  Creates <Root>\Documents-like folders with random content:
    Office\      many small "Office" files (random bytes, Office names)
    Pictures\    medium files
    Large\       a few large files (size set by -LargeFileMB)
    Deep\        a path longer than 400 characters
    Names\       accented, Japanese and symbol file names
    Excluded\    files the default exclusions skip (desktop.ini, ~$*, *.tmp, Thumbs.db)
    Unticked\    a folder for the "untick a subfolder" step
  With -FourGbFile also Large\over-4GB.bin (4.2 GB) for the FAT32 test.
  With -ProfileFolders the same tree is put into Desktop, Documents, Pictures and Downloads of
  the CURRENT user's profile under a "DttTest" subfolder, for the User data preset tests.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File New-DttTestData.ps1 -Root C:\DttTest -TotalMB 2000
#>
param(
  [string]$Root = "C:\DttTest",
  [int]$TotalMB = 1000,
  [int]$LargeFileMB = 200,
  [switch]$FourGbFile,
  [switch]$ProfileFolders,
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'

function Remove-Tree([string]$dir) {
  if (-not (Test-Path -LiteralPath $dir)) { return }
  # robocopy /MIR of an empty folder removes paths longer than 260 characters reliably
  $empty = Join-Path $env:TEMP "dtt_empty_$PID"
  New-Item -ItemType Directory -Force $empty | Out-Null
  robocopy $empty $dir /MIR /NJH /NJS /NFL /NDL /NP | Out-Null
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $empty -Force -ErrorAction SilentlyContinue
}

$profileTargets = @('Desktop','Documents','Pictures','Downloads') | ForEach-Object { Join-Path (Join-Path $env:USERPROFILE $_) 'DttTest' }

if ($Remove) {
  Remove-Tree $Root
  if ($ProfileFolders) { $profileTargets | ForEach-Object { Remove-Tree $_ } }
  "Removed."; return
}

$rng = New-Object System.Random 20261115
function New-RandomFile([string]$path, [long]$bytes) {
  $dir = Split-Path -Parent $path
  [System.IO.Directory]::CreateDirectory("\\?\$dir") | Out-Null
  $buf = New-Object byte[] ([Math]::Min([long]1MB, [Math]::Max([long]1, $bytes)))
  $fs = [System.IO.File]::Open("\\?\$path", 'Create', 'Write', 'None')
  try {
    $left = $bytes
    while ($left -gt 0) { $rng.NextBytes($buf); $n = [int][Math]::Min($left, $buf.Length); $fs.Write($buf, 0, $n); $left -= $n }
  } finally { $fs.Dispose() }
}

function New-Tree([string]$base, [long]$budget) {
  $written = [long]0
  $ext = '.docx','.xlsx','.pptx','.pdf','.txt','.msg'
  # Office: about 30% of the budget in files of 5-200 KB
  $i = 0
  while ($written -lt $budget * 0.3) {
    $size = $rng.Next(5KB, 200KB)
    New-RandomFile (Join-Path $base ("Office\Folder {0:00}\Document {1:0000}{2}" -f ($i % 40), $i, $ext[$i % $ext.Count])) $size
    $written += $size; $i++
  }
  # Pictures: about 30% in files of 1-6 MB
  $i = 0
  while ($written -lt $budget * 0.6) {
    $size = $rng.Next(1MB, 6MB)
    New-RandomFile (Join-Path $base ("Pictures\{0}\IMG_{1:0000}.jpg" -f (2019 + $i % 6), $i)) $size
    $written += $size; $i++
  }
  # Large: the rest in files of -LargeFileMB
  $i = 0
  while ($written -lt $budget) {
    $size = [long]$LargeFileMB * 1MB
    New-RandomFile (Join-Path $base ("Large\video {0:00}.mp4" -f $i)) $size
    $written += $size; $i++
  }
  $deep = Join-Path $base 'Deep'
  0..9 | ForEach-Object { $deep = Join-Path $deep ("level{0:00}_{1}" -f $_, ('x' * 32)) }
  New-RandomFile (Join-Path $deep 'deep file.txt') 4096
  New-RandomFile (Join-Path $base 'Names\Été & Co (2024)\résumé – final.docx') 12000
  New-RandomFile (Join-Path $base 'Names\日本語フォルダ\報告書 ①.txt') 3000
  New-RandomFile (Join-Path $base "Names\it's [brackets] {braces} 100%.txt") 2000
  foreach ($n in 'desktop.ini','Thumbs.db','~$Budget.xlsx','scratch.tmp') { New-RandomFile (Join-Path $base "Excluded\$n") 100 }
  New-RandomFile (Join-Path $base 'Excluded\kept.txt') 100
  New-RandomFile (Join-Path $base 'Unticked\should not be copied when unticked.txt') 1000
  New-Item -ItemType Directory -Force (Join-Path $base 'Empty folder') | Out-Null
  if ($FourGbFile) { New-RandomFile (Join-Path $base 'Large\over-4GB.bin') ([long]4300MB) }
  return $written
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$bytes = New-Tree $Root ([long]$TotalMB * 1MB)
"Created $Root ($([Math]::Round($bytes/1MB)) MB)"
if ($ProfileFolders) {
  foreach ($t in $profileTargets) { New-Tree $t ([long]([Math]::Max(20, $TotalMB / 10)) * 1MB) | Out-Null; "Created $t" }
}
$files = (Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Measure-Object).Count
"$files files in $Root; took $([int]$sw.Elapsed.TotalSeconds) s. Remove later with: -Remove" + ($(if ($ProfileFolders) { ' -ProfileFolders' } else { '' }))
