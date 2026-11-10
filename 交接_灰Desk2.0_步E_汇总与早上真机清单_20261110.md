# 步E · 汇总：包在哪、本地已验什么、早上真机怎么测

日期：2026-11-10 02:50 · 版本 **2.4.0** · 窗口：灰Desk 2.0 开发窗
三份分步报告：
`交接_灰Desk2.0_步E-1_ToDesk命令行通道_20261110.md`
`交接_灰Desk2.0_步E-2_向日葵剪贴板接力_20261110.md`
`交接_灰Desk2.0_步E-3_收尾与令牌时点_20261110.md`

---

## 一、包在哪、怎么起

```
E:\开发项目\GrayDesk2.0\dist\步E-2.4.0-20261110\GrayDesk2.0.exe
```

- 自包含单文件，**90 MB**，`IncludeNativeLibrariesForSelfExtract=true` 已在 csproj 写死；同目录 `Models/ Tools/ Wallpapers/ ThirdParty/` 是 OCR 模型与第三方说明，**要一起拷，别只拷 exe**。
- **怎么起**：双击 `GrayDesk2.0.exe`。首次启动会弹「软件说明」，要勾「我已知悉并允许读取剪贴板」再保存（现成行为，与步E 无关）。之后托盘常驻，主界面可关。
- 认版本：卡片预览底栏显示 `… · v2.4.0`；日志 `%APPDATA%\GrayDesk20\logs\helper-20261110.log` 里搜「灰Desk启动 2.4.0」。
- 想退回老办法：托盘右键 →「老办法连接（OCR 点按）」，立刻生效，不用重启。

---

## 二、本地已经验过的（这部分不用早上再验）

| 项 | 结果 | 怎么复现 |
|---|---|---|
| 编译 | 0 警告 0 错误 | `dotnet build` |
| 认码/解析回归（一步没动） | **35 passed** | `GrayDesk2.0.exe --verify` |
| 会话判定/自适应/UIA 回归 | **62 passed** | `GrayDesk2.0.exe --verify-graydesk` |
| 步E 新增自检 | **21 passed** | `GrayDesk2.0.exe --verify-step-e` |
| ToDesk 参数拼装（URL/转义/空密码/-control/混字母） | 全过 | 同上 + `--probe-todesk-cli 905098332 ub14eq0r` |
| **Alt+V 钩子真按键**（本机注入两次） | 挂上、**2/2 收到并吞掉** | `--probe-altv-hook <json>` |
| 剪贴板接力往返 + 还原原文 | `restored=true` | `--probe-relay-clipboard <json>` |
| 接力状态机六种走法（含「前台不是向日葵就不粘」） | 全过 | `--verify-step-e` |
| 卡片四态离屏渲染 | 文字无截断/无重叠 | `--render-connect-card <png> 620 relay1` |
| **单文件包能起不崩** | 启动后存活 8 秒被外部结束（`EXIT=124`），日志 `灰Desk启动 2.4.0`、OCR 后台检查通过、无异常 | `timeout 8s GrayDesk2.0.exe` |
| 单文件包内 OCR 原生库 | `--probe-ocr-health` 退出码 0（真跑了一次 PP-OCR 并读回 123456789） | 同左 |

证据文件都在 `E:\开发项目\灰Desk重构\assets\stepE\`：`todesk-cli.json`、`altv-hook.json`、`relay-clipboard.json`、`card-relay1.png`、`card-relay2.png`、`card-none.png`、`card-sent.png`。

---

## 三、早上真机清单（4 项，按这个顺序做）

### ① ToDesk：URL 和 `-control` 各试一次，定最终顺序

- **怎么测**：拿一个**真客户**的设备码（能连通的），在卡片里填码 → 点「识别并连接」。跑完一次看日志：
  ```
  %APPDATA%\GrayDesk20\logs\helper-20261110.log  搜 "ToDesk 命令行 route="
  ```
- 想单独验参数（不发连接）：`GrayDesk2.0.exe --probe-todesk-cli <设备码> <密码>`
- **看什么算过**：
  1. `route=UrlArgument` 且 ToDesk 自己弹出连接/要密码窗 → **URL 这条路成立**（这是主通道，成立就不用管别的）
  2. 若 URL 起来但没反应（20 秒后卡片显示「尚未确认」），再试 `-control`：把开关打开方式我留了口子（下一版加一键切换），**先手工在 ToDesk 里试**：
     `"D:\todesk\ToDesk.exe" -control -id <码> -passwd <密码>`
  3. 记下：能连的是哪条、几秒起来、窗口什么样 → 我按这个定顺序
- **可能翻车**：临时码走 `-control` 静默失败（官方文档就写了只对设备列表有效），这是预期内，不算 bug。

### ② 向日葵：真人按 Alt+V 走全程

- **怎么测**：卡片填真客户设备码（有验证码就一起填）→ 点「识别并连接」→ 向日葵被唤起、卡片出现「接力 · 第 1 步 / 共 2 步」→ **点一下「伙伴识别码」框**，按 `Alt+V`。
- **看什么算过**：
  1. 设备码**出现在输入框里**，且**自动回车**后界面开始走（不是还停在原处）
  2. 有验证码时：弹出密码框 → 卡片变「第 2 步 / 共 2 步」→ 点密码框按 `Alt+V` → 验证码进框
  3. 界面进「连接中/等待对方」→ 卡片出**令牌**、上报状态变成「已进台账」
  4. 结束后随手 `Ctrl+V` 一下：**剪贴板里是你原来复制的东西，不是验证码**
- **可能翻车**：
  - Alt+V 被向日葵/输入法抢了（比如当成菜单键）→ **扣键没反应**。这时告诉我，我把热键换成别的（代码里只改一个键值，`AltVHotkey` 顶部常量）。
  - 界面文案跟我写的判据不一样（我按 16.8 写的「输入连接密码」「正在连接」）→ 表现是「界面没前进」判失败。**截一张密码框和等待界面的图给我**，我按真文案补词。
  - 粘码到回车之间 300ms 不够（码没进框就回车了）→ 表现是界面没前进。

### ③ 全链路：真连一次，台账落一行

- **怎么测**：真连成功后，去《灰Desk 客户连接台账》看最新一行。
- **看什么算过**：这一行里 **识别码 / 本周令牌 / 客户号码 / 结果** 四项对得上，且**没有密码字段**（红线）。
- 卡片上「本周令牌」那个四位号要跟台账里的一致（「念给客服，对准这一行」就是干这个的）。

### ④ 包：双击启动不崩

- 已代验一半（包能起、8 秒存活、OCR 原生库正常）。剩下要你**在客服机上双击**试一次：托盘出现、主界面出来、Ctrl+Alt+R 能读剪贴板。
- 若报「缺 dll / 16 位应用程序」→ 说明 exe 被单独拷走了，**把整个文件夹一起拷**。

---

## 四、改了什么（文件级）

**新增 7 个**
| 文件 | 干什么 |
|---|---|
| `Infrastructure/ToDeskLink.cs` | 拼 URL / `-control` 参数、转义、起进程、读协议注册 |
| `Connections/ToDeskDirectConnector.cs` | URL→协议→`-control` 三段式发起 |
| `Infrastructure/ClipboardRelay.cs` | Win32 剪贴板：装码/换密码/还原原文 |
| `Infrastructure/AltVHotkey.cs` | 低级键盘钩子（装不上退 RegisterHotKey），只在接力期上膛 |
| `Connections/SunloginScreen.cs` | 观察型 OCR 的界面判据（主界面/密码框/等待/报错/已连上） |
| `Connections/SunloginRelay.cs` | 接力状态机 + 静/动开关 `ConnectOptions` |
| `Diagnostics/StepETests.cs` | 21 项自检 + 3 个探针 |

**改动 10 个**：`ClientConnector`（新通道分流 + ToDesk 45s 观察）、`AssistantController`（接力状态转发、停止时叫停、连接中文案）、`ConnectionCard.xaml(.cs)`（接力区 + 文案）、`DesktopHost`（开关落地 + 托盘切换动作）、`App.cs`（托盘菜单项）、`Preferences`（`UseLegacyClickPath` / `RelayWaitSeconds`）、`InputSimulator`（`SendPaste` / `SendEnter`）、`CommandLine`（4 个新入口 + 两种渲染态）、`GrayDesk2.0.csproj`（2.4.0）、`CHANGELOG.md`。

**没动的**：认码 OCR 与解析（`RecognitionPipeline` / `Parsing/*`）、上报映射与服务端、`WeekToken` 生成、客户信息识别、软件发现。

---

## 五、要你（和经理）过一下的三件事

1. **老点按代码：删还是留？** 现在「默认不走、代码留着、托盘开关可退」（决策第 4 条要我留，第 1 条写的是「退役」）。要彻底删就下一版切，那时没退路。
2. **取消要不要进台账？** 现在是「取消也有令牌、也上报 cancelled」（沿用步B）。若改成「没连上不进台账」，是改口径，我没擅自动。
3. **热键最终定哪个？** 代码里是 Alt+V；真机上若撞键，一个常量就能换（建议备选 `Ctrl+Alt+V`，与读屏的 `Ctrl+Alt+R` 同族但不冲突）。

---

## 六、红线自查

- 密码：只进剪贴板，**不进日志、不进台账、不进上报**；结束/超时/取消一律清掉 ✓
- 剪贴板：收尾还原用户原文（本地实测 `restored=true`）✓
- 认码 OCR：**一行未改**（`--verify` 35 项全过）✓
- 一代仓库：**未动**（只在 `GrayDesk2.0` 里改）✓
- 小灰机运行时（8520/8521/8080）：**未碰** ✓

---

## 七、部署记录：已发到客服机桌面（2026-11-10 09:08）

**目标机**：192.168.X.X（客服机，用户 HD），经 C 盘管理共享写入。

**包路径**：`C:/Users/HD/Desktop/灰Desk2.0-步E测试/`
**启动入口**：桌面 `灰Desk2.0 步E测试.cmd`（双击即起，工作目录已设好）
**版本**：2.4.1（46 个文件 / 130.3 MB / 单文件自包含）
**exe SHA256**：`D847C3F37DE62C8A38649A18BC22D0F5CAC841C0506535503FBEBF9768B828D5`
**核验**：源与目标文件数 46=46、exe 哈希一致、ProductVersion=2.4.1

### 为此做的一处修补（2.4.0 → 2.4.1）

客服机向日葵装在 `C:/Program Files/Oray/AweSun/AweSun.exe`，而 `RemoteSoftwareDiscovery` 的常见路径表只有 `Oray/SunLogin/SunloginClient`——注册表若读不到 DisplayIcon/InstallLocation，就会报「找不到向日葵」，真机第一步就卡住。
已补 `Oray/AweSun`、`Oray/AweSun/agent`、`Program Files/Oray/AweSun` 三条候选，其余逻辑一行未动。本机仍靠用户配置命中 `D:/AweSun`，`--verify-step-e` 21/21 通过、`--probe-ocr-health` 退出码 0。

### 客服机环境已核对

| 项 | 状态 |
|---|---|
| 向日葵 | `C:/Program Files/Oray/AweSun/AweSun.exe` 在 |
| ToDesk | `C:/Program Files/ToDesk/ToDesk.exe` 在 |
| 旧版灰Desk（一代） | `%LOCALAPPDATA%\Programs\GrayDesk\灰Desk.exe` v2.2.0 在，**未动**，桌面 `灰Desk.lnk` 未改 |
| 2.0 配置目录 | `%APPDATA%\GrayDesk20` 全新（一代是 `%APPDATA%\GrayDeskV2`，互不干扰） |

**测试提醒**：老灰Desk 若在运行会抢剪贴板，测前先从托盘退掉。包目录内已放 `真机测试步骤.txt`（那边看不到 E 盘报告，把清单带过去了）。


---

## 八、安装包（2.4.1，2026-11-10 15:22）

**产物**：`E://开发项目//GrayDesk2.0//dist//灰Desk2.0-2.4.1-安装包.exe`（117.5 MB）
**已发**：`C://Users//HD//Desktop//灰Desk2.0-2.4.1-安装包.exe`（哈希一致）
**SHA256**：`D7D51D5455B3EA2E4CDC6F540496490861A3190EDAC1B5ABE918C0DB80ABDB4A`

| 项 | 值 |
|---|---|
| 打包工具 | Inno Setup 6.5（winget 装到用户目录）+ 简体中文语言包 |
| 安装位置 | `{localappdata}\Programs\GrayDesk20`，与一代 `Programs\GrayDesk` 并列 |
| 权限 | `PrivilegesRequired=lowest`，不需要管理员，客服机可直接装 |
| 快捷方式 | 桌面「灰Desk 2.0」+ 开始菜单，卸载项「灰Desk 2.0」 |
| 打包内容 | 45 个文件（排除 .pdb），Models/Tools/ThirdParty/Wallpapers 全带 |
| 脚本 | `E://开发项目//GrayDesk2.0//installer//灰Desk2.0.iss` |

**本地安装验证（已跑完）**：静默安装 → 47 个文件到位（45 + unins000.exe/dat）→ `--verify-step-e` 21/21 → `--probe-ocr-health` 退出码 0 → `unins000.exe /VERYSILENT` 卸载后目录干净消失。

**共存注意**：安装版与桌面绿色版共用 `%APPDATA%\GrayDesk20` 与单实例信号 `Local\GrayDesk20-Open`。三个灰Desk（一代 2.2.0、绿色 2.4.0、绿色 2.4.1）任两个同时开都会互相干扰，测前必须全退。`CloseApplications=yes` 只能关 `{app}` 目录内的进程，关不掉桌面绿色版。
