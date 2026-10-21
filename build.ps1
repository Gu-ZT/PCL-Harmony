<#
.SYNOPSIS
    Builds the loader and the sample plugin, then refreshes dist\.

.DESCRIPTION
    Builds src\loader\PclHarmony.csproj, src\sample-plugin\Example.csproj and
    src\example-title\Example-Title.csproj in Release and copies the results into
    the distributable tree:

      dist\PclHarmony\PclHarmony.dll        <- loader
      dist\PclHarmony\0Harmony.dll          <- Lib.Harmony 2.4.2 runtime dependency
      dist\plugins\Example.dll              <- sample plugin
      dist\plugins\Example-Title.dll        <- title-bar badge example

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

$RepoRoot        = $PSScriptRoot
$LoaderProject   = Join-Path $RepoRoot 'src\loader\PclHarmony.csproj'
$SampleProject   = Join-Path $RepoRoot 'src\sample-plugin\Example.csproj'
$TitleProject    = Join-Path $RepoRoot 'src\example-title\Example-Title.csproj'

$LoaderOut  = Join-Path $RepoRoot 'dist\PclHarmony'
$PluginsOut = Join-Path $RepoRoot 'dist\plugins'

$AllProjects = @($LoaderProject, $SampleProject, $TitleProject)

function Write-Step([string]$Message) { Write-Host "[build] $Message" }
function Write-Fail([string]$Message) { Write-Host "[build] ERROR: $Message" -ForegroundColor Red }

foreach ($project in $AllProjects) {
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
foreach ($project in $AllProjects) {
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
$sampleDir  = Join-Path $RepoRoot "src\sample-plugin\bin\$Configuration"
$titleDir   = Join-Path $RepoRoot "src\example-title\bin\$Configuration"

$loaderDll  = Join-Path $loaderDir 'PclHarmony.dll'
$harmonyDll = Join-Path $loaderDir '0Harmony.dll'
$sampleDll  = Join-Path $sampleDir 'Example.dll'
$titleDll   = Join-Path $titleDir  'Example-Title.dll'

foreach ($artifact in @($loaderDll, $harmonyDll, $sampleDll, $titleDll)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        Write-Fail "expected build output missing: $artifact"
        exit 1
    }
}

$null = New-Item -ItemType Directory -Force -Path $LoaderOut
$null = New-Item -ItemType Directory -Force -Path $PluginsOut

Copy-Item -LiteralPath $loaderDll  -Destination (Join-Path $LoaderOut 'PclHarmony.dll')  -Force
Copy-Item -LiteralPath $harmonyDll -Destination (Join-Path $LoaderOut '0Harmony.dll')    -Force
Copy-Item -LiteralPath $sampleDll  -Destination (Join-Path $PluginsOut 'Example.dll')     -Force
Copy-Item -LiteralPath $titleDll   -Destination (Join-Path $PluginsOut 'Example-Title.dll') -Force

Write-Step "refreshed dist\PclHarmony\PclHarmony.dll ($((Get-Item (Join-Path $LoaderOut 'PclHarmony.dll')).Length) bytes)"
Write-Step "refreshed dist\PclHarmony\0Harmony.dll   ($((Get-Item (Join-Path $LoaderOut '0Harmony.dll')).Length) bytes)"
Write-Step "refreshed dist\plugins\Example.dll       ($((Get-Item (Join-Path $PluginsOut 'Example.dll')).Length) bytes)"
Write-Step "refreshed dist\plugins\Example-Title.dll ($((Get-Item (Join-Path $PluginsOut 'Example-Title.dll')).Length) bytes)"

# ------------------------------------------------------------- stamped version ---
# Directory.Build.props records the version it stamped into the loader assembly
# in the project's obj folder. Surface it in dist/ so CI can assert on it without
# re-reading the compiled DLL (loading it is unreliable on the Linux runner).
$stampedPath = Join-Path $RepoRoot 'src\loader\obj\' `
               | Join-Path -ChildPath $Configuration `
               | Join-Path -ChildPath 'product-version.txt'

if (Test-Path -LiteralPath $stampedPath) {
    $stamped = (Get-Content -LiteralPath $stampedPath -Raw).Trim()
    Set-Content -LiteralPath (Join-Path $LoaderOut 'product-version.txt') `
                -Value $stamped -NoNewline -Encoding ascii
    Write-Step "stamped version: $stamped"
} else {
    Write-Fail "stamp file missing: $stampedPath"
    exit 1
}

# ------------------------------------------------------------------ sanity check ---
if (-not $SkipTests) {
    Write-Step 'sanity check'
    $expected = @(
        @{ Path = Join-Path $LoaderOut 'PclHarmony.dll';      Name = 'PclHarmony'    },
        @{ Path = Join-Path $LoaderOut '0Harmony.dll';        Name = '0Harmony'      },
        @{ Path = Join-Path $PluginsOut 'Example.dll';        Name = 'Example'       },
        @{ Path = Join-Path $PluginsOut 'Example-Title.dll';  Name = 'Example-Title' }
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
