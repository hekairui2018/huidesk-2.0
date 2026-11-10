# 灰Desk 2.0 架构设计（WorkBuddy 出稿 · 先设计不写码）

> 决策（铠江 2026-11-09/10 定）：灰Desk 客户端**全新 2.0**，不在一代上 bolt-on。
> 但"全新"= 干净新骨架 + **广泛复用一代能用的（含前端）**，不是白纸重写。
> 本文只出《架构设计》报方案，**先设计、不写码**，经理审过再分步建。

---

## 〇、一句话结论

一代的"认码 + UIA 填码 + 前端交互"三根硬骨头**全部直接复用**，2.0 只做四件新事：**①连接事件上报 ②4位本周令牌 ③截图识别客户信息（复用认码那条 OCR 管线）④outbox 兜底**。风险几乎全在"对接服务端契约"和"4位令牌生命周期"，这两个点已单独设步验证。

---

## 一、新工程分层结构

沿用一代已验证的分层（一代这套分层本身是干净的，2.0 继承它，不重排）：

```
灰Desk2.0（新 csproj，AssemblyName 灰Desk20 或 GrayDesk20）
├── App.cs                      # 入口：托盘/热键/悬浮球编排（改接口搬自一代 App.cs）
├── Domain/                     # 纯模型，无依赖（直接搬）
│   ├── Models.cs               # RemoteApp / OcrTextBlock / DeviceCodeCandidate / RecognitionResult ...
│   └── ConnectionPolicy.cs     # ValidCode 口径（直接搬）
├── Ocr/                        # PP-OCRv5 管线（直接搬，不重写）
│   ├── OcrProviders.cs         # RapidOcrProvider / IsolatedRapidOcrProvider / IOcrProvider
│   ├── RecognitionPipeline.cs  # 多旋转 + 交叉校验 + 缓存
│   └── ImagePreparation.cs     # 预处理
├── Parsing/                    # 认码规则引擎（直接搬，边界坑全在这）
│   ├── RecognitionEngine.cs    # 形近映射/软件识别/码提取/密码提取
│   ├── ConfidenceEngine.cs     # 风险判定
│   └── RemoteTextParser.cs     # 纯文本解析
├── Automation/                 # UIA 填码连接（改接口搬）
│   ├── ScreenOps.cs            # Win32 选窗/前置/截图/点按（直接搬）
│   ├── Connectors.cs           # SunloginConnector / ToDeskConnector / ConnectResult（直接搬）
│   └── InputSimulator / InputProtection / TargetWindowPriority（直接搬）
├── Infrastructure/
│   ├── ClientConnector.cs      # 连接收口（改接口搬：终态处加上报钩子）
│   ├── OnlineUpdater.cs        # 参考其 HttpClient 封装（改接口搬）
│   ├── Preferences.cs          # 配置（改接口搬：加 outbox/令牌字段）
│   ├── Log.cs                  # 日志（直接搬）
│   └── 【新增】ConnectLogReporter.cs    # ① 上报器
│   ├── 【新增】WeekToken.cs             # ② 4位本周令牌
│   ├── 【新增】CustomerInfoRecognizer.cs # ③ 客户信息识别器
│   └── 【新增】Outbox.cs                # ④ outbox 本地队列
├── Legacy/                     # 兼容层（改接口搬）
│   ├── HotkeyManager.cs         # 全局热键（直接搬）
│   ├── ClipboardImage.cs        # 剪贴板取图（直接搬）
│   └── ScreenOps 依赖的辅助类
└── UI/                         # 前端（直接搬 + 少量加字段）
    ├── ConnectionCard.xaml(.cs) # 主卡片（改接口搬：加"客户信息"区 + 令牌展示）
    ├── FloatingBall.cs          # 悬浮球（直接搬）
    ├── DesktopHost.cs           # 编排（改接口搬）
    ├── SoftwareManagerWindow.cs # 软件管理（直接搬）
    └── ...
```

**关键判断**：一代的 `Domain`/`Ocr`/`Parsing`/`Automation` 四层是**纯规则 + Win32 自动化**，与业务上报无关，可整层平移。真正要改的只有 `Application`（连接收口挂上报）和 `UI`（加客户信息/令牌两个展示位）。

---

## 二、复用清单（逐项标处置）

> 处置三档：**直接搬**（代码原样进 2.0）/ **改接口搬**（逻辑复用，接上新增四块的钩子）/ **弃用**（一代专有、2.0 不需要）。

### 2.1 前端 UI（重点，经理点名"含前端"）

| 组件 | 文件 | 处置 | 说明 |
|---|---|---|---|
| 主卡片（识别/填码/连接/预览/框选标记/缩放平移） | `UI/ConnectionCard.xaml(.cs)` | **改接口搬** | 一代最重的 UI，800 行全复用。2.0 只加两处：①连接成功后卡片显示"客户信息（号码/名称，可编辑）"输入区；②显示本周令牌。其余（预览框选、右键设码、Ctrl+C 复制、历史码、拖拽、壁纸）全不动。 |
| 悬浮球 | `UI/FloatingBall.cs` | **直接搬** | 点击识别并连接、拖拽换位、右键菜单。 |
| 欢迎/说明窗 | `UI/DesktopHost.cs` 的 `ShowHelp`/`ShowQuickHelp` | **直接搬** | 软件说明、快捷操作说明。 |
| 软件管理窗 | `UI/SoftwareManagerWindow.cs` | **直接搬** | 添加/删除/换路径/重命名远程软件。 |
| 确认气泡卡 / 被动展示 | `ConnectionCard.ShowPassive/PulseFront` + `DesktopWindows` | **直接搬** | 识别后气泡弹出、成功自动收起的整套交互。 |
| 壁纸/主题/透明度 | `WallpaperPlayer` / `ThemeManager` / `OpacityPicker` | **直接搬** | 外观类，无业务耦合。 |
| 图片选择器 | `UI/ImageFilePicker.cs` | **直接搬** | 打开本地图片。 |

### 2.2 双 Ctrl+V（经理点名的交互）

| 项 | 位置 | 处置 | 说明 |
|---|---|---|---|
| 卡片内 Ctrl+V 读剪贴板 | `ConnectionCard.Card_KeyDown`（第 774 行）+ `ShellWindow.Window_PreviewKeyDown` | **直接搬** | "输入框内正常粘贴文本、输入框外 Ctrl+V 读截图"这条规则一代已做对，原样保留。 |
| 剪贴板取图（多通道 + 锁内快拷） | `Legacy/ClipboardImage.cs` | **直接搬** | CF_DIB/PNG/JPEG/HDROP/WPF 五通道，锁内只拷字节立即关锁，避免"贴不出来"。2.0 识别客户信息也用同一条 `ClipboardImage.TryGet()`。 |

> 注意：一代的"双 Ctrl+V"本质是**全局剪贴板监听 + 卡片内 Ctrl+V 兜底**两条路径，都已在。2.0 不改这条交互，只在识别结果里**多解析一类字段（客户信息）**。

### 2.3 远程码快连（历史码一键重连）

| 项 | 位置 | 处置 | 说明 |
|---|---|---|---|
| 最近目标记忆 + 回填 | `ConnectionCard` 的 `_recent` / `RememberTarget` / `BuildRecent` / `MatchingDigits` | **直接搬** | 识别后自动记住 (app, code)，卡片显示历史码，点击回填不自动连接；★收藏、前缀匹配排序、本次运行有效。 |
| 快连接"查回/快速远程" | 同上 | **直接搬** | 一代已有"历史码一键回填 → 回车连接"，正好对上"查回/快速远程"场景。2.0 无改动，上报会把这些连接也记为一条台账。 |

### 2.4 密钥/密码处理

| 项 | 位置 | 处置 | 说明 |
|---|---|---|---|
| 密码只用于当次填码 | `AssistantController` 的 `ConnectCoreAsync(password)` → `ClientConnector.ConnectAsync` | **直接搬（红线）** | 密码从识别结果流入连接器，**绝不落任何持久化、绝不进上报 payload**。2.0 上报函数签名**不带 password**（见 §三.1）。 |
| 成功后清敏感信息 | `ConnectionCard.ClearSensitive` / `AssistantController.ForgetFinished` | **直接搬** | 确认连接后清空截图+密码。 |
| 密码候选置信度/形近提示 | `ConfidenceEngine.NeedsPasswordCheck` | **直接搬** | 密码含易混字符提示核对。 |

### 2.5 认码（PP-OCRv5 + 全部边界坑，不从零重写）

**整条链路直接搬，一个字不改**：

| 子能力 | 文件 | 坑（一代已解决，2.0 继承） |
|---|---|---|
| 形近字符转数字 | `RecognitionEngine.TryDigits` | `O/o→0`、`I/l/|→1`、`S→5`、`Z→2`、`B→8`，转换标记 `HadCharacterConversion` 回吐"需核对原文" |
| 软件识别（向日葵 vs ToDesk） | `RecognitionEngine.DetectSoftware` | SunTerms/DeskTerms 词表；AnyDesk/UU/RustDesk/TeamViewer/UltraViewer 新品牌出现时**剔除向日葵/ToDesk 误判** |
| Autodesk 误判排除 | `DetectSoftware` 的 `newBrand&&!oldBrand` 分支 | AnyDesk 与 Autodesk 形近，用 `oldBrand`（向日葵/sunlogin/awesun/贝锐/todesk）强制区分 |
| 16位垫片排除 | `ExtractCodes` 的 `value.Length > 10` 断链 + 手机号末位排除 | 只取 7-10 位有效段，16位银行卡/手机号长串被排除 |
| 模糊照片容错 | `RecognitionPipeline` 四旋转 + 增强对比重试 | 0/90/180/270 四方向各试，模糊走 `contrast=true` 增强，交叉校验不一致转人工 |
| 历史连接记录排除 | `ExtractCodes` 的 sameRow 正则 | `历史连接/最近连接/连接记录` 字样旁的数字不当设备码 |
| 灰Desk 界面截图自识别 | `RecognitionEngine.Analyze` 尾部 | 截到自己界面时强制转人工 |
| 密码成对/评分 | `ExtractPasswords` | 密码只与设备码成对、按规则评分排序，不硬要求必须有密码 |

**结论：认码是 2.0 最不该重写的部分，整层 `Parsing/` + `Ocr/` 直接搬。**

### 2.6 UIA 自动填码连接

| 项 | 文件 | 处置 | 说明 |
|---|---|---|---|
| 纯 Win32 屏幕自动化（选窗/前置/截图/点按/填码/回读核对） | `Automation/ScreenOps.cs` | **直接搬** | 一代实测向日葵(Flutter)/ToDesk 不暴露 UIA 控件树，走"OCR 定位 + 物理点按 + 回读验证"这套，2.0 原样继承。 |
| 向日葵/ToDesk 连接器 | `Automation/Connectors.cs`（SunloginConnector/ToDeskConnector） | **直接搬** | 定位输入框、填码、填密码、找亮蓝按钮、点连接、点后轮询判终态、密码弹窗处理，全在。 |
| 连接收口 | `Infrastructure/ClientConnector.cs` + `Application/AssistantController.cs` | **改接口搬** | 终态收口点（`ConnectCoreAsync`）加上报钩子（见 §三.1）。这是 2.0 唯一的连接侧改动。 |
| 断开兜底 | `ScreenOps.CloseSession` | **直接搬** | 点连接后取消=尽力断开。 |

### 2.7 框选截图 + PP-OCRv5 管线（认码与客户信息识别共用）

| 项 | 文件 | 处置 | 说明 |
|---|---|---|---|
| 框选截图（Win+Shift+S 全屏框选） | 系统自带 + `ClipboardImage.TryGet()` 接 CF_DIB | **直接搬** | 用户 Win+Shift+S 框选 → 剪贴板 → 灰Desk 读取。 |
| PP-OCRv5 管线 | `Ocr/RapidOcrProvider` + `models/v5/*.onnx` | **直接搬** | 同一套模型、同一 `IOcrProvider` 接口。2.0 的"客户信息识别"**复用这条管线，不另造**（见 §三.3）。 |
| OCR 隔离/自检/修复 | `Infrastructure/OcrBootstrap` / `OcrMaintenance` / `IsolatedRapidOcrProvider` | **直接搬** | 崩溃隔离、OCR 自检修复入口。 |

### 2.8 弃用清单（一代有、2.0 不需要）

| 项 | 原因 |
|---|---|
| `Diagnostics/**`（20+ 个测试探针文件） | 一代开发期探针，2.0 不搬（保留一代仓库可查）。 |
| `Tools/**`、`Tools/OcrRepair` | 一代开发工具，2.0 不搬。 |
| `ThirdParty/**`（若与 OCR 无关的残留） | 逐个确认，OCR 依赖的保留。 |
| `Connections/ManualSessionTracker` 等的**会话观测轮询**部分 | 一代用进程/窗口观测来"确认连接成功"，2.0 若保留则搬，若上报已覆盖终态则简化——**待步3 定**（见 §四 待拍板）。 |

---

## 三、新增四块接口设计

### 3.1 ① 连接事件上报

**职责**：连接终态（成功/失败/取消）→ 组装事件 → POST `/graydesk/connect-log`。

**挂点**：`AssistantController.ConnectCoreAsync`（一代第 128-149 行），`_connect(...)` 返回 `result.Status` 后、`Publish` 前（或后）fire-and-forget。

```csharp
// 伪代码（接口契约，不写实现）
public sealed class ConnectLogReporter
{
    // 签名【不带 password】（红线：密码永不进上报）
    public static void Report(RemoteApp app, string remoteCode, string result /* success|failed|cancelled */,
                              string? phone = null, string? customerName = null, string? siteNote = null);
    // 内部：组装 JSON → 交给 Outbox.Enqueue → 有网即 POST，无网落盘补传
}
```

**终态映射**（`ConnectStatus` → `result` 字段）：
- `Confirmed`/`Uncertain` → `success`（发起成功/已确认/已发起，都算"本次连上了"）
- `Failed`/`NeedPassword` → `failed`
- `Cancelled` → `cancelled`

> **口径待你拍板**（见 §四）：`Uncertain`（已发起但未确认）算 `success` 还是单独值？服务端当前只认 `success|failed|cancelled` 三值，我倾向 `Uncertain`→`success`（"已发起连接"如实记）。

**上报 payload**（对齐服务端 `handleGraydeskConnectLog` 已实现的契约，见 §五）：

```jsonc
{
  "remote_code": "913123456",      // 设备码（必填，6-20位数字）
  "remote_app": "sunlogin",        // sunlogin|todesk（RemoteApp.Id）
  "result": "success",             // success|failed|cancelled
  "client_ts": "2026-11-10T14:30:00", // 客户端本地时间（无时区也行，服务端 tolerant）
  "phone": "138...",               // 客户号码（可空，认不准不填）
  "customer_name": "张三",         // 客户名称（可空）
  "site_note": ""                  // 现场/问题（可空）
  // 明确【无 password/pwd】字段
}
```

**防重**：一次连接会话只上报一条（用 `SessionId` 去重，`HasAttemptedConnection` 已有防重语义，直接对齐）。

### 3.2 ② 4 位本周令牌

**职责**：灰Desk 生成"本周唯一"的 4 位令牌，界面显示给客服；随连接事件写台账，作为"灰Desk↔小灰匠跨端关联轴"。

```csharp
public sealed class WeekToken
{
    public string Current { get; }          // 本周令牌，4位（数字或字母数字，待定）
    public DateTime WeekStart { get; }      // 本周起点（周一 00:00）
    // 逻辑：按 ISO 周（或本地周一）计算，跨周自动重置；进程重启仍一致（写 preferences 持久化）
}
```

**关键设计点（需你拍板，见 §四）**：
- **生成规则**：4 位是什么？随机数字、还是"周序号的确定性散列"？我建议**确定性**（由"当前周"派生，如 `(年*100+周) 取模/散列` 得到固定 4 位），这样灰Desk 和小灰匠两处**算得出同一个值**，无需握手同步。
- **写入台账哪一列**：设计 §3 台账有「本周关联令牌」（第15列，文本）。上报 payload 需加一个 `week_token` 字段（服务端目前还没收这个字段，需补，见 §五 的"契约待补项"）。
- **生命周期**：周一 00:00 起算，下周一重置；进程内/跨进程一致。

### 3.3 ③ 截图识别客户信息

**职责**：框选微信名片/客户资料截图 → OCR 出号码+客户名称 → 填台账。**复用认码那条 OCR 管线，只加一个识别器。**

```csharp
public sealed class CustomerInfoRecognizer
{
    // 输入：一张截图字节（复用 ClipboardImage.TryGet() 取图 + RapidOcrProvider 走 OCR）
    // 输出：CustomerInfo { Phone?: string, Name?: string, Confidence: ... }
    public static Task<CustomerInfo> RecognizeAsync(byte[] image, CancellationToken ct);
}
```

**识别规则（号码/姓名正则 + 容错，认不准留空待认领，不编）**：
- **号码**：正则 `1[3-9]\d{9}`（11位大陆手机号），容错：允许 OCR 把 `O→0`/`l→1` 的形近转换；**多个号码**→ 取最像名片主号的（靠"姓名"行最近），认不准留空。
- **客户名称**：CJK 2-4 字 + 常见名片标签（"微信"、"联系人"、"姓名"、"昵称"、"Name"）邻近；**认不准留空，不猜**。
- **关键红线**：识别结果**只作预填**，界面显示可编辑，客服确认后才落台账；识别不出就空着，绝不编。

**与认码的关系**：完全复用 `RapidOcrProvider.ReadAsync`（同一模型），只是 `RecognitionEngine.Analyze` 之外再跑一个**轻量 `CustomerInfoRecognizer`**（独立正则，不碰认码那套设备码/密码规则）。不另造 OCR 管线。

### 3.4 ④ outbox 兜底

**职责**：断网先落本地队列，有网补传。

```csharp
public sealed class Outbox
{
    public static void Enqueue(string payload);        // 落本地 jsonl（追加写）
    public static Task FlushAsync();                    // 有网时补传（重试 + 去重）
    // 存储：%AppData%/GrayDesk/outbox.jsonl（与一代 Log.Root 同目录体系）
    // 每条带：payload + 生成时间 + 尝试次数；成功上传后删除
}
```

**设计点**：
- **顺序补传**：按落盘顺序补，避免乱序（虽然台账是追加，顺序不关键，但保持简单）。
- **去重**：服务端按"会话键追加"，天然幂等；客户端补传失败重试不产生重复（一条 outbox 条目对应一次连接）。
- **触发**：①上报时失败→入 outbox；②启动时、定时（如每 30s）、网络恢复时→ Flush。
- **上限**：outbox 文件设上限（如 1000 条），超限丢弃最旧（避免无限膨胀）。

---

## 四、分步建方案（每步可独立验、可回滚）

> 原则：每一步都能**单独验证、单独回滚**，不依赖上一步"全对"才能继续。真机连向日葵/ToDesk 的验证**排到最后有远程软件时**（任务约束）。

| 步 | 内容 | 独立验证方式 | 回滚方式 |
|---|---|---|---|
| **步 A · 骨架** | 新建 2.0 csproj + 分层目录，**整层平移** Domain/Ocr/Parsing/Automation/Legacy/UI（直接搬的部分），跑通"编译 + 启动 + 识别一张本地截图出设备码"（不接任何新功能） | 编译通过 + 用一代同款截图跑认码，结果一致 | 删新工程，一代仓库原样不动 |
| **步 B · 上报链路（对接服务端）** | 加 `ConnectLogReporter` + `Outbox`，在 `ConnectCoreAsync` 挂钩；用 `curl --noproxy '*'` 或本地起服务端灌假事件，验证 POST 契约、密钥、密码拦截、落 outbox | **纯本地可验**：假事件 curl 到本地服务端，读飞书新表看是否追加一行；断网模拟看 outbox | 撤销上报钩子，2.0 退化为"只连不上报" |
| **步 C · 4位本周令牌** | 加 `WeekToken`，界面显示；先本地验证"本周唯一 + 跨周重置 + 进程重启一致"；再决定是否上报到台账（补服务端 `week_token` 字段） | **纯本地可验**：改系统日期测跨周重置 | 删令牌展示，不影响上报 |
| **步 D · 客户信息识别** | 加 `CustomerInfoRecognizer`，复用 OCR 管线；界面加"客户信息"预填区 | **纯本地可验**：拿微信名片截图 OCR 出号码/姓名，核对准确率；认不准留空 | 删识别器，认码不受影响 |
| **步 E · 前端整合** | 把 B/C/D 三块的 UI 并入 ConnectionCard（客户信息区 + 令牌展示 + 上报状态提示） | 真机截图确认（约束要求界面改动需真机截图） | 撤回 UI 改动 |
| **步 F · 真机验证** | 有远程软件时，真机连向日葵/ToDesk，验证完整链路（认码→填码→连接→上报→台账落一行） | 真机 | — |

**建议顺序**：A → B → C → D → E → F。**B 优先于 C/D**，因为上报是核心价值，且服务端已就绪，最早能端到端看到成果。

---

## 五、对接服务端的调用契约

**端点**：`POST /graydesk/connect-log`（小灰机 backend，步1 已上线）

**请求**：
- Header：`X-Upload-Key: <GRAYDESK_UPLOAD_KEY>`（独立密钥，与 huidu 分开）；`Content-Type: application/json`
- Body（对齐服务端 `handleGraydeskConnectLog` 已实现字段）：

```jsonc
{
  "remote_code": "913123456",     // 必填，6-20 位数字，服务端正则 GRAYDESK_CODE_RE
  "remote_app": "sunlogin",       // 可选，^[a-z0-9]{1,20}$，空默认 sunlogin
  "result": "success",            // 可选，success|failed|cancelled，空默认 success
  "client_ts": "2026-11-10 14:30", // 可选，服务端 tolerant 拆分日期+时间
  "phone": "138XXXXXXXX",         // 可选，≤32 字
  "customer_name": "张三",        // 可选，≤64 字
  "site_note": "..."              // 可选，≤2000 字
  // 红线：出现 password/pwd 字段 → 服务端直接 400 拒收
}
```

**响应**：`200 { ok:true, action:"created", key:"<会话键>", record_id:"" }`；飞书未配/写失败时 `200 { ok:true, queued:true }`（服务端落 outbox 留底）。

**落表**：飞书《灰Desk 客户连接台账》`tblaEK8PHoNcowQm`，17 列，**按会话键追加（不 upsert）**。字段映射（服务端 `syncGraydeskConnectLog` 已实现）：
- 日期 ← client_ts 取日期（主字段，文本 yyyy-MM-dd）
- 连接时间 ← client_ts 完整到分（第17列，yyyy-MM-dd HH:mm）
- 联系方式 ← 固定「远程」
- 号码 ← phone；问题描述 ← site_note；远程码 ← remote_code；远程软件 ← remote_app→中文；连接结果 ← result→中文；客户名称 ← customer_name
- 设备/型号/状态/类型/记录标签/责任人/本周关联令牌 ← P1 留空

**契约待补项（需你拍板）**：
1. **`week_token` 字段**：4位令牌若需入台账（第15列「本周关联令牌」），服务端 `handleGraydeskConnectLog` + `syncGraydeskConnectLog` 目前**都还没收这个字段**，需补一个小改动（步 C 时一并做）。
2. **`client_ts` 时区**：客户端发本地时间（不带时区），服务端目前按原样拆日期/时间，不转时区。需确认口径（客户机与服务端同北京时间，暂可忽略时区）。

---

## 六、待你拍板的问题（我停在这）

1. **`Uncertain` 终态口径**：已发起但未确认连接，算 `success` 还是单独记？服务端现在只认三值，我建议 `Uncertain`→`success`。
2. **4位令牌生成规则**：确定性派生（灰Desk/小灰匠可各自算出同一值，无需握手）还是纯随机（需另存同步）？我强烈建议**确定性**。
3. **4位令牌是否 P1 就入台账**：若要，需补服务端 `week_token` 字段（步 C 做）；若 P1 只在灰Desk 界面显示、不入表，则服务端不动。
4. **会话观测轮询是否保留**（§2.8 弃用清单里的待定项）：一代用进程/窗口观测"确认连接成功"，2.0 若上报已覆盖终态，是否简化掉这套轮询？

---

## 七、风险与红线复述

- **出包铁律**：`IncludeNativeLibrariesForSelfExtract=true` 一个不能漏（历史崩溃事故），步 A 建 csproj 时就写死。
- **不碰小灰机运行时**（8520/8521/8080）。
- **老实红线**：号码/客户名称/令牌认不准留空待认领，绝不编；连接结果如实记；**密码永不入台账、永不进上报 payload**。
- **真机验证**：连向日葵/ToDesk 排到有远程软件时（步 F）。

---

（本文为架构设计，未写任何代码、未动任何文件。等你审过，再按 §四 分步建。）
