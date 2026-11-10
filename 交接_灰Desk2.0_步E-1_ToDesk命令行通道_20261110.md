# 步E-1 · ToDesk 命令行通道（实施报告）

日期：2026-11-10 02:45 · 窗口：灰Desk 2.0 开发窗 · 版本：2.4.0
前置：`交接_灰Desk2.0_步E_连接升级调查与影响面_20261110.md` · 任务：`任务_灰Desk2.0_步E_全程实施_20261110.md`
状态：**代码完成、本地可验部分全过、真连留早上（清单见汇总）**

---

## 1. 做了什么

新建 `Infrastructure/ToDeskLink.cs`（只负责怎么拼、怎么起）＋ `Connections/ToDeskDirectConnector.cs`（负责按顺序发出去）：

| 顺序 | 通道 | 原文 | 说明 |
|---|---|---|---|
| 主 | URL 当参数交给 exe | `"ToDesk.exe" "ToDesk://connect&905098332&ub14eq0r"` | 等价于协议处理器，但不走 shell（不会命令注入）。**不起第二个 ToDesk 进程就不另发** |
| 次 | Windows 协议处理器 | `ToDesk://connect&…`（ShellExecute） | exe 不认参数时的退路，注册表指向同一个 exe |
| 备 | `-control` | `-control -id 905098332 -passwd ub14eq0r` | 官方文档注明「仅支持设备列表里已解锁设备」，客户临时码可能静默失败 |
| 都不行 | — | 直接报失败、**不点任何按钮** | 请人工在客户端点连接，日志留原因 |

一代 `AdditionalClientConnector.cs:52` 那行 `ToDesk://connect&…` 一直是死代码（路由名单里没有 ToDesk），这次把它提成了正式通道。

**空密码**：只发 `ToDesk://connect&905098332`，结尾不留空 `&`；`-control` 也只给 `-id`，不带空 `-passwd`。
**转义**：密码过 `Uri.EscapeDataString`——密码里有 `&`/`#`/`%` 会截断整条 URL，这条单独写了测试。
**设备码里混字母**：不拼 URL、不猜（`90509O332` 不会被当成 `905098332`）。

## 2. 终态判定（这一步才是关键）

`ClientConnector.ConnectToDeskByCommandAsync`：先把「发之前」的窗口集合记下来 → 命令行发出去 → 交给 `SessionObserver.ConfirmAsync` 判：

- 连上 = **新窗 + 标题带本次设备码 + 明确「已连接」+ 有「断开连接」按钮**（沿用步B 的口径，两条连续才认）
- 要密码 = `NeedPassword`（弹卡让人补）
- 失败 = 客户端明确报错文案
- 45 秒内都没证据 = `Uncertain`（界面上是「尚未确认」，上报 `initiated`）

## 3. 一处按决策「删」的地方，我实际是「移出主路径」，要你确认

决策写的是「删：控制型 OCR——连接环节的『找框点按＋点后全屏找红字轮询』退役」。
`ToDeskConnector.PostConnect` 那套全屏 OCR 轮询**已经从默认路径上摘掉了**，但函数本身留着——因为同一个决策第 4 条又要求「老点按路径留代码但默认关，新法坏了能一键退回」。函数删掉，退回开关就是空的。

所以现在的实际状态是：**默认一律走命令行；点按 + 全屏轮询只活在托盘「老办法连接」开关后面**。若你要的是彻底删代码，说一声，我下一版把老路整体切除（那时就没有一键退回了）。

## 4. 本地验过什么

```
--probe-todesk-cli 905098332 ub14eq0r
  url                = ToDesk://connect&905098332&ub14eq0r
  转义示例            = ToDesk://connect&905098332&a%26b%23c%25d
  controlDescription = -control -id 905098332 -passwd ub14eq0r
  protocolRegistry   = "D:\todesk\ToDesk.exe" "%1"
  executable         = D:\todesk\ToDesk.exe（存在）
```

自检里 ToDesk 相关 5 项全过：URL 格式、空格清理、`& # %` 转义、空密码分支、混字母拒绝。
证据 JSON：`灰Desk重构/assets/stepE/todesk-cli.json`。

**没做的**：真发一次连接。凌晨不向随便一个码发起远控请求，这条留早上第 1 项。

## 5. 早上要验的（详见汇总清单第 1 项）

用真客户的码各试一次 URL 和 `-control`，只记「能不能连、几秒起来、窗口长什么样」。
看日志关键字：`ToDesk 命令行 route=UrlArgument|ProtocolShell|ControlCommand`。若 URL 起来但没连上，我再按 route 调顺序。
