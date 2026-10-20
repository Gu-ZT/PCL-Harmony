<#
.SYNOPSIS
    Builds the loader and the sample plugin, then refreshes dist\.

.DESCRIPTION
    Builds src\loader\PclHarmony.csproj and src\testplugin\Test.csproj in Release
    and copies the results into the distributable tree:

      dist\PclHarmony\PclHarmony.dll   <- loader
      dist\PclHarmony\0Harmony.dll     <- Lib.Harmony 2.4.2 runtime dependency
      dist\plugins\Test.dll            <- sample plugin

    Only the .NET SDK is required: the net48 reference assemblies are restored
    from NuGet, so Visual Studio is not needed.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipTests
    Skip the post-build assembly identity check.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot      = $PSScriptRoot
$LoaderProject = Join-Path $RepoRoot 'src\loader\PclHarmony.csproj'
$TestProject   = Join-Path $RepoRoot 'src\testplugin\Test.csproj'

$LoaderOut  = Join-Path $RepoRoot 'dist\PclHarmony'
$PluginsOut = Join-Path $RepoRoot 'dist\plugins'

function Write-Step([string]$Message) { Write-Host "[build] $Message" }
function Write-Fail([string]$Message) { Write-Host "[build] ERROR: $Message" -ForegroundColor Red }

foreach ($project in @($LoaderProject, $TestProject)) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        Write-Fail "project not found: $project"
        exit 1
    }
}

# ------------------------------------------------------------------- preflight ---
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Fail '.NET SDK not found in PATH - install it from https://dotnet.microsoft.com/download'
    exit 1
}
Write-Step "dotnet: $($dotnet.Source) ($(& $dotnet.Source --version))"

# ----------------------------------------------------------------------- build ---
foreach ($project in @($LoaderProject, $TestProject)) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
    Write-Step "building $name ($Configuration)"
    & $dotnet.Source build $project -c $Configuration --nologo -v:m
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "build failed: $name (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

# --------------------------------------------------------------------- publish ---
$loaderDir  = Join-Path $RepoRoot "src\loader\bin\$Configuration"
$testDir    = Join-Path $RepoRoot "src\testplugin\bin\$Configuration"

$loaderDll  = Join-Path $loaderDir 'PclHarmony.dll'
$harmonyDll = Join-Path $loaderDir '0Harmony.dll'
$testDll    = Join-Path $testDir   'Test.dll'

foreach ($artifact in @($loaderDll, $harmonyDll, $testDll)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        Write-Fail "expected build output missing: $artifact"
        exit 1
    }
}

$null = New-Item -ItemType Directory -Force -Path $LoaderOut
$null = New-Item -ItemType Directory -Force -Path $PluginsOut

Copy-Item -LiteralPath $loaderDll  -Destination (Join-Path $LoaderOut 'PclHarmony.dll') -Force
Copy-Item -LiteralPath $harmonyDll -Destination (Join-Path $LoaderOut '0Harmony.dll')   -Force
Copy-Item -LiteralPath $testDll    -Destination (Join-Path $PluginsOut 'Test.dll')      -Force

Write-Step "refreshed dist\PclHarmony\PclHarmony.dll ($((Get-Item (Join-Path $LoaderOut 'PclHarmony.dll')).Length) bytes)"
Write-Step "refreshed dist\PclHarmony\0Harmony.dll   ($((Get-Item (Join-Path $LoaderOut '0Harmony.dll')).Length) bytes)"
Write-Step "refreshed dist\plugins\Test.dll          ($((Get-Item (Join-Path $PluginsOut 'Test.dll')).Length) bytes)"

# ------------------------------------------------------------------ sanity check ---
if (-not $SkipTests) {
    Write-Step 'sanity check'
    $expected = @(
        @{ Path = Join-Path $LoaderOut 'PclHarmony.dll'; Name = 'PclHarmony' },
        @{ Path = Join-Path $LoaderOut '0Harmony.dll';   Name = '0Harmony'   },
        @{ Path = Join-Path $PluginsOut 'Test.dll';      Name = 'Test'       }
    )
    foreach ($item in $expected) {
        $asmName = [System.Reflection.AssemblyName]::GetAssemblyName($item.Path)
        if ($asmName.Name -ne $item.Name) {
            Write-Fail "assembly name mismatch: $($item.Path) -> $($asmName.Name), expected $($item.Name)"
            exit 1
        }
        Write-Step ("  {0,-12} {1,-9} {2}" -f $asmName.Name, $asmName.Version, $item.Path)
    }
}

Write-Host ''
Write-Step 'Done. Install with: .\install.ps1 -TargetDir "<launcher folder>"'
