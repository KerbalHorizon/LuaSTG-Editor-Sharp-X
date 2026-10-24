# LuaSTG Editor Sharp X · MCP 桥接插件 安装说明（终极小白版）

> 适用版本：**v1.0.1**（含 2026-10-24 的 Newtonsoft.Json 兼容修复）
> 支持引擎：**LuaSTG Sub**、**LuaSTG ExPlus**
> 适用编辑器：**v0.78.x 纯净版**（官方发布版即可，**不需要**重新编译编辑器）

---

## 一、这是什么？

它给 LuaSTG Editor Sharp X 加了一个**本地小服务**，监听 `127.0.0.1:8090`。
AI / 脚本可以通过它读取和修改节点树，效果和你用鼠标点一样：

| 命令 | 作用 |
|---|---|
| `ping` | 握手，返回编辑器版本、引擎名、是否兼容 |
| `editor_state` | 当前打开的文档信息 |
| `get_tree` | 读取整棵节点树（JSON） |
| `set_attribute` | 修改某个节点的属性 |
| `insert_node` | 插入新节点 |
| `delete_node` | 删除节点 |

**所有修改都走编辑器自己的命令栈**，所以 `Ctrl+Z` / `Ctrl+Y` 和左上角的撤销/重做按钮**完全正常**，未保存的 `*` 标记也会正常出现。

安全说明：

- 只监听 `127.0.0.1`（本机回环），**外网连不上**，不需要开放防火墙；
- 只在编辑器运行时存在，关掉编辑器就没了；
- 不需要管理员权限，不需要改系统配置。

---

## 二、包里有什么

```
LuaSTG-MCP-Bridge-v1.0.1/
├─ csharp-bridge-install.md      ← 你正在看的这份说明
├─ README-FIRST.txt              ← 三行字的极简步骤
├─ lib/                          ← 要复制到编辑器 lib 目录的东西
│  ├─ LuaSTGMcpBridge.dll        ← 桥接本体（核心）
│  ├─ LuaSTGSubLib.dll           ← Sub 引擎的插件入口
│  ├─ LuaSTGPlusLib.dll          ← ExPlus 引擎的插件入口
│  └─ BridgeDeps/
│     ├─ Serilog.dll             ← 备用依赖，正常不会用到
│     └─ Newtonsoft.Json.dll     ← 备用依赖，正常不会用到
└─ tools/
   └─ mcp-crud-test.ps1          ← 一键测试脚本（可选，强烈建议跑一次）
```

> **两个引擎入口都给你了，但编辑器只会加载你实际用的那一个**，所以两个一起放进去是安全的。
> 只有 **Sub** 和 **ExPlus** 有现成入口。X / Evo / Flux 用户请用本仓库对应工程的 `PluginEntry.cs` 自行编译。

---

## 三、安装（选一种，推荐 A）

### 先做一件事：备份

打开编辑器安装目录，找到 `lib` 文件夹，把这两个文件**复制一份**到别处存着（以防万一）：

- `lib\LuaSTGSubLib.dll`
- `lib\LuaSTGPlusLib.dll`

### 方案 A（推荐）：全部放进 `lib` 目录

1. 关闭编辑器。
2. 把包里的 `lib` 文件夹里的**所有东西**，复制进编辑器的 `lib` 目录：
   ```
   <编辑器目录>\lib\LuaSTGMcpBridge.dll
   <编辑器目录>\lib\LuaSTGSubLib.dll        ← 覆盖同名文件
   <编辑器目录>\lib\LuaSTGPlusLib.dll       ← 覆盖同名文件
   <编辑器目录>\lib\BridgeDeps\Serilog.dll
   <编辑器目录>\lib\BridgeDeps\Newtonsoft.Json.dll
   ```
3. 启动编辑器 → 完成。

纯净版默认就是这个布局，**不需要改任何设置**。

### 方案 B：放进编辑器根目录（进阶）

也可以把同样的文件放到 `<编辑器目录>\`（和 `LuaSTGEditorSharp.exe` 同级，`BridgeDeps` 也放根目录）。

⚠️ 但要注意：这时候编辑器设置里的**插件路径**必须是**不带 `lib\` 前缀**的裸文件名，否则它还是去 `lib` 里找。普通用户请直接用方案 A。

### 三条铁律（踩过坑的总结）

1. **`LuaSTGMcpBridge.dll` 必须和插件 DLL 在同一个目录**，否则插件加载了也起不来桥接。
2. **`BridgeDeps` 必须跟着 `LuaSTGMcpBridge.dll` 走**（`BridgeDeps` 和它同级）。
3. **不要跨引擎混用插件 DLL**！Sub 的编辑器只能用 `LuaSTGSubLib.dll`，ExPlus 只能用 `LuaSTGPlusLib.dll`。把 Plus 的 DLL 放进 Sub 编辑器里，表面上"能加载"，实际节点定义全错、编译行为也会错——这是之前真实踩过的坑。

不知道自己是什么引擎？打开编辑器 →「帮助 / 关于」，或看 `LuaSTGEditorSharp.exe` 的属性 →「详细信息」→「产品名称」：

| 产品名包含 | 引擎 | 用哪个插件 DLL |
|---|---|---|
| `Plus` | ExPlus | `LuaSTGPlusLib.dll` |
| `Sub` | Sub | `LuaSTGSubLib.dll` |

---

## 四、验证（3 步，1 分钟）

### 第 1 步：启动编辑器

正常情况下编辑器照常打开，**不会弹任何报错框**。

### 第 2 步：看日志

打开编辑器目录下的 `mcp-bridge.log`（用记事本即可）。
如果那里没有，去 `%TEMP%\mcp-bridge.log` 找。

看到这几行就是成功了：

```
... [INFORMATION] [McpBridgeHost] MCP bridge log: "...\mcp-bridge.log"
... [INFORMATION] [McpBridgeHost] MCP bridge starting for editor "0.78.3.0"; compatible=True.
... [INFORMATION] [McpBridgeServer] MCP bridge listening on 127.0.0.1:8090.
... [INFORMATION] [McpBridgeHost] MCP bridge ready on 127.0.0.1:8090.
```

多了一行 `Bound libraries: ...`，它是用来排查"依赖库版本不对"的，正常不用管。

### 第 3 步：ping 一下

按 `Win + X` → 打开「终端」或「Windows PowerShell」，粘贴：

```powershell
$c = New-Object Net.Sockets.TcpClient('127.0.0.1', 8090)
$s = $c.GetStream()
$w = New-Object IO.StreamWriter($s); $w.AutoFlush = $true; $w.NewLine = "`n"
$r = New-Object IO.StreamReader($s)
$w.WriteLine('{"action":"ping"}')
$r.ReadLine()
$c.Close()
```

期望输出（一行 JSON）：

```json
{"status":"pong","editor":"connected","editorVersion":"0.78.3.0","engine":"LuaSTG Sub","plugin":"LuaSTGSubLib","bridgeCompatible":true}
```

重点看两个字段：

- `"engine":"LuaSTG Sub"` → 说明加载的是 Sub 入口（ExPlus 用户这里会显示 `LuaSTG ExPlus`）
- `"bridgeCompatible":true` → 说明编辑器版本够新，全部命令都能用

---

## 五、跑一遍增删改演示（可选，30 秒）

把 `tools\mcp-crud-test.ps1` 复制到任意位置，然后在 PowerShell 里：

```powershell
cd <你放脚本的目录>
powershell -ExecutionPolicy Bypass -File .\mcp-crud-test.ps1
```

它会自动做这一串，**每步暂停，等你看清楚再按回车**：

1. 连接并握手
2. 读取当前树，打印文档名和节点数
3. 在根节点下插入一个 `Comment` 节点 → 你会看到树里多了一行、并且被选中
4. 读回来确认真的插进去了
5. 把它的属性改成 `Hello from MCP (edited)` → 行文字立刻变
6. 删除它 → 行消失
7. 最后提示你去按 `Ctrl+Z` / `Ctrl+Y`

按三次 `Ctrl+Z`，你应该看到：节点回来 → 属性还原 → 节点消失；再按三次 `Ctrl+Y` 全部重放。

只想看树、不想改东西：

```powershell
powershell -ExecutionPolicy Bypass -File .\mcp-crud-test.ps1 -Step tree
```

---

## 六、出问题了？（对照表）

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 编辑器启动正常，但 `ping` 连不上 | 桥接没起来 | 看 `mcp-bridge.log`；没有这个文件说明插件没加载 |
| `mcp-bridge.log` 里出现 `cannot bind 127.0.0.1:8090` | 8090 端口被别的程序占了 | 关掉占用的程序，或重启电脑；桥接失败**不会**影响编辑器使用 |
| 日志里出现 `MCP bridge client ... failed` | 某个请求处理出错 | 先更新到 v1.0.1（已修 JSON 兼容 bug），还有问题把日志发给作者 |
| 完全没有 `mcp-bridge.log` | 插件 DLL 没被加载 | 确认插件路径设置指向正确的 `LuaSTGSubLib.dll`；确认文件放对目录（铁律 1） |
| `editor.log` 里有 `A plugin/target LuaSTG version has not been set` | 插件加载失败，回退到了默认插件 | 通常是引擎 DLL 放错了（铁律 3），换成对应引擎的 DLL |
| `"bridgeCompatible":false` | 编辑器版本太老，缺少某个 API | 返回里会带 `missing` 字段说明缺什么；升级编辑器到 v0.78.x |
| 返回 `unsupported_editor_version` | 同上，具体命令缺 API | 同上 |
| `Ctrl+Z` 撤销不了 | 不可能是桥接（桥接只走命令栈） | 说明编辑器本身没记录，检查是不是在别的文档标签页里按的 |
| 中文属性值变问号 | 终端编码问题（不是桥接问题） | 先执行 `chcp 65001`，或用脚本参数传值 |

日志文件在哪？优先级：`<编辑器目录>\mcp-bridge.log` → `%TEMP%\mcp-bridge.log`。

---

## 七、卸载

1. 关闭编辑器。
2. 删除 `<编辑器目录>\lib\LuaSTGMcpBridge.dll`。
3. 删除 `<编辑器目录>\lib\BridgeDeps\`（如果这个目录里只有本包的两个文件，整目录删掉即可）。
4. 把备份的 `LuaSTGSubLib.dll` / `LuaSTGPlusLib.dll` 还原回去（或者找一份官方纯净版的覆盖）。
5. 顺手删掉 `mcp-bridge.log`。

> 如果你**忘记备份**：本包的插件 DLL 只是"官方 DLL + 一行启动代码"，行为与官方一致。也可以从对应版本的官方发布包里取回原文件。

---

## 八、更新记录

| 版本 | 日期 | 内容 |
|---|---|---|
| **v1.0.1** | 2026-10-24 | **修复**：在纯净版 v0.78.3 上 `ping` 直接断连的问题。根因是 Newtonsoft.Json 13.0.4 新增的 `JToken.ToString(Formatting)` 重载在宿主自带的 13.0.3 上不存在（两者程序集版本都是 13.0.0.0，绑定重定向不会报错）。现在所有 JSON 序列化改用 `JsonTextWriter` + 显式空转换器数组，**不依赖任何新版 API**；同时加固了错误路径，序列化再出错也不会踢掉连接。 |
| v1.0.0 | 2026-10-24 | 首个版本：`ping` / `editor_state` / `get_tree` / `set_attribute` / `insert_node` / `delete_node`，全部走编辑器命令栈，支持撤销重做。 |

---

## 九、给想改代码的人

- 源码：本仓库 `LuaSTGMcpBridge/`
- 入口：`LuaSTGSubLib/PluginEntry.cs`（Sub）、`LuaSTGPlusLib/PluginEntry.cs`（ExPlus），各自构造函数里两行：
  ```csharp
  try { LuaSTGMcpBridge.BridgeBootstrap.EnsureInstalled(); } catch (Exception) { }
  ...
  try { LuaSTGMcpBridge.BridgeHost.Start(); }
  catch (Exception ex) { try { LuaSTGMcpBridge.BridgeLog.StartupFailure(ex); } catch (Exception) { } }
  ```
  **顺序不能换**：必须先装依赖解析器，再启动桥接。
- 新增命令：在 `LuaSTGMcpBridge/Actions/` 里写处理函数，然后在 `BridgeHost.CreateRouter()` 里 `router.Register("你的命令", ...)`。
- **写 JSON 时禁止使用** `JToken.ToString(Formatting)` 和 `token.WriteTo(writer)`（无参形式），一律用 `Protocol/JsonCompat.cs` 里的 `JsonCompat.ToCompactString(...)`。原因写在 `JsonCompat` 的注释里。
