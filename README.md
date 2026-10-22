# PCL Harmony

**为 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL) 提供 Harmony 插件加载能力。**

简体中文 | [English](README.en_US.md)

[原理](#原理) · [安装](#安装) · [卸载](#卸载) · [编写插件](#编写插件) · [从源码构建](#从源码构建) · [已知限制](#已知限制)

---

## 这是什么

PCL Harmony 是一个 **插件加载器**：它在启动器启动的过程中取得执行权，把 [Harmony](https://github.com/pardeike/Harmony) 注入到启动器的 AppDomain 内，从而让第三方插件可以在运行时对启动器的方法打补丁。

**同时支持两个启动器**：

| | 官方 PCL | PCL-CE |
| --- | --- | --- |
| 可执行文件 | `Plain Craft Launcher 2.exe` | `PCL2_CE_*.exe` |
| 目标框架 | net48（CLR 4） | net10（CoreCLR） |
| 取得执行权的方式 | `AppDomainManager` 注入 | .NET 启动钩子（startup hook） |
| 是否修改启动器本体 | 否（只写 `exe.config`） | 否（靠包装脚本传环境变量） |

两者运行时互不兼容，因此 `libs/` 下按目标各放一套载荷，`install.ps1` 会自动识别并选用。插件若只针对其中一个启动器，按普通插件写即可；若想**一份插件两端通用**，见「[跨启动器插件](#跨启动器插件)」。

仓库同时包含**示例插件**（`src/sample-plugin/`、`src/example-title/`、`src/example-shared/`），用于演示插件如何接入。示例插件只作参考用途，本文不介绍它们的具体功能。

---

## 原理

### 1. 在 Main 之前取得执行权

安装脚本会在 PCL 目录下写入 `Plain Craft Launcher 2.exe.config`，其中包含两个关键节点：

```xml
<runtime>
  <appDomainManagerAssembly value="PclHarmony" />
  <appDomainManagerType value="PclHarmony.Entry" />
</runtime>
```

CLR 在创建 AppDomain 时会先实例化 `PclHarmony.Entry`（它继承自 `AppDomainManager`），**早于 PCL 自己的 `Main()`**。加载器在 `InitializeNewDomain` 里只做最轻的动作——记录目录、订阅 `AssemblyLoad` 事件——**不碰文件系统**。这一点是硬性要求：该方法运行时 CLR 尚未完成 AppDomain 初始化，此时做 I/O 会污染 PCL 自己的启动流程。

### 2. 在目标程序集加载时应用补丁

当 `AssemblyLoad` 事件报告 `Plain Craft Launcher 2` 程序集已加载时，加载器依次执行：

1. 安装内嵌依赖解析器（见下一节）；
2. 扫描 `<exe 所在目录>\plugins\*.dll`，用 `Assembly.LoadFrom` 载入；
3. 找出实现了 `PclHarmony.IPlugin` 的类型，实例化并调用其 `Load(Harmony, Assembly)`，把目标程序集交给插件。

此时 PCL 的 `Main()` 还没开始跑，所以插件打上的补丁对整次运行全程有效。

### 3. 为什么 exe.config 必须带 `UseLegacyPathHandling=false`

安装脚本写入的配置里有这样一段：

```xml
<AppContextSwitchOverrides
    value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />
```

**这一项不能省。** 一旦通过 `appDomainManagerAssembly` 注入，如果不同时关掉 legacy 路径处理，CLR 会回退到旧的路径规则，PCL 构造的 `\\?\` 扩展前缀路径会在校验中被判为非法字符，启动器随即弹出 **「运行环境错误」** 并退出。开启该开关后 PCL 可正常进入主窗口（窗口标题为 `Plain Craft Launcher　`，末尾是一个 U+3000 全角空格）。

### 4. 为什么还要修 TLS 默认值

注入 `AppDomainManager` 会让 CLR 把宿主当作 “v4 之前的旧式框架应用”，除了上一条的路径处理，还会带来第二个副作用：

**`ServicePointManager.SecurityProtocol` 停留在 `Ssl3, Tls`，拿不到 TLS 1.2+。** 于是所有 HTTPS 请求都以 `SecureChannelFailure` 失败，启动器显示「你的网络环境不佳，请稍后再试，或使用 VPN 改善网络环境。」——下载页因此一直是空的。

PCL 自己不会修这个问题：它只在 `Environment.OSVersion < 6.2`（Windows 8 之前）时才设置 `SecurityProtocol`，现代 Windows 上它完全依赖 CLR 默认值，而默认值已经被注入带偏了。

**加载器在 `InitializeNewDomain` 里主动补齐 TLS 1.2 / 1.3**，并且只做“添加”——不会移除宿主已配置的协议。这一步发生在 PCL 的 `Main()` 之前，早于任何网络请求。修复结果会写进 `PclHarmony.log`：

```
TLS: SecurityProtocol Ssl3, Tls -> Ssl3, Tls, Tls12, Tls13
```

之所以放在代码里而不是只靠 config 开关（`Switch.System.Net.DontEnableSchUseStrongCrypto=false`），是因为该项只在极早期读取一次，config 被手工改动或合并异常时就会静默失效；写在加载器里更可靠。

### 5. 原生依赖解析：为什么加载器要自己实现 AssemblyResolve

PCL 把它的全部依赖（`MeloongCore`、`MeloongCore.Wpf`、`PCLCS`、`Newtonsoft.Json`、`NAudio`、`Ookii.Dialogs.Wpf`、`Imazen.WebP`、`CacheCow.*`、`ThrottleDebounce`、`System.Net.Http.Formatting`、`Microsoft.Win32.Registry`，共 13 个）以 `byte[]` 形式内嵌在自身的清单资源 `PCL.Resources.resources` 里，并在 `Application.New()` 中注册 `AssemblyResolve` 处理器按需解出。

问题在于：**Harmony 需要在 `Application.New()` 运行之前就解析被补丁方法的元数据**，此时 PCL 的解析器还不存在，这些依赖会抛 `FileNotFoundException`。因此加载器必须自己提前实现一份同款解析器（`EmbeddedResolver`），从同一份资源里按前缀取出 `byte[]` 并 `Assembly.Load`。

### 6. 项目结构

```
project.json               版本清单（唯一版本来源：name / version / description）
Directory.Build.props      把 project.json 的 version 注入所有项目的程序集版本

src/contract/              跨运行时插件契约（netstandard2.0）
  Contract.cs              IDeclarativePlugin / PatchSpec

src/loader/                官方 PCL 加载器（net48，输出 PclHarmony.dll）
  Entry.cs                 AppDomainManager 入口，订阅 AssemblyLoad
  EmbeddedResolver.cs      内嵌依赖解析（PCL.Resources.resources）
  PluginHost.cs            plugins\ 扫描、插件实例化与加载（含声明式插件）
  LoaderLog.cs             延迟初始化的低开销日志（PclHarmony.log）
  IPlugin.cs               原生插件接口
  PluginApi.cs             暴露给插件的辅助 API

src/ce-host/               PCL-CE 加载器（net8，输出 PclHarmony.CeHost.dll）
  StartupHook.cs           启动钩子入口（全局 StartupHook 类型）
  Host.cs                  plugins\ 扫描、按 PatchSpec 应用补丁

src/sample-plugin/         示例插件（net48，输出 Example.dll）
src/example-title/         示例插件：标题栏标记（net48，输出 Example-Title.dll）
src/example-shared/        示例插件：跨启动器通用（netstandard2.0，输出 Example.Shared.dll）

install.ps1                一键安装（自动识别目标；省略 -TargetDir 时用当前目录）
uninstall.ps1              一键卸载
build.ps1                  构建全部项目并刷新 dist\ 与 libs\
libs/official/             官方 PCL 载荷（net48）
libs/ce/                   PCL-CE 载荷（net10）
dist/                      构建产物（由 build.ps1 生成，不纳入版本控制）
```

**版本号只在 `project.json` 里改**。`Directory.Build.props` 会读取其中的 `version` 并写入程序集版本，所以二进制、Release 标签和清单不会互相漂移；CI 还会在构建后读回实际写入的版本做校验，不一致直接失败。

版本在 CI 里的流转：

| 事件 | 发布标签 | 产物里的版本 |
| --- | --- | --- |
| 推送到 `main` | 预发布 `v1.0.0-dev.<run>` | `1.0.0-dev.<run>`（**不是** `1.0.0`） |
| 推 `v1.0.0` 标签 | 正式版 `v1.0.0` | `1.0.0` |

`AssemblyVersion` / `FileVersion` 是四段数字，放不下 `-dev.<run>` 后缀，所以只取数字核心（`1.0.0`）；完整版本串在 `InformationalVersion` 里（例如 `1.0.0-dev.15+<commit>`）。要看一个产物到底是什么版本，读 `InformationalVersion` 而不是 `AssemblyVersion`。

发布正式版（推 `v*` 标签）时，除了 `PCL-Harmony.zip` 还会附加两个文件：

- `project.json` —— 本次构建用的版本清单；
- `SHA256SUMS.txt` —— 压缩包的 SHA-256 校验值。

正式版发布成功后，CI 会自动把 `project.json` 的版本推进到下一个 patch（`1.0.0` → `1.0.1`）并提交回 `main`，这样下一次普通推送不会重复构建已经发布的版本。预发布标签不会推进版本。该步骤是幂等的：只有当 `project.json` 仍等于刚发布的版本时才会 bump，因此重跑 job 或手动 bump 都不会重复推进。

本仓库不包含 PCL 本体或其源码。

---

## 安装

### 前置条件

- Windows
- 官方 PCL：一份 **PCL 2.99.0.0** 的安装目录 + [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)（PCL 本身即基于它运行）
- PCL-CE：一份 PCL-CE 的安装目录 + [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（CE 需要它才能启动）

### 使用 Releases（推荐）

1. 从 Releases 下载 `PCL-Harmony.zip` 并解压，得到：

   ```
   install.ps1   uninstall.ps1   README.md
   libs\official\   PclHarmony.dll   PclHarmony.Contract.dll   0Harmony.dll
   libs\ce\         PclHarmony.CeHost.dll   PclHarmony.Contract.dll   0Harmony.dll
   plugins\Example.dll   plugins\Example-Title.dll   plugins\Example.Shared.dll
   ```

2. 关闭正在运行的启动器。
3. 把压缩包解压到**启动器目录**（和 exe 放一起），然后在该目录打开 PowerShell：

   ```powershell
   .\install.ps1
   ```

   `-TargetDir` 可省略，省略时就用当前目录。要装到别处则显式指定：

   ```powershell
   .\install.ps1 -TargetDir "D:\你的\PCL 目录"
   ```

`install.ps1` 会**自动识别目标**：看到 `Plain Craft Launcher 2.exe` 按官方 PCL 处理，看到 `PCL2_CE_*.exe` 按 PCL-CE 处理，并从 `libs\` 取对应载荷。

#### 官方 PCL

| 步骤 | 动作 |
| --- | --- |
| 1 | 把 `PclHarmony.dll`、`PclHarmony.Contract.dll`、`0Harmony.dll` 复制到 exe 旁边 |
| 2 | 创建**空的** `plugins\` 目录（已存在则跳过） |
| 3 | 写入 / 合并 `Plain Craft Launcher 2.exe.config` 中前述三段配置 |

安装完成后直接启动 PCL 即可。

#### PCL-CE

CE 的 exe 是 .NET apphost，**无法注入 `AppDomainManager`**，因此改用 .NET 官方的启动钩子（startup hook）：

| 步骤 | 动作 |
| --- | --- |
| 1 | 复制 `PclHarmony.CeHost.dll`、`PclHarmony.Contract.dll`、`0Harmony.dll` |
| 2 | 生成 `Run-with-PCL-Harmony.cmd`（设置 `DOTNET_STARTUP_HOOKS` 后启动 CE） |
| 3 | 创建**空的** `plugins\` 目录 |

**CE 必须通过 `Run-with-PCL-Harmony.cmd` 启动，直接双击 exe 不会加载插件。** 原因是启动钩子只能通过环境变量传入：CE 是单文件 bundle，配置已内嵌，外部 `runtimeconfig.json` 会被忽略。启动器本体不会被修改。

> CE 是单实例程序。若已有 CE 在运行，新实例会立刻正常退出（exit code 0），这是 CE 自身的行为，不是安装失败。

两个启动器都会在各自目录下写一份 `PclHarmony.log`，可以从中确认加载器是否生效。

**`install.ps1` 只创建 `plugins\`，不往里放任何插件。** 压缩包自带的 `plugins\` 只是示例插件的来源，脚本不会把它复制到目标目录；要用哪个插件，手动把对应 DLL 复制进去即可。

### 放置插件

插件就是普通的 `.dll`，**直接放进启动器目录下的 `plugins\` 文件夹**即可：

```
D:\你的\PCL 目录\
  Plain Craft Launcher 2.exe
  Plain Craft Launcher 2.exe.config
  PclHarmony.dll
  0Harmony.dll
  PclHarmony.log
  plugins\
    Example.dll            <- 示例插件
    Example-Title.dll      <- 示例插件：标题栏标记
    你的插件.dll         <- 你自己的插件
```

几点说明：

- 只扫描 `plugins\` 的**第一层**，不支持子目录。
- 按文件名排序依次加载，每个 DLL 互不影响。
- **插件只应依赖、不应自带** `0Harmony.dll` 或 `PclHarmony.dll`。如果插件自己复制了一份这两个 DLL，会加载出第二个程序集标识，导致 `IPlugin` 类型转换失败、Harmony 补丁打到错误的目标上。
- 插件需要但 `plugins\` 里没有的依赖，加载器会依次到 PCL 目录和 `plugins\` 里按文件名查找。

### 故障排查

| 现象 | 说明 |
| --- | --- |
| 启动时弹出「运行环境错误」 | `AppContextSwitchOverrides` 没写进 config，或被后续操作覆盖。重跑 `install.ps1`。 |
| PCL 正常启动，但没有 `PclHarmony.log` | 加载器没被加载。检查 config 里的 `appDomainManagerAssembly` / `appDomainManagerType`，以及两个 DLL 是否与 exe 同目录。 |
| 有日志但插件没生效 | 看日志里 `plugin: ... FAILED` 或 `skipping` 之类的行，通常是插件没实现 `IPlugin`、缺无参构造函数，或依赖缺失。 |
| 想临时禁用加载器 | 设环境变量 `PCLHARMONY_DISABLE=1` 后启动 PCL。 |

日志中一次正常的插件加载大致长这样：

```
PclHarmony loader 1.0.0.0 attached (ADM PclHarmony.Entry)
target assembly loaded: Plain Craft Launcher 2, Version=2.99.0.0, ...
embedded assembly resolver installed (resource "PCL.Resources.resources", 13 prefixes)
plugin: loading '<名称>' (...) from <文件名>.dll
plugin: loaded '<名称>' OK, harmony="<名称>", patch count=N
plugins: N plugin(s) loaded from M dll(s)
```

---

## 卸载

关闭 PCL，然后：

```powershell
.\uninstall.ps1 -TargetDir "D:\你的\PCL 目录"
```

它会：

1. 删除 `PclHarmony.dll` 与 `0Harmony.dll`；
2. 从 config 中移除 `appDomainManagerAssembly` / `appDomainManagerType`；
3. 移除安装时写入的两个 `AppContextSwitchOverrides` 开关（若因此空了则连 `<AppContextSwitchOverrides>` 与空 `<runtime>` 一起删掉）。

config 里其他内容一律保留。`plugins\` 文件夹与 `PclHarmony.log` **默认保留**（那里放的是你自己的插件），需要一并清理时加参数：

```powershell
.\uninstall.ps1 -TargetDir "D:\你的\PCL 目录" -RemovePlugins -RemoveLog
```

卸载后 PCL 会像从未安装过一样正常启动。

---

## 编写插件

插件分两类，按你要支持几个启动器选：

| | 原生插件（`IPlugin`） | 跨启动器插件（`IDeclarativePlugin`） |
| --- | --- | --- |
| 目标框架 | net48 | **netstandard2.0** |
| 可运行于 | 仅官方 PCL | 官方 PCL **和** PCL-CE |
| 插件内能否直接用 Harmony | 可以 | **不能**，只能声明补丁点 |
| 适用场景 | 只针对官方 PCL | 想一份 DLL 两端通用 |

只针对官方 PCL 写插件时用前者，最直接；想两端通用则用后者，见「跨启动器插件」。

### 插件接口

插件是一个 net48 类库，其中至少有一个类型实现 `PclHarmony.IPlugin`，并且具备**公开无参构造函数**：

```csharp
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    public interface IPlugin
    {
        /// <summary>插件名称，同时作为 Harmony 实例 id。</summary>
        string Name { get; }

        /// <summary>
        /// 在目标程序集加载后、PCL 的 Main() 运行前调用一次。
        /// 在此打上的补丁对整次运行有效。
        /// </summary>
        void Load(Harmony harmony, Assembly target);
    }
}
```

`Load` 收到的 `harmony` 是一个已按插件 `Name` 命名好的 Harmony 实例，补丁因此可以按插件归属。`target` 是 `Plain Craft Launcher 2` 程序集，PCL 的类型要通过它来反射获取。

### 辅助 API

`PclHarmony.PluginApi` 提供几个便捷成员：

| 成员 | 说明 |
| --- | --- |
| `Assembly Target` | 目标程序集（很早期可能为 `null`） |
| `Harmony SharedHarmony` | 加载器全局共享的 Harmony 实例，id 为 `pcl.harmony.pluginloader` |
| `Harmony GetHarmony(string id)` | 按 id 获取（并缓存）一个 Harmony 实例；`id` 为空时返回共享实例 |
| `void Log(string message)` | 写入 PCL 目录下的 `PclHarmony.log` |

### 最小示例

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

            // 反射定位目标方法（PCL 的类型都在 target 程序集里）
            var type = target.GetType("PCL.ModBase", false);
            var method = type == null ? null : type.GetMethod("某个方法",
                BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                PluginApi.Log("[MyPlugin] 目标方法未找到，跳过");
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

### 编写要点

- **net48**：插件必须与 PCL 使用同一目标框架。
- **`0Harmony` 与 `PclHarmony` 必须 `Private=false`**（或 `ExcludeAssets="runtime"`）。参见上文“放置插件”。
- **强烈建议用反射而非直接引用 PCL 类型**：PCL 不是可被广泛引用的库，直接编译期依赖它会让插件与具体版本强绑定。
- **失败必须被隔离**：加载器会捕获每个插件的异常并撤销它半途打上的补丁，但插件自身仍应尽量在 `Load` 内做好防御。
- **写日志用 `PluginApi.Log`**，便于用户把问题贴给你。
- 可参考 `src/sample-plugin/` 里的示例插件实现。

---

## 跨启动器插件

想让**一个 DLL 同时运行在官方 PCL 和 PCL-CE 上**，需要绕开两个硬约束。

### 约束一：两端唯一的共同目标是 netstandard2.0

| 目标框架 | 官方 PCL（CLR 4） | PCL-CE（CoreCLR） |
| --- | --- | --- |
| net48 | 可以 | 不可以 |
| net10.0 | 不可以 | 可以 |
| **netstandard2.0** | **可以** | **可以** |

所以跨启动器插件必须编译为 **netstandard2.0**。

### 约束二：netstandard2.0 插件不能自己调用 Harmony

Lib.Harmony 2.4.2 **没有 netstandard2.0 构建**（只有 net35 / net452 / net472 / net48 / net5.0…net10.0 / netcoreapp3.x）。而且即使反射加载 net48 版，从 netstandard2.0 调用方驱动它也会失败：

```
TypeLoadException: "MonoMod.Utils.Cil.ILGeneratorProxy[TTarget]" 上的
GenericArguments[0] ("MonoMod.Utils.Cil.CecilILGenerator") 与类型参数"TTarget"的约束冲突
```

同样的补丁逻辑，从**原生 net48 程序集**调用就完全正常——这是调用方运行时导致的差异，不是用法问题。

### 解决办法：插件只声明，宿主来执行

跨启动器插件实现 `PclHarmony.Contract.IDeclarativePlugin`，**只描述要打哪些补丁**；Harmony 由各自的宿主按自己的运行时应用（官方侧用 net48 版，CE 侧用 net10 版）。

```csharp
using System.Collections.Generic;
using PclHarmony.Contract;

public sealed class MySharedPlugin : IDeclarativePlugin
{
    public string Name { get { return "My.Shared.Plugin"; } }

    public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion)
    {
        // hostKind: "official" 或 "ce"；按需分支
        if (hostKind == "ce")
        {
            yield return new PatchSpec
            {
                TypeName       = "PCL.Program",     // 目标类型
                MethodName     = "Main",            // 目标方法
                HookKind       = "postfix",         // "prefix" 或 "postfix"
                HookTypeName   = typeof(MySharedPlugin).FullName,
                HookMethodName = "CeAfterMain",     // 必须是真实静态方法
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

    // hook 必须是 public static 具名方法
    public static void CeAfterMain()       { /* ... */ }
    public static void OfficialAfterMain() { /* ... */ }
}
```

工程文件只需引用契约，**不要引用 Harmony**：

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

完整可运行示例见 `src/example-shared/`。

### 编写要点

- **hook 必须是具名的 `public static` 方法**，不能是 lambda。编译器生成的闭包（形如 `<GetPatches>b__2_0`）交给 Harmony 会失败，即使函数体完全相同。
- **两端的目标方法名通常不同**：官方 PCL 是 VB 的 `PCL.Application.Main`，PCL-CE 是 C# 的 `PCL.Program.Main`，补丁目标要分别指定。
- **CE 侧存在加载时机问题**：启动钩子在 `Main` 之前运行，此时 `PCL.Core` 等程序集尚未加载。CE 宿主会订阅 `AssemblyLoad`，等目标程序集出现后再应用补丁，因此 `TypeName` 指向稍后才加载的类型也能生效。
- **补丁逻辑基本无法共享**：两端的内部类型和方法是两套代码，能共享的是框架与调度，具体 patch 仍要各写一份。
- **`hostVersion` 可用来做版本判断**：官方 PCL 是 `2.99.x`，PCL-CE 是 `2.15.x`，两者程序集同名（`Plain Craft Launcher 2`），只能靠版本号区分。

---

## 从源码构建

需要 .NET SDK 8（net48 的参考程序集由 NuGet 还原，无需安装 Visual Studio）。

```powershell
git clone <本仓库地址>
cd PCL-Harmony
.\build.ps1
```

`build.ps1` 会构建全部项目，并刷新两个输出目录：

| 输出 | 内容 |
| --- | --- |
| `dist\` | `PclHarmony.dll`、`0Harmony.dll`、`plugins\Example*.dll` |
| `libs\official\` | 官方 PCL 载荷（net48） |
| `libs\ce\` | PCL-CE 载荷（net8 宿主 + net10 Harmony） |

最后校验各产物的程序集名称与版本。

之后即可用 `libs\` 配合 `install.ps1 -TargetDir <启动器目录>` 安装。

> **CE 宿主为什么是 net8 而不是 net10？** 它运行在 CE 的 .NET 10 运行时上，但 net8 程序集可以正常加载到 net10 运行时（已实测），而 net10 目标需要 .NET 10 SDK 才能编译。若你本机装了 .NET 10 SDK，可把 `src\ce-host\CeHost.csproj` 的 `TargetFramework` 改成 `net10.0`。

推送到 `main` 会自动构建并发布一个 **预发布**（`v<version>-dev.<run_number>`）；推送 `v*` 标签则发布**正式版**，并额外附带 `project.json` 与 `SHA256SUMS.txt`。详见 `.github/workflows/ci.yml`。

---

## 已知限制

以下限制都是**真实存在**的，请在依赖本方案前先了解：

1. **依赖具体启动器版本的精确方法签名与 IL 形态。** 官方侧针对 PCL 2.99.0.0、CE 侧针对 2.15.1.0 逆向实现。任一侧升级后，对应插件很可能需要重做。
2. **示例插件只用于演示插件如何接入。** 它们是参考实现，不是产品功能；本仓库不描述、也不承诺它们的具体行为。
3. **插件以完全信任方式运行。** 插件在启动器的 AppDomain 内执行，拥有与启动器相同的权限，等同于任意代码执行。**只安装你信任的 DLL。**
4. **这是运行时补丁方案，不是启动器的官方扩展机制。** 官方 PCL 侧依赖注入 `AppDomainManager` 并修改 `exe.config`；CE 侧依赖 `DOTNET_STARTUP_HOOKS` 启动钩子。两者都未提供插件接口。
5. **CE 侧必须通过生成的包装脚本启动。** 直接双击 exe 不会加载插件——启动钩子只能经环境变量传入，而 CE 是单文件 bundle，外部配置文件会被忽略。启动器本体始终不被修改。
6. **跨启动器插件的补丁代码基本是两份。** 两端内部类型与方法完全不同，能共享的只有契约与调度逻辑。

---

## 许可与归属

- **本仓库的加载器与示例插件源码**：MIT 许可，见 [LICENSE](./LICENSE)。
- **Lib.Harmony**：MIT 许可，见 [pardeike/Harmony](https://github.com/pardeike/Harmony)。Release 中分发的 `0Harmony.dll`（Lib.Harmony 2.4.2，官方侧为 net48 构建、CE 侧为 net10 构建）依其原许可分发。
- **Plain Craft Launcher 2**：其版权归原作者所有。PCL **不是**以常见的开源许可发布——其许可证为一份限定分发范围的许可（见 PCL 仓库中的 `LICENCE`）。本仓库**不重新分发 PCL 本体**，也不包含 PCL 的源码；加载器只是在你本地的 PCL 上于运行时打补丁。使用前请自行确认你所在地区的法律法规与 PCL 的许可条款。
