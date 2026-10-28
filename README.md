# PCL Harmony

**给 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL) 装上 Harmony 插件加载功能。**

简体中文 | [English](README.en_US.md)

[实现原理](#实现原理) · [安装](#安装) · [卸载](#卸载) · [写个插件](#写个插件) · [从源码编译](#从源码编译) · [避坑指南（已知限制）](#避坑指南（已知限制）)

---

## 这是什么

PCL Harmony 是一个**插件加载器**。它会在启动器启动时提前介入，把 [Harmony](https://github.com/pardeike/Harmony) 注入到启动器的进程（AppDomain）里。这样一来，第三方插件就能在运行时直接 Hook（拦截并修改）启动器内部的方法，而且**完全不需要修改启动器原本的 exe 文件**。

**同时支持官方版和社区版（CE）**：

|  | 官方 PCL | PCL-CE |
| --- | --- | --- |
| 启动文件 | `Plain Craft Launcher 2.exe` | `PCL2_CE_*.exe` |
| 运行环境 | .NET Framework 4.8（CLR 4） | .NET 10（CoreCLR） |
| 注入方式 | `AppDomainManager` 注入 | .NET 启动钩子（Startup Hook） |
| 是否改动原版 exe | 否（只生成一份 `exe.config`） | 否（通过启动脚本传环境变量） |

由于两者的底层运行时完全不兼容，`libs/` 目录下分别放了两套对应的底层文件。运行 `install.ps1` 时脚本会自动识别你用的是哪个版本，不用手动挑。

如果你写的插件只打算给其中一个版本用，按常规方式写就行；如果想**写一个插件同时兼容两边**，请看下文的「[编写双端通用插件](#编写双端通用插件)」。

仓库里自带了两个**示例插件**（`src/sample-plugin/` 和 `src/example-title/`），演示了怎么声明补丁位置、怎么针对不同启动器写分支逻辑，以及怎么往启动器标题栏里塞一个自定义的 WPF 控件。这两个示例都基于 `netstandard2.0` 编译，编译出来的同一个 DLL 扔进哪个启动器都能直接跑。

---

## 实现原理

### 1. 赶在 `Main` 之前接管程序

安装脚本会在 PCL 目录下生成一个 `Plain Craft Launcher 2.exe.config` 配置文件，里面最关键的是这两行：

```xml
<runtime>
  <appDomainManagerAssembly value="PclHarmony" />
  <appDomainManagerType value="PclHarmony.Entry" />
</runtime>

```

CLR 在创建 AppDomain 时，会先初始化 `PclHarmony.Entry`（继承自 `AppDomainManager`），这个时机**比 PCL 自己的 `Main()` 函数还要早**。

不过，加载器在 `InitializeNewDomain` 阶段只做最轻量级的操作——记下当前目录、监听 `AssemblyLoad` 事件，**绝对不碰文件读写**。这是个死规矩：因为这时候 CLR 还没把 AppDomain 彻底初始化完，一旦提前进行磁盘 I/O，就会把 PCL 自己的启动流程搞崩。

### 2. 等本体加载完再打补丁

当 `AssemblyLoad` 事件通知我们 `Plain Craft Launcher 2` 本体已经加载进内存时，加载器就会按顺序干三件事：

1. 挂载内置的依赖解析器（原因见第 5 点）；
2. 扫描 `<exe 所在目录>\plugins\*.dll`，用 `Assembly.LoadFrom` 把插件全读进来；
3. 找出实现了 `PclHarmony.IPlugin` 接口的类，实例化后调用它的 `Load(Harmony, Assembly)` 方法，把启动器本体的程序集交出去任其发挥。

这时候 PCL 的 `Main()` 还没开始执行，所以插件在此时打上的所有补丁，在启动器接下来的整个运行周期里都是生效的。

### 3. 为什么配置文件里必须加 `UseLegacyPathHandling=false`

安装脚本写入的配置文件里还有这么一段：

```xml
<AppContextSwitchOverrides
    value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />

```

**这行配置千万不能删。** 这是因为只要用了 `appDomainManagerAssembly` 注入，CLR 就会自作主张地退回老版本的路径解析规则。而 PCL 内部大量使用了带 `\\?\` 前缀的扩展路径，在老规则下会被直接判定为“包含非法字符”，导致启动器当场弹窗报错 **「运行环境错误」** 并闪退。加上这个开关后，PCL 才能正常打开主窗口（顺带一提，PCL 主窗口标题是 `Plain Craft Launcher `，末尾其实藏了个全角空格 `U+3000`）。

### 4. 为什么要手动修 TLS 协议版本

用 `AppDomainManager` 注入带来的第二个副作用是：CLR 会把当前程序当成“.NET 4.0 以前的老古董”对待。

这会导致 **`ServicePointManager.SecurityProtocol` 的默认值卡死在 `Ssl3, Tls`，根本不启用 TLS 1.2 和 1.3**。结果就是所有 HTTPS 请求全部报 `SecureChannelFailure` 错误，启动器直接提示「你的网络环境不佳，请稍后再试，或使用 VPN 改善网络环境。」，下载列表一片空白。

指望 PCL 自己修是不行的——PCL 只有在检测到系统版本低于 Windows 8（`Environment.OSVersion < 6.2`）时才会手动去改 `SecurityProtocol`；在现在的 Win10/Win11 上，它完全依赖 CLR 的默认配置，而这个默认配置偏偏被我们的注入操作给带沟里去了。

所以，**加载器会在 `InitializeNewDomain` 里主动把 TLS 1.2 和 1.3 补上去**（只做追加，不删原有的协议）。这一步同样发生在 `Main()` 和任何网络请求之前。修复成功后，你会在 `PclHarmony.log` 里看到这行记录：

```
TLS: SecurityProtocol Ssl3, Tls -> Ssl3, Tls, Tls12, Tls13

```

之所以直接写死在代码里，而不是靠配置文件里的 `Switch.System.Net.DontEnableSchUseStrongCrypto=false` 开关，是因为那个配置开关只在极早期读一次，万一用户的 config 文件被别的工具改乱了或者合并出错，它就会悄悄失效且不报任何错。还是写在代码里最稳妥。

### 5. 为什么加载器要自己写一遍 `AssemblyResolve`

PCL 把它用到的 13 个第三方依赖库（比如 `MeloongCore`、`Newtonsoft.Json`、`NAudio` 等）全部打包成了 `byte[]` 字节数组，塞进了自己内嵌的资源文件 `PCL.Resources.resources` 里。正常情况下，PCL 会在 `Application.New()` 里注册 `AssemblyResolve` 事件，用到哪个库再现场从资源里解压加载。

但这里有个时间差问题：**Harmony 在给目标方法打补丁时，需要先解析方法签名里用到的各种类型，而这时候还没走到 `Application.New()**`。PCL 自己的解压逻辑还没挂载上，Harmony 一解析就会直接抛出 `FileNotFoundException`。

为了破局，加载器只能“抢跑”：自己提前写一套一模一样的解析器（`EmbeddedResolver`），在 Harmony 干活之前，先一步从 PCL 的资源文件里把这些依赖库捞出来 `Assembly.Load` 掉。

### 6. 目录与代码结构

```
project.json               版本号的唯一来源（统一管 name / version / description）
Directory.Build.props      编译时自动读取 project.json，把版本号刷进所有 DLL

src/contract/              跨版本通用插件接口（netstandard2.0）
  Contract.cs              定义 IDeclarativePlugin 和 PatchSpec

src/loader/                官方 PCL 专用的加载器（net48，编译出 PclHarmony.dll）
  Entry.cs                 AppDomainManager 入口，负责监听 AssemblyLoad
  EmbeddedResolver.cs      提前解压 PCL 内置依赖（读取 PCL.Resources.resources）
  PluginHost.cs            扫描 plugins\ 目录、实例化并加载两类插件
  LoaderLog.cs             按需初始化的轻量日志（写到 PclHarmony.log）
  IPlugin.cs               官方版原生插件接口
  PluginApi.cs             提供给插件调用的工具类

src/ce-host/               PCL-CE 专用的加载器（net8，编译出 PclHarmony.CeHost.dll）
  StartupHook.cs           .NET 启动钩子入口（全局 StartupHook 类）
  Host.cs                  扫描 plugins\ 目录、根据 PatchSpec 打补丁

src/sample-plugin/         示例插件（netstandard2.0，输出 Example.dll）
src/example-title/         示例插件：修改标题栏（netstandard2.0，输出 Example-Title.dll）

install.ps1                一键安装脚本（自动判断版本；不传 -TargetDir 默认装在当前目录）
uninstall.ps1              一键卸载脚本
build.ps1                  一键编译所有项目，并更新 dist\ 和 libs\ 目录
libs/official/             官方 PCL 用的核心文件（net48）
libs/ce/                   PCL-CE 用的核心文件（net10）
dist/                      编译输出目录（由 build.ps1 生成，不进 Git）

```

**改版本号只需要改 `project.json` 这一个地方**。`Directory.Build.props` 会自动读取它并写入所有 DLL 的文件属性里，保证编译产物、GitHub Release 标签和配置文件里的版本号绝对一致。CI 在打包完后还会反查一遍生成的 DLL，如果版本号对不上会直接报错中断。

CI 自动发版的逻辑如下：

| 触发条件 | 发布的 Release 标签 | DLL 内部记录的版本号 |
| --- | --- | --- |
| 推送代码到 `main` 分支 | 预发布版 `v1.0.0-dev.<构建号>` | `1.0.0-dev.<构建号>`（注意**不是**纯 `1.0.0`） |
| 推送 `v1.0.0` 标签 | 正式版 `v1.0.0` | `1.0.0` |

因为 Windows 的 `AssemblyVersion` 和 `FileVersion` 只认纯数字（四段式），塞不进 `-dev.<构建号>` 这种后缀，所以这两个字段里只保留纯数字部分（如 `1.0.0`）；完整的带后缀版本号会写在 `InformationalVersion` 字段里（例如 `1.0.0-dev.15+<commit>`）。如果你想排查某个 DLL 到底是哪次构建出来的，请看它的 `InformationalVersion`。

发正式版（推 `v*` 标签）时，Release 页面除了 `PCL-Harmony.zip`，还会额外附带两个文件：

* `project.json` —— 留档当次构建的版本信息；
* `SHA256SUMS.txt` —— 压缩包的 SHA-256 校验码。

正式版发布成功后，CI 会自动把 `project.json` 里的版本号加一（比如 `1.0.0` → `1.0.1`）并推回 `main` 分支，防止下次日常提交代码时又用老版本号打包。发测试版（预发布）不会触发自动改版本号。另外这步做了防重处理：只有当 `project.json` 里的版本号确实等于刚刚发布的版本时才会加一，所以就算手动重跑 CI 或者自己提前改了版本号，也不会加乱。

*注：本仓库不包含 PCL 启动器本体及任何 PCL 源码。*

---

## 安装

### 准备工作

* Windows 系统
* **如果你用官方 PCL**：需要 **PCL 2.99.0.0**，系统需装有 [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)（只要你能正常打开 PCL 就说明已经装了）。
* **如果你用 PCL-CE**：需要装好 [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（CE 本体运行所必需的环境）。

### 从 Releases 下载安装（推荐）

1. 去 Releases 页面下载 `PCL-Harmony.zip` 并解压，里面有这些东西：
```
install.ps1   uninstall.ps1   README.md
libs\official\   PclHarmony.dll   PclHarmony.Contract.dll   0Harmony.dll
libs\ce\         PclHarmony.CeHost.dll   PclHarmony.Contract.dll   0Harmony.dll
plugins\Example.dll   plugins\Example-Title.dll

```


2. 先关掉正在运行的 PCL 启动器。
3. 把解压出来的所有文件直接复制到**启动器所在文件夹**（和启动器 exe 放在同一级目录），然后在当前目录打开 PowerShell 运行：
```powershell
.\install.ps1

```


如果你不想把安装文件拷过去，也可以在原地运行脚本，用 `-TargetDir` 指定你的 PCL 路径：
```powershell
.\install.ps1 -TargetDir "D:\你的\PCL 目录"

```



`install.ps1` 会**自动判断你的启动器版本**：看到 `Plain Craft Launcher 2.exe` 就按官方版装，看到 `PCL2_CE_*.exe` 就按 CE 版装，自动去 `libs\` 里拷对应的文件。

#### 官方 PCL 的安装过程

| 步骤 | 脚本做了什么 |
| --- | --- |
| 1 | 把 `PclHarmony.dll`、`PclHarmony.Contract.dll`、`0Harmony.dll` 复制到 exe 旁边 |
| 2 | 在同级创建一个**空的** `plugins\` 文件夹（如果本来就有则跳过） |
| 3 | 生成或修改 `Plain Craft Launcher 2.exe.config`，把前面讲的几项配置塞进去 |

装完之后，像平时一样直接双击启动器 exe 就能用了。

#### PCL-CE 的安装过程

因为 CE 的 exe 是个打包好的 .NET 宿主程序（apphost），**不支持 `AppDomainManager` 注入**，所以我们改用 .NET 官方提供的启动钩子（Startup Hook）机制：

| 步骤 | 脚本做了什么 |
| --- | --- |
| 1 | 复制 `PclHarmony.CeHost.dll`、`PclHarmony.Contract.dll`、`0Harmony.dll` |
| 2 | 生成一个 `Run-with-PCL-Harmony.cmd` 启动脚本（里面会自动设置好 `DOTNET_STARTUP_HOOKS` 环境变量再去调起 CE） |
| 3 | 在同级创建一个**空的** `plugins\` 文件夹 |

**注意：CE 版必须双击 `Run-with-PCL-Harmony.cmd` 启动！直接双击原本的 exe 是不会加载插件的。**
这是因为 CE 是单文件打包（Single-file bundle）发布的，它把配置锁死在了内部，无视外部的 `runtimeconfig.json`；想要挂载启动钩子，唯一的办法就是通过脚本在启动前传入环境变量。这样做的好处是完全不用动 CE 的原版 exe。

> 补充说明：CE 本身有单实例限制。如果你后台已经开着一个 CE，再运行脚本开启的新进程会立刻自动退出（返回码为 0）。这是 CE 自己的防多开机制，不是插件挂了。

无论哪个版本，启动后都会在目录下生成一份 `PclHarmony.log` 日志文件，打开看看就能知道加载器有没有正常工作。

**注意：`install.ps1` 只会帮你建一个空的 `plugins\` 文件夹，不会自动把示例插件拷进去。** 压缩包里自带的 `plugins\` 只是给你拿来测试或参考的；你想启用哪个插件，手动把对应的 `.dll` 拖进启动器的 `plugins\` 目录里就行。

### 怎么放插件

插件就是普通的 `.dll` 文件，**直接丢进启动器目录下的 `plugins\` 文件夹**即可：

```
D:\你的\PCL 目录\
  Plain Craft Launcher 2.exe
  Plain Craft Launcher 2.exe.config
  PclHarmony.dll
  0Harmony.dll
  PclHarmony.log
  plugins\
    Example.dll            <- 示例插件
    Example-Title.dll      <- 示例插件：给标题栏加小尾巴
    你的插件.dll           <- 你自己写的或下载的插件

```

有几点需要注意：

* 只会扫描 `plugins\` **根目录**下的 `.dll`，放在子文件夹里的不管。
* 加载顺序按文件名（字母序）排列，各个插件之间互相独立，一个崩了不影响别的。
* **插件自己千万别打包带上 `0Harmony.dll` 或 `PclHarmony.dll**`。如果插件目录里多出一份同名 DLL，CLR 会把它们当成两个不同的程序集加载进内存，直接导致 `IPlugin` 接口类型转换失败，或者 Harmony 补丁打偏。
* 如果你的插件还依赖了其他的第三方 DLL，直接丢在 `plugins\` 目录或者 PCL 根目录都行，加载器会自动在这两个地方按文件名去找。

### 常见问题排查

| 遇到的问题 | 原因与解决办法 |
| --- | --- |
| 一打开就弹窗报「运行环境错误」 | 配置文件里的 `AppContextSwitchOverrides` 没写进去，或者被别的软件覆盖了。重新运行一次 `install.ps1` 即可。 |
| PCL 能正常打开，但目录下没生成 `PclHarmony.log` | 加载器根本没启动。官方版请检查 `.exe.config` 里的配置还在不在、核心 DLL 是否和 exe 在同一目录；CE 版请确认你是双击 `.cmd` 脚本启动的。 |
| 有生成日志，但插件没效果 | 打开 `PclHarmony.log` 搜 `FAILED` 或 `skipping`。一般是因为插件没实现指定接口、缺少公开的无参构造函数，或者缺了前置依赖库。 |
| 想暂时关掉插件加载器（纯净启动） | 设置系统环境变量 `PCLHARMONY_DISABLE=1` 后再打开 PCL，加载器就会自动跳过所有操作。 |

正常加载成功时，`PclHarmony.log` 里的记录大概长这样：

```
PclHarmony loader 1.0.0.0 attached (ADM PclHarmony.Entry)
target assembly loaded: Plain Craft Launcher 2, Version=2.99.0.0, ...
embedded assembly resolver installed (resource "PCL.Resources.resources", 13 prefixes)
plugin: loading '<插件名>' (...) from <文件名>.dll
plugin: loaded '<插件名>' OK, harmony="<插件名>", patch count=N
plugins: N plugin(s) loaded from M dll(s)

```

---

## 卸载

先关掉 PCL，然后在 PowerShell 里运行：

```powershell
.\uninstall.ps1 -TargetDir "D:\你的\PCL 目录"

```

卸载脚本会做这几件事：

1. 删掉注入用的 `PclHarmony*.dll`、`0Harmony.dll` 以及启动脚本；
2. 把 `exe.config` 里当初加进去的 `appDomainManagerAssembly` 等注入节点删干净；
3. 移除那两个路径处理的 `AppContextSwitchOverrides` 开关（如果删完发现整个 `<runtime>` 节点空了，就会顺手把空壳也清掉）。

配置文件里你原有的其他设置一概不动。另外，为了防止误删你的个人数据，**`plugins\` 文件夹和 `PclHarmony.log` 默认是保留的**。如果你确定想连根拔起，加上后面的参数就行：

```powershell
.\uninstall.ps1 -TargetDir "D:\你的\PCL 目录" -RemovePlugins -RemoveLog

```

卸载完后，PCL 就彻底恢复原状了。

---

## 写个插件

插件分两种写法，取决于你想支持哪个启动器：

|  | 原生插件（实现 `IPlugin`） | 双端通用插件（实现 `IDeclarativePlugin`） |
| --- | --- | --- |
| 目标框架 | `net48` | **`netstandard2.0`** |
| 支持的启动器 | 仅限官方 PCL | 官方 PCL **和** PCL-CE 通吃 |
| 能不能在代码里直接调 Harmony API | 可以，完全自由 | **不能**，只能声明你想 Hook 哪个方法 |
| 怎么选 | 只给官方版写插件用这个，最省事 | 想编译一个 DLL 两边都能跑，用这个 |

下面先讲最直接的官方版原生插件；想写双端通用的请直接往下翻看「[编写双端通用插件](#编写双端通用插件)」。

### 原生插件接口

新建一个目标框架为 `net48` 的 C# 类库项目，写一个类实现 `PclHarmony.IPlugin` 接口，并确保它有**公开的无参构造函数**（默认不写构造函数就有）：

```csharp
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    public interface IPlugin
    {
        /// <summary>插件名字，同时也会作为专属 Harmony 实例的 ID。</summary>
        string Name { get; }

        /// <summary>
        /// 在 PCL 本体加载进内存后、Main() 函数执行前调用一次。
        /// 在这里打的补丁整个运行期间都有效。
        /// </summary>
        void Load(Harmony harmony, Assembly target);
    }
}

```

加载器在调 `Load` 时，传给你的 `harmony` 参数已经用你填的 `Name` 初始化好了，方便区分不同插件打的补丁。`target` 参数就是内存中已经加载好的 `Plain Craft Launcher 2` 本体程序集，你可以直接对它用反射（Reflection）去抓里面的类和方法。

### 内置工具类 `PluginApi`

`PclHarmony.PluginApi` 提供了几个顺手的静态属性和方法：

| 成员 | 作用 |
| --- | --- |
| `Assembly Target` | 获取启动器本体的程序集（如果在极早期调用可能会是 `null`） |
| `Harmony SharedHarmony` | 加载器全局公用的 Harmony 实例（ID 为 `pcl.harmony.pluginloader`） |
| `Harmony GetHarmony(string id)` | 按指定 ID 获取（并自动缓存）一个 Harmony 实例；不传 ID 则返回上面那个全局实例 |
| `void Log(string message)` | 往 PCL 目录下的 `PclHarmony.log` 里写一行日志 |

### 最简代码示例

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
            PluginApi.Log("[MyPlugin] 准备挂载，当前 PCL 版本：" + target.GetName().Version);

            // 用反射从 target 程序集里找出你要改的类和方法
            var type = target.GetType("PCL.ModBase", false);
            var method = type == null ? null : type.GetMethod("某个方法名",
                BindingFlags.Public | BindingFlags.Static);
            
            if (method == null)
            {
                PluginApi.Log("[MyPlugin] 没找到目标方法，放弃打补丁");
                return;
            }

            // 挂载 Prefix 前置补丁
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(MyPlugin).GetMethod(
                    nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: null);
        }

        private static void Before() 
        { 
            // 目标方法执行前会先跑这里的代码
        }
    }
}

```

### 写插件的几个规矩

* **目标框架选 `net48**`：必须跟官方 PCL 保持一致。
* **引用 `0Harmony` 和 `PclHarmony` 时必须设为“不复制到输出目录”**：也就是在 `.csproj` 里加上 `Private="false"` 或 `ExcludeAssets="runtime"`，原因前面“怎么放插件”里解释过了。
* **强烈建议用反射找方法，别直接引用 `Plain Craft Launcher 2.exe**`：PCL 本身不是按公开库设计的，如果你在编译时直接强引用它，一旦 PCL 小版本更新改动了签名，你的插件就会直接报错崩掉；用反射至少还能做个判空降级。
* **自己做好异常处理**：虽然加载器外层套了 `try-catch`，一旦发现某个插件报错就会自动回滚它打了一半的补丁，但自己代码里还是尽量稳一点好。
* **多用 `PluginApi.Log` 打日志**：这样用户用你的插件出问题时，直接让他把 `PclHarmony.log` 发给你就能定位原因。
* 具体工程配置可以直接抄仓库里的 `src/sample-plugin/`。

---

## 编写双端通用插件

如果你想**只编译一个 DLL，同时丢进官方 PCL 和 PCL-CE 里都能跑**，就得解决两个底层环境的冲突。

### 难点一：两边唯一能通用的框架只有 `netstandard2.0`

| 目标框架 | 官方 PCL（.NET Framework 4.8） | PCL-CE（.NET 10） |
| --- | --- | --- |
| `net48` | 支持 | 不支持 |
| `net10.0` | 不支持 | 支持 |
| **`netstandard2.0`** | **支持** | **支持** |

所以通用插件的项目框架必须设为 **`netstandard2.0`**。

### 难点二：`netstandard2.0` 的插件没法自己调 Harmony

Harmony 官方包（Lib.Harmony 2.4.2）压根**没有提供 `netstandard2.0` 的版本**（它只打包了 net35 / net452 / net472 / net48 / net5.0~net10.0 / netcoreapp3.x）。

更要命的是，哪怕你在 `netstandard2.0` 的插件里用反射强行加载 `net48` 版的 Harmony 去调，运行时也会直接炸出泛型约束冲突：

```
TypeLoadException: "MonoMod.Utils.Cil.ILGeneratorProxy[TTarget]" 上的
GenericArguments[0] ("MonoMod.Utils.Cil.CecilILGenerator") 与类型参数"TTarget"的约束冲突

```

明明是一模一样的打补丁代码，放在原生 `net48` 的 DLL 里调就一点事没有，放在 `netstandard2.0` 的 DLL 里调就会崩——这是跨运行时类型解析的底层坑，改写法是绕不过去的。

### 解决思路：插件只管“填表”，让加载器去动手

既然插件自己调 Harmony 会崩，那就别让插件调了。

通用插件只需要实现 `PclHarmony.Contract.IDeclarativePlugin` 接口，**像填表一样告诉加载器：“我想在哪个类的哪个方法上挂哪个函数”**。至于具体怎么调用 Harmony 把补丁打上去，交给两边各自的加载器宿主去干（官方版宿主是原生 `net48`，CE 宿主用 `net10` 版的 Harmony，由它们出手就不会触发泛型冲突）。

代码写起来很简单：

```csharp
using System.Collections.Generic;
using PclHarmony.Contract;

public sealed class MySharedPlugin : IDeclarativePlugin
{
    public string Name { get { return "My.Shared.Plugin"; } }

    public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion)
    {
        // hostKind 会传入 "official" 或 "ce"，据此区分当前是在哪个启动器里运行
        if (hostKind == "ce")
        {
            yield return new PatchSpec
            {
                TypeName       = "PCL.Program",                  // 要 Hook 的目标类
                MethodName     = "Main",                         // 要 Hook 的目标方法
                HookKind       = "postfix",                      // "prefix"（前置）或 "postfix"（后置）
                HookTypeName   = typeof(MySharedPlugin).FullName,
                HookMethodName = "CeAfterMain",                  // 指向下面写好的静态方法名
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

    // 注意：用来挂载的钩子函数必须是普通的 public static 方法！
    public static void CeAfterMain()       { /* CE 版会跑这里 */ }
    public static void OfficialAfterMain() { /* 官方版会跑这里 */ }
}

```

项目的 `.csproj` 文件也极其干净，只需要引用 `Contract` 接口层，**完全不用碰 Harmony**：

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

完整的项目例子可以直接看仓库里的 `src/sample-plugin/` 和 `src/example-title/`。

### 通用插件避坑指南

* **钩子函数必须是老老实实的 `public static` 具名方法，千万别用 Lambda 表达式**。因为 C# 编译器会把 Lambda 编译成类似 `<GetPatches>b__2_0` 的内部闭包类方法，Harmony 拿到这种自动生成的方法会直接报错罢工，哪怕里面代码一模一样也不行。
* **两边的目标类名和方法名通常不一样**：官方 PCL 是用 VB.NET 写的（入口比如是 `PCL.Application.Main`），而 PCL-CE 很多地方用 C# 重构过（入口是 `PCL.Program.Main`），所以通常得靠 `if (hostKind == "ce")` 分开写两套目标配置。
* **不用担心 CE 版的延迟加载问题**：CE 的启动钩子跑得非常早，那时候像 `PCL.Core` 之类的核心库都还没加载进内存。不过 CE 版的加载器宿主已经帮你处理好了——它会自动监听 `AssemblyLoad`，等你要 Hook 的那个类所在的程序集一露面，立马把补丁拍上去，你只管在 `TypeName` 里填名字就行。
* **具体的业务代码基本没法复用**：毕竟官方版和 CE 版的内部代码结构差得太远，双端通用省下的是工程打包和分发的事（不用发两个 DLL），但里面具体的 Hook 逻辑往往还是得各写各的。
* **可以用 `hostVersion` 判断具体版本号**：官方版目前是 `2.99.x`，CE 版是 `2.15.x`。因为两边编译出来的程序集名字居然都叫 `Plain Craft Launcher 2`，所以真要细分版本时看 `hostVersion` 最准。

---

## 从源码编译

本地只需要装有 **.NET SDK 8** 即可编译（`net48` 所需的引用包会通过 NuGet 自动拉取，不需要装庞大的 Visual Studio）。

```powershell
git clone <本仓库地址>
cd PCL-Harmony
.\build.ps1

```

运行 `build.ps1` 会一口气编译所有子项目，并自动整理好两个输出目录：

| 目录 | 里面有什么 |
| --- | --- |
| `dist\` | 打包好的发布文件（包含加载器、Harmony 以及两个示例插件） |
| `libs\official\` | 官方 PCL 专用的核心文件（net48） |
| `libs\ce\` | PCL-CE 专用的核心文件（net8 宿主 + net10 Harmony） |

脚本最后还会自动核对一遍所有生成的 DLL 名称和版本号对不对。编译完后，直接跑 `.\install.ps1 -TargetDir <你的启动器目录>` 就能测试安装了。

> **为什么 CE 的加载器宿主是用 `net8` 编译的，而不是 `net10`？**
> 虽然 CE 本体跑在 .NET 10 运行时上，但 .NET 具有向下兼容性，`net8` 编译出来的 DLL 放进 .NET 10 环境里完全能正常加载运行（已实测）。用 `net8` 的好处是大家电脑上有 .NET 8 SDK 就能直接编译源码，不用专门去装预览版或最新版的 .NET 10 SDK。如果你电脑上正好装了 .NET 10 SDK，也可以自己去把 `src\ce-host\CeHost.csproj` 里的 `TargetFramework` 改成 `net10.0`。

---

## 避坑指南（已知限制）

在决定用这套方案写插件之前，请先了解以下几个**客观存在的硬伤**：

1. **启动器一更新，插件很可能就得跟着重写。**
这套方案本质上是靠逆向分析启动器内部的方法名和 IL 代码结构来打补丁的（目前官方版针对 `2.99.0.0`，CE 版针对 `2.15.1.0`）。只要启动器作者发新版本改了方法名、参数或者重构了逻辑，依赖这些方法的插件就会失效。
2. **自带的两个插件只是“写法演示”，不是正式功能。**
`Example.dll` 它们只是写给开发者看怎么接接口的，普通用户没必要装。
3. **安全警告：插件拥有和启动器完全相同的系统权限！**
插件被加载后直接跑在启动器进程内部，没有任何沙盒隔离，相当于直接运行了一个本地 exe。**请只安装你绝对信任的来源提供的插件 DLL，乱装来路不明的插件等于直接引狼入室。**
4. **这是民间外挂式的 Hook 方案，不是启动器官方支持的插件系统。**
无论是官方 PCL 还是 PCL-CE，本身都没有提供插件接口。我们只是利用了 .NET 底层的 `AppDomainManager` 和 `DOTNET_STARTUP_HOOKS` 机制强行插队进去的。
5. **CE 版没法直接双击原版 exe 生效。**
前面提过，受限于单文件打包机制，CE 版必须通过我们生成的 `Run-with-PCL-Harmony.cmd` 脚本启动才能把环境变量传进去。
6. **所谓“双端通用”，通常只是打包在一起，内部逻辑还是得写两套。**
因为官方版（VB.NET）和 CE 版（C#）的内部类库结构差异巨大，除了外壳配置能写在一起，真正干活的代码基本没法共用。

---

## 开源许可与版权说明

* **本仓库源码（加载器及示例插件）**：采用 MIT 许可证开源，详见 [LICENSE](./LICENSE)。
* **Lib.Harmony**：采用 MIT 许可证，项目地址 [pardeike/Harmony](https://github.com/pardeike/Harmony)。Release 压缩包里附带的 `0Harmony.dll`（版本 2.4.2，分为 net48 和 net10 两个版本）均遵循其原许可证原样分发。
* **Plain Craft Launcher 2**：其著作权归原作者（龙腾猫跃等）所有。请注意，PCL **并不是**传统意义上的开源软件——它采用的是一份限制分发的自定义许可证（详见 PCL 仓库内的 `LICENCE` 文件）。因此，**本仓库绝对不会分发、打包任何 PCL 启动器本体文件，也不包含任何一行 PCL 的源代码**；本工具仅仅是在你电脑本地运行 PCL 时动态往内存里打补丁。在使用本工具前，请自行确认该行为符合你所在地区的法律法规以及 PCL 的使用条款。
