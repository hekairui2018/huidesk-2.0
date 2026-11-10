# 步E · 连接升级：调查与影响面方案

日期：2026-11-10 · 窗口：灰Desk 2.0 开发窗 · 性质：**调查稿，不含实施**
配套：`交接_灰Desk2.0_步A_骨架` / `步B_上报链路` / `步C_本周令牌` / `步D_客户信息识别`

---

## 0. 先说清我做过什么（免得你看到半截代码）

接到这版任务前，我按上一版口径（"向日葵用双Ctrl触发"）先动了几处代码，**已全部回滚，仓库恢复干净**：

| 动过的文件 | 动作 | 现状 |
|---|---|---|
| `Infrastructure/DirectConnect.cs`（新增，直连通道） | 新建 | **已删** |
| `Diagnostics/DirectPathProbe.cs`（新增，探针） | 新建 | **已删** |
| `Infrastructure/ClientConnector.cs` | 加 13 行直连分支 | **已还原** |
| `Legacy/InputSimulator.cs` | 加 `SendDoubleCtrl` / `SendPaste` | **已还原** |
| `Legacy/ScreenOps.cs` | 加 `Refresh(win)`（窗口矩形重算） | **已还原** |
| `UI/ConnectionCard.xaml.cs` | 改令牌/上报/客户信息文案 | **已还原** |

`dotnet build` 现状：**0 警告 0 错误**。用回退前代码出的那个单文件包也已删除，避免误拿去实测。

唯一留下来的是**调查证据**：`灰Desk重构/assets/probe/` 下的截图与 json（下文引用）。

**我为什么先动手**：我把"双Ctrl"理解成了"程序连按两次 Ctrl 自动触发"。实测证明这个理解是错的——见 §2，这正是本稿最该先纠正的一件事。

---

## 1. 一代里有没有现成这两条路

结论：**ToDesk 有一条，但是死代码；向日葵一条都没有。**

### 1.1 ToDesk：有代码，但从来没被走过

一代 `RemoteConnectHelper.V2/Infrastructure/AdditionalClientConnector.cs` 第 52 行确实有：

```csharp
start.ArgumentList.Add("ToDesk://connect&"+code+"&"+Uri.EscapeDataString(password));
```

但**路由不到它**。`Infrastructure/ClientConnector.cs` 第 23 行：

```csharp
if(app is RemoteApp.AnyDesk or RemoteApp.UU or RemoteApp.RustDesk or RemoteApp.TeamViewer or RemoteApp.UltraViewer){
    var added=await AdditionalClientConnector.ConnectAsync(...);   // ToDesk 不在这个名单里
...
IRemoteConnector connector=app==RemoteApp.Sunlogin?new SunloginConnector():new ToDeskConnector();  // ToDesk 走这里＝OCR 点按
```

也就是说：ToDesk 一直走的是 `Legacy/Connectors.cs` 里 `ToDeskConnector` 的"OCR 找框 → 点框 → 打字 → 点连接 → 点后轮询全屏找红字"。那行 URL 代码是一次尝试留下的残骸，**没有任何真机验证记录**。步E 用它，等于第一次真用它。

### 1.2 向日葵：没有，而且命令行那条路已经试死过

一代没有任何双 Ctrl+V 实现，也没有 `fastcontrol` 调用；只有 `SunloginConnector`（OCR 定位"伙伴识别码"→点→填→回读→点连接）。

命令行那条路 `决策记录.md`（2026-11-09）已经查过并停下：

- 快捷方式格式 `AweSun.exe --mod=fastcontrol --fastcode=<加密识别码> --control=<加密串>`
- 识别码是**加密的**（DES 类 8 字节分组 + PKCS7），明文命令向日葵不响应
- 密钥运行时派生，反汇编一轮（780 万次尝试）未命中，按"只做一轮"约定停止

所以向日葵只剩**人在环的剪贴板接力**这条路，也就是决策记录里定的"双 Ctrl+V"。**不是程序自动按键。**

---

## 2. 新连法①：向日葵「双 Ctrl+V」（先把操作说准）

### 2.1 决策记录定的操作（`决策记录.md` 第 175 行原文）

> **向日葵**：双 Ctrl+V 方案——助手把识别码放进剪贴板，人在向日葵里按第一次 Ctrl+V 粘识别码；助手自动把剪贴板换成验证码，人按第二次 Ctrl+V。

拆成动作就是：

| 步 | 谁做 | 动作 |
|---|---|---|
| 1 | 助手 | 识别出设备码（OCR，逻辑不动）→ 把**设备码**写进剪贴板 |
| 2 | 助手 | 把向日葵窗口调到前台，卡片提示「到向日葵里按 Ctrl+V」 |
| 3 | **人** | 点一下"请输入伙伴识别码"输入框，按 **Ctrl+V** → 设备码进框 |
| 4 | 助手 | 判定"第一次粘完了" → 立刻把剪贴板换成**验证码** → 提示「再按一次 Ctrl+V」 |
| 5 | **人** | 按第二次 **Ctrl+V** → 验证码进框 |
| 6 | 人 | 点「连接」（或向日葵自动连，看版本） |

**助手全程不点鼠标、不模拟打字、不 OCR 找输入框。** 这就是它比一代"找框点按"稳的根本原因：坐标和界面改版都不再影响它。

### 2.2 我实测到的事实（今天，本机）

| 实验 | 做法 | 结果 |
|---|---|---|
| 会不会"自动双 Ctrl" | 向日葵主窗在前台，剪贴板放 `000000000 0000`，SendInput 连按两次 Ctrl（间隔 70ms），900ms 后截图对比 | **输入框毫无变化**，前后截图逐像素一致（`assets/probe/sunlogin-before.png` vs `sunlogin-after-0.png`） |
| 客户端里有没有这个功能 | 向日葵 16.8 全部二进制搜 `双击`（UTF-16）| **0 命中**；`Ctrl`/`double` 只命中类名与设置项 |
| 输入注入是否可用（对照组）| 跑一代现成探针 `--probe-live-input sunlogin` | 通过：点框 → 打 `123456` → 回读 `Matched`，三种窗口状态（正常/最小化/隐藏）全过 |

**结论：向日葵 16.8 没有"自动双 Ctrl"这种东西，别按那个做。** 决策记录里的双 Ctrl+V 是**人按两次**，方向本来就对，是我一开始读偏了。

### 2.3 这套操作里，还没确定的 4 个细节（要铠江拍）

1. **第二次 V 粘哪儿**：验证码框和识别码框是同一个框内的两段，还是各一个框？粘贴时是否需要人再点一下验证码框？
2. **粘完要不要人点「连接」**：我看的 16.8 界面是"识别码框 + 验证码框 + 连接按钮"，粘完按钮才由灰变亮——**大概率仍要人点一下**。若确认要人点，助手就不该去点它（点按正是我们要去掉的）。
3. **没有验证码时**：只按一次 V 就结束？还是照样提示两次（第二次粘空）？
4. **"第一次粘完了"怎么判**：靠剪贴板序列号（`GetClipboardSequenceNumber` 变化）+ 人按了 V 的组合，还是靠回读窗口文字？我倾向**序列号 + 时间窗**（不碰 OCR，也不依赖界面改版）；回读只作为"是否成功"的辅助日志。

> 这 4 条定了，我才好写状态机。**没定之前我不动代码。**

---

## 3. 新连法②：ToDesk 命令行（实测参数，两种入口有差别）

### 3.1 官方文档口径（todesk.com 帮助中心 · 命令行参数）

- 快速发起连接（URL 协议）：`ToDesk://connect&123456789&a12345`
- 发起控制：`ToDesk.exe -control -id 123456789 -passwd a12345`，文档明确注一句：**"仅支持设备列表里已解锁设备发起控制"**

### 3.2 本机实测（ToDesk 5.2.1.0 · `D:\todesk\ToDesk.exe`）

```
> ToDesk.exe -help
Parameter: -help -getid -setpasswd <password> -autostart -user -signout
           -control -setproxy -proxy -version -status -tray -exit

> ToDesk.exe -help -control
Parameter:-id <device ID> -passwd <secure password>
Example : ToDesk -control -id 123456789 -passwd 123456
explanation : connect to a remote desktop

> 注册表 ToDesk\shell\open\command = "D:\todesk\ToDesk.exe" "%1"
```

参数确实在，协议也确实注册在本机 ToDesk 上。

### 3.3 一个必须你拍的关键差别

| 入口 | 官方定位 | 我们的场景（临时客户识别码）能不能用 |
|---|---|---|
| `ToDesk://connect&码&密码` | 浏览器"快速发起一个远程连接" | 按文档像是给任意码用的，**首选** |
| `ToDesk.exe -control -id 码 -passwd 密码` | 文档注明"仅支持设备列表里已解锁设备" | 客户临时码大概率**不在设备列表**，可能静默失败 |

**建议**：主用 URL，`-control` 作为第二手（URL 拉不起来时再试），两个都失败才回退老点按路径。**这条要铠江用真客户的码各试一次**，把"到底哪个能连"钉死——这是步E 真机实测的第一项，不测出来后面都白搭。

### 3.4 参数细节（写码时要处理的）

- 密码进 URL 要转义（`Uri.EscapeDataString`），否则密码里的 `&` `#` 会截断整条 URL；
- 空密码：`ToDesk://connect&码&` 结尾多个 `&` 是否合法未知，建议无密码时只发 `ToDesk://connect&码`；
- 密码会出现在进程命令行里（别的程序可见）——比一代"打字"多一个暴露面，要不要接受要你定（我倾向接受，客户临时码价值低、且一代把密码显示在卡片上同样可见）；
- 拉起方式：直接 `Process.Start(exe, url)`（不经过 shell，避免命令注入），失败再走协议处理器。

---

## 4. 影响面：逐项「要改 / 不改」

这是这版任务的重点。换连法不是一个函数替换，**牵连主要在"状态机"和"谁来收尾"**。

| # | 环节 | 一代/2.0 现状 | 新连法下会怎样 | 结论 |
|---|---|---|---|---|
| 1 | **连接发起** | `SunloginConnector`/`ToDeskConnector`：OCR 找框→点→打字→回读→点连接 | 向日葵只写剪贴板＋等人按键；ToDesk 一条命令行 | **要改（大）**：两个连接器整体退役，换成"剪贴板接力"＋"命令行"两个新通道 |
| 2 | **取码时机** | 识别完立刻自动连接（`AssistantController.RecognizeAsync` 里 decision=Automatic 直接 `ConnectCoreAsync`） | 识别完**不能立刻"连接"**，要先"备好剪贴板等人"；ToDesk 仍可立刻发起 | **要改（中）**：`ConnectCoreAsync` 拆成"发起"与"待人动作"两段 |
| 3 | **等待与超时** | 45s 总超时（`ClientConnector` watchdog）＋点连接后 6s 轮询 | 向日葵的节奏由人驱动，**人可能去接电话**；45s 会把正常操作掐断 | **要改（中）**：向日葵改"等人"长窗（如 3 分钟）＋可"停止"；ToDesk 保留 45s |
| 4 | **终态判定** | `SessionObserver`（会话窗/UIA/负向文案）＋点后全屏 OCR 找红字/密码弹窗 | 我们没点过连接，"点后轮询 OCR"失去触发点；但**会话窗判据仍有效**（新窗＋标题含码＋有断开按钮） | **部分改**：`SessionObserver` 保留；`ToDeskConnector.PostConnect` 那套全屏 OCR 轮询**删掉** |
| 5 | **取消 / 断开** | 点过连接后取消＝断开（`CloseSession` 尽力关会话窗） | 助手没点过连接，取消只应"中止助手动作"，**不该去关人家已经连上的会话** | **要改（简化）**：取消语义退回"只停助手"，删 `ConnectClicked` 分支 |
| 6 | **密码处理** | 打框，用完即弃 | 只进剪贴板；**切换/结束必须清剪贴板**，否则密码留在剪贴板里被下一个 Ctrl+V 粘出去 | **要改（+红线）**：结束/超时/取消一律清剪贴板；上报仍永不带密码（步B 红线不动） |
| 7 | **界面反馈** | "正在打开向日葵并连接…"＋停止按钮＋成功气泡 | 需要"第 1 步 / 第 2 步"接力提示、当前剪贴板装的是什么、超时提醒、可"重新装一次" | **要改（中）**：卡片加接力区；文案收尾（令牌/客户信息/上报状态一并顺一遍） |
| 8 | **本周令牌分配时点** | 连接终态分配（`ConnectCoreAsync` 末尾） | 终态从"点完就知道"变成"人确认/会话出现才算"，时点要重定 | **待拍（见 §8-Q3）**：识别即分配 vs 确认才分配 |
| 9 | **上报 result 口径** | Confirmed→`success`；Launched/Uncertain→`initiated`；Cancelled→`cancelled`；其余→`failed` | 语义不变；但"人还没按完"会更多落在 `initiated` | **不改**：步B 的映射正好够用，服务端不用动 |
| 10 | **客户信息 / 剪贴板冲突** | 名片图从剪贴板读，读完进内存 | 接力期间剪贴板被助手占用（**只占文本，图已在内存**）；但用户此刻复制别的东西会打断接力 | **要改（小）**：接力期间卡片标注"剪贴板正被占用"，并提供"重新装"按钮；结束恢复用户原剪贴板文本 |
| 11 | **软件发现 / 路径** | `RemoteSoftwareDiscovery.Find(id)` | 不变（ToDesk 需要 exe 路径来起命令行） | **不改** |
| 12 | **认码 OCR** | `RecognitionPipeline` / `Parsing/*` | **一行不动** | **不改（硬约束）** |
| 13 | **兜底（老点按路）** | 现役主力 | 若向日葵版本不配合、或人不在，是否留一条"老办法"开关？留着代码量大、维护双路；砍掉则新法一坏就没救 | **建议保留但默认关**（见 §8-Q4） |
| 14 | **诊断探针** | `--probe-live-input` / `--verify-*` 一堆 | 需要新增"只验剪贴板接力"的探针（不点连接），真机自查用 | **要改（小）** |

### 一句话总结影响面

**认码、上报、令牌、客户信息这四块几乎不用动；要动的是"发起—等待—收尾"这条链，以及围绕它的一整套状态与文案。** 其中 3 项（#3 等待、#5 取消、#8 令牌时点）会直接改变客服的操作感受，建议实施前先跟你对一遍。

---

## 5. 认码原则不动，但取码时机有 3 个关联点

1. **"识别完就自动连"要拆**：现在是识别到 `Automatic` 就直接 `ConnectCoreAsync`。新流程里向日葵这一段是"备好剪贴板 → 等人"，不能再叫"连接中"，否则卡片会一直转圈、令牌也会提前分配。
2. **剪贴板的占用窗口要收窄**：建议"识别完先只在内存里存好码，等人按下第一次 V 的前一刻才写剪贴板"，避免长时间占着用户剪贴板（用户可能正在复制客户名片）。
3. **重试语义要重写**：一代"这张图连过了就不许再连"（`_attemptedImages`）。新流程里人可能按错、按晚，需要允许"同一张图重新装剪贴板再试"，但**仍要防重复上报**（否则台账蹦两行）。

---

## 6. 建议的实施切分（每步可单独上线/回退）

| 步 | 内容 | 本地能验什么 | 真机要验什么 |
|---|---|---|---|
| E-1 | ToDesk 命令行通道（URL 为主、`-control` 备、老路兜底） | URL 构造/转义、协议存在、`-help` 参数（已在 §3 验过） | 用真客户码连一次：URL 能不能连、`-control` 能不能连 |
| E-2 | 向日葵剪贴板接力（含"第 1/2 步"界面与剪贴板清理） | 剪贴板写入/恢复、序列号判定、状态机单测、卡片离屏渲染 | 真人按两次 V：能否粘上、要不要人点连接、粘错怎么办 |
| E-3 | 收尾：等待窗、取消语义、令牌时点、文案 | 渲染快照 + 逻辑单测 | 全流程走通并落台账一行 |

出包：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`（`IncludeNativeLibrariesForSelfExtract=true` 已在 csproj 写死）。**E-1 出来就能先给铠江试 ToDesk 一半**，不必等 E-2。

---

## 7. 风险

1. **向日葵没有人端配合就连不上**——这是"人按 V"的固有代价，客服必须坐在电脑前。若客服希望"点一下全自动"，这套方案不满足，得回到点按（或等官方接口）。
2. **剪贴板是全局资源**：接力窗口内用户任何复制都会打断；Windows 剪贴板还可能被输入法、微信、剪贴板管理器改写。要有"重新装一次"的退路。
3. **密码进命令行**：ToDesk URL 会把密码暴露在进程命令行（本机管理员可见）。客户临时码可接受，但要记录在案。
4. **ToDesk `-control` 可能只对设备列表有效**：不实测就写死会留下"有时能连有时不能"的幽灵问题（§3.3）。
5. **两套连法并存**：兜底老路留着会让代码分叉，建议设一个开关并写清楚默认值。

---

## 8. 要你（和铠江）拍板的 6 件事

| # | 问题 | 我的建议 |
|---|---|---|
| Q1 | 向日葵第二次 V 之后，**要不要人点「连接」**？ | 要人点。助手不去点任何按钮（点按正是要淘汰的东西） |
| Q2 | ToDesk 用 **URL** 还是 `-control`？ | URL 为主，`-control` 作备；真机各试一次再定最终顺序 |
| Q3 | 令牌什么时候出号？ | **人确认已连上/会话窗出现时出号**（"已发起"也出号），保证一行一令牌 |
| Q4 | 老点按路径留不留？ | 留代码但默认关（`Preferences` 一个开关），新法出问题能一键退回 |
| Q5 | 向日葵"等人"给多久？ | 3 分钟，超时卡片提示"可以再点一次重来"，不自动关窗 |
| Q6 | 剪贴板结束要不要恢复用户原内容？ | 恢复"文本"（图不恢复），并清掉密码 |

---

## 附录 A · 今日实测记录（可复现）

环境：Windows 10.0.26200 · 向日葵 16.8.0.33445（`D:\AweSun\flutter\AweSun.exe`）· ToDesk 5.2.1.0（`D:\todesk\ToDesk.exe`），两者均在线。

1. `ToDesk.exe -help`、`-help -control` 输出见 §3.2（原样抄录）。
2. 向日葵双 Ctrl 试验：`assets/probe/sunlogin-before.png` / `sunlogin-after-0.png`（前后一致）。
3. 输入注入对照组：`--probe-live-input sunlogin` → `filled=True readback=Matched clicked=False`（正常/最小化/隐藏三种状态全过）。
4. ToDesk URL 构造与协议注册：`assets/probe/todesk-url.json`。

> 探针代码按要求已删除，只留结果与截图；要复现我可以按 §6 的 E-1 重写一个只读探针。

## 附录 B · 代码位置索引（实施时照着改）

| 位置 | 行/方法 | 处置 |
|---|---|---|
| `Legacy/Connectors.cs` | `SunloginConnector.Connect`、`ToDeskConnector.Connect`、`PostConnect` | 退役（或开关后置） |
| `Infrastructure/ClientConnector.cs` | `ConnectAsync` 第 21-35 行 | 换成"直连通道优先 + 兜底" |
| `Infrastructure/AdditionalClientConnector.cs` | 第 52 行 `ToDesk://connect&...` | 提出来做成正式 ToDesk 通道 |
| `Application/AssistantController.cs` | `ConnectCoreAsync`、`ReportConnectOutcome` | 拆"发起/待人动作"，令牌时点按 Q3 定 |
| `Legacy/InputSimulator.cs` | `ClearFocusedField`、`TypeText` | 新法不需要；只留剪贴板与按键组合 |
| `UI/ConnectionCard.xaml(.cs)` | 状态区 / 本次连接区 | 加接力提示，文案收尾 |
| `Legacy/ConnectionContext.cs` | `MarkConnectClicked` / `CloseSession` | 取消语义简化（§4-#5） |

---

（步E 调查完成，**未写实现代码**。等你和铠江过方案、把 §8 六条拍下来，我再动手。停。）
