<#
.SYNOPSIS
    Uninstalls the PCL Harmony loader from a Plain Craft Launcher installation.

.DESCRIPTION
    Reverses install.ps1:

      1. deletes PclHarmony.dll and 0Harmony.dll next to the launcher executable;
      2. removes <appDomainManagerAssembly> and <appDomainManagerType> from
         "Plain Craft Launcher 2.exe.config";
      3. removes the two AppContextSwitchOverrides entries that install.ps1 added
         (Switch.System.IO.UseLegacyPathHandling / Switch.System.IO.BlockLongPaths),
         keeping any other override the config already had.

    Everything else is preserved: other <runtime> settings, <startup>, the
    plugins\ folder (it holds your own plugins) and the launcher executable.

.PARAMETER TargetDir
    Directory that contains "Plain Craft Launcher 2.exe". Defaults to the
    directory the script is run from.

.PARAMETER RemovePlugins
    Also delete the plugins\ folder. Off by default because the folder is where
    you keep your own plugin DLLs.

.PARAMETER RemoveLog
    Also delete PclHarmony.log.

.EXAMPLE
    .\uninstall.ps1 -TargetDir "D:\PCL"

.EXAMPLE
    .\uninstall.ps1 -RemovePlugins -RemoveLog
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetDir = (Get-Location).Path,

    [switch]$RemovePlugins,

    [switch]$RemoveLog
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ExeName    = 'Plain Craft Launcher 2.exe'
$ConfigName = 'Plain Craft Launcher 2.exe.config'
$LogName    = 'PclHarmony.log'
$Payloads   = @('PclHarmony.dll', '0Harmony.dll')
# Switch NAMES (without "=value"): uninstall.ps1 owns these two switches and
# removes them whatever their current value, so a re-install is never faked by
# a stale "=true" left behind from a hand-edited config.
$OwnedSwitchNames = @(
    'Switch.System.IO.UseLegacyPathHandling',
    'Switch.System.IO.BlockLongPaths'
)
$AdmNodes = @('appDomainManagerAssembly', 'appDomainManagerType')

function Write-Step([string]$Message) { Write-Host "[uninstall] $Message" }
function Write-Fail([string]$Message) { Write-Host "[uninstall] ERROR: $Message" -ForegroundColor Red }

# ---------------------------------------------------------------- preflight ---
$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
    Write-Fail "target directory does not exist: $TargetDir"
    exit 1
}

$ExePath = Join-Path $TargetDir $ExeName
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Fail "'$ExeName' not found in $TargetDir"
    exit 1
}
Write-Step "target: $TargetDir"

$running = @(Get-Process -ErrorAction SilentlyContinue |
             Where-Object { $_.ProcessName -like '*Plain Craft*' })
if ($running.Count -gt 0) {
    Write-Fail 'the launcher is running - close it first (the DLLs are locked while it runs)'
    exit 1
}

# ------------------------------------------------------------- delete binaries ---
$deleted = 0
foreach ($name in $Payloads) {
    $path = Join-Path $TargetDir $name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        Write-Step "deleted $name"
        $deleted++
    }
    else {
        Write-Step "not present: $name"
    }
}
if ($deleted -eq 0) { Write-Step 'loader DLLs were already absent' }

# --------------------------------------------------------------- patch config ---
$ConfigPath = Join-Path $TargetDir $ConfigName

if (Test-Path -LiteralPath $ConfigPath) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $false
    try {
        $xml.Load($ConfigPath)
    }
    catch {
        Write-Fail "$ConfigName is not valid XML - fix it by hand (nothing was changed)"
        exit 1
    }

    $touched = $false

    # 1. drop the AppDomainManager nodes
    foreach ($name in $AdmNodes) {
        $nodes = @($xml.SelectNodes("configuration/runtime/$name"))
        foreach ($node in $nodes) {
            $null = $node.ParentNode.RemoveChild($node)
            Write-Step "removed <$name>"
            $touched = $true
        }
    }

    # 2. drop our two switches, keep any foreign ones
    $switch = $xml.SelectSingleNode('configuration/runtime/AppContextSwitchOverrides')
    if ($null -ne $switch) {
        $kept = New-Object System.Collections.Generic.List[string]
        $removed = 0
        foreach ($pair in ([string]$switch.GetAttribute('value') -split ';')) {
            $pair = $pair.Trim()
            if (-not $pair) { continue }
            # Match on the switch NAME only: a hand-edited value such as
            # "Switch.System.IO.BlockLongPaths=true" is still ours to remove.
            $name = $pair.Trim()
            $equals = $name.IndexOf('=')
            if ($equals -ge 0) { $name = $name.Substring(0, $equals).Trim() }

            $owned = $false
            foreach ($candidate in $OwnedSwitchNames) {
                if ([string]::Equals($name, $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $owned = $true
                    break
                }
            }
            if ($owned) { $removed++ } else { $kept.Add($pair) }
        }
        if ($removed -gt 0) {
            $touched = $true
            if ($kept.Count -eq 0) {
                $null = $switch.ParentNode.RemoveChild($switch)
                Write-Step "removed <AppContextSwitchOverrides> ($removed switch(es), nothing left)"
            }
            else {
                $switch.SetAttribute('value', ($kept -join ';'))
                Write-Step "trimmed <AppContextSwitchOverrides> ($removed switch(es) removed, $($kept.Count) kept)"
            }
        }
        else {
            Write-Step 'no loader switches in <AppContextSwitchOverrides>'
        }
    }

    # 3. drop <runtime> if our edits left it empty
    $runtime = $xml.SelectSingleNode('configuration/runtime')
    if ($null -ne $runtime -and $runtime.ChildNodes.Count -eq 0 -and
        $runtime.Attributes.Count -eq 0) {
        $null = $runtime.ParentNode.RemoveChild($runtime)
        Write-Step 'removed now-empty <runtime>'
    }

    if ($touched) {
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Indent = $true
        $settings.IndentChars = '  '
        $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
        $settings.NewLineChars = "`r`n"
        $writer = [System.Xml.XmlWriter]::Create($ConfigPath, $settings)
        try { $xml.Save($writer) } finally { $writer.Dispose() }
        Write-Step "config written: $ConfigName"
    }
    else {
        Write-Step 'config needed no change'
    }

    # verify
    $verify = New-Object System.Xml.XmlDocument
    $verify.Load($ConfigPath)
    $left = @($verify.SelectNodes('configuration/runtime/appDomainManagerAssembly')) +
            @($verify.SelectNodes('configuration/runtime/appDomainManagerType'))
    if ($left.Count -gt 0) {
        Write-Fail 'verification failed: AppDomainManager entries are still present'
        exit 1
    }
    Write-Step 'verification: AppDomainManager entries absent'
}
else {
    Write-Step "no $ConfigName - nothing to patch"
}

# -------------------------------------------------------------- optional extras ---
if ($RemovePlugins) {
    $pluginsDir = Join-Path $TargetDir 'plugins'
    if (Test-Path -LiteralPath $pluginsDir) {
        Remove-Item -LiteralPath $pluginsDir -Recurse -Force
        Write-Step 'deleted plugins\'
    }
}

if ($RemoveLog) {
    $logPath = Join-Path $TargetDir $LogName
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
        Write-Step "deleted $LogName"
    }
}

Write-Host ''
Write-Host '[uninstall] Done. The launcher starts normally without the loader.'
