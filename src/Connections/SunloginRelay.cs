using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Connections;

/// <summary>向日葵接力的第几步（界面按这个显示「第 X 步 / 共 Y 步」）。</summary>
public enum RelayStep { Idle, LoadedCode, Observing, LoadedPassword, Done, Failed, TimedOut, Cancelled }

/// <summary>界面要显示的一帧接力状态。Hint 是给人看的一句话；Clipboard 说明剪贴板里现在装的是什么。</summary>
public sealed record RelaySnapshot(bool Active, RelayStep Step, int StepIndex, int StepTotal,
    string Hint, string Clipboard, bool CanReinstall, string Evidence)
{
    public static RelaySnapshot Idle { get; } =
        new(false, RelayStep.Idle, 0, 0, "", "", false, "");
}

/// <summary>
/// 步E：向日葵「剪贴板接力」。
/// 助手全程只做四件事：把设备码放进剪贴板、感知 Alt+V 后代按 Ctrl+V、代发回车、观察界面判「已发起」。
/// 不找框、不点按、不 OCR 找坐标——这正是要淘汰一代那套的原因。
/// 流程（决策记录 2026-11-10 定稿）：
/// ① 人点识别码框 → 按 Alt+V（粘设备码，剪贴板立刻换成验证码）→ ② 助手代发回车
/// ③ 观察界面：进密码框/等待界面才算「已发起」→ ④ 有验证码就等人再按一次 Alt+V。
/// </summary>
public static class SunloginRelay
{
    private static readonly string[] Procs = { "AweSun", "SunloginClient" };
    private static readonly string[] Cls = { "FLUTTER_RUNNER_WIN32_WINDOW", "SunloginClient" };
    private static readonly string[] ClsExclude = { "FlutterMultiWindow" };
    private static readonly string[] Others = { "ToDesk" };

    private static RelaySession? _running;
    private static RelaySnapshot _current = RelaySnapshot.Idle;

    /// <summary>卡片读它来画接力区；不接力时是 Idle。</summary>
    public static RelaySnapshot Current => _current;

    /// <summary>接力状态变了（可能来自键盘钩子线程，订阅方自己切 UI 线程）。</summary>
    public static event Action? Changed;

    internal static void Publish(RelaySnapshot snapshot)
    {
        _current = snapshot;
        try { Changed?.Invoke(); } catch { /* 界面异常不能打断接力 */ }
    }

    /// <summary>「重新装一次」：两次粘之间用户复制了别的东西，把当前该装的内容重新装回剪贴板。</summary>
    public static bool Reinstall() => _running?.Reinstall() ?? false;

    /// <summary>中止接力（取消/退出用），剪贴板由外层 Release 还原。</summary>
    public static void Abort() => _running?.Abort();

    public static async Task<ConnectResult> ConnectAsync(string deviceCode, string password, string? exePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var exe = exePath ?? AppPaths.FindSunlogin();
        var win = ScreenOps.Ensure(exe == null ? Array.Empty<string>() : new[] { exe }, Procs, Cls, ClsExclude, Others, null);
        if (win == null)
            return ConnectResult.Fail("没找到向日葵主窗口，请先在客户端登录后重试");
        if (!ScreenOps.EnsureForeground(win))
            return ConnectResult.Fail("没法把向日葵调到最前（可能被弹窗挡住）；为避免连错已停下，请切到向日葵再试");

        using var clipboard = new ClipboardRelay();
        using var hotkey = new AltVHotkey();
        using var presses = new SemaphoreSlim(0);
        hotkey.Pressed += () => { try { presses.Release(); } catch { } };
        if (!hotkey.InstallOnDispatcher())
        {
            Log.Write("接力热键没装上：" + hotkey.Detail);
            return ConnectResult.Fail("Alt+V 热键装不上（" + hotkey.Detail + "），接力没法开始");
        }
        hotkey.Armed = true;

        using var session = new RelaySession(
            deviceCode, password,
            hold: clipboard.Hold,
            waitPress: ms => presses.Wait(ms),
            observe: () => ObserveScreen(deviceCode),
            paste: PasteIfSunloginInFront,
            enter: InputSimulator.SendEnter,
            publish: Publish,
            waitSeconds: Math.Max(30, ConnectOptions.RelayWaitSeconds),
            progressSeconds: 10,
            clock: () => Environment.TickCount64);

        _running = session;
        try
        {
            Log.Write($"接力开始 app=Sunlogin code={Mask(deviceCode)} 有验证码={(string.IsNullOrEmpty(password) ? "否" : "是")} 等人上限={ConnectOptions.RelayWaitSeconds}s");
            var result = await Task.Run(() => session.Run(token), CancellationToken.None).ConfigureAwait(false);
            Log.Write($"接力结束 status={result.Status} msg={result.Message}");
            return result;
        }
        finally
        {
            hotkey.Armed = false;
            _running = null;
            _current = RelaySnapshot.Idle;
        }
    }

    private static string Mask(string code) => code.Length > 4 ? "•••" + code[^4..] : "•••";

    /// <summary>
    /// 判界面走到哪一步。两道证据源：
    /// ① 会话窗（UIA 读标题/按钮，不依赖屏幕可见）——连上/要密码/报错最先在这里出现；
    /// ② 主窗界面文字（观察型 OCR）——判断是不是还停在输入设备码这一步。
    /// 抓不到画面（用户切走了、被我们自己的卡片挡住）就当「看不出」，绝不因此判失败。
    /// </summary>
    internal static (SunloginScreenState State, string Reason) ObserveScreen(string deviceCode)
    {
        var now = Environment.TickCount64;
        if (now - _lastSessionScan > 1200)
        {
            _lastSessionScan = now;
            try
            {
                foreach (var window in SessionObserver.Windows(RemoteApp.Sunlogin).Take(6))
                {
                    var sample = SessionObserver.Read(window, true, deviceCode);
                    if (sample.Failure && sample.TargetMatches)
                        return (SunloginScreenState.Failure, sample.Reason);
                    if (sample.Verification) return (SunloginScreenState.PasswordPrompt, sample.Reason);
                    if (sample.ExplicitConnected && sample.SessionControl && sample.TargetMatches)
                        return (SunloginScreenState.Connected, sample.Reason);
                }
            }
            catch { /* UIA 拿不到就退回看画面 */ }
        }

        var win = ScreenOps.FindWindow(Procs, Cls, ClsExclude);
        if (win == null) return (SunloginScreenState.Unknown, "没找到向日葵窗口");
        try
        {
            using var bitmap = ScreenOps.CaptureClient(win);
            var document = ScreenOps.OcrAsync(bitmap).GetAwaiter().GetResult();
            return SunloginScreen.Classify(document);
        }
        catch (Exception ex) { return (SunloginScreenState.Unknown, "画面没抓到 " + ex.GetType().Name); }
    }
    private static long _lastSessionScan;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>
    /// 粘之前先确认前台就是向日葵。
    /// 这一条是安全阀：Ctrl+V 是全局粘贴，万一用户在聊天窗口里按了 Alt+V，绝不能把设备码/验证码糊到别人的对话框里。
    /// </summary>
    internal static bool PasteIfSunloginInFront()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!Procs.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        InputSimulator.SendPaste();
        return true;
    }
}

/// <summary>步E 的运行时开关（由 Preferences 灌进来）。老点按那一个开关在 <see cref="ClientConnector.LegacyClickEnabled"/>。</summary>
public static class ConnectOptions
{
    /// <summary>向日葵等人上限（秒）。超时不自动关窗，只提示可重来。</summary>
    public static int RelayWaitSeconds { get; set; } = 180;
}

/// <summary>
/// 接力状态机本体。所有外部动作（剪贴板、按键、OCR、时钟）都是注入的，便于本地用假实现跑完整流程。
/// 每一步只做一件事：装剪贴板 / 粘 / 回车 / 判界面，任何时刻都能被取消。
/// </summary>
public sealed class RelaySession : IDisposable
{
    private readonly string _code, _password;
    private readonly Func<string, bool> _hold;
    private readonly Func<int, bool> _waitPress;
    private readonly Func<(SunloginScreenState State, string Reason)> _observe;
    private readonly Func<bool> _paste;
    private readonly Action _enter;
    private readonly Action<RelaySnapshot> _publish;
    private readonly int _waitSeconds, _progressSeconds, _pauseMs;
    private readonly Func<long> _clock;

    private RelayStep _step = RelayStep.LoadedCode;
    private long _startedAt, _enteredAt;
    private bool _aborted, _disposed;
    private string _evidence = "";

    internal RelaySession(string code, string password,
        Func<string, bool> hold, Func<int, bool> waitPress, Func<(SunloginScreenState State, string Reason)> observe,
        Func<bool> paste, Action enter, Action<RelaySnapshot> publish,
        int waitSeconds, int progressSeconds, Func<long> clock, int pauseMs = 300)
    {
        _code = code; _password = password ?? "";
        _hold = hold; _waitPress = waitPress; _observe = observe;
        _paste = paste; _enter = enter; _publish = publish;
        _waitSeconds = waitSeconds; _progressSeconds = progressSeconds;
        _clock = clock; _pauseMs = pauseMs;
    }

    private int TotalSteps => string.IsNullOrEmpty(_password) ? 1 : 2;
    private int StepIndex => _step == RelayStep.LoadedPassword ? 2 : 1;

    public ConnectResult Run(CancellationToken token)
    {
        _startedAt = _clock();
        if (!_hold(_code))
            Emit(RelayStep.LoadedCode, "剪贴板被别的程序占着，先点「重新装一次」", "剪贴板没装上");
        else
            Emit(RelayStep.LoadedCode, "点一下向日葵的「伙伴识别码」框，按 Alt+V —— 我来粘设备码",
                string.IsNullOrEmpty(_password) ? "剪贴板里是设备码" : "剪贴板里是设备码，粘完我会换成验证码");

        while (true)
        {
            if (_aborted || token.IsCancellationRequested)
            {
                Emit(RelayStep.Cancelled, "已停止；剪贴板已还原", "");
                return ConnectResult.Cancel("已停止接力；剪贴板已还原，已有会话不受影响");
            }

            if (_waitPress(200)) HandlePress();

            var (state, reason) = _observe();
            _evidence = reason;
            var (frame, verdict) = Decide(state);
            if (frame != null) Emit(frame);
            if (verdict != null) return verdict;

            if (_clock() - _startedAt > _waitSeconds * 1000L)
            {
                Emit(RelayStep.TimedOut, $"等了 {_waitSeconds / 60.0:0.#} 分钟没等到界面反应，可以点「重新装一次」再来一遍", "");
                return ConnectResult.Unknown($"等人超时（{_waitSeconds / 60.0:0.#} 分钟）；界面还停在原处，可以重来一次，或手动在向日葵点连接");
            }
        }
    }

    /// <summary>按一次 Alt+V：第一步粘设备码并回车，第二步粘验证码。</summary>
    private void HandlePress()
    {
        if (_step == RelayStep.LoadedCode)
        {
            _hold(_code);
            if (!_paste())
            {
                _hold(_code);
                Emit(RelayStep.LoadedCode, "没粘上：先点一下向日葵的「伙伴识别码」框，再按 Alt+V", "剪贴板里是设备码");
                return;
            }
            // 粘完立刻把剪贴板换成验证码：验证码只在这个接力窗口里存在，用户下一步按 Alt+V 才会用到
            _hold(_password);
            if (_pauseMs > 0) Thread.Sleep(_pauseMs);
            _enter();
            _enteredAt = _clock();
            Emit(RelayStep.Observing,
                string.IsNullOrEmpty(_password) ? "已经粘好并回车，正在看界面反应…" : "已经粘好并回车，正在看界面反应…下一步会用到验证码",
                string.IsNullOrEmpty(_password) ? "剪贴板已清空" : "剪贴板里是验证码（别复制别的，否则要我重新装）");
        }
        else if (_step == RelayStep.LoadedPassword)
        {
            _hold(_password);
            if (!_paste())
            {
                _hold(_password);
                Emit(RelayStep.LoadedPassword, "没粘上：先点一下密码框，再按 Alt+V", "剪贴板里是验证码");
                return;
            }
            _enteredAt = _clock();
            Emit(RelayStep.Observing, "验证码粘好了，等界面走完", "剪贴板里是验证码");
        }
        // 其它状态按了也不动：防止把验证码误粘进设备码框
    }

    /// <summary>判界面：返回要显示的下一帧（可空）和终局结论（可空＝继续等）。</summary>
    private (RelaySnapshot? Frame, ConnectResult? Verdict) Decide(SunloginScreenState state)
    {
        switch (state)
        {
            case SunloginScreenState.Failure:
                return (Frame(RelayStep.Failed, "向日葵报错了：" + _evidence, ""),
                    ConnectResult.Fail("向日葵报错：" + (string.IsNullOrWhiteSpace(_evidence) ? "界面提示连接失败" : _evidence) + "，请核对设备码后重试"));

            case SunloginScreenState.PasswordPrompt:
                if (string.IsNullOrEmpty(_password))
                    return (Frame(RelayStep.Done, "界面已到密码框；没读到验证码，需要时人工填一下", ""),
                        ConnectResult.Ok("已发起向日葵连接（界面已到密码框）；没有读到验证码，需要时人工补"));
                if (_step == RelayStep.LoadedPassword) return (null, null);   // 已经提示过，等按键
                return (Frame(RelayStep.LoadedPassword, "密码框出来了：点一下密码框，按 Alt+V —— 我来粘验证码", "剪贴板里是验证码"), null);

            case SunloginScreenState.Waiting:
                return (Frame(RelayStep.Done, "已进入连接中/等待对方界面", ""), ConnectResult.Ok("已发起向日葵连接（界面已进入连接中）"));

            case SunloginScreenState.Connected:
                return (Frame(RelayStep.Done, "客户端已显示连接成功", ""), ConnectResult.Ok("向日葵已显示连接成功"));

            case SunloginScreenState.MainPage when _step == RelayStep.Observing:
                if (_enteredAt > 0 && _clock() - _enteredAt > _progressSeconds * 1000L)
                    return (Frame(RelayStep.Failed, "按了回车界面没往前走，识别码可能不对", ""),
                        ConnectResult.Fail("向日葵界面没有前进（识别码可能不对），请核对后重试"));
                return (null, null);

            default:
                return (null, null);
        }
    }

    /// <summary>「重新装一次」：把当前这一步该装的内容重新写回剪贴板。</summary>
    public bool Reinstall()
    {
        var text = _step == RelayStep.LoadedPassword ? _password : _code;
        if (string.IsNullOrEmpty(text)) return false;
        bool ok = _hold(text);
        Emit(_step,
            _step == RelayStep.LoadedPassword ? "已重新装好验证码，再按一次 Alt+V" : "已重新装好设备码，再按一次 Alt+V",
            _step == RelayStep.LoadedPassword ? "剪贴板里是验证码" : "剪贴板里是设备码");
        return ok;
    }

    public void Abort() => _aborted = true;

    private RelaySnapshot Frame(RelayStep step, string hint, string clipboard)
    {
        // 只有「还在半路」的步骤才改变状态机的档位；终态只影响界面帧，
        // 否则取消/失败会把档位抹掉，「重新装一次」就不知道装码还是装密码。
        if (step is RelayStep.LoadedCode or RelayStep.Observing or RelayStep.LoadedPassword) _step = step;
        bool active = step is RelayStep.LoadedCode or RelayStep.Observing or RelayStep.LoadedPassword;
        return new RelaySnapshot(active, step, Math.Max(1, StepIndex), TotalSteps, hint, clipboard,
            CanReinstall: step is RelayStep.LoadedCode or RelayStep.LoadedPassword, _evidence);
    }

    private void Emit(RelayStep step, string hint, string clipboard) => Emit(Frame(step, hint, clipboard));

    private void Emit(RelaySnapshot snapshot)
    {
        try { _publish(snapshot); } catch { /* 界面异常不影响接力 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aborted = true;
    }
}
