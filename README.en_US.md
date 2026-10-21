# PCL Harmony

**Harmony plugin loading for [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL).**

[简体中文](README.md) | English

[How it works](#how-it-works) · [Installing](#installing) · [Uninstalling](#uninstalling) · [Writing a plugin](#writing-a-plugin) · [Building from source](#building-from-source) · [Known limitations](#known-limitations)

---

## What this is

PCL Harmony is a **plugin loader**: it takes control while Plain Craft Launcher 2 ("PCL") starts up and injects [Harmony](https://github.com/pardeike/Harmony) into PCL's AppDomain, so that third-party plugins can patch PCL methods at runtime.

The repository also contains a **sample plugin** (`src/sample-plugin/`) that demonstrates how a plugin plugs in. The sample is reference material only; this document does not describe what it does.

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
src/sample-plugin/            sample plugin C# sources (net48 -> Example.dll)
src/example-title/             sample plugin: title-bar badge (net48 -> Example-Title.dll)
install.ps1                one-shot installer (defaults to the current directory)
uninstall.ps1              one-shot uninstaller
build.ps1                  builds three projects and refreshes dist\
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

`install.ps1` performs three steps and is safe to re-run:

| Step | Action |
| --- | --- |
| 1 | Copies `PclHarmony.dll` and `0Harmony.dll` next to the launcher executable |
| 2 | Creates an **empty** `plugins\` folder (skipped when it already exists) |
| 3 | Writes / merges the three config parts described above into `Plain Craft Launcher 2.exe.config` |

The script locates `PclHarmony.dll` and `0Harmony.dll` automatically: first next to itself (the layout of a release archive), then in `dist\PclHarmony\`, then in `src\loader\bin\Release\`.

**`install.ps1` only creates `plugins\`, it never puts a plugin in it.** The archive's own `plugins\` is just where the sample plugins come from - the script does not copy it across. To use a plugin, copy its DLL into the folder yourself.

After installing, just start PCL. The loader writes `PclHarmony.log` into the PCL folder so you can confirm it attached.

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

1. **Tied to the exact method signatures and IL of PCL 2.99.0.0.** Both the loader and the sample plugin were written against that version. After a PCL upgrade, plugins will likely need rework.
2. **The sample plugin exists only to show how a plugin plugs in.** It is reference material, not a product feature; this repository neither documents nor promises any specific behaviour of it.
3. **Plugins run with full trust.** A plugin executes inside PCL's AppDomain with PCL's own privileges — the equivalent of arbitrary code execution. **Only install DLLs you trust.**
4. **This is a runtime patch, not an official extension mechanism.** It relies on injecting an `AppDomainManager` and editing `exe.config`; PCL itself provides no plugin interface.

---

## Licence and attribution

- **Loader and sample plugin sources in this repository**: MIT licensed, see [`LICENSE`](./LICENSE).
- **Lib.Harmony**: MIT licensed, see [pardeike/Harmony](https://github.com/pardeike/Harmony). The `0Harmony.dll` distributed in releases (the net48 build of Lib.Harmony 2.4.2) is redistributed under that licence.
- **Plain Craft Launcher 2**: copyright its original authors. PCL is **not** released under a conventional open-source licence — its `LICENCE` is a limited distribution licence (see the `LICENCE` file in the PCL repository). This repository **does not redistribute PCL**, nor does it contain PCL's source; the loader merely patches your local copy of PCL at runtime. Check both your local regulations and PCL's licence terms before use.
