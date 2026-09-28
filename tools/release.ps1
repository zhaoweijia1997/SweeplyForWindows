<#
.SYNOPSIS
  One-click build of a release package.

.DESCRIPTION
  1. Refuses to run with uncommitted changes (everything released must be committed and checked).
  2. Runs the privacy check on every tracked file.
  3. Runs the unit tests.
  4. Publishes a self-contained single-file SweeplyForWindows.exe (no .pdb).
  5. Scans the compiled assemblies for build-machine paths and block-listed words.
  6. Zips exe + LICENSE + README into artifacts/, prints size and SHA-256.

  -GitHubRelease  also creates a GitHub release for the tag v<version> and uploads the zip
                  (needs the gh CLI, signed in). Marked as a pre-release while the version is 0.x.
#>
param([switch]$GitHubRelease, [switch]$AllowDirty)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "release: $msg" -ForegroundColor Red; exit 1 }

# 1. clean tree
if (-not $AllowDirty) {
    $dirty = git status --porcelain
    if ($dirty) { Fail "working tree has uncommitted changes - commit first:`n$dirty" }
}

# 2. privacy
Step 'privacy check'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'privacy-check.ps1')
if ($LASTEXITCODE -ne 0) { Fail 'privacy check failed' }

# 3. tests
Step 'unit tests'
dotnet test --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail 'tests failed' }

# 4. publish
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$rid = 'win-x64'
$pubDir = Join-Path $root 'artifacts\publish'
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
Step "publish v$version ($rid, self-contained single file)"
dotnet publish src/SweeplyForWindows -c Release -r $rid --self-contained true --nologo -v quiet `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $pubDir
if ($LASTEXITCODE -ne 0) { Fail 'publish failed' }
$exe = Join-Path $pubDir 'SweeplyForWindows.exe'
if (-not (Test-Path $exe)) { Fail "no exe at $exe" }
$extra = Get-ChildItem $pubDir | Where-Object { $_.Name -ne 'SweeplyForWindows.exe' }
if ($extra) { Fail "unexpected files in publish output: $($extra.Name -join ', ')" }

# 5. leak scan on the uncompressed assemblies of this build
Step 'scan binaries for private strings'
$needles = New-Object System.Collections.Generic.List[string]
$needles.Add($root.TrimEnd('\'))
$needles.Add($env:USERNAME)
$needles.Add($env:USERPROFILE)
$gitDir = (git rev-parse --git-dir).Trim()
$blockFile = Join-Path $gitDir 'privacy-blocklist.txt'
if (Test-Path $blockFile) {
    Get-Content -LiteralPath $blockFile -Encoding UTF8 | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $needles.Add($_) }
}
$dlls = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Include 'SweeplyForWindows.dll', 'Sweeply.Core.dll' |
    Where-Object { $_.FullName -match '\\bin\\Release\\' -and $_.FullName -match [regex]::Escape($rid) }
if (-not $dlls) { Fail 'could not find the Release assemblies to scan' }
foreach ($dll in $dlls) {
    $bytes = [System.IO.File]::ReadAllBytes($dll.FullName)
    $asUtf8 = [System.Text.Encoding]::UTF8.GetString($bytes)
    $asUtf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    foreach ($n in $needles) {
        if ($asUtf8.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $asUtf16.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Fail "$($dll.Name) contains a private string (build path, user name or block-listed word)"
        }
    }
}
Write-Host "   $($dlls.Count) assemblies clean"

# 6. zip
$zipName = "SweeplyForWindows-$version-$rid.zip"
$zip = Join-Path $root "artifacts\$zipName"
if (Test-Path $zip) { Remove-Item $zip -Force }
$stage = Join-Path $root 'artifacts\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item $exe, (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $stage
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Step "done: artifacts\$zipName  ($sizeMb MB)"
Write-Host "   sha256 $hash"

# 7. optional GitHub release
if ($GitHubRelease) {
    $remote = (git remote get-url origin).Trim()
    if ($remote -notmatch '^https://github\.com/zhaoweijia1997/SweeplyForWindows(\.git)?$') {
        Fail "origin is '$remote', not the SweeplyForWindows repo - refusing to release"
    }
    $tag = "v$version"
    Step "GitHub release $tag"
    git tag -a $tag -m $tag
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { Fail 'pushing the tag failed' }
    $pre = if ($version.StartsWith('0.')) { '--prerelease' } else { '' }
    $notes = "SHA-256: $hash"
    gh release create $tag $zip --repo zhaoweijia1997/SweeplyForWindows --title "SweeplyForWindows $tag" --notes $notes $pre
    if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed' }
}
