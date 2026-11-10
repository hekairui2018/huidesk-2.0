# 交接 · 灰Desk 2.0 · 步B 上报链路 · 2026-11-10

## 一、本步目标

把灰Desk 连接终态事件通过公网回传上报到服务端 `POST /graydesk/connect-log`，落飞书《灰Desk 客户连接台账》一行；断网/失败落本地 outbox 补传；同时把经理审定第 1 条「Uncertain → 单独『已发起』」的服务端改动一并做完。

## 二、改动清单

### 灰Desk 端（`E:\开发项目\GrayDesk2.0`）

| 文件 | 改动 | 性质 |
|---|---|---|
| `Infrastructure/ConnectLogReporter.cs` | **新增**。连接事件上报 + 本地 outbox（断网补传） | 新增 |
| `Application/AssistantController.cs` | `ConnectCoreAsync` 终态收口点挂 fire-and-forget 上报钩子，新增 `ReportConnectOutcome` 终态映射 | 改 |

**上报签名（红线：不带 password）**：

```csharp
ConnectLogReporter.Report(app, code, result, clientTs?, phone?, customerName?, siteNote?)
```

**终态映射**（照经理审定）：

| `ConnectStatus` | result 值 | 说明 |
|---|---|---|
| `Confirmed` | `success` | 连接确认成功 |
| `Uncertain` / `Launched` | `initiated` | 已发起、结果未定（经理审定第1条） |
| `Cancelled` | `cancelled` | 用户取消 |
| `NeedPassword` / `Failed` / 其他 | `failed` | 失败 |

**outbox 机制**：
- 上报事件先落本地 `%AppData%/GrayDesk20/connect-log.outbox.jsonl` 留底，再尝试补传。
- 补传串行（同一时刻只一个 flush），逐条发送，成功删头、失败保留下次再补。
- 密钥从 `%AppData%/GrayDesk20/connect-log.key` 读（未配则只落 outbox 不发送，绝不硬编密钥进代码）。
- 全部 try-catch，绝不阻塞/中断主流程。

### 服务端（`E:\开发项目\xiaohuiji\backend`）

| 文件 | 改动 |
|---|---|
| `server.js` | `GRAYDESK_RESULT_RE` 加 `initiated`；报错文案同步 |
| `feishu-bitable.cjs` | 连接结果映射加 `initiated → 已发起` |
| 飞书表《灰Desk 客户连接台账》 | 「连接结果」单选加选项「已发起」（现为 成功/失败/取消/已发起） |

## 三、验证结果（全实测通过）

### ① 正常落表（success）

```
POST /graydesk/connect-log  X-Upload-Key 正确
→ HTTP 200, action=created, record_id=reczz28XeUrdhWM3
```

读回字段映射全对：日期=2026-11-10、连接时间=2026-11-10 00:50、联系方式=远程、远程软件=向日葵、连接结果=成功、号码/客户名称/问题描述全落。

### ② initiated 落表（新增值）

```
POST ... result=initiated, remote_app=todesk
→ HTTP 200, action=created, record_id=reczz28XeUtDiwst
```

读回：连接结果=**已发起**（新选项生效）、远程软件=ToDesk。服务端 accept `initiated` 通过。

### ③ 密码拦截（红线）

```
POST ... 带 "password":"abc123"
→ HTTP 400, message="密码字段禁止上报（红线：密码永不入台账）"
```

### ④ 密钥校验 + result 白名单

- 错误密钥 → HTTP 401 `X-Upload-Key 无效`
- `result=bogus` → HTTP 400 `result 必须为 success|initiated|failed|cancelled`

### ⑤ 断网/失败落 outbox（兜底）

临时把 `FEISHU_GRAYDESK_TABLE_ID` 改成非法值 → curl 触发飞书写入失败：

```
→ HTTP 200, queued=true, "飞书写入失败，已落 outbox 待补录"
→ outbox 落盘 graydesk_outbox.jsonl（含完整事件，无密码字段）
```

验证后已恢复 `.env`、删除 outbox 测试文件、删除飞书两条测试记录（台账干净）。

## 四、清理与环境

- 服务端验证用临时密钥 `test-key-stepB-20261110`，验证完已把 `.env` 的 `GRAYDESK_UPLOAD_KEY` 改回**空**（正式密钥仍待经理定稿）。
- 服务端已停（3789 无 LISTENING，仅残留 TIME_WAIT）。
- 小灰机运行时 8520/8521/8080 全程未碰，仍在运行。

## 五、遗留/待办

1. **正式 `GRAYDESK_UPLOAD_KEY` 待经理定稿**：灰Desk 端运行时把密钥写到 `%AppData%/GrayDesk20/connect-log.key`（或经 Preferences 配置）才能真连公网。目前代码读该文件，缺省空则只落 outbox。
2. **灰Desk 端 outbox 未做真机断网实测**：`ConnectLogReporter` 的补传逻辑已编译通过 + 代码审查，但未在真机断网/恢复网络场景下实测。服务端 outbox 兜底已实测通过。
3. **`client_ts` 时区格式**：灰Desk 端上报 `yyyy-MM-dd HH:mm:ss zzz`，服务端 `graydeskSplitTs` 用正则 `yyyy-MM-dd[ T]HH:mm` 截取到分，已验证兼容（curl 用 `+08:00` 也正确拆出 00:50）。

## 六、下一步

等经理定稿 `GRAYDESK_UPLOAD_KEY`，并放行后进步 C（4位每周顺序号令牌）或步 D（截图识别客户信息）。
