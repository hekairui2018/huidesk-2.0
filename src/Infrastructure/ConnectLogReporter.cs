using System.Net.Http;
using System.Text;
using System.Text.Json;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>一次上报的落点状态：发给服务端成功 / 只留在 outbox / 没配密钥 / 已失败。</summary>
internal enum UploadState { Sending, Sent, Queued, Skipped, Failed }

/// <summary>上报状态快照（给界面显示用）；Detail 是给人看的一句话，不含号码密码。</summary>
internal readonly record struct UploadStatus(UploadState State, string Detail);

/// <summary>
/// 灰Desk 连接事件上报（公网回传 → 服务端 /graydesk/connect-log → 飞书《灰Desk 客户连接台账》）。
/// 红线：上报签名绝不携带 password；号码/客户名称认不准留空不编。
/// 断网/失败先落 outbox（本地 JSONL），有网时顺序补传；所有异常内部吞掉，绝不影响主流程。
/// </summary>
internal static class ConnectLogReporter
{
    internal const string HttpHost = "43nudj314064.vicp.fun";
    internal const string Endpoint = "http://" + HttpHost + "/graydesk/connect-log";

    /// <summary>仅诊断探针用：把上报目标指到本地假服务端，验证状态流转（生产恒为 null）。</summary>
    internal static string? EndpointOverride;

    // 独立上报密钥：优先读本地配置，缺省为空（空则跳过上报，只落 outbox，不硬编密钥进代码）。
    internal const string ConfigKey = "GrayDesk20.ConnectLog.UploadKey";

    private static readonly object Gate = new();
    private static HttpClient? _client;

    private static string OutboxPath => Path.Combine(Log.Root, "connect-log.outbox.jsonl");

    /// <summary>fire-and-forget 上报；绝不抛异常到调用方。onStatus 在后台线程回调，界面自行切线程。</summary>
    internal static void Report(RemoteApp app, string remoteCode, string result, DateTimeOffset? clientTs = null, string? phone = null, string? customerName = null, string? siteNote = null, string? weekToken = null, Action<UploadStatus>? onStatus = null)
    {
        if (string.IsNullOrWhiteSpace(remoteCode)) return;
        // 记录体在主线程先拼好：万一后台线程被回收，也有一份可落盘的完整内容。
        var rec = new Dictionary<string, object?>
        {
            ["remote_code"] = remoteCode,
            ["remote_app"] = RemoteApps.Id(app),
            ["result"] = result,
            ["client_ts"] = (clientTs ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz")
        };
        if (!string.IsNullOrWhiteSpace(phone)) rec["phone"] = phone.Trim();
        if (!string.IsNullOrWhiteSpace(customerName)) rec["customer_name"] = customerName.Trim();
        if (!string.IsNullOrWhiteSpace(siteNote)) rec["site_note"] = siteNote.Trim();
        // 本周关联令牌：只上报本地已分配的号码，绝不在这里推导/重算
        if (!string.IsNullOrWhiteSpace(weekToken)) rec["week_token"] = weekToken.Trim();
        // 明确不含 password —— 红线
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    onStatus?.Invoke(new(UploadState.Sending, "正在写入台账…"));
                    if (string.IsNullOrEmpty(ReadUploadKey()))
                    {
                        // 没配密钥：不能留白丢，落 outbox 等配好后补传
                        AppendOutbox(rec);
                        onStatus?.Invoke(new(UploadState.Skipped, "未配置上报密钥，本次记录已留本地"));
                        return;
                    }
                    // 先落 outbox 留底（防进程被杀丢数据），再顺序补传；补传后在队尾消失了才算送达。
                    var before = PendingCount();
                    AppendOutbox(rec);
                    await FlushAsync();
                    var after = PendingCount();
                    if (after < before + 1) onStatus?.Invoke(new(UploadState.Sent, "已写入台账"));
                    else onStatus?.Invoke(new(UploadState.Queued, $"网络不通，已排队待补传（本地 {after} 条）"));
                }
                catch (Exception ex)
                {
                    Log.Write("上报失败 " + ex.GetType().Name);
                    onStatus?.Invoke(new(UploadState.Failed, "上报失败，本次记录已留本地"));
                }
            });
        }
        catch { }
    }

    /// <summary>outbox 里还有几条没送出去（读文件行数，只在需要显示/补传时调用）。</summary>
    internal static int PendingCount()
    {
        try
        {
            if (!File.Exists(OutboxPath)) return 0;
            return File.ReadLines(OutboxPath).Count(line => !string.IsNullOrWhiteSpace(line));
        }
        catch { return 0; }
    }

    /// <summary>手动/启动补传：把 outbox 顺序补发一遍，结束时回报最终状态。</summary>
    internal static void FlushNow(Action<UploadStatus>? onStatus = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(ReadUploadKey()))
                {
                    onStatus?.Invoke(new(UploadState.Skipped, "未配置上报密钥，记录仍留本地"));
                    return;
                }
                var before = PendingCount();
                if (before == 0) { onStatus?.Invoke(new(UploadState.Sent, "没有待补传的记录")); return; }
                onStatus?.Invoke(new(UploadState.Sending, $"正在补传 {before} 条…"));
                await FlushAsync();
                var left = PendingCount();
                onStatus?.Invoke(left == 0
                    ? new(UploadState.Sent, "补传完成")
                    : new(UploadState.Queued, $"仍有 {left} 条待补传，网络恢复后可再试"));
            }
            catch (Exception ex)
            {
                Log.Write("补传失败 " + ex.GetType().Name);
                onStatus?.Invoke(new(UploadState.Queued, "补传未完成，记录已留本地"));
            }
        });
    }

    private static async Task FlushAsync()
    {
        lock (Gate)
        {
            // 串行补传：同一时刻只允许一个 flush。
            if (_flushing) return;
            _flushing = true;
        }
        try
        {
            while (true)
            {
                var rec = ReadFirstOutbox();
                if (rec == null) break;
                var ok = await TrySendAsync(rec);
                if (!ok) break; // 网络仍不通，保留 outbox 下次再补
                RemoveFirstOutbox();
            }
        }
        finally { _flushing = false; }
    }
    private static bool _flushing;

    private static async Task<bool> TrySendAsync(Dictionary<string, object?> rec)
    {
        var key = ReadUploadKey();
        if (string.IsNullOrEmpty(key)) return false; // 未配密钥，不发送，保留 outbox
        try
        {
            var client = GetClient();
            var body = JsonSerializer.Serialize(rec);
            using var req = new HttpRequestMessage(HttpMethod.Post, EndpointOverride ?? Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("X-Upload-Key", key);
            using var resp = await client.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Write("上报发送失败 " + ex.GetType().Name);
            return false;
        }
    }

    private static HttpClient GetClient()
    {
        if (_client != null) return _client;
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GrayDesk-ConnectLog/2.0");
        _client = c;
        return c;
    }

    private static string ReadUploadKey()
    {
        try
        {
            var file = Path.Combine(Log.Root, "connect-log.key");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        catch { }
        return "";
    }

    private static void AppendOutbox(Dictionary<string, object?> rec)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutboxPath)!);
            File.AppendAllText(OutboxPath, JsonSerializer.Serialize(rec) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { Log.Write("outbox 写入失败 " + ex.GetType().Name); }
    }

    private static Dictionary<string, object?>? ReadFirstOutbox()
    {
        try
        {
            if (!File.Exists(OutboxPath)) return null;
            var line = File.ReadLines(OutboxPath).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(line);
        }
        catch { return null; }
    }

    private static void RemoveFirstOutbox()
    {
        try
        {
            if (!File.Exists(OutboxPath)) return;
            var lines = File.ReadAllLines(OutboxPath).ToList();
            if (lines.Count > 0) lines.RemoveAt(0);
            File.WriteAllLines(OutboxPath, lines, Encoding.UTF8);
        }
        catch (Exception ex) { Log.Write("outbox 清理失败 " + ex.GetType().Name); }
    }
}
