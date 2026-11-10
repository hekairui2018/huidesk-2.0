# 灰Desk 2.0 P1 · 步1 服务端写通 交接报告（终版）

> 任务：`E:\开发项目\任务_灰Desk2.0_P1_客户连接台账_20261109.md`
> 前置：步0 已认可；口径按更新后设计 §3（铠江 2026-11-09 定稿）落地
> 铁律：一步一停一报。本步已建飞书新表 + 改服务端代码 + 回填 table_id，**未动小灰机运行时（8520/8521/8080）**。
> 日期：2026-11-09

---

## 一、本步完成的三件事

### 1. 飞书新表《灰Desk 客户连接台账》已建好

- **table_id = `tblaEK8PHoNcowQm`**（同 app `GNEZbCXJ7aFL5js6BGDc9suanqA`，与《HD升级助手运行日志》不同 table）。
- 已回填 `.env`：`FEISHU_GRAYDESK_TABLE_ID=tblaEK8PHoNcowQm`。

**17 列结构（列顺序 = 日报列在前、机制列在后，已实测确认）**：

| # | 列名 | 类型 | 选项 | P1 落表 |
|---|---|---|---|---|
| 1 | 日期（主字段）| 文本 | — | ✅ client_ts 取日期 yyyy-MM-dd |
| 2 | 设备 | 单选 | 发送设备/A系列/B系列/C系列/D系列 | 空（回传补）|
| 3 | 型号 | 文本 | — | 空（回传补）|
| 4 | 联系方式 | 单选 | 企业微信/微信/电话/远程/现场 | ✅ 固定「远程」|
| 5 | 号码 | 文本 | — | ✅ OCR，可空待认领 |
| 6 | 问题描述 | 文本 | — | ✅ site_note |
| 7 | 状态 | 单选 | 已解决/未解决 | 空（回传/手动）|
| 8 | 备注 | 文本 | — | 空 |
| 9 | 类型 | 单选 | 常规调试/问题支持/复杂调试/困难调试 | 空（默认常规，后续）|
| 10 | 记录标签 | 单选 | 全彩/单色/双色/异步/同步 | 空（回传补）|
| 11 | 责任人 | 单选 | 何铠江 | 空（后续）|
| 12 | 远程码 | 文本 | — | ✅ 设备/查回锚 |
| 13 | 远程软件 | 单选 | 向日葵/ToDesk | ✅ 识别结果 |
| 14 | 连接结果 | 单选 | 成功/失败/取消 | ✅ 终态 |
| 15 | 本周关联令牌 | 文本 | — | 空（握手后填）|
| 16 | 客户名称 | 文本 | — | ✅ OCR 名片 |
| 17 | 连接时间 | 文本 | — | ✅ client_ts 完整 yyyy-MM-dd HH:mm（放最后）|

### 2. `syncGraydeskConnectLog` 字段映射已改为新列名

`backend/feishu-bitable.cjs`：
- `CFG` 新增 `graydeskTableId`（读 `FEISHU_GRAYDESK_TABLE_ID`）。
- 新增 `graydeskConfigured()`、`graydeskSplitTs()`（client_ts 拆「日期」+「连接时间」，精确到分）。
- `syncGraydeskConnectLog(rec)`：**只追加（POST 新建，不 upsert）**，字段映射：
  - `日期` ← client_ts 取日期（主字段，只日期）
  - `连接时间` ← client_ts 完整 yyyy-MM-dd HH:mm
  - `联系方式` ← 固定「远程」
  - `号码` ← phone、`问题描述` ← site_note、`远程码` ← remote_code
  - `远程软件` ← remote_app（向日葵/ToDesk）、`连接结果` ← result（成功/失败/取消）、`客户名称` ← customer_name
  - P1 没源的日报列（设备/型号/状态/类型/记录标签/责任人/本周关联令牌）**留空不写**。

### 3. 服务端端点 `POST /graydesk/connect-log` 已就位

`backend/server.js`：
- `handleGraydeskConnectLog`：`X-Upload-Key` 校验（独立 `GRAYDESK_UPLOAD_KEY`）→ JSON 校验 → 字段白名单 → 调 `syncGraydeskConnectLog` 追加。
- **红线硬拦截**：请求体含 `password`/`pwd` 直接 400 拒收。
- **兜底**：飞书写失败落 `graydesk_outbox.jsonl`。
- 路由分支已加：`POST /graydesk/connect-log`。

---

## 二、验证结果（全部实测）

| 项 | 结果 |
|---|---|
| `node --check server.js` / `feishu-bitable.cjs` | ✅ 语法通过 |
| 飞书建表 | ✅ 17 列全对，主字段「日期」，单选选项精确匹配 |
| `graydeskConfigured()` | ✅ true（table_id 已回填）|
| `isConfigured()`（HD日志表）| ✅ 仍 true，未破坏老链路 |
| **端到端落表**（直接调函数写真实记录）| ✅ created，record_id `reczz28XdaCrLTLF` |
| 读回字段映射 | ✅ 日期/连接时间/联系方式/号码/远程码/远程软件/连接结果/客户名称/问题描述 全对，无密码字段，P1 无源列正确留空 |
| 测试记录清理 | ✅ 已删除（不污染台账）|

> 注：本次端到端验证是**直接调 `syncGraydeskConnectLog` 函数**（绕开 HTTP 密钥，因为 `GRAYDESK_UPLOAD_KEY` 还没给我）。完整 HTTP 链路（灰Desk → POST /graydesk/connect-log → 落表）的 curl 验证，按你的要求**等 `GRAYDESK_UPLOAD_KEY` 到了再做**。

---

## 三、待你补的一个值

`GRAYDESK_UPLOAD_KEY`（独立上报密钥）——你定一把给我，我填进 `.env` 后立即用 curl `--noproxy '*'` 走完整 HTTP 链路验证：

```
curl --noproxy '*' -X POST http://127.0.0.1:3789/graydesk/connect-log \
  -H "Content-Type: application/json" -H "X-Upload-Key: <密钥>" \
  -d '{"remote_code":"913123456","remote_app":"sunlogin","result":"success","client_ts":"2026-11-09T22:16:00+08:00","phone":"138XXXXXXXX","customer_name":"测试客户"}'
```

---

## 四、老实说明（红线自检）

- 密码字段：端点硬拦截 `password`/`pwd`，`syncGraydeskConnectLog` 无任何密码字段。✅
- 号码/客户名称：空则留空待认领，绝不编。✅
- 追加不 upsert：只 POST 建行，无 search→PUT 合并。✅
- 连接结果如实记：result 直传，无美化。✅
- 日期列只日期、连接时间列精确到分。✅
- 未动小灰机运行时（8520/8521/8080），未动 HD 日志表同步。✅

**停在步1，等你给 GRAYDESK_UPLOAD_KEY。**
