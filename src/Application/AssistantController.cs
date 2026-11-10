using System.Security.Cryptography;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.Parsing;

namespace RemoteConnectHelper.V2.Application;
public enum AssistantStage { Ready, Recognizing, Recognized, Connecting, Launched, NeedInput, Cancelled, Failed, Confirmed, Uncertain }

/// <summary>本次连接的上报落点：界面上那个状态点就是它。</summary>
public enum ConnectReportState { None, Sending, Sent, Queued, Skipped, Failed }

/// <summary>One customer, explicit retries, generation-checked callbacks. No window operations.</summary>
public sealed class AssistantController : IAsyncDisposable
{
    private readonly OperationHost _operations = new();
    private readonly RecognitionPipeline? _pipeline;
    private readonly Func<byte[], CancellationToken, Action<string>?, Task<RecognitionResult>> _analyze;
    private readonly Func<RemoteApp, string, string, CancellationToken, Task<ConnectResult>> _connect;
    private long _revision;
    private string? _imageKey;
    private bool _lastReadOnly;
    private readonly HashSet<string> _attemptedImages = [];
    private readonly Queue<string> _attemptOrder = new();
    public event Action? Changed;
    public AssistantStage Stage { get; private set; } = AssistantStage.Ready;
    public string Status { get; private set; } = "截图后按 Ctrl+Alt+R，或从托盘读取";
    /// <summary>本次连接分配到的 4 位本周令牌（界面显示、人工念；不参与任何推导）。</summary>
    public string? LastWeekToken { get; private set; }
    /// <summary>本次连接台账上报的落点状态（界面状态点）。</summary>
    public ConnectReportState ReportState { get; private set; } = ConnectReportState.None;
    /// <summary>上报状态的一句话说明（不含号码、绝不含密码）。</summary>
    public string ReportDetail { get; private set; } = "";
    /// <summary>outbox 里尚未送达的记录条数（构建/上报/补传时更新，不随重绘反复读盘）。</summary>
    public int PendingReports { get; private set; }
    /// <summary>客户锚（号码）与客户名称；认不准留空待认领，绝不猜。</summary>
    public CustomerInfo Customer { get; private set; } = CustomerInfo.Empty;
    /// <summary>名片识别的依据（读到了什么、为什么采纳）。</summary>
    public IReadOnlyList<string> CustomerEvidence { get; private set; } = [];
    /// <summary>名片识别的存疑项（多个候选分不清、疑似远程码被排除等），界面如实提示。</summary>
    public IReadOnlyList<string> CustomerIssues { get; private set; } = [];
    private bool _customerManual;
    public RecognitionResult? Result { get; private set; }
    public RecognitionResult? PreviewResult {get;private set;}
    public Task<RecognitionResult> AnalyzePreviewAsync(byte[] image,CancellationToken token)=>_analyze(image,token,null);
    public void ApplyPreviewResult(byte[] image,RecognitionResult result,long revision) {
        if(revision!=_revision||!ReferenceEquals(Image,image)||Busy)return;
        PreviewResult=result;Changed?.Invoke();
    }
    public string? PreviewText {get;private set;}
    public byte[]? Image { get; private set; }
    public int PreviewRotation {get;private set;}
    private readonly Func<byte[],CancellationToken,Task<int>> _detectOrientation;
    public Task<int> DetectOrientationAsync(byte[] image,CancellationToken token)=>_detectOrientation(image,token);
    public void ApplyPreviewOrientation(byte[] image,int rotation,long revision) {
        if(revision!=_revision||!ReferenceEquals(Image,image)||Busy)return;
        PreviewRotation=rotation;Changed?.Invoke();
    }
    public long SessionId { get; private set; }
    public long Revision => _revision;
    public bool Busy => Stage is AssistantStage.Recognizing or AssistantStage.Connecting;
    public Func<RemoteApp,bool> SoftwareAllowed {get;set;}=app=>RemoteApps.Supported.Contains(app);
    public bool AutoConnect { get; set; } = true;
    public bool HasAttemptedConnection {get;private set;}
    public int DuplicateTriggers { get; private set; }
    /// <summary>步E：向日葵接力的当前一帧（第几步、该按什么、剪贴板里是什么）。不接力时是 Idle。</summary>
    public RelaySnapshot Relay => _relaySeeded ?? SunloginRelay.Current;
    private RelaySnapshot? _relaySeeded;
    /// <summary>「重新装一次」：接力中剪贴板被别的复制顶掉时，把该装的内容重装回去。</summary>
    public bool ReinstallRelay() => SunloginRelay.Reinstall();
    /// <summary>仅离屏渲染用：把卡片摆成「正在等按键」的样子，不碰剪贴板、不装热键。</summary>
    internal void SeedRelay(RelaySnapshot snapshot) { _relaySeeded = snapshot; Changed?.Invoke(); }
    public AssistantController(
        Func<byte[], CancellationToken, Action<string>?, Task<RecognitionResult>>? analyze = null,
        Func<RemoteApp, string, string, CancellationToken, Task<ConnectResult>>? connect = null,
        Func<byte[],CancellationToken,Task<int>>? detectOrientation = null)
    {
        if (analyze == null) { _pipeline = new(); _analyze = (image,token,progress)=>_pipeline.AnalyzeAsync(image,token,progress); }
        else _analyze = analyze;
        _detectOrientation=detectOrientation??((image,token)=>_pipeline?.DetectOrientationAsync(image,token)??Task.FromResult(0));
        _connect = connect ?? new ClientConnector().ConnectAsync;
        PendingReports = ConnectLogReporter.PendingCount();
        // 步E：接力状态从后台线程来（键盘钩子/OCR），统一转成一次界面刷新
        SunloginRelay.Changed += OnRelayChanged;
        if (PendingReports > 0)
        {
            // 上次运行没送出去的记录：启动时静默补传一次（失败就留在 outbox，不弹窗打扰）
            ReportState = ConnectReportState.Queued;
            ReportDetail = $"上次运行有 {PendingReports} 条记录未送达";
            ConnectLogReporter.FlushNow(ApplyUploadStatus);
        }
    }
    public Task<RecognitionResult> PreviewRemoteAsync(byte[] image,CancellationToken token)
        => _pipeline!=null ? _pipeline.AnalyzeAsync(image,token,null,true) : _analyze(image,token,null);

    /// <summary>显式读名片：复用认码那条 OCR 管线（同一个 OCR 进程），只做客户信息解析。</summary>
    public async Task<CustomerInfo> RecognizeCustomerAsync(byte[] image,CancellationToken token)
    {
        if (_pipeline == null) { Customer = CustomerInfo.Empty; CustomerEvidence = []; CustomerIssues = ["当前环境不支持名片识别"]; Changed?.Invoke(); return Customer; }
        var document = await _pipeline.ReadDocumentAsync(image, token, PreviewRotation);
        var parsed = CustomerInfoRecognizer.Parse(document);
        Customer = parsed.Info; CustomerEvidence = parsed.Evidence; CustomerIssues = parsed.Issues;
        _customerManual = false;
        Log.Write($"名片识别 phone={(parsed.Info.HasPhone ? "有" : "无")} name={(parsed.Info.HasName ? "有" : "无")}");
        Changed?.Invoke();
        return Customer;
    }

    /// <summary>界面手填/修正客户信息。手填过就不再被自动识别覆盖。</summary>
    public void SetCustomer(string? phone,string? name)
    {
        Customer = new CustomerInfo((phone ?? "").Trim(), (name ?? "").Trim());
        _customerManual = true;
        CustomerEvidence = ["手动填写"];
        CustomerIssues = [];
        Changed?.Invoke();
    }

    public void ClearCustomer()
    {
        Customer = CustomerInfo.Empty; CustomerEvidence = []; CustomerIssues = []; _customerManual = false;
        Changed?.Invoke();
    }

    /// <summary>点状态点补传本地积压记录（界面上只在待补传/失败时给这个动作）。</summary>
    public void RetryReport()
    {
        if (ReportState == ConnectReportState.Sending) return;
        ReportState = ConnectReportState.Sending; ReportDetail = "正在补传…"; Changed?.Invoke();
        ConnectLogReporter.FlushNow(ApplyUploadStatus);
    }

    /// <summary>仅离屏渲染/诊断用：把卡片摆成「刚连完一次」的样子，不碰网络、不开窗口。</summary>
    internal void SeedConnectOutcome(string weekToken, ConnectReportState state, string detail, string phone = "", string name = "", bool manualCustomer = false, int pending = 0, string issue = "")
    {
        LastWeekToken = weekToken; ReportState = state; ReportDetail = detail; PendingReports = pending;
        if (phone.Length > 0 || name.Length > 0)
        {
            Customer = new CustomerInfo(phone, name);
            CustomerEvidence = [manualCustomer ? "手动填写" : "手机号（带标签）：" + phone];
            CustomerIssues = [];
        }
        if (issue.Length > 0) CustomerIssues = [issue];
        Changed?.Invoke();
    }

    private void ApplyUploadStatus(UploadStatus status)
    {
        ReportState = status.State switch
        {
            UploadState.Sending => ConnectReportState.Sending,
            UploadState.Sent => ConnectReportState.Sent,
            UploadState.Queued => ConnectReportState.Queued,
            UploadState.Skipped => ConnectReportState.Skipped,
            _ => ConnectReportState.Failed
        };
        ReportDetail = status.Detail;
        if (status.State != UploadState.Sending) PendingReports = ConnectLogReporter.PendingCount();
        Changed?.Invoke();
    }

    /// <summary>新会话：清掉上一次的令牌与上报状态，但本地积压条数照实留着。</summary>
    private void ResetReportState()
    {
        if (PendingReports > 0) { ReportState = ConnectReportState.Queued; ReportDetail = $"本地还有 {PendingReports} 条待补传"; }
        else { ReportState = ConnectReportState.None; ReportDetail = ""; }
    }

    public Task RecognizeAsync(byte[] image, RecognitionResult? prepared = null, bool? allowAutoConnect = null, bool refill = false, bool explicitRetry=false)
    {
        string key = Convert.ToHexString(SHA256.HashData(image));
        if(explicitRetry){if(Busy)return Task.CompletedTask;_attemptedImages.Remove(key);_imageKey=null;}
        if(key==_imageKey&&_lastReadOnly&&allowAutoConnect!=false){prepared??=Result;}
        else if (!refill && key == _imageKey && Stage != AssistantStage.Ready) { DuplicateTriggers++; return Task.CompletedTask; }
        _lastReadOnly=allowAutoConnect==false;
        _imageKey = key; SessionId++; HasAttemptedConnection=false; LastWeekToken=null; ResetReportState();
        // 客户信息跟随本次会话：新截图即清空，避免上一位客户的号码串到这一行（宁可重读，不可串号）
        Customer = CustomerInfo.Empty; CustomerEvidence = []; CustomerIssues = []; _customerManual = false;
        long revision = Interlocked.Increment(ref _revision);
        Result = null; PreviewResult=null;PreviewText=null; Image = null;
        Publish(revision, AssistantStage.Recognizing, "正在读取新截图…");
        return ExecuteAsync(revision, async token =>
        {
            using var data = SkiaSharp.SKData.CreateCopy(image);
            using var codec = SkiaSharp.SKCodec.Create(data);
            if (codec == null) throw new InvalidDataException("图片无法打开，可以重新截图或手动填写");
            if ((long)codec.Info.Width * codec.Info.Height > 50_000_000) throw new InvalidDataException("图片过大，请截取远程信息区域");
            token.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            Image = image;
            try{PreviewRotation=await DetectOrientationAsync(image,token);}catch(OperationCanceledException){throw;}catch{PreviewRotation=0;}
            token.ThrowIfCancellationRequested();if(revision!=_revision)return;
            Publish(revision, AssistantStage.Recognizing, "正在识别远程信息…");
            var result = prepared ?? await _analyze(image, token, text => Publish(revision, AssistantStage.Recognizing, text));
            token.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            Result = result;
            Log.Write($"识别完成 session={SessionId} app={result.App} decision={result.Decision}");
            // 认码没拿到设备码 → 这张图可能是微信名片/聊天截图，补跑一次名片识别（复用同一 OCR 文档）。
            if (result.DeviceCode.Length == 0 && !_customerManual && _pipeline != null)
            {
                try
                {
                    Publish(revision, AssistantStage.Recognizing, "正在读取客户信息…");
                    var document = await _pipeline.ReadDocumentAsync(image, token, PreviewRotation);
                    token.ThrowIfCancellationRequested();
                    if (revision != _revision) return;
                    var parsed = CustomerInfoRecognizer.Parse(document);
                    Customer = parsed.Info; CustomerEvidence = parsed.Evidence; CustomerIssues = parsed.Issues;
                    Log.Write($"名片识别 session={SessionId} phone={(parsed.Info.HasPhone ? "有" : "无")} name={(parsed.Info.HasName ? "有" : "无")}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Write("名片识别失败 " + ex.GetType().Name); }
            }
            if ((allowAutoConnect??AutoConnect) && result.Decision == ConnectionDecision.Automatic && !_attemptedImages.Contains(key))
                await ConnectCoreAsync(revision, result.App, result.DeviceCode, result.Password, token);
            else Publish(revision, AssistantStage.NeedInput,
                allowAutoConnect!=false&&_attemptedImages.Contains(key) ? "这张截图已经发起过连接，需要时请明确点击重试"
                : result.Decision == ConnectionDecision.Automatic ? "识别完成，可以核对后连接" : result.Reason);
        });
    }
    public Task AcceptTextAsync(string text, RecognitionResult result, bool? allowAutoConnect=null,byte[]? previewImage=null,bool refill=false,bool explicitRetry=false)
    {
        string key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        if(explicitRetry){if(Busy)return Task.CompletedTask;_attemptedImages.Remove(key);_imageKey=null;}
        if(!refill&&key==_imageKey && Stage!=AssistantStage.Ready&&!(_lastReadOnly&&allowAutoConnect!=false)){DuplicateTriggers++;return Task.CompletedTask;}
        _lastReadOnly=allowAutoConnect==false;
        _imageKey=key;SessionId++;HasAttemptedConnection=false;LastWeekToken=null;ResetReportState();long revision=Interlocked.Increment(ref _revision);
        Result=null;PreviewResult=null;Image=previewImage;PreviewText=text;PreviewRotation=0;Publish(revision,AssistantStage.Recognizing,"正在读取远程文本…");
        return ExecuteAsync(revision,async token=>{
            token.ThrowIfCancellationRequested();if(revision!=_revision)return;Result=result;
            if((allowAutoConnect??AutoConnect) && result.Decision==ConnectionDecision.Automatic && !_attemptedImages.Contains(key))
                await ConnectCoreAsync(revision,result.App,result.DeviceCode,result.Password,token);
            else Publish(revision,AssistantStage.NeedInput,allowAutoConnect!=false&&_attemptedImages.Contains(key)?"此内容已尝试连接，请核对后明确重试":result.Reason);
        });
    }
    public Task ConnectAsync(RemoteApp app, string code, string password)
    {
        if (Busy) return Task.CompletedTask;
        long revision = Interlocked.Increment(ref _revision);
        return ExecuteAsync(revision, token => ConnectCoreAsync(revision, app, code, password, token));
    }
    public Task ConnectAdaptiveAsync(Discovery.SoftwareEntry entry,string code,string password)
    {
        if(Busy)return Task.CompletedTask;
        long revision=Interlocked.Increment(ref _revision);
        return ExecuteAsync(revision,async token=>{
            if(entry.Path==null){Publish(revision,AssistantStage.NeedInput,"请先给所选软件设置启动路径");return;}
            HasAttemptedConnection=true;Publish(revision,AssistantStage.Connecting,"正在自适应定位"+entry.Name+"的连接控件…");
            var result=await AdaptiveClientConnector.ConnectAsync(entry.Path,code.Trim().Replace(" ",""),password,token);
            token.ThrowIfCancellationRequested();
            Publish(revision,result.Status==ConnectStatus.Failed?AssistantStage.Failed:result.Status==ConnectStatus.Cancelled?AssistantStage.Cancelled:AssistantStage.NeedInput,result.Message);
        });
    }
    private async Task ConnectCoreAsync(long revision, RemoteApp app, string code, string password, CancellationToken token)
    {
        if(!SoftwareAllowed(app)){Publish(revision,AssistantStage.NeedInput,"请先在远程软件列表中添加对应软件");return;}
        code = code.Trim().Replace(" ", "");
        if (!ConnectionPolicy.ValidCode(app, code)) { Publish(revision, AssistantStage.NeedInput, "请选择软件并填写完整的数字设备码"); return; }
        token.ThrowIfCancellationRequested();
        if (revision != _revision) return;
        if (_imageKey != null && _attemptedImages.Add(_imageKey))
        {
            _attemptOrder.Enqueue(_imageKey);
            while (_attemptOrder.Count > 64) _attemptedImages.Remove(_attemptOrder.Dequeue());
        }
        HasAttemptedConnection=true;
        Publish(revision, AssistantStage.Connecting, ConnectingText(app));
        var result = await _connect(app, code, password, token);
        token.ThrowIfCancellationRequested();
        var stage = result.Status switch {
            ConnectStatus.Confirmed => AssistantStage.Confirmed,
            ConnectStatus.Uncertain or ConnectStatus.Launched => AssistantStage.Uncertain, ConnectStatus.NeedPassword => AssistantStage.NeedInput,
            ConnectStatus.Cancelled => AssistantStage.Cancelled, _ => AssistantStage.Failed
        };
        // 本周关联令牌：走到终态这一帧才出号——新的连接方式下「终态」＝观察型OCR确认已发起/会话出现，
        // 或明确的失败/取消；令牌与上报行一一对应（一行一个号），所以取消的那一行也有号。
        var weekToken = WeekToken.Next();
        LastWeekToken = weekToken;
        // 先挂「上报中」，让终态那一帧就带出状态点，而不是连完了还显示空白
        ReportState = ConnectReportState.Sending; ReportDetail = "正在写入台账…";
        Publish(revision, stage, result.Message);
        // 连接终态上报（fire-and-forget，绝不阻塞主流程；签名不带 password）
        ReportConnectOutcome(app, code, result.Status, weekToken);
    }

    private static string ConnectingText(RemoteApp app) => ClientConnector.LegacyClickEnabled
        ? "正在打开" + RemoteApps.Name(app) + "并连接…"
        : app switch
        {
            RemoteApp.Sunlogin => "正在唤起向日葵，等你在识别码框上按 Alt+V",
            RemoteApp.ToDesk => "正在用命令行发起 ToDesk 连接…",
            _ => "正在打开" + RemoteApps.Name(app) + "并连接…"
        };

    private void ReportConnectOutcome(RemoteApp app, string code, ConnectStatus status, string weekToken)
    {
        var result = status switch
        {
            ConnectStatus.Confirmed => "success",
            ConnectStatus.Uncertain or ConnectStatus.Launched => "initiated",
            ConnectStatus.Cancelled => "cancelled",
            _ => "failed" // NeedPassword / Failed 等一律 failed
        };
        // 号码/客户名称：只在读到时传（认不准留空待认领，绝不编）；password 永不进上报（红线）
        ConnectLogReporter.Report(app, code, result,
            phone: Customer.HasPhone ? Customer.Phone : null,
            customerName: Customer.HasName ? Customer.Name : null,
            weekToken: weekToken,
            onStatus: ApplyUploadStatus);
    }
    private async Task ExecuteAsync(long revision, Func<CancellationToken, Task> action)
    {
        try { await _operations.RunAsync(async token => { if (revision == _revision) await action(token); }); }
        catch (OperationCanceledException) { Publish(revision, AssistantStage.Cancelled, "已停止，可以修改后重试"); }
        catch (Exception ex) { Log.Write("操作失败 " + ex.GetType().Name); Publish(revision, AssistantStage.Failed, "处理未完成：" + ex.Message); }
    }
    public async Task StopAsync()
    {
        long revision = Interlocked.Increment(ref _revision);
        SunloginRelay.Abort();   // 接力是等人驱动的，先叫停，键盘钩子立刻下膛
        await _operations.StopAsync();
        Publish(revision, AssistantStage.Cancelled, "已停止，可以修改后重试");
    }
    public async Task PreviewClipboardAsync(byte[]? image,string? text=null)
    {
        if(Busy)return;
        if(image!=null){using var data=SkiaSharp.SKData.CreateCopy(image);using var codec=SkiaSharp.SKCodec.Create(data);if(codec==null||(long)codec.Info.Width*codec.Info.Height>50_000_000)return;}
        long revision=Interlocked.Increment(ref _revision);await _operations.StopAsync();if(revision!=_revision)return;
        SessionId++;_imageKey=null;_lastReadOnly=false;Result=null;PreviewResult=null;Image=image;PreviewText=text;PreviewRotation=0;HasAttemptedConnection=false;LastWeekToken=null;ResetReportState();
        Publish(revision,AssistantStage.Ready,"已缓存新内容，点击识别或悬浮球");
    }
    public void ClearPreview(){
        if(Busy)return;Interlocked.Increment(ref _revision);Image=null;PreviewText=null;PreviewResult=null;Changed?.Invoke();
    }
    public async Task NewManualAsync()
    {
        long revision = Interlocked.Increment(ref _revision);
        await _operations.StopAsync();
        if (revision != _revision) return;
        SessionId++; _imageKey = null; Result = null; PreviewResult=null;PreviewText=null; Image = null; HasAttemptedConnection=false; LastWeekToken=null; ResetReportState();
        Publish(revision, AssistantStage.NeedInput, "填写设备码和验证码；没有验证码可留空");
    }
    public void ForgetFinished(long session)
    {
        if (SessionId != session || Stage != AssistantStage.Confirmed) return;
        Result = null; PreviewResult=null;PreviewText=null; Image = null;
        Publish(_revision, AssistantStage.Confirmed, "已确认连接；界面的截图与密码已清空");
    }
    public bool ConfirmObservedSession(long session,long revision,RemoteApp app)
    {
        if(SessionId!=session||_revision!=revision||Busy||Stage==AssistantStage.Confirmed||!SoftwareAllowed(app))return false;
        Publish(_revision,AssistantStage.Confirmed,"已确认远程软件连接成功");return true;
    }
    internal bool ApplyObservedConnectionState(long session,long revision,RemoteApp app,RemoteConnectHelper.V2.Connections.SessionSample sample)
    {
        if(SessionId!=session||_revision!=revision||Busy||Stage==AssistantStage.Confirmed||!HasAttemptedConnection||Result?.App!=app||!sample.TargetMatches)return false;
        var stage=sample.Failure?AssistantStage.Failed:sample.Verification?AssistantStage.NeedInput:(AssistantStage?)null;
        if(stage==null)return false;
        if(Stage!=stage.Value||Status!=sample.Reason)Publish(revision,stage.Value,sample.Reason);
        return true;
    }
    private void OnRelayChanged() => Changed?.Invoke();
    public void ShowMessage(string text) => Publish(_revision, Busy ? Stage : AssistantStage.NeedInput, text);
    private void Publish(long revision, AssistantStage stage, string text)
    { if (revision != _revision) return; if(stage==AssistantStage.Confirmed&&Stage!=stage)Log.Write($"连接成功 session={SessionId}"); Stage = stage; Status = text; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() { Interlocked.Increment(ref _revision); SunloginRelay.Changed -= OnRelayChanged; SunloginRelay.Abort(); await _operations.StopAsync(); _pipeline?.Dispose(); }
}
