# PCL Harmony

**为 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL) 提供 Harmony 插件加载能力。**

简体中文 | [English](README.en_US.md)

[原理](#原理) · [安装](#安装) · [卸载](#卸载) · [编写插件](#编写插件) · [从源码构建](#从源码构建) · [已知限制](#已知限制)

---

## 这是什么

PCL Harmony 是一个 **插件加载器**：它在 Plain Craft Launcher 2（下称 PCL）启动的过程中取得执行权，把 [Harmony](https://github.com/pardeike/Harmony) 注入到 PCL 的 AppDomain 内，从而让第三方插件可以在运行时对 PCL 的方法打补丁。

仓库同时包含一个**示例插件**（`src/testplugin/`），用于演示插件如何接入。示例插件只作参考用途，本文不介绍它的具体功能。

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
src/loader/                加载器 C# 源码（net48，输出 PclHarmony.dll）
  Entry.cs                 AppDomainManager 入口，订阅 AssemblyLoad
  EmbeddedResolver.cs      内嵌依赖解析（PCL.Resources.resources）
  PluginHost.cs            plugins\ 扫描、插件实例化与加载
  LoaderLog.cs             延迟初始化的低开销日志（PclHarmony.log）
  IPlugin.cs               插件接口
  PluginApi.cs             暴露给插件的辅助 API
src/testplugin/            示例插件 C# 源码（net48，输出 Test.dll）
install.ps1                一键安装（省略 -TargetDir 时使用当前目录）
uninstall.ps1              一键卸载
build.ps1                  构建两个项目并刷新 dist\
dist/                      构建产物（由 build.ps1 生成，不纳入版本控制）
```

**版本号只在 `project.json` 里改**。`Directory.Build.props` 会读取其中的 `version` 并写入程序集版本，所以二进制、Release 标签和清单不会互相漂移；CI 还会在构建后从 `PclHarmony.dll` 读回版本做校验，不一致直接失败。

发布正式版（推 `v*` 标签）时，除了 `PCL-Harmony.zip` 还会附加两个文件：

- `project.json` —— 本次构建用的版本清单；
- `SHA256SUMS.txt` —— 压缩包的 SHA-256 校验值。

本仓库不包含 PCL 本体或其源码。

---

## 安装

### 前置条件

- Windows
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)（PCL 本身即基于它运行）
- 一份 **PCL 2.99.0.0** 的安装目录

### 使用 Releases（推荐）

1. 从 Releases 下载 `PCL-Harmony.zip` 并解压，得到：

   ```
   install.ps1   uninstall.ps1   README.md
   PclHarmony.dll   0Harmony.dll
   plugins\Test.dll
   ```

2. 关闭正在运行的 PCL。
3. 把压缩包解压到 **PCL 目录**（和 `Plain Craft Launcher 2.exe` 放一起），然后在该目录打开 PowerShell：

   ```powershell
   .\install.ps1
   ```

   `-TargetDir` 可省略，省略时就用当前目录。要装到别处则显式指定：

   ```powershell
   .\install.ps1 -TargetDir "D:\你的\PCL 目录"
   ```

`install.ps1` 会做三件事，且可以重复执行：

| 步骤 | 动作 |
| --- | --- |
| 1 | 把 `PclHarmony.dll`、`0Harmony.dll` 复制到 PCL 可执行文件旁边 |
| 2 | 在可执行文件旁创建 `plugins\` 目录 |
| 3 | 写入 / 合并 `Plain Craft Launcher 2.exe.config` 中前述三段配置 |

脚本会自动定位 `PclHarmony.dll` 和 `0Harmony.dll`：先找脚本自身所在目录（Release 压缩包的布局），再找 `dist\PclHarmony\`，最后找 `src\loader\bin\Release\`。

如果你还想把**示例插件**一并放进去（仅用于确认加载器工作正常），加 `-InstallSamplePlugin`：

```powershell
.\install.ps1 -TargetDir "D:\你的\PCL 目录" -InstallSamplePlugin
```

示例插件默认**不安装**：它会改变启动器行为，且只是参考实现，是否启用应由你决定。

安装完成后直接启动 PCL 即可。加载器会在 PCL 目录下写一份 `PclHarmony.log`，可以从中确认它是否生效。

### 放置插件

插件就是普通的 `.dll`，**直接放进 PCL 目录下的 `plugins\` 文件夹**即可：

```
D:\你的\PCL 目录\
  Plain Craft Launcher 2.exe
  Plain Craft Launcher 2.exe.config
  PclHarmony.dll
  0Harmony.dll
  PclHarmony.log
  plugins\
    Test.dll            <- 示例插件
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
- 可参考 `src/testplugin/` 里的示例插件实现。

---

## 从源码构建

只需要 .NET SDK（net48 的参考程序集由 NuGet 还原，无需安装 Visual Studio）：

```powershell
git clone <本仓库地址>
cd PCL-Harmony
.\build.ps1
```

`build.ps1` 会：

1. `dotnet build src\loader\PclHarmony.csproj -c Release`
2. `dotnet build src\testplugin\Test.csproj -c Release`
3. 把产物刷新到 `dist\PclHarmony\PclHarmony.dll`、`dist\PclHarmony\0Harmony.dll`、`dist\plugins\Test.dll`
4. 校验三个产物的程序集名称与版本

之后即可用 `dist\` 里的文件配合 `install.ps1 -TargetDir <PCL 目录>` 安装。

推送到 `main` 会自动构建并发布一个 **预发布**（`v<version>-dev.<run_number>`）；推送 `v*` 标签则发布**正式版**，并额外附带 `project.json` 与 `SHA256SUMS.txt`。详见 `.github/workflows/ci.yml`。

---

## 已知限制

以下限制都是**真实存在**的，请在依赖本方案前先了解：

1. **依赖 PCL 2.99.0.0 的精确方法签名与 IL 形态。** 本加载器与示例插件都是针对该版本逆向实现的。PCL 升级后，插件很可能需要重做。
2. **示例插件中有一部分逻辑无法无条件工作。** 某些方法在官方的发布版本里被编译期折叠，正常的分支已被裁掉（例如某个方法的所有正常 `return` 都被删除，IL 里没有 `ret`，因此无法直接对其打补丁，只能改为重定向调用点）。这类重建出来的逻辑在**缺少 PCL 运行上下文**时会失败（例如在独立宿主中、没有已选游戏文件夹或加载器状态的情况下）；它在**正常的 UI 流程中可用**，但并非无条件可用。
3. **资源页的“点击安装”需要先把版本卡片展开才会生效。** PCL 的资源详情页把版本按 MC 版本分组折叠成多张卡片，只有卡片内容被构建时才会生成版本行。卡片**多张时不会自动展开**（PCL 仅在只有 1 张卡片时自动展开），所以需要先点一下卡片标题；展开后该行即为“点击安装 + 另存为”。这是 PCL 自身的交互，补丁只是把行的点击行为换回安装。
4. **插件以完全信任方式运行。** 插件在 PCL 的 AppDomain 内执行，拥有与 PCL 相同的权限，等同于任意代码执行。**只安装你信任的 DLL。**
5. **这是运行时补丁方案，不是 PCL 的官方扩展机制。** 它依赖注入 `AppDomainManager` 并修改 `exe.config`；PCL 自身未提供插件接口。

---

## 许可与归属

- **本仓库的加载器与示例插件源码**：MIT 许可，见 [LICENSE](./LICENSE)。
- **Lib.Harmony**：MIT 许可，见 [pardeike/Harmony](https://github.com/pardeike/Harmony)。Release 中分发的 `0Harmony.dll`（Lib.Harmony 2.4.2 的 net48 构建）依其原许可分发。
- **Plain Craft Launcher 2**：其版权归原作者所有。PCL **不是**以常见的开源许可发布——其许可证为一份限定分发范围的许可（见 PCL 仓库中的 `LICENCE`）。本仓库**不重新分发 PCL 本体**，也不包含 PCL 的源码；加载器只是在你本地的 PCL 上于运行时打补丁。使用前请自行确认你所在地区的法律法规与 PCL 的许可条款。
