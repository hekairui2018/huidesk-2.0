# 交接 · 灰Desk 2.0 · 步A 骨架（2026-11-10）

> 任务：灰Desk 2.0 全新骨架，整层平移一代可用的源码，跑通"编译+启动+识别一张本地截图出设备码与一代一致"。先 A 骨架 → 停报。

---

## 一、做了什么

**新建独立工程** `E:\开发项目\GrayDesk2.0\`（经理指定：独立目录、英文名，与一代 RemoteConnectHelper 仓库分开）。

**整层平移一代源码**（一代 `RemoteConnectHelper.V2` 磁盘现状，含未提交改动）：

| 平移内容 | 处置 |
|---|---|
| `Application/ Connections/ Discovery/ Domain/ Infrastructure/ Legacy/ Ocr/ Parsing/ UI/`（9 个源码目录） | 直接搬 |
| `App.cs` `GlobalUsings.cs`（根文件） | 直接搬 |
| `Models/ Wallpapers/ Tools/ ThirdParty/`（资源） | 直接搬 |
| `Assets/`（app.ico / app_glyph.png / app_logo.png，从一代 Assets 拷贝） | 直接搬 |
| `README.md CHANGELOG.md THIRD-PARTY.md`（文档） | 直接搬 |
| `Diagnostics/`（24 个探针） | **保留**（见下方"架构纠正"） |
| `scripts/`（开发期脚本）、`bin/ obj/`（构建产物） | 弃用，不搬 |

**关键改动（4 类）**：

1. **新 csproj**：`AssemblyName=GrayDesk2.0`，`RootNamespace` 保持 `RemoteConnectHelper.V2`（避免 172 处 namespace 改动引入无关 diff）；**`IncludeNativeLibrariesForSelfExtract=true` 写死**（出包铁律）；`Version=2.3.0`；`Product=灰Desk 2.0`；图标改 `Assets/` 本地路径。
2. **进程标识改 `GrayDesk20-*`**（与一代 `GrayDeskV2-*` 区分，可共存）：单例 Mutex、Open/Exit 事件、OCR 准备 Mutex。
3. **本地数据目录改 `GrayDesk20`**（与一代 `GrayDeskV2` 区分）：`Log.Root`（AppData）、OcrMaintenance（ocr-compat.json）、OnlineUpdater（update-source.json / Updates）。
4. **RootNamespace 不动**：`RemoteConnectHelper.V2` 是内部实现细节，不影响外部，2.0 内部沿用（避免巨量 namespace diff）。

---

## 二、架构纠正（发现架构稿 §2.8 判断有误，如实报）

架构稿 §2.8 我把 `Diagnostics/` 标为"弃用"。**实测发现它有 3 个运行时必需命令，不能砍**：

- `--probe-ocr-health` → `OcrBootstrap.ProbeAsync()`（启动时 OCR 环境自检，`OcrBootstrap.CheckAndRepair` 依赖）
- `--prepare-ocr` → `OcrBootstrap.Prepare()`（OCR 后台准备/修复）
- `--ocr-worker` / `--ocr-worker-test-exit` / `--ocr-worker-test-hang` → `OcrWorker.Run()`（`IsolatedRapidOcrProvider` 独立进程 OCR 隔离，崩溃保护的核心）

**结论**：`Diagnostics/` 目录**步 A 保留整目录**（它含运行时必需的 OCR worker/自检 + 一堆开发期回归探针）。"精简掉纯开发探针"留作**后续独立优化项**（不影响步 A/B/C 主线，且需逐个甄别哪些被运行时引用、哪些纯属回归测试）。

其余架构稿结论不变（复用清单、新增四块、分步方案、服务端契约均有效）。

---

## 三、验证结果（全部实测通过）

| 验证项 | 方法 | 结果 |
|---|---|---|
| 编译 | `dotnet build` | ✅ 0 警告 0 错误 |
| 识别一致性（向日葵图 `T2_sun_longpwd.png`） | 一代 vs 2.0 `--selftest` | ✅ AppName=向日葵、DeviceCode=491223887、Decision=自动，**逐字段一致** |
| 识别一致性（ToDesk 图 `T14_todesk_light_905.png`） | 一代 vs 2.0 `--selftest` | ✅ AppName=ToDesk、DeviceCode=905098332、Password=ub14eq0r、Decision=自动，**逐字段一致** |
| OCR 初始化链路 | `--prepare-ocr` | ✅ 退出码 0，OCR 后台准备通过 |
| GUI 启动冒烟 | 启动 exe 5 秒看存活 + 查日志 | ✅ 进程存活无崩溃，生成 `%AppData%/GrayDesk20/logs/helper-20261110.log` |
| 启动日志内容 | 读日志 | ✅ OCR 工作进程启动、PP-OCRv5 模型加载（engine.models.done）、识别请求完成（blocks=49/121）、OCR 启动后台检查 |
| 进程标识切换 | 单例锁 `GrayDesk20-SingleInstance` | ✅ 与一代 `GrayDeskV2` 不冲突，可共存 |

**核心验证（任务要求"识别一张本地截图出设备码与一代一致"）**：两张不同软件（向日葵 + ToDesk）的测试截图，一代和 2.0 的 `--selftest` 输出**逐字段完全一致**（软件类型、设备码、密码、连接决策、理由），证明认码管线整层平移无损。

---

## 四、当前工程状态

```
E:\开发项目\GrayDesk2.0\
├── GrayDesk2.0.csproj        # AssemblyName=GrayDesk2.0，IncludeNativeLibrariesForSelfExtract=true
├── App.cs                    # 进程标识 GrayDesk20-*
├── GlobalUsings.cs
├── Application/ Connections/ Diagnostics/ Discovery/ Domain/
├── Infrastructure/           # Log.Root/OcrMaintenance/OnlineUpdater 改 GrayDesk20
├── Legacy/ Ocr/ Parsing/ UI/
├── Assets/ Models/ Wallpapers/ Tools/ ThirdParty/
└── README.md CHANGELOG.md THIRD-PARTY.md
```

一代仓库 `E:\开发项目\RemoteConnectHelper\` **原样未动**（只读平移，没改一代任何文件）。

---

## 五、遗留 / 待办

1. **Diagnostics 精简**（后续独立项）：保留运行时必需的 3 个命令 + OcrWorker，砍掉纯开发回归探针（--verify-*、--probe-*、--batch、--evaluate、--render、--selftest 等）。
2. **出包验证**：步 A 只验证了 `dotnet build`（Debug 编译），未做 `dotnet publish` 自包含单文件出包 + 原生库解压验证（出包铁律的实机验证，排到后续出包步）。
3. **真机连向日葵/ToDesk**：按约束排到有远程软件时（步 F）。

---

## 六、下一步（步 B，等经理放行）

按架构稿 §四，步 B = **上报链路**：`ConnectLogReporter` + `Outbox` 挂 `ConnectCoreAsync`，本地 curl 灌假事件验证落台账一行、密码拦截、断网落 outbox。

**注意**：经理审定第 1 条要求 **Uncertain → 单独「已发起」**，这涉及**服务端改动**（result 加第 4 值 `initiated`、飞书「连接结果」单选加「已发起」）。步 B 时我会把服务端这个改动一并做（属于"对接服务端契约"的一部分）。

---

（步 A 骨架完成，停报经理。等放行再进步 B。）
