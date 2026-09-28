<#
.SYNOPSIS
  Blocks commits / releases that would publish private information.

.DESCRIPTION
  Checks (a) the staged files (-Staged, used by the git pre-commit hook) or
  (b) every tracked file (default, used by the release script).

  Rules:
    - Words listed in the local block list must not appear in any text file.
      The list lives at <git-dir>/privacy-blocklist.txt, which is never pushed,
      so the words themselves are never published. One entry per line,
      case-insensitive substring match, '#' starts a comment.
    - No private-network IPv4 addresses.
    - Only known file types; images only under docs/ or the app's Assets/; no file over 5 MB.

  Exit code 0 = clean, 1 = something must be fixed first.
#>
param([switch]$Staged)

$ErrorActionPreference = 'Stop'

function Invoke-GitBytes([string[]]$GitArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = ($GitArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $ms = New-Object System.IO.MemoryStream
    $p.StandardOutput.BaseStream.CopyTo($ms)
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "git $($GitArgs -join ' ') failed ($($p.ExitCode))" }
    return ,$ms.ToArray()
}

function Get-GitText([string[]]$GitArgs) {
    return [System.Text.Encoding]::UTF8.GetString((Invoke-GitBytes $GitArgs))
}

$gitDir = (Get-GitText @('rev-parse', '--git-dir')).Trim()
$blockFile = Join-Path $gitDir 'privacy-blocklist.txt'
$blockWords = @()
if (Test-Path $blockFile) {
    $blockWords = Get-Content -LiteralPath $blockFile -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
} else {
    Write-Host "privacy-check: WARNING no block list at $blockFile - only generic checks run." -ForegroundColor Yellow
}

if ($Staged) {
    $raw = Get-GitText @('diff', '--cached', '--name-only', '-z', '--diff-filter=ACMR')
} else {
    $raw = Get-GitText @('ls-files', '-z')
}
$files = $raw.Split([char]0) | Where-Object { $_ }

$textExt  = @('.cs', '.xaml', '.csproj', '.sln', '.slnx', '.props', '.targets', '.md', '.txt', '.json',
              '.resx', '.ps1', '.psm1', '.yml', '.yaml', '.xml', '.config', '.manifest', '.editorconfig',
              '.gitignore', '.gitattributes', '.svg', '.iss', '.cmd', '.bat')
$imageExt = @('.png', '.ico', '.jpg', '.jpeg', '.gif')
$ipPattern = '\b(192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3})\b'

$problems = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    $name = [System.IO.Path]::GetFileName($f)
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if (-not $ext) { $ext = $name.ToLowerInvariant() }   # .gitignore, .gitattributes, LICENSE

    $bytes = if ($Staged) { Invoke-GitBytes @('show', ":$f") } else { [System.IO.File]::ReadAllBytes((Join-Path (Get-Location) $f)) }

    if ($bytes.Length -gt 5MB) { $problems.Add("$f : larger than 5 MB"); continue }

    if ($imageExt -contains $ext) {
        if (-not ($f.StartsWith('docs/') -or $f.StartsWith('src/SweeplyForWindows/Assets/'))) {
            $problems.Add("$f : images are only allowed under docs/ or src/SweeplyForWindows/Assets/")
        }
        continue
    }
    if (($textExt -notcontains $ext) -and ($name -ne 'LICENSE')) {
        $problems.Add("$f : file type '$ext' is not on the allowed list (tools/privacy-check.ps1)")
        continue
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        foreach ($w in $blockWords) {
            if ($line.IndexOf($w, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $problems.Add("$f : line $($i + 1) contains a blocked word (see $blockFile)")
            }
        }
        if ($line -match $ipPattern) {
            $problems.Add("$f : line $($i + 1) contains a private IP address ($($Matches[0]))")
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "privacy-check: BLOCKED - fix these first:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "privacy-check: OK ($($files.Count) file(s) checked)" -ForegroundColor Green
exit 0
