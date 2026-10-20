<#
.SYNOPSIS
    Installs the PCL Harmony loader into a Plain Craft Launcher installation.

.DESCRIPTION
    One-shot installer. Three independent steps, safe to re-run:

      1. copies PclHarmony.dll and 0Harmony.dll next to "Plain Craft Launcher 2.exe";
      2. creates "plugins\" next to the executable (plugins are loaded from there);
      3. patches "Plain Craft Launcher 2.exe.config" so the CLR instantiates our
         AppDomainManager ("PclHarmony.Entry") before the launcher's Main() runs.

    The config patch writes all three required parts and preserves unrelated
    content already present in the file:

      <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup>
      <runtime>
        <AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;
                                         Switch.System.IO.BlockLongPaths=false" />
        <appDomainManagerAssembly value="PclHarmony" />
        <appDomainManagerType value="PclHarmony.Entry" />
      </runtime>

    Without the AppContextSwitchOverrides the injected launcher aborts with
    "运行环境错误": the CLR would fall back to legacy path handling and reject the
    \\?\ prefixed extended paths PCL builds.

    The loader binaries are looked up next to this script first (the layout of a
    release archive), then in dist\PclHarmony\, then in src\loader\bin\Release\.

.PARAMETER TargetDir
    Directory that contains "Plain Craft Launcher 2.exe".

.PARAMETER Force
    Overwrite an existing PclHarmony.dll / 0Harmony.dll without prompting.

.PARAMETER InstallSamplePlugin
    Also copy the sample plugin (plugins\Test.dll from this archive) into the
    target plugins\ folder. Off by default: the sample changes launcher behaviour
    and only exists as a reference, so installing it is an explicit choice.

.EXAMPLE
    .\install.ps1 -TargetDir "D:\PCL"

.EXAMPLE
    .\install.ps1 -TargetDir "D:\PCL" -Force

.EXAMPLE
    .\install.ps1 -TargetDir "D:\PCL" -InstallSamplePlugin
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetDir,

    [switch]$Force,

    [switch]$InstallSamplePlugin
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ExeName         = 'Plain Craft Launcher 2.exe'
$ConfigName      = 'Plain Craft Launcher 2.exe.config'
$LogName         = 'PclHarmony.log'
$ManagerAssembly = 'PclHarmony'
$ManagerType     = 'PclHarmony.Entry'

function Write-Step([string]$Message) { Write-Host "[install] $Message" }
function Write-Fail([string]$Message) { Write-Host "[install] ERROR: $Message" -ForegroundColor Red }

# ------------------------------------------------------- locate the payload ----
$payloadCandidates = @(
    $PSScriptRoot,
    (Join-Path $PSScriptRoot 'dist\PclHarmony'),
    (Join-Path $PSScriptRoot 'src\loader\bin\Release')
)

$PayloadDir = $null
foreach ($candidate in $payloadCandidates) {
    if ([string]::IsNullOrEmpty($candidate)) { continue }
    if ((Test-Path -LiteralPath (Join-Path $candidate 'PclHarmony.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $candidate '0Harmony.dll') -PathType Leaf)) {
        $PayloadDir = $candidate
        break
    }
}

if ($null -eq $PayloadDir) {
    Write-Fail 'loader binaries not found (PclHarmony.dll + 0Harmony.dll).'
    Write-Host  '[install] place this script next to the two DLLs, or run a build first.'
    exit 1
}

# ---------------------------------------------------------------- preflight ---
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    Write-Fail 'no target directory given.'
    Write-Host  '[install] usage: .\install.ps1 -TargetDir "<folder containing Plain Craft Launcher 2.exe>"'
    exit 1
}

$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
    Write-Fail "target directory does not exist: $TargetDir"
    exit 1
}

$ExePath = Join-Path $TargetDir $ExeName
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Fail "'$ExeName' not found in $TargetDir"
    Write-Host  '[install] pass -TargetDir <folder containing the launcher executable>'
    exit 1
}
Write-Step "target: $TargetDir"
Write-Step "payload: $PayloadDir"

$running = @(Get-Process -ErrorAction SilentlyContinue |
             Where-Object { $_.ProcessName -like '*Plain Craft*' })
if ($running.Count -gt 0) {
    Write-Fail 'the launcher is running - close it first (the DLLs are locked while it runs)'
    exit 1
}

# ------------------------------------------------------------- copy binaries ---
foreach ($item in @(@{ Src = 'PclHarmony.dll'; Dst = 'PclHarmony.dll' },
                    @{ Src = '0Harmony.dll';   Dst = '0Harmony.dll' })) {
    $source = Join-Path $PayloadDir $item.Src
    $dest   = Join-Path $TargetDir $item.Dst

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        Write-Fail "payload file missing: $source"
        exit 1
    }
    if ((Test-Path -LiteralPath $dest) -and -not $Force) {
        Write-Step "already present: $($item.Dst) (use -Force to overwrite)"
    }
    else {
        Copy-Item -LiteralPath $source -Destination $dest -Force
        Write-Step "copied $($item.Dst)"
    }
}

# --------------------------------------------------------------- plugins dir ---
$PluginsDir = Join-Path $TargetDir 'plugins'
if (-not (Test-Path -LiteralPath $PluginsDir)) {
    $null = New-Item -ItemType Directory -Path $PluginsDir
    Write-Step 'created plugins\'
}
else {
    Write-Step 'plugins\ already exists'
}

# ------------------------------------------------------------ sample plugin ---
if ($InstallSamplePlugin) {
    # Release archive: plugins\Test.dll beside install.ps1.
    # Repository checkout: dist\plugins\Test.dll (staged by build.ps1).
    $sampleCandidates = @(
        (Join-Path $PSScriptRoot 'plugins\Test.dll'),
        (Join-Path $PSScriptRoot 'dist\plugins\Test.dll'),
        (Join-Path $PSScriptRoot 'src\testplugin\bin\Release\Test.dll')
    )
    $sampleSource = $null
    foreach ($candidate in $sampleCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $sampleSource = $candidate
            break
        }
    }
    if ($null -eq $sampleSource) {
        Write-Fail 'sample plugin not found'
        Write-Host  '[install] looked for: plugins\Test.dll, dist\plugins\Test.dll, src\testplugin\bin\Release\Test.dll'
        exit 1
    }
    $sampleDest = Join-Path $PluginsDir 'Test.dll'
    if ((Test-Path -LiteralPath $sampleDest) -and -not $Force) {
        Write-Step 'already present: plugins\Test.dll (use -Force to overwrite)'
    }
    else {
        Copy-Item -LiteralPath $sampleSource -Destination $sampleDest -Force
        Write-Step 'copied sample plugin to plugins\Test.dll'
    }
}

# --------------------------------------------------------------- patch config ---
$ConfigPath = Join-Path $TargetDir $ConfigName

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $false

if (Test-Path -LiteralPath $ConfigPath) {
    try {
        $xml.Load($ConfigPath)
        Write-Step "patching existing config: $ConfigName"
    }
    catch {
        $backup = $ConfigPath + '.bak'
        Copy-Item -LiteralPath $ConfigPath -Destination $backup -Force
        Write-Host "[install] WARNING: existing config is not valid XML, backed up to $backup" -ForegroundColor Yellow
        $xml = New-Object System.Xml.XmlDocument
    }
}

if ($null -eq $xml.DocumentElement) {
    $null = $xml.AppendChild($xml.CreateXmlDeclaration('1.0', 'utf-8', $null))
    $null = $xml.AppendChild($xml.CreateElement('configuration'))
}
$root = $xml.DocumentElement
if ($root.LocalName -ne 'configuration') {
    Write-Fail "unexpected root element <$($root.LocalName)> in $ConfigName"
    exit 1
}

function Get-Or-Add-Child {
    param([System.Xml.XmlNode]$Parent, [string]$Name)
    $node = $Parent.SelectSingleNode($Name)
    if ($null -eq $node) {
        $node = $Parent.OwnerDocument.CreateElement($Name)
        $null = $Parent.AppendChild($node)
    }
    return $node
}

# 1. <startup><supportedRuntime .../></startup>
$startup = Get-Or-Add-Child -Parent $root -Name 'startup'
$supported = $startup.SelectSingleNode('supportedRuntime')
if ($null -eq $supported) {
    $supported = $xml.CreateElement('supportedRuntime')
    $null = $startup.AppendChild($supported)
}
$supported.SetAttribute('version', 'v4.0')
$supported.SetAttribute('sku', '.NETFramework,Version=v4.8')

# 2. <runtime> - path-handling switches plus the AppDomainManager identity
$runtime = Get-Or-Add-Child -Parent $root -Name 'runtime'

$requiredSwitches = @(
    'Switch.System.IO.UseLegacyPathHandling=false',
    'Switch.System.IO.BlockLongPaths=false'
)
$switch = $runtime.SelectSingleNode('AppContextSwitchOverrides')
if ($null -eq $switch) {
    $switch = $xml.CreateElement('AppContextSwitchOverrides')
    $null = $runtime.PrependChild($switch)
}

function Get-SwitchName([string]$Pair) {
    $index = $Pair.IndexOf('=')
    if ($index -lt 0) { return $Pair.Trim() }
    return $Pair.Substring(0, $index).Trim()
}

# Merge by switch NAME, not by whole pair: a pre-existing
# "Switch.System.IO.BlockLongPaths=true" must be replaced by our required
# value instead of ending up as a conflicting duplicate.
$merged = New-Object System.Collections.Generic.List[string]
foreach ($pair in ([string]$switch.GetAttribute('value') -split ';')) {
    $pair = $pair.Trim()
    if (-not $pair) { continue }

    $replaced = $false
    foreach ($required in $requiredSwitches) {
        if ([string]::Equals((Get-SwitchName $pair), (Get-SwitchName $required),
                             [System.StringComparison]::OrdinalIgnoreCase)) {
            if (-not [string]::Equals($pair, $required, [System.StringComparison]::Ordinal)) {
                Write-Step "replaced $pair -> $required"
            }
            $replaced = $true
            break
        }
    }
    if (-not $replaced) { $merged.Add($pair) }
}
foreach ($required in $requiredSwitches) {
    $found = $false
    foreach ($have in $merged) {
        if ([string]::Equals($have, $required, [System.StringComparison]::OrdinalIgnoreCase)) {
            $found = $true
            break
        }
    }
    if (-not $found) { $merged.Add($required) }
}
$switch.SetAttribute('value', ($merged -join ';'))

$admAsm = Get-Or-Add-Child -Parent $runtime -Name 'appDomainManagerAssembly'
$admAsm.SetAttribute('value', $ManagerAssembly)

$admType = Get-Or-Add-Child -Parent $runtime -Name 'appDomainManagerType'
$admType.SetAttribute('value', $ManagerType)

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.NewLineChars = "`r`n"
$writer = [System.Xml.XmlWriter]::Create($ConfigPath, $settings)
try { $xml.Save($writer) } finally { $writer.Dispose() }

Write-Step "config written: $ConfigName"
Write-Step '  startup/supportedRuntime    = v4.0 / .NETFramework,Version=v4.8'
Write-Step "  AppContextSwitchOverrides  = $($switch.GetAttribute('value'))"
Write-Step "  appDomainManagerAssembly   = $ManagerAssembly"
Write-Step "  appDomainManagerType       = $ManagerType"

# --------------------------------------------------------------- verify result ---
$verify = New-Object System.Xml.XmlDocument
$verify.Load($ConfigPath)
$ok = $true
foreach ($probe in @(
    @{ Node = 'configuration/runtime/appDomainManagerAssembly';   Label = 'appDomainManagerAssembly' },
    @{ Node = 'configuration/runtime/appDomainManagerType';       Label = 'appDomainManagerType' },
    @{ Node = 'configuration/runtime/AppContextSwitchOverrides';  Label = 'AppContextSwitchOverrides' }
)) {
    $node = $verify.SelectSingleNode($probe.Node)
    if ($null -eq $node) { Write-Fail "verification failed: <$($probe.Label)> missing"; $ok = $false }
}
if (-not $ok) { exit 1 }

Write-Step 'verification: 3/3 config parts present'
Write-Step '  DLLs : PclHarmony.dll, 0Harmony.dll'
Write-Step '  plugins\ exists'
if (-not $InstallSamplePlugin) {
    Write-Step '  sample plugin NOT installed (pass -InstallSamplePlugin to add plugins\Test.dll)'
}
Write-Host ''
Write-Host '[install] Done. Start the launcher; the loader writes:'
Write-Host "          $(Join-Path $TargetDir $LogName)"
