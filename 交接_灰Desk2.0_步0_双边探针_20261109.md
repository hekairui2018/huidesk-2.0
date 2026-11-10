# 灰Desk 2.0 P1 · 步0 双边现状探针报告

> 任务：`E:\开发项目\任务_灰Desk2.0_P1_客户连接台账_20261109.md`
> 设计：`E:\开发项目\灰Desk2.0_身份台账设计_20261109.md`（已拍定：身份码=设备码种子可散开、走服务端复用 feishu-bitable、密码日抛不落）
> 铁律：一步一停一报。本报告只探现状、报方案，**未写任何代码、未动任何文件**。
> 日期：2026-11-09

---

## 一、灰Desk 连接后数据接入点（`E:\开发项目\RemoteConnectHelper\RemoteConnectHelper.V2`，.NET10/WPF/x64）

### 1. 连接走完后手上已有哪些数据

| 数据 | 位置 | 现状 |
|---|---|---|
| 软件类型 | `RecognitionResult.App`（`RemoteApp` 枚举：Sunlogin/ToDesk/AnyDesk/UU/RustDesk/TeamViewer/UltraViewer） | ✅ 已有，可直接映射「远程软件」单选 |
| 设备码 | `RecognitionResult.DeviceCode`（= `SelectedCode.Value`） | ✅ 已有，即身份码种子 |
| 密码 | `RecognitionResult.Password`（= `SelectedPassword.Value`） | ⚠️ 红线：只用于当次填码，**绝不落台账/飞书** |
| 连接终态 | `AssistantController.Stage` + `ConnectResult.Status` | ✅ 已有，`ConnectCoreAsync` 里 `result.Status` 即终态 |
| 连接上下文 | `ConnectionContext`（`Legacy/ConnectionContext.cs`） | 只承载「取消/是否已点连接」，连完即忘（设计 §1 已确认） |

关键代码位置：
- `Domain/Models.cs`：`RemoteApp` 枚举、`RecognitionResult`（`App`/`DeviceCode`/`Password`/`AppName`）、`ConnectionPolicy.ValidCode`。
- `Application/AssistantController.cs`：`ConnectCoreAsync`（128-149 行）是连接终态唯一收口点。
- 注意：`App.xaml.cs` 不存在，入口是 `App.cs`（`Main` → `OnStartup`），简报里的 "RunPipeline" 实为 `AssistantController` 的 `RecognizeAsync`/`ConnectCoreAsync` 链路。

### 2. 最合适的"连接事件上报"挂点

`Application/AssistantController.cs` 的 `ConnectCoreAsync` 是唯一收口：`_connect(app, code, password, token)` 返回后，`result.Status` 即终态，`app/code` 在参数、`Result` 含完整识别信息。

- **建议挂点**：`ConnectCoreAsync` 里 `Publish(...)` 之后 fire-and-forget 调 `ConnectLogReporter.ReportAsync(app, code, 终态)`，成功/失败/取消三种终态全覆盖。
- **红线**：上报函数签名不得包含 `password`。

### 3. 缺失部件（步2 要新建）

- 灰Desk 侧**无** HTTP 上报客户端。现有 `Infrastructure/OnlineUpdater.cs` 是下载用（含 `HttpClient` 封装 + `43nudj314064.vicp.fun` 公网主机），可参考写法、不可复用逻辑。
- 灰Desk 侧**无** outbox 本地补传队列（断网落本地、有网补传），需新建。

---

## 二、服务端加端点的位置（`E:\开发项目\xiaohuiji\backend`）

### 1. HTTP 适配层位置

- 入口：`backend/server.js`，原生 `http.createServer`（1156 行起），路由是一串 `if (req.method === ... && req.url === ...)` 判断，**无框架**。
- 现成模板：`POST /api/huidu/log`（`handleHuiduLog`，152-266 行）= 公网回传 → 密钥校验 → 字段校验 → 落盘 → 飞书 upsert 的完整范式。
- 新端点接入：在 `createServer` 回调加一个 `if (req.method === "POST" && req.url === "/graydesk/connect-log")` 分支即可，零框架成本。

### 2. feishu-bitable upsert 如何复用到新表

- `feishu-bitable.cjs` 的 `CFG.tableId` **单表写死**（读 `FEISHU_TABLE_ID`，69-74 行）。
- `syncHuiduRecord` 是专用函数：字段名硬编码《HD升级助手运行日志》那套列（发送型号/FPGA版本/…），主键按「设备码@日期」。
- **结论**：不能直接调 `syncHuiduRecord` 写新表（字段和主键都对不上）。
- **方案**：在 `feishu-bitable.cjs` 新增一个平行的通用函数 `syncGraydeskConnectLog(rec)`，复用底层 `api()` / `getTenantToken()` / `findRecordId()` / `ensureSchema` 字段自愈能力，但：
  - 独立 tableId（`.env` 新增 `FEISHU_GRAYDESK_TABLE_ID`，指向新表《灰Desk 客户连接台账》）；
  - 主键按**身份码**（= 规整化设备码）upsert；
  - 字段按设计 §3，**无任何密码字段**。

### 3. .env 飞书配置 & 公网映射

- `.env` 现有：`FEISHU_APP_ID` / `FEISHU_APP_SECRET` / `FEISHU_APP_TOKEN`（同一个多维表 app）/ `FEISHU_TABLE_ID`（《HD升级助手运行日志》表）。
- 新表同 app（`FEISHU_APP_TOKEN` 不变），只需新增 `FEISHU_GRAYDESK_TABLE_ID`。
- 端口：`server.js` 代码默认 `PORT = 3788`（`process.env.PORT` 优先，`.env` 写 `PORT=3789`），监听 `0.0.0.0`。
- 公网映射：花生壳穿透，主机 `43nudj314064.vicp.fun`（灰Desk 更新接口已在此跑通，见 `OnlineUpdater.cs` / README）。灰Desk 上报 POST 目标 = `http://43nudj314064.vicp.fun/graydesk/connect-log`。

---

## 三、飞书新表建法（《灰Desk 客户连接台账》）

同一飞书 app（`FEISHU_APP_TOKEN` 不变），新建一个 table，字段严格照设计 §3：

| 列 | 类型 | 说明 |
|---|---|---|
| 身份码（主键） | 文本 | 设备码规整化，upsert 键 |
| 客户名称/备注 | 文本 | 留空不猜 |
| 远程软件 | 单选 | 向日葵/ToDesk |
| 本次远程码 | 文本 | 设备码（**无密码**） |
| 连接时间 | 日期 | 自动 |
| 连接结果 | 单选 | 成功/失败/取消 |
| 关联发送型号 | 文本 | 交叉引用，可空 |
| 连接次数 | 数字 | upsert 累加 |
| 备注 | 文本 | 客服手记 |

**表建法两种**：① 飞书手动建表，把 table_id 手填进 `.env`（P1 最快最稳）；② 用 `ensureSchema` 思路自动建字段。**建议 P1 用 ①**。

---

## 四、上报字段草案（`POST /graydesk/connect-log`）

```jsonc
{
  "identity_code": "913123456",   // 身份码 = 规整化设备码（主键）
  "remote_code":   "913123456",   // 本次远程码（设备码，P1 复用身份码）
  "remote_app":    "sunlogin",    // sunlogin | todesk（RemoteApp.Id）
  "result":        "success",     // success | failed | cancelled
  "client_ts":     "2026-11-09T22:16:00+08:00"
  // 明确不含 password 字段
}
```

- 密钥：建议复用 `X-Upload-Key`（`HUIDU_UPLOAD_KEY`），不另起密钥体系（符合"单服务端一处"约束）。

---

## 五、待拍板项（我停在这）

1. **身份码口径**：设备码规整化直接当身份码，一客户多机/换机会散成多条，P1 接受散开、不跨码并客户（设计 §4 已定，我按此走）。
2. **新表建法**：P1 用「飞书手动建表 + 手填 `FEISHU_GRAYDESK_TABLE_ID`」还是「自动建表 + 字段自愈」？
3. **上报密钥**：`/graydesk/connect-log` 复用现有 `HUIDU_UPLOAD_KEY`，还是单独配 `GRAYDESK_UPLOAD_KEY`？
4. **挂点确认**：灰Desk 上报挂 `ConnectCoreAsync` 终态收口点（成功/失败/取消全覆盖），是否认可？
