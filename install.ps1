<#
.SYNOPSIS
    Installs the PCL Harmony loader into a Plain Craft Launcher installation.

.DESCRIPTION
    One-shot installer. Three independent steps, safe to re-run:

      1. copies PclHarmony.dll and 0Harmony.dll next to "Plain Craft Launcher 2.exe";
      2. creates an EMPTY "plugins\" next to the executable, as a drop point;
      3. patches "Plain Craft Launcher 2.exe.config" so the CLR instantiates our
         AppDomainManager ("PclHarmony.Entry") before the launcher's Main() runs.

    Step 2 creates the folder only. No plugin DLL is ever copied into it - not
    from this script's own plugins\ folder, not from anywhere else. Which plugins
    run is the user's decision, so drop the DLLs in by hand.

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
    Optional: when omitted, the current working directory is used, so unzipping
    the release into the launcher folder and running .\install.ps1 is enough.

.PARAMETER Force
    Overwrite an existing PclHarmony.dll / 0Harmony.dll without prompting.

.EXAMPLE
    .\install.ps1
    Installs into the current directory.

.EXAMPLE
    .\install.ps1 -TargetDir "D:\PCL"

.EXAMPLE
    .\install.ps1 -TargetDir "D:\PCL" -Force
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetDir,

    [switch]$Force
)

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
# -TargetDir is optional: with no argument, the current working directory is
# used. This matches the common case of unzipping the release next to the
# launcher and running .\install.ps1 with no arguments at all.
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = (Get-Location).ProviderPath
    Write-Step "no -TargetDir given, using the current directory: $TargetDir"
}

$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
    Write-Fail "target directory does not exist: $TargetDir"
    exit 1
}

$ExePath = Join-Path $TargetDir $ExeName
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Fail "'$ExeName' not found in $TargetDir"
    Write-Host  '[install] run this script from the launcher folder, or pass -TargetDir <that folder>'
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
# Created empty, so there is an obvious place to drop plugin DLLs. Nothing is
# copied into it: which plugins run is the user's decision, and the archive's
# plugins\ folder is only a supply of samples.
$PluginsDir = Join-Path $TargetDir 'plugins'
if (Test-Path -LiteralPath $PluginsDir -PathType Container) {
    Write-Step 'plugins\ already exists'
}
else {
    $null = New-Item -ItemType Directory -Path $PluginsDir -Force
    Write-Step 'created empty plugins\'
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
Write-Step '  plugins\ : created empty, no plugin DLL copied (drop them in yourself)'
Write-Host ''
Write-Host '[install] Done. Start the launcher; the loader writes:'
Write-Host "          $(Join-Path $TargetDir $LogName)"
