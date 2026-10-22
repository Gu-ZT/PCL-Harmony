# PCL Harmony

**Harmony plugin loading for [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL).**

[简体中文](README.md) | English

[How it works](#how-it-works) · [Installing](#installing) · [Uninstalling](#uninstalling) · [Writing a plugin](#writing-a-plugin) · [Building from source](#building-from-source) · [Known limitations](#known-limitations)

---

## What this is

PCL Harmony is a **plugin loader**: it takes control while the launcher starts up and injects [Harmony](https://github.com/pardeike/Harmony) into its AppDomain, so that third-party plugins can patch launcher methods at runtime.

**Both launchers are supported:**

| | Official PCL | PCL-CE |
| --- | --- | --- |
| Executable | `Plain Craft Launcher 2.exe` | `PCL2_CE_*.exe` |
| Target framework | net48 (CLR 4) | net10 (CoreCLR) |
| How control is taken | `AppDomainManager` injection | .NET startup hook |
| Is the launcher modified? | No (only `exe.config`) | No (env var via a wrapper script) |

The two runtimes are incompatible, so `libs/` carries one payload per target and `install.ps1` picks the matching one automatically. A plugin aimed at a single launcher is written the ordinary way; to make **one plugin work on both**, see [Cross-launcher plugins](#cross-launcher-plugins).

The repository also contains two **sample plugins** (`src/sample-plugin/`, `src/example-title/`) that demonstrate how a declarative plugin plugs in: declaring patch points, branching per launcher, and attaching a WPF element to the title bar. Both target netstandard2.0, so the same DLL works on either launcher.

---

## How it works

### 1. Gaining control before `Main`

The installer writes a `Plain Craft Launcher 2.exe.config` next to the launcher, containing two key elements:

```xml
<runtime>
  <appDomainManagerAssembly value="PclHarmony" />
  <appDomainManagerType value="PclHarmony.Entry" />
</runtime>
```

When the CLR creates the AppDomain it instantiates `PclHarmony.Entry` (an `AppDomainManager` subclass) **before PCL's own `Main()` runs**. Inside `InitializeNewDomain` the loader does as little as possible — record the directory, subscribe to `AssemblyLoad` — and performs **no file I/O**. That is a hard requirement: the CLR has not finished bringing up the domain at that point, and touching the filesystem there corrupts PCL's own startup.

### 2. Patching when the target assembly loads

When the `AssemblyLoad` event reports that the `Plain Craft Launcher 2` assembly has loaded, the loader:

1. installs the embedded-dependency resolver (see the next section);
2. scans `<directory of the exe>\plugins\*.dll` and loads each with `Assembly.LoadFrom`;
3. finds types implementing `PclHarmony.IPlugin`, instantiates them and calls `Load(Harmony, Assembly)`, handing the target assembly to the plugin.

PCL's `Main()` has not started yet, so patches applied here stay in effect for the whole run.

### 3. Why `exe.config` must set `UseLegacyPathHandling=false`

The installer writes this alongside the AppDomainManager identity:

```xml
<AppContextSwitchOverrides
    value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />
```

**This is not optional.** Once injection is active via `appDomainManagerAssembly`, leaving legacy path handling enabled makes the CLR fall back to the old path rules; the `\\?\` extended-prefix paths PCL builds then fail validation as containing illegal characters, and the launcher dies with a **"运行环境错误"** (environment error) dialog. With the switch in place PCL reaches its main window normally (title `Plain Craft Launcher　`, ending in a U+3000 ideographic space).

### 4. Why the TLS defaults must be repaired too

Injecting an `AppDomainManager` makes the CLR treat the host as a *legacy* (pre-v4) framework application. Besides the path handling above, that has a second consequence:

**`ServicePointManager.SecurityProtocol` stays at `Ssl3, Tls` and never picks up TLS 1.2+.** Every HTTPS request then fails with `SecureChannelFailure` and the launcher reports "你的网络环境不佳，请稍后再试，或使用 VPN 改善网络环境。" ("your network is poor…") — which is why the download page stays empty.

PCL does not repair this itself: it only sets `SecurityProtocol` when `Environment.OSVersion < 6.2` (pre-Windows 8). On modern Windows it relies entirely on the CLR default, and the default has already been skewed by the injection.

**The loader therefore adds TLS 1.2 / 1.3 explicitly in `InitializeNewDomain`**, additively — it never removes protocols the host already configured. This happens before the launcher's `Main()`, i.e. before any network request. The result is logged to `PclHarmony.log`:

```
TLS: SecurityProtocol Ssl3, Tls -> Ssl3, Tls, Tls12, Tls13
```

It lives in code rather than only in the config switch (`Switch.System.Net.DontEnableSchUseStrongCrypto=false`) because that switch is read exactly once very early: a hand-edited or badly merged `exe.config` would silently break networking again.

### 5. Native dependency resolution: why the loader needs its own `AssemblyResolve`

PCL embeds all of its dependencies (`MeloongCore`, `MeloongCore.Wpf`, `PCLCS`, `Newtonsoft.Json`, `NAudio`, `Ookii.Dialogs.Wpf`, `Imazen.WebP`, `CacheCow.*`, `ThrottleDebounce`, `System.Net.Http.Formatting`, `Microsoft.Win32.Registry` — 13 in total) as `byte[]` entries in its own manifest resource `PCL.Resources.resources`, and resolves them from an `AssemblyResolve` handler registered in `Application.New()`.

The problem: **Harmony has to resolve the metadata of the methods it patches before `Application.New()` ever runs.** At that point PCL's resolver does not exist yet, so those dependencies throw `FileNotFoundException`. The loader therefore implements an equivalent resolver of its own (`EmbeddedResolver`) that serves the same resource by prefix and `Assembly.Load`s the bytes.

### 6. Repository layout

```
project.json               version manifest - the ONLY place the version lives
                           (name / version / description)
Directory.Build.props      feeds project.json's version into every assembly
src/loader/                loader C# sources (net48 -> PclHarmony.dll)
  Entry.cs                 AppDomainManager entry point, subscribes to AssemblyLoad
  EmbeddedResolver.cs      embedded dependency resolution (PCL.Resources.resources)
  PluginHost.cs            plugins\ scan, plugin instantiation and loading
  LoaderLog.cs             lazily initialised, low-cost logging (PclHarmony.log)
  IPlugin.cs               plugin interface
  PluginApi.cs             helper API exposed to plugins
src/contract/                 runtime-neutral plugin contract (netstandard2.0)
  Contract.cs                 IDeclarativePlugin / PatchSpec / IPluginHost
src/sample-plugin/            sample plugin (netstandard2.0 -> Example.dll)
src/example-title/            sample plugin: title-bar badge (netstandard2.0)

src/ce-host/                  PCL-CE loader (net8 -> PclHarmony.CeHost.dll)
  StartupHook.cs              startup-hook entry point
  Host.cs                     plugin scan + patch application
libs/official/                official PCL payload (net48)
libs/ce/                      PCL-CE payload (net10)
install.ps1                one-shot installer (defaults to the current directory)
uninstall.ps1              one-shot uninstaller
build.ps1                  builds all projects and refreshes dist\ + libs\
dist/                      build output (produced by build.ps1, not version controlled)
```

**Change the version only in `project.json`.** `Directory.Build.props` reads its `version` field and stamps every assembly with it, so binaries, release tags and the manifest cannot drift apart; CI additionally verifies the version that was actually stamped after building and fails on any mismatch.

How the version flows through CI:

| Event | Published tag | Version inside the artifact |
| --- | --- | --- |
| Push to `main` | pre-release `v1.0.0-dev.<run>` | `1.0.0-dev.<run>` (**not** `1.0.0`) |
| Push a `v1.0.0` tag | stable `v1.0.0` | `1.0.0` |

`AssemblyVersion` / `FileVersion` are four-part numeric versions and cannot carry a `-dev.<run>` suffix, so they use the numeric core (`1.0.0`); the full string lives in `InformationalVersion` (for example `1.0.0-dev.15+<commit>`). To tell which version an artifact really is, read `InformationalVersion`, not `AssemblyVersion`.

Stable releases (a `v*` tag) attach two extra files next to `PCL-Harmony.zip`:

- `project.json` - the manifest used for that build;
- `SHA256SUMS.txt` - the archive's SHA-256 checksum.

After a stable release succeeds, CI advances `project.json` to the next patch (`1.0.0` → `1.0.1`) and commits it back to `main`, so the next ordinary push does not rebuild an already-shipped version. Pre-release tags never bump. That step is idempotent: it only bumps while `project.json` still equals the version just released, so re-running the job or bumping by hand can never double-advance.

This repository does not contain PCL itself or its source.

---

## Installing

### Prerequisites

- Windows
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) (PCL itself runs on it)
- An installation of **PCL 2.99.0.0**

### From a release (recommended)

1. Download `PCL-Harmony.zip` from Releases and unzip it. You get:

   ```
   install.ps1   uninstall.ps1   README.md
   PclHarmony.dll   0Harmony.dll
   plugins\Example.dll   plugins\Example-Title.dll
   ```

2. Close any running PCL.
3. Unzip the archive **into the PCL folder** (next to `Plain Craft Launcher 2.exe`) and run PowerShell there:

   ```powershell
   .\install.ps1
   ```

   `-TargetDir` is optional and defaults to the current directory. To install somewhere else, pass it explicitly:

   ```powershell
   .\install.ps1 -TargetDir "D:\path\to\PCL"
   ```

`install.ps1` **detects the target automatically**: `Plain Craft Launcher 2.exe` means the official launcher, `PCL2_CE_*.exe` means PCL-CE. The matching payload is taken from `libs\`.

#### Official PCL

`install.ps1` performs three steps and is safe to re-run:

| Step | Action |
| --- | --- |
| 1 | Copies `PclHarmony.dll` and `0Harmony.dll` next to the launcher executable |
| 2 | Creates an **empty** `plugins\` folder (skipped when it already exists) |
| 3 | Writes / merges the three config parts described above into `Plain Craft Launcher 2.exe.config` |

#### PCL-CE

CE's executable is a .NET apphost and **cannot take an `AppDomainManager`**, so the .NET startup hook mechanism is used instead:

| Step | Action |
| --- | --- |
| 1 | Copies `PclHarmony.CeHost.dll`, `PclHarmony.Contract.dll`, `0Harmony.dll` |
| 2 | Generates `Run-with-PCL-Harmony.cmd` (sets `DOTNET_STARTUP_HOOKS`, then starts CE) |
| 3 | Creates an **empty** `plugins\` folder |

**On CE you must start via `Run-with-PCL-Harmony.cmd`; double-clicking the exe will not load plugins.** The startup hook can only arrive through an environment variable, and the CE single-file bundle ignores an external `runtimeconfig.json`. The launcher binary itself is never modified.

> CE is single-instance. If a CE instance is already running, a new one exits immediately and cleanly (exit code 0). That is CE's own behaviour, not a failed install.

Both launchers write `PclHarmony.log` into their own folder so you can confirm the loader attached.

**`install.ps1` only creates `plugins\`, it never puts a plugin in it.** The archive's own `plugins\` is just where the sample plugins come from - the script does not copy it across. To use a plugin, copy its DLL into the folder yourself.

### Placing plugins

A plugin is an ordinary `.dll`. **Drop it into the `plugins\` folder next to the launcher**:

```
D:\path\to\PCL\
  Plain Craft Launcher 2.exe
  Plain Craft Launcher 2.exe.config
  PclHarmony.dll
  0Harmony.dll
  PclHarmony.log
  plugins\
    Example.dll            <- sample plugin
  Example-Title.dll      <- sample plugin: title-bar badge
    YourPlugin.dll      <- your own plugin
```

Notes:

- Only the **first level** of `plugins\` is scanned; subdirectories are ignored.
- DLLs are loaded in filename order and isolated from one another.
- A plugin should **reference but never ship** its own `0Harmony.dll` or `PclHarmony.dll`. A second copy produces a second assembly identity, which breaks the `IPlugin` cast and makes Harmony patches target the wrong type.
- Dependencies a plugin needs but does not ship are looked up by filename in the PCL folder and then in `plugins\`.

### Troubleshooting

| Symptom | Explanation |
| --- | --- |
| "运行环境错误" dialog at startup | `AppContextSwitchOverrides` is missing from the config, or was overwritten. Re-run `install.ps1`. |
| PCL starts fine but there is no `PclHarmony.log` | The loader was not loaded. Check `appDomainManagerAssembly` / `appDomainManagerType` in the config, and that both DLLs sit beside the exe. |
| Log exists but plugins do nothing | Look for `plugin: ... FAILED` or `skipping` lines. Usually the plugin does not implement `IPlugin`, lacks a parameterless constructor, or is missing a dependency. |
| Temporarily disable the loader | Set the environment variable `PCLHARMONY_DISABLE=1` before starting PCL. |

A healthy plugin load looks roughly like this:

```
PclHarmony loader 1.0.0.0 attached (ADM PclHarmony.Entry)
target assembly loaded: Plain Craft Launcher 2, Version=2.99.0.0, ...
embedded assembly resolver installed (resource "PCL.Resources.resources", 13 prefixes)
plugin: loading '<name>' (...) from <file>.dll
plugin: loaded '<name>' OK, harmony="<name>", patch count=N
plugins: N plugin(s) loaded from M dll(s)
```

---

## Uninstalling

Close PCL, then:

```powershell
.\uninstall.ps1 -TargetDir "D:\path\to\PCL"
```

It will:

1. delete `PclHarmony.dll` and `0Harmony.dll`;
2. remove `appDomainManagerAssembly` / `appDomainManagerType` from the config;
3. remove the two `AppContextSwitchOverrides` switches the installer added (dropping the empty `<AppContextSwitchOverrides>` and empty `<runtime>` too, if that leaves them empty).

Everything else in the config is preserved. The `plugins\` folder and `PclHarmony.log` are **kept by default** (that is where your plugins live). To remove them as well:

```powershell
.\uninstall.ps1 -TargetDir "D:\path\to\PCL" -RemovePlugins -RemoveLog
```

After uninstalling, PCL starts normally, exactly as if the loader had never been installed.

---

## Writing a plugin

### The plugin interface

A plugin is a net48 class library with at least one type that implements `PclHarmony.IPlugin` and has a **public parameterless constructor**:

```csharp
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    public interface IPlugin
    {
        /// <summary>Plugin name; also used as the Harmony instance id.</summary>
        string Name { get; }

        /// <summary>
        /// Called once, after the target assembly loaded and before PCL's Main()
        /// runs. Patches applied here are in effect for the whole run.
        /// </summary>
        void Load(Harmony harmony, Assembly target);
    }
}
```

The `harmony` passed to `Load` is an instance already named after the plugin's `Name`, so patches remain attributable per plugin. `target` is the `Plain Craft Launcher 2` assembly; PCL types must be obtained by reflecting over it.

### Helper API

`PclHarmony.PluginApi` exposes a few conveniences:

| Member | Purpose |
| --- | --- |
| `Assembly Target` | the target assembly (may be `null` very early) |
| `Harmony SharedHarmony` | loader-wide shared Harmony instance, id `pcl.harmony.pluginloader` |
| `Harmony GetHarmony(string id)` | returns (and caches) a Harmony instance per id; empty `id` yields the shared one |
| `void Log(string message)` | appends a line to `PclHarmony.log` in the PCL folder |

### Minimal example

```csharp
using System;
using System.Reflection;
using HarmonyLib;
using PclHarmony;

namespace MyPlugin
{
    public sealed class MyPlugin : IPlugin
    {
        public string Name { get { return "My.FirstPlugin"; } }

        public void Load(Harmony harmony, Assembly target)
        {
            PluginApi.Log("[MyPlugin] attaching to " + target.GetName().Version);

            // Locate the target method by reflection (PCL types live in `target`)
            var type = target.GetType("PCL.ModBase", false);
            var method = type == null ? null : type.GetMethod("SomeMethod",
                BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                PluginApi.Log("[MyPlugin] target method not found, skipping");
                return;
            }

            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(MyPlugin).GetMethod(
                    nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: null);
        }

        private static void Before() { /* ... */ }
    }
}
```

### Guidelines

- **Target net48**: a plugin must use the same framework as PCL.
- **`0Harmony` and `PclHarmony` must be `Private=false`** (or `ExcludeAssets="runtime"`); see "Placing plugins" above.
- **Prefer reflection over referencing PCL types directly.** PCL is not a library meant for wide referencing; a compile-time dependency pins your plugin to one exact build.
- **Failures must stay isolated.** The loader catches each plugin's exception and reverts its half-applied patches, but a plugin should still defend itself inside `Load`.
- **Log through `PluginApi.Log`** so users can paste the output back to you.
- See the sample plugin under `src/sample-plugin/` for a complete implementation.

---

## Cross-launcher plugins

To make **one DLL run on both the official launcher and PCL-CE**, two hard constraints have to be worked around.

**Constraint 1 - the only common target is netstandard2.0.** The official launcher runs on CLR 4 (net48); PCL-CE runs on CoreCLR (net10). netstandard2.0 is the single framework both can load.

**Constraint 2 - a netstandard2.0 plugin cannot call Harmony itself.** Lib.Harmony 2.4.2 ships no netstandard2.0 build, and reflectively driving the net48 build from a netstandard2.0 caller fails:

```
TypeLoadException: "MonoMod.Utils.Cil.ILGeneratorProxy[TTarget]" violates the
constraint of type parameter "TTarget" (GenericArguments[0] = CecilILGenerator)
```

The identical patch logic called from a native net48 assembly works fine - the difference is the caller's runtime, not the usage.

**The workaround: the plugin declares, the host applies.** A cross-launcher plugin implements `PclHarmony.Contract.IDeclarativePlugin` and only *describes* the patches it wants; each host applies them with the Harmony build matching its own runtime (net48 on the official side, net10 on the CE side).

```csharp
using System.Collections.Generic;
using PclHarmony.Contract;

public sealed class MySharedPlugin : IDeclarativePlugin
{
    public string Name { get { return "My.Shared.Plugin"; } }

    public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion)
    {
        // hostKind is "official" or "ce" - branch as needed
        yield return new PatchSpec
        {
            TypeName       = hostKind == "ce" ? "PCL.Program" : "PCL.Application",
            MethodName     = "Main",
            HookKind       = "postfix",              // "prefix" or "postfix"
            HookTypeName   = typeof(MySharedPlugin).FullName,
            HookMethodName = "AfterMain",            // must be a real static method
        };
    }

    public static void AfterMain() { /* ... */ }
}
```

Reference the contract only - **never Harmony**:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>MySharedPlugin</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\contract\PclHarmony.Contract.csproj" Private="false" />
  </ItemGroup>
</Project>
```

Complete working examples are in `src/sample-plugin/` and `src/example-title/`.

### Guidelines

- **Hooks must be named `public static` methods**, never lambdas. A compiler-generated closure (`<GetPatches>b__2_0`) makes Harmony fail even though an identical named method works.
- **The two sides patch different methods**: the official launcher is VB (`PCL.Application.Main`), PCL-CE is C# (`PCL.Program.Main`).
- **CE loads assemblies late**: the startup hook runs before `Main`, so `PCL.Core` and friends do not exist yet. The CE host subscribes to `AssemblyLoad` and applies patches once the target type appears.
- **Patch logic is largely not shareable.** Only the framework and dispatch are common; the concrete patches are two separate sets.
- **Use `hostVersion` to tell them apart**: both assemblies are named `Plain Craft Launcher 2`, so only the version distinguishes them (2.99.x vs 2.15.x).

---

## Building from source

Only the .NET SDK is required (the net48 reference assemblies are restored from NuGet, so Visual Studio is not needed):

```powershell
git clone <this repository>
cd PCL-Harmony
.\build.ps1
```

`build.ps1` will:

1. `dotnet build src\loader\PclHarmony.csproj -c Release`
2. `dotnet build src\sample-plugin\Example.csproj -c Release`
3. refresh `dist\PclHarmony\PclHarmony.dll`, `dist\PclHarmony\0Harmony.dll` and `dist\plugins\Example.dll`, `dist\plugins\Example-Title.dll`
4. verify the assembly name and version of all three artifacts

You can then install from `dist\` with `install.ps1 -TargetDir <PCL folder>`.

Pushing to `main` builds and publishes a **pre-release** (`v<version>-dev.<run_number>`); pushing a `v*` tag publishes a **stable release** and additionally attaches `project.json` and `SHA256SUMS.txt`. See `.github/workflows/ci.yml`.

---

## Known limitations

These are **real** constraints; understand them before relying on this project:

1. **Tied to the exact method signatures and IL of specific launcher versions.** The official side targets PCL 2.99.0.0, the CE side 2.15.1.0. After either upgrade, the matching plugins will likely need rework.
2. **The sample plugins are reference implementations, not product features.** They show how a plugin plugs in; use them as needed.
3. **Plugins run with full trust.** A plugin executes inside the launcher's AppDomain with the launcher's own privileges — the equivalent of arbitrary code execution. **Only install DLLs you trust.**
4. **This is a runtime patch, not an official extension mechanism.** The official side injects an `AppDomainManager` and edits `exe.config`; the CE side uses a `DOTNET_STARTUP_HOOKS` startup hook. Neither launcher provides a plugin interface.
5. **On PCL-CE you must start through the generated wrapper script.** Double-clicking the exe will not load plugins: the startup hook can only arrive via an environment variable, and the CE bundle ignores an external runtimeconfig.json. The launcher binary is never modified.
6. **A cross-launcher plugin's patch code is effectively two sets.** The internal types and methods differ completely; only the contract and dispatch are shared.

---

## Licence and attribution

- **Loader and sample plugin sources in this repository**: MIT licensed, see [`LICENSE`](./LICENSE).
- **Lib.Harmony**: MIT licensed, see [pardeike/Harmony](https://github.com/pardeike/Harmony). The `0Harmony.dll` distributed in releases (the net48 build of Lib.Harmony 2.4.2) is redistributed under that licence.
- **Plain Craft Launcher 2**: copyright its original authors. PCL is **not** released under a conventional open-source licence — its `LICENCE` is a limited distribution licence (see the `LICENCE` file in the PCL repository). This repository **does not redistribute PCL**, nor does it contain PCL's source; the loader merely patches your local copy of PCL at runtime. Check both your local regulations and PCL's licence terms before use.
