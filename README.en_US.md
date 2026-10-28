# PCL Harmony

**Harmony runtime plugin loader for [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL).**

[简体中文](README.md) | English

[How It Works](#how-it-works) · [Installation](#installation) · [Uninstallation](#uninstallation) · [Writing a Plugin](#writing-a-plugin) · [Building from Source](#building-from-source) · [Caveats & Limitations](#caveats--limitations)

---

## What Is This?

PCL Harmony is a **plugin loader** that hooks into the launcher at startup and injects [Harmony](https://github.com/pardeike/Harmony) into its AppDomain. This lets third-party plugins intercept and patch internal launcher methods on the fly—**without modifying the launcher executable on disk**.

**Supports both the Official and Community Edition (CE) builds**:

|  | Official PCL | PCL-CE |
| --- | --- | --- |
| Executable | `Plain Craft Launcher 2.exe` | `PCL2_CE_*.exe` |
| Runtime | .NET Framework 4.8 (CLR 4) | .NET 10 (CoreCLR) |
| Injection Method | `AppDomainManager` injection | .NET Startup Hook |
| Touches original `.exe`? | No (drops an `exe.config` file) | No (passes env vars via a wrapper script) |

Because the two runtimes are mutually incompatible, `libs/` ships separate binaries for each target. `install.ps1` automatically detects which launcher you have and installs the matching files.

If you're only targeting one edition, you can write a standard plugin. If you want **a single `.dll` that runs on both**, see [Cross-Launcher Plugins](#cross-launcher-plugins).

This repo also includes two **sample plugins** (`src/sample-plugin/` and `src/example-title/`) showing how declarative plugins work: defining patch targets, branching by launcher edition, and injecting a custom WPF control into the title bar. Both target `netstandard2.0`, so the exact same binary runs on either launcher out of the box.

---

## How It Works

### 1. Hijacking Execution Before `Main`

For Official PCL, the installer generates a `Plain Craft Launcher 2.exe.config` file in the launcher directory with two key entries:

```xml
<runtime>
  <appDomainManagerAssembly value="PclHarmony" />
  <appDomainManagerType value="PclHarmony.Entry" />
</runtime>

```

When the CLR spins up the AppDomain, it instantiates `PclHarmony.Entry` (which inherits from `AppDomainManager`) **before PCL's own `Main()` method even runs**.

During `InitializeNewDomain`, the loader does the bare minimum: it records the working directory and subscribes to the `AssemblyLoad` event, **strictly avoiding any file I/O**. That's a hard rule—the CLR hasn't finished initializing the AppDomain at this point, and touching the filesystem this early corrupts PCL's own startup sequence.

### 2. Patching When the Target Assembly Loads

Once the `AssemblyLoad` event fires for the `Plain Craft Launcher 2` assembly, the loader does three things in order:

1. Hooks up a custom assembly resolver for embedded dependencies (see #5 below).
2. Scans `<exe_dir>\plugins\*.dll` and loads everything via `Assembly.LoadFrom`.
3. Finds all types implementing `PclHarmony.IPlugin`, instantiates them, and calls `Load(Harmony, Assembly)`, handing over the launcher assembly.

Since PCL's `Main()` still hasn't run yet, any patches applied here stay active for the entire lifecycle of the process.

### 3. Why `UseLegacyPathHandling=false` Is Mandatory in `exe.config`

The installer also writes this switch into the config file:

```xml
<AppContextSwitchOverrides
    value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />

```

**Do not remove this.** As soon as you inject code via `appDomainManagerAssembly`, the CLR assumes it's running a legacy pre-.NET 4.0 app and falls back to archaic path validation rules. PCL heavily relies on extended-length paths prefixed with `\\?\`; under legacy rules, the CLR flags those as invalid characters, causing PCL to immediately throw a **"Runtime Environment Error"** dialog and crash. Flipping this switch keeps modern path handling intact so PCL can reach its main window (fun fact: the window title is `Plain Craft Launcher ` with a trailing U+3000 full-width space).

### 4. Fixing Broken TLS Defaults

Injecting an `AppDomainManager` triggers a second legacy quirk: because the CLR treats the host as a pre-v4 app, **`ServicePointManager.SecurityProtocol` gets stuck on `Ssl3, Tls`, completely disabling TLS 1.2 and 1.3**.

As a result, every HTTPS request fails with `SecureChannelFailure`, the launcher complains about a "poor network environment," and the download tab stays completely blank.

PCL won't fix this on its own—it only sets `SecurityProtocol` manually if it detects an OS older than Windows 8 (`Environment.OSVersion < 6.2`). On Windows 10/11, it trusts the CLR defaults, which our injection inadvertently broke.

To fix this, **the loader explicitly enables TLS 1.2 and 1.3 inside `InitializeNewDomain**` (appending flags without stripping existing ones) before `Main()` or any network calls occur. You'll see this confirmed in `PclHarmony.log`:

```
TLS: SecurityProtocol Ssl3, Tls -> Ssl3, Tls, Tls12, Tls13

```

We enforce this in code rather than relying solely on the `Switch.System.Net.DontEnableSchUseStrongCrypto=false` config flag because that switch is only read once during early startup—if a user's config file gets mangled or merged improperly, it fails silently. Doing it in code is bulletproof.

### 5. Resolving Embedded Dependencies Early

PCL packs all 13 of its third-party dependencies (`MeloongCore`, `Newtonsoft.Json`, `NAudio`, etc.) as raw `byte[]` blobs inside its manifest resource `PCL.Resources.resources`. Normally, PCL registers its own `AssemblyResolve` handler inside `Application.New()` to unpack them on demand.

Here's the catch: **Harmony needs to inspect the metadata of target methods *before* `Application.New()` runs**. Because PCL's resolver isn't hooked up yet, Harmony hits a `FileNotFoundException` the moment a patched method references one of those libraries.

To get around this, our loader registers its own resolver (`EmbeddedResolver`) ahead of time, pulling the exact same `byte[]` blobs out of `PCL.Resources.resources` and calling `Assembly.Load` before Harmony starts patching.

### 6. Project Layout

```
project.json               Single source of truth for metadata (name / version / description)
Directory.Build.props      Reads version from project.json and stamps it into all assemblies

src/contract/              Cross-runtime plugin contract (netstandard2.0)
  Contract.cs              Defines IDeclarativePlugin and PatchSpec

src/loader/                Official PCL loader (net48, builds PclHarmony.dll)
  Entry.cs                 AppDomainManager entry point; listens to AssemblyLoad
  EmbeddedResolver.cs      Unpacks PCL's embedded dependencies from PCL.Resources.resources
  PluginHost.cs            Scans plugins\, instantiates and loads plugins
  LoaderLog.cs             Lazy-initialized, low-overhead logger (writes PclHarmony.log)
  IPlugin.cs               Native plugin interface for Official PCL
  PluginApi.cs             Helper APIs exposed to plugins

src/ce-host/               PCL-CE loader (net8, builds PclHarmony.CeHost.dll)
  StartupHook.cs           .NET Startup Hook entry point (global StartupHook class)
  Host.cs                  Scans plugins\ and applies PatchSpec definitions

src/sample-plugin/         Sample plugin (netstandard2.0, builds Example.dll)
src/example-title/         Sample plugin: custom title bar badge (netstandard2.0, builds Example-Title.dll)

install.ps1                One-click installer (auto-detects edition; defaults to current dir)
uninstall.ps1              One-click uninstaller
build.ps1                  Builds everything and populates dist\ and libs\
libs/official/             Binaries for Official PCL (net48)
libs/ce/                   Binaries for PCL-CE (net10)
dist/                      Build output (generated by build.ps1, gitignored)

```

**Version numbers are managed exclusively in `project.json**`. `Directory.Build.props` pulls the `version` field at build time and stamps it into every assembly, keeping binaries, Git tags, and manifests in sync. CI also reads back the compiled assembly versions post-build and fails the pipeline if anything mismatches.

How versioning works in CI:

| Trigger | Release Tag | Version Stamped in Binaries |
| --- | --- | --- |
| Push to `main` | Pre-release `v1.0.0-dev.<run>` | `1.0.0-dev.<run>` (**not** `1.0.0`) |
| Push `v1.0.0` tag | Stable `v1.0.0` | `1.0.0` |

Because Windows `AssemblyVersion` and `FileVersion` only accept four-part numbers, they get the numeric core (`1.0.0`), while the full string with `-dev.<run>+<commit>` goes into `InformationalVersion`. Always check `InformationalVersion` if you need to know the exact build of a binary.

When cutting a stable release (pushing a `v*` tag), CI attaches two extra files alongside `PCL-Harmony.zip`:

* `project.json` — the exact manifest used for that build.
* `SHA256SUMS.txt` — SHA-256 checksums for the zip archive.

After a stable release succeeds, CI automatically bumps `project.json` to the next patch version (`1.0.0` → `1.0.1`) and commits it back to `main` so subsequent commits don't collide with an already-released version. Pre-releases don't trigger a bump. This step is idempotent: it only increments if `project.json` still matches the tag that was just published, so re-running a CI job or bumping manually won't double-increment.

*Note: This repository does not contain or distribute the PCL executable or its source code.*

---

## Installation

### Prerequisites

* Windows
* **For Official PCL**: An installation of **PCL 2.99.0.0** + [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) (already installed if PCL runs on your machine).
* **For PCL-CE**: An installation of PCL-CE + the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (required by CE itself).

### Using Releases (Recommended)

1. Grab `PCL-Harmony.zip` from Releases and extract it. You'll get:
```
install.ps1   uninstall.ps1   README.md
libs\official\   PclHarmony.dll   PclHarmony.Contract.dll   0Harmony.dll
libs\ce\         PclHarmony.CeHost.dll   PclHarmony.Contract.dll   0Harmony.dll
plugins\Example.dll   plugins\Example-Title.dll

```


2. Make sure the launcher is closed.
3. Extract the files right into your **launcher directory** (next to the `.exe`), open PowerShell there, and run:
```powershell
.\install.ps1

```


Or run it from anywhere by pointing `-TargetDir` to your launcher folder:
```powershell
.\install.ps1 -TargetDir "D:\Path\To\Your\PCL"

```



`install.ps1` **auto-detects your launcher edition**: if it sees `Plain Craft Launcher 2.exe`, it sets up the Official version; if it sees `PCL2_CE_*.exe`, it sets up the CE version, pulling the right files from `libs\`.

#### What Happens on Official PCL

| Step | Action |
| --- | --- |
| 1 | Copies `PclHarmony.dll`, `PclHarmony.Contract.dll`, and `0Harmony.dll` next to the `.exe` |
| 2 | Creates an **empty** `plugins\` folder (if one doesn't already exist) |
| 3 | Creates or merges the required runtime settings into `Plain Craft Launcher 2.exe.config` |

Once done, just launch PCL normally.

#### What Happens on PCL-CE

Because CE's executable is a native .NET apphost, **`AppDomainManager` injection doesn't work**. Instead, we use .NET's built-in Startup Hook mechanism:

| Step | Action |
| --- | --- |
| 1 | Copies `PclHarmony.CeHost.dll`, `PclHarmony.Contract.dll`, and `0Harmony.dll` |
| 2 | Generates `Run-with-PCL-Harmony.cmd` (which sets `DOTNET_STARTUP_HOOKS` and launches CE) |
| 3 | Creates an **empty** `plugins\` folder |

**Important: For PCL-CE, you must launch the app via `Run-with-PCL-Harmony.cmd`. Double-clicking the `.exe` directly will bypass the plugin loader.**
Because CE is packaged as a single-file bundle with its config baked in, it ignores external `runtimeconfig.json` files. Passing the startup hook via an environment variable in a wrapper script is the only way in without modifying the binary on disk.

> Note: CE enforces a single-instance check. If CE is already running in the background, launching a new instance will immediately exit with code 0. That's normal CE behavior, not a loader crash.

Both editions write a `PclHarmony.log` file in the launcher directory—check it to verify the loader is up and running.

**Note: `install.ps1` only creates an empty `plugins\` directory; it does not copy the sample plugins over.** The `plugins\` folder in the zip is just there if you want to test them out. To enable a plugin, manually drop its `.dll` into your launcher's `plugins\` folder.

### Installing Plugins

Plugins are just standard `.dll` files. **Drop them straight into the `plugins\` folder** inside your launcher directory:

```
D:\Path\To\Your\PCL\
  Plain Craft Launcher 2.exe
  Plain Craft Launcher 2.exe.config
  PclHarmony.dll
  0Harmony.dll
  PclHarmony.log
  plugins\
    Example.dll            <- Sample plugin
    Example-Title.dll      <- Sample plugin: title bar badge
    YourPlugin.dll         <- Your own plugin

```

A few ground rules:

* Only the **top level** of `plugins\` is scanned; subdirectories are ignored.
* Plugins load in alphabetical order by filename and are isolated from one another.
* **Plugins must reference—not bundle—`0Harmony.dll` and `PclHarmony.dll`.** If a plugin ships its own copy of either DLL in `plugins\`, the CLR will load a duplicate assembly identity into memory, breaking `IPlugin` type casting and causing Harmony patches to miss their targets.
* If your plugin relies on extra third-party DLLs, drop them in either the PCL root folder or `plugins\`; the loader searches both locations by filename.

### Troubleshooting

| Symptom | Cause & Fix |
| --- | --- |
| "Runtime Environment Error" popup on launch | `AppContextSwitchOverrides` is missing from `.exe.config` or got overwritten. Re-run `install.ps1`. |
| PCL launches fine, but `PclHarmony.log` isn't created | The loader isn't running. On Official, check `.exe.config` and make sure the core DLLs sit next to the `.exe`. On CE, make sure you're launching via `Run-with-PCL-Harmony.cmd`. |
| Log file exists, but my plugin does nothing | Check `PclHarmony.log` for `FAILED` or `skipping`. Usually means the plugin doesn't implement the interface, lacks a parameterless public constructor, or is missing a dependency. |
| Want to temporarily disable the loader | Set the environment variable `PCLHARMONY_DISABLE=1` before launching PCL. |

A healthy run in `PclHarmony.log` looks like this:

```
PclHarmony loader 1.0.0.0 attached (ADM PclHarmony.Entry)
target assembly loaded: Plain Craft Launcher 2, Version=2.99.0.0, ...
embedded assembly resolver installed (resource "PCL.Resources.resources", 13 prefixes)
plugin: loading '<Name>' (...) from <File>.dll
plugin: loaded '<Name>' OK, harmony="<Name>", patch count=N
plugins: N plugin(s) loaded from M dll(s)

```

---

## Uninstallation

Close PCL and run:

```powershell
.\uninstall.ps1 -TargetDir "D:\Path\To\Your\PCL"

```

The script will:

1. Delete `PclHarmony*.dll`, `0Harmony.dll`, and the CE wrapper script.
2. Strip `appDomainManagerAssembly` and `appDomainManagerType` out of `.exe.config`.
3. Remove the two path-handling switches from `AppContextSwitchOverrides` (and clean up the `<AppContextSwitchOverrides>` / `<runtime>` tags if they're left empty).

Any other custom settings in your config file are left untouched. By default, **`plugins\` and `PclHarmony.log` are kept** so you don't lose your plugins. To wipe those out too, pass the extra flags:

```powershell
.\uninstall.ps1 -TargetDir "D:\Path\To\Your\PCL" -RemovePlugins -RemoveLog

```

Once uninstalled, PCL runs completely stock again.

---

## Writing a Plugin

There are two ways to write a plugin depending on which launcher(s) you want to target:

|  | Native Plugin (`IPlugin`) | Cross-Launcher Plugin (`IDeclarativePlugin`) |
| --- | --- | --- |
| Target Framework | `net48` | **`netstandard2.0`** |
| Runs On | Official PCL only | Both Official PCL **and** PCL-CE |
| Direct Harmony API Access? | Yes | **No** (declarative patch specs only) |
| When to Use | Targeting Official PCL only (simplest) | Shipping a single `.dll` for both editions |

We'll cover Native Plugins first. For universal plugins, skip down to [Cross-Launcher Plugins](#cross-launcher-plugins).

### The Native Plugin Interface

Create a `net48` class library containing at least one class that implements `PclHarmony.IPlugin` with a **public parameterless constructor**:

```csharp
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    public interface IPlugin
    {
        /// <summary>Plugin name, also used as the Harmony instance ID.</summary>
        string Name { get; }

        /// <summary>
        /// Called once after the launcher assembly loads, right before PCL's Main() runs.
        /// Patches applied here stay active for the entire session.
        /// </summary>
        void Load(Harmony harmony, Assembly target);
    }
}

```

The `harmony` instance passed into `Load` is pre-initialized with your plugin's `Name` so patches can be tracked per plugin. `target` is the loaded `Plain Craft Launcher 2` assembly—use reflection against it to locate internal PCL types and methods.

### Helper API (`PluginApi`)

`PclHarmony.PluginApi` exposes a few handy utilities:

| Member | Description |
| --- | --- |
| `Assembly Target` | The launcher assembly (may be `null` if accessed too early) |
| `Harmony SharedHarmony` | Global shared Harmony instance (`pcl.harmony.pluginloader`) |
| `Harmony GetHarmony(string id)` | Gets (and caches) a Harmony instance by ID; returns the shared instance if `id` is null/empty |
| `void Log(string message)` | Appends a line to `PclHarmony.log` in the launcher directory |

### Minimal Example

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

            // Locate the target method via reflection
            var type = target.GetType("PCL.ModBase", false);
            var method = type == null ? null : type.GetMethod("SomeMethod",
                BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                PluginApi.Log("[MyPlugin] Target method not found, skipping");
                return;
            }

            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(MyPlugin).GetMethod(
                    nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: null);
        }

        private static void Before() { /* Runs before target method */ }
    }
}

```

### Best Practices

* **Target `net48**`: Must match Official PCL's runtime.
* **Set `Private=false` (or `ExcludeAssets="runtime"`) on `0Harmony` and `PclHarmony` references**: Never copy them to your output folder (see [Installing Plugins](#installing-plugins)).
* **Use reflection instead of referencing `Plain Craft Launcher 2.exe` directly**: PCL isn't a stable public API. Hard-referencing the binary tightly couples your plugin to a specific build, whereas reflection lets you fail gracefully if a method signature changes.
* **Handle errors gracefully**: While the loader wraps each plugin in a `try-catch` and rolls back partial patches on crash, you should still guard your own `Load` logic.
* **Use `PluginApi.Log**`: Makes debugging infinitely easier when users send you their `PclHarmony.log`.
* Check out `src/sample-plugin/` for a working reference setup.

---

## Cross-Launcher Plugins

Getting **a single `.dll` to run on both Official PCL and PCL-CE** requires working around two runtime constraints.

### Constraint 1: `netstandard2.0` Is the Only Common Ground

| Target Framework | Official PCL (CLR 4) | PCL-CE (CoreCLR) |
| --- | --- | --- |
| `net48` | Yes | No |
| `net10.0` | No | Yes |
| **`netstandard2.0`** | **Yes** | **Yes** |

Your cross-launcher plugin must target **`netstandard2.0`**.

### Constraint 2: `netstandard2.0` Plugins Cannot Call Harmony Directly

Lib.Harmony 2.4.2 **does not ship a `netstandard2.0` build** (only `net35`, `net452`, `net472`, `net48`, `net5.0`–`net10.0`, and `netcoreapp3.x`).

Worse, even if you load the `net48` Harmony binary via reflection, driving it from a `netstandard2.0` assembly crashes at runtime with a generic constraint violation:

```
TypeLoadException: GenericArguments[0] ("MonoMod.Utils.Cil.CecilILGenerator") on
"MonoMod.Utils.Cil.ILGeneratorProxy[TTarget]" violates the constraint of type parameter "TTarget"

```

The exact same patch call works fine when invoked from a **native `net48` assembly**—it's a quirk of how the runtime resolves types across the `netstandard` facade boundary.

### The Workaround: Declarative Patching

Instead of calling Harmony yourself, implement `PclHarmony.Contract.IDeclarativePlugin` and **declare *what* you want patched**. The host loader on each side (running natively on `net48` or `net8`/`net10`) reads your specs and invokes its own native Harmony build on your behalf.

```csharp
using System.Collections.Generic;
using PclHarmony.Contract;

public sealed class MySharedPlugin : IDeclarativePlugin
{
    public string Name { get { return "My.Shared.Plugin"; } }

    public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion)
    {
        // hostKind is either "official" or "ce" — branch as needed
        if (hostKind == "ce")
        {
            yield return new PatchSpec
            {
                TypeName       = "PCL.Program",                  // Target class
                MethodName     = "Main",                         // Target method
                HookKind       = "postfix",                      // "prefix" or "postfix"
                HookTypeName   = typeof(MySharedPlugin).FullName,
                HookMethodName = "CeAfterMain",                  // Must be a real static method
            };
        }
        else
        {
            yield return new PatchSpec
            {
                TypeName       = "PCL.Application",
                MethodName     = "Main",
                HookKind       = "postfix",
                HookTypeName   = typeof(MySharedPlugin).FullName,
                HookMethodName = "OfficialAfterMain",
            };
        }
    }

    // Hooks MUST be named public static methods
    public static void CeAfterMain()       { /* ... */ }
    public static void OfficialAfterMain() { /* ... */ }
}

```

Your `.csproj` only needs to reference the contract—**do not reference Harmony**:

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

See `src/sample-plugin/` and `src/example-title/` for complete, working examples.

### Gotchas for Cross-Launcher Plugins

* **Hooks must be named `public static` methods—no lambdas.** Compiler-generated closure methods (like `<GetPatches>b__2_0`) will be rejected by Harmony, even if the method body is identical.
* **Target names almost always differ between editions**: Official PCL is written in VB.NET (`PCL.Application.Main`), whereas PCL-CE is largely C# (`PCL.Program.Main`). You'll need to specify separate targets per `hostKind`.
* **Delayed assembly loading on CE is handled automatically**: Because CE's startup hook runs before `Main`, assemblies like `PCL.Core` aren't in memory yet. The CE host listens to `AssemblyLoad` and automatically applies your `PatchSpec` the moment the target assembly shows up.
* **You're mostly sharing packaging, not patch logic**: Since internal types and methods differ wildly between Official and CE, you'll still write separate hook implementations for each—this architecture just lets you ship them in a single `.dll`.
* **Use `hostVersion` for version checks**: Official PCL is on `2.99.x` while PCL-CE is on `2.15.x`. Both compile to the same assembly name (`Plain Craft Launcher 2`), so checking `hostVersion` is the cleanest way to tell versions apart.

---

## Building from Source

Requires the **.NET 8 SDK** (reference assemblies for `net48` are pulled automatically via NuGet—no Visual Studio install needed).

```powershell
git clone <repo-url>
cd PCL-Harmony
.\build.ps1

```

`build.ps1` compiles all projects and populates two output directories:

| Directory | Contents |
| --- | --- |
| `dist\` | Release bundle (`PclHarmony.dll`, `0Harmony.dll`, `plugins\Example*.dll`) |
| `libs\official\` | Official PCL payload (`net48`) |
| `libs\ce\` | PCL-CE payload (`net8` host + `net10` Harmony) |

Finally, it verifies assembly names and version numbers across all built artifacts. Once built, run `.\install.ps1 -TargetDir <launcher_dir>` to test locally.

> **Why does the CE host target `net8` instead of `net10`?**
> Even though PCL-CE runs on the .NET 10 runtime, a `net8` assembly loads into a .NET 10 process without issue. Targeting `net8` lets contributors build the entire repo using the widely installed .NET 8 SDK instead of forcing everyone to install the .NET 10 SDK. If you have the .NET 10 SDK installed locally, feel free to change `TargetFramework` to `net10.0` in `src\ce-host\CeHost.csproj`.

---

## Caveats & Limitations

Please keep these trade-offs in mind before relying on this project:

1. **Launcher updates will likely break your plugins.**
Patches rely on exact method signatures and IL structures reverse-engineered from specific builds (Official `2.99.0.0` and CE `2.15.1.0`). When either launcher updates, your plugins will probably need updating too.
2. **Sample plugins are code demos, not end-user features.**
`Example.dll` and `Example-Title.dll` exist solely to show developers how the API works.
3. **Security warning: Plugins run with full trust.**
Plugins execute directly inside the launcher's process with the exact same OS permissions as the launcher itself—equivalent to running arbitrary native code. **Only install `.dll` files from sources you completely trust.**
4. **This is an unofficial runtime hook, not an official plugin API.**
Neither Official PCL nor PCL-CE natively supports plugins. We're forcing our way in via `AppDomainManager` injection and `DOTNET_STARTUP_HOOKS`.
5. **PCL-CE must be started via the generated `.cmd` script.**
Double-clicking the CE `.exe` directly won't load any plugins because single-file bundles ignore external config files, leaving environment variables as the only way to register the startup hook without modifying the binary.
6. **Cross-launcher plugins still require writing two sets of patches.**
Because the Official (VB.NET) and CE (C#) codebases have completely different internals, only the contract and loader plumbing are shared.

---

## License & Attribution

* **Loader & sample plugins in this repository**: Licensed under the MIT License. See [LICENSE](./LICENSE).
* **Lib.Harmony**: Licensed under the MIT License ([pardeike/Harmony](https://github.com/pardeike/Harmony)). The `0Harmony.dll` binaries distributed in Releases (Lib.Harmony 2.4.2, built for `net48` and `net10`) are redistributed under their original license.
* **Plain Craft Launcher 2**: Copyright belongs to its original authors. Note that PCL is **not** distributed under a standard open-source license—it uses a custom license with distribution restrictions (see `LICENCE` in the PCL repository). **This repository does not redistribute the PCL binary or any of its source code**; it only applies in-memory patches to your local installation at runtime. Please ensure your use complies with local laws and PCL's license terms.
