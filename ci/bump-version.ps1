<#
.SYNOPSIS
    Advances project.json to the next patch version after a stable release.

.DESCRIPTION
    Mirrors the release flow: once a stable tag (v1.0.0, v0.2.4, ...) has been
    published, the manifest moves one patch ahead (1.0.0 -> 1.0.1) so the NEXT
    plain push does not keep building the already-released version.

    Pre-release tags (v1.0.0-dev.12, v1.0.0-rc.1) never bump - only three-part
    numeric tags do.

    Idempotent by design: it only bumps when project.json still carries exactly
    the released version. Re-running the job, or a manual bump, can therefore
    never double-advance or downgrade the version.

.PARAMETER Tag
    The released tag, e.g. "v1.0.0".

.EXAMPLE
    ./ci/bump-version.ps1 v1.0.0
    # project.json: 1.0.0 -> 1.0.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'project.json'

function Write-Step([string] $Message) { Write-Host "[bump] $Message" }

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "project.json not found at $manifestPath"
}

# The tag must be a stable vX.Y.Z to trigger a bump. Anything else is a normal
# no-op: dev/rc tags are cut from an unreleased version and must not advance it.
if ($Tag -notmatch '^v') {
    throw "tag '$Tag' must start with 'v'"
}
$released = $Tag.Substring(1)
if ($released -notmatch '^\d+\.\d+\.\d+$') {
    Write-Step "tag $Tag is not a stable release; no bump"
    exit 0
}

$raw = [System.IO.File]::ReadAllText($manifestPath)
$match = [regex]::Match($raw, '"version"\s*:\s*"([^"]+)"')
if (-not $match.Success) {
    throw "project.json has no 'version' field"
}
$current = $match.Groups[1].Value

# Idempotence guard: never bump twice, never bump from an unrelated version.
if ($current -ne $released) {
    Write-Step "manifest version $current != released $released; no bump"
    exit 0
}

$parts = $released.Split('.')
$next = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)

# Replace only the version value so the rest of the file keeps its formatting,
# key order and description text byte-for-byte.
$updated = $raw.Substring(0, $match.Groups[1].Index) + $next +
           $raw.Substring($match.Groups[1].Index + $match.Groups[1].Length)
[System.IO.File]::WriteAllText($manifestPath, $updated,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Step "bumped $current -> $next"

# Fail loudly if the file no longer parses or reads back a different value;
# a silently corrupted manifest would break every later build.
$check = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$check.version -ne $next) {
    throw "post-bump verification failed: project.json reports '$($check.version)', expected '$next'"
}
Write-Step "verified project.json version = $next"
