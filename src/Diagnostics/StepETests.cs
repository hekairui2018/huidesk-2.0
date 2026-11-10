using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Diagnostics;

/// <summary>
/// 步E 自检：连接方式升级后，哪些判定是纯逻辑、能在本机离线验的，全在这里。
/// 覆盖四块：ToDesk 命令行参数、向日葵界面判据（观察型 OCR）、接力状态机（假剪贴板/假按键/假时钟）、剪贴板接力。
/// 真机才好验的（真客户码能不能连、真人按 Alt+V、台账落一行、单文件包双击启动）不在这里，见早上真机清单。
/// </summary>
public static class StepETests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private static OcrDoc Screen(string flat) => new() { FlatText = flat, FullText = flat };

    public static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0;
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Task Run(Action action) { action(); return Task.CompletedTask; }

        // ---------------- E-1 ToDesk 命令行 ----------------
        await Test("ToDesk URL 主通道按官方格式拼装", () => Run(() =>
        {
            Check(ToDeskLink.BuildUrl("905098332", "ub14eq0r") == "ToDesk://connect&905098332&ub14eq0r", "URL 格式不对");
            Check(ToDeskLink.BuildUrl("905 098 332", "ub14eq0r") == "ToDesk://connect&905098332&ub14eq0r", "空格没清掉");
        }));

        await Test("ToDesk 密码里的 & # % 必须转义，否则整条 URL 被截断", () => Run(() =>
        {
            var url = ToDeskLink.BuildUrl("905098332", "a&b#c%d")!;
            Check(url == "ToDesk://connect&905098332&a%26b%23c%25d", "转义不对：" + url);
            Check(url.Split('&').Length == 3, "密码里的 & 把 URL 拆成了多段");
        }));

        await Test("ToDesk 空密码只发设备码，不留空 &", () => Run(() =>
        {
            Check(ToDeskLink.BuildUrl("905098332", "") == "ToDesk://connect&905098332", "空密码多了一个 &");
            Check(ToDeskLink.BuildControlArguments("905098332", "").SequenceEqual(new[] { "-control", "-id", "905098332" }), "空密码不该带 -passwd");
        }));

        await Test("ToDesk 备选通道 -control 参数与官方示例一致", () => Run(() =>
        {
            Check(ToDeskLink.Describe("905098332", "ub14eq0r") == "-control -id 905098332 -passwd ub14eq0r", "备选参数不对");
        }));

        await Test("设备码里混了字母就不拼 URL，绝不猜", () => Run(() =>
        {
            Check(ToDeskLink.BuildUrl("90509O332", "x") == null, "把字母 O 当成 0 了");
            Check(ToDeskLink.BuildControlArguments("", "x").Count == 0, "空码还拼了参数");
        }));

        // ---------------- E-2 向日葵界面判据 ----------------
        await Test("向日葵主界面不会被当成密码框（验证码（可为空）是占位文字）", () => Run(() =>
        {
            var doc = Screen("向日葵远程控制远程协助他人请输入伙伴识别码验证码（可为空）连接");
            var (state, _) = SunloginScreen.Classify(doc);
            Check(state == SunloginScreenState.MainPage, "主界面判成了 " + state);
        }));

        await Test("向日葵弹出密码框＝已发起的前置证据", () => Run(() =>
        {
            Check(SunloginScreen.Classify(Screen("输入连接密码确定取消")).State == SunloginScreenState.PasswordPrompt, "密码框没认出来");
            Check(SunloginScreen.Classify(Screen("请输入验证码")).State == SunloginScreenState.PasswordPrompt, "验证码弹窗没认出来");
        }));

        await Test("向日葵连接中／等待对方＝已发起", () => Run(() =>
        {
            Check(SunloginScreen.Classify(Screen("正在连接请稍候")).State == SunloginScreenState.Waiting, "连接中没认出来");
            Check(SunloginScreen.Classify(Screen("等待对方接受远程协助")).State == SunloginScreenState.Waiting, "等待对方没认出来");
        }));

        await Test("向日葵报错优先于其它判断（识别码错、设备离线）", () => Run(() =>
        {
            Check(SunloginScreen.Classify(Screen("识别码错误请核对后重试")).State == SunloginScreenState.Failure, "识别码错没认出来");
            Check(SunloginScreen.Classify(Screen("对方设备不在线")).State == SunloginScreenState.Failure, "离线没认出来");
        }));

        await Test("空画面判不出，不硬猜成功", () => Run(() =>
        {
            Check(SunloginScreen.Classify(new OcrDoc()).State == SunloginScreenState.Unknown, "空画面被当成成功");
            Check(SunloginScreen.Classify(null).State == SunloginScreenState.Unknown, "null 被当成成功");
        }));

        // ---------------- E-2 热键判据 ----------------
        await Test("Alt+V 判据：只在上膛且有 Alt 时吞键", () => Run(() =>
        {
            Check(AltVHotkey.ShouldCapture(true, true, 0x56, true), "该吞的没吞");
            Check(!AltVHotkey.ShouldCapture(false, true, 0x56, true), "没上膛也吞了，会挡住用户平时的 Alt+V");
            Check(!AltVHotkey.ShouldCapture(true, false, 0x56, true), "没按 Alt 也吞了，会挡住单独按 V");
            Check(!AltVHotkey.ShouldCapture(true, true, 0x43, true), "吞错了键（C）");
            Check(!AltVHotkey.ShouldCapture(true, true, 0x56, false), "抬键不该再算一次触发");
        }));

        // ---------------- E-2 接力状态机 ----------------
        await Test("接力·有验证码：粘码→回车→等密码框→粘密码→已发起", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t is 0 or 2 };
            h.Screen.Enqueue(SunloginScreenState.PasswordPrompt);
            h.Screen.Enqueue(SunloginScreenState.PasswordPrompt);
            h.Screen.Enqueue(SunloginScreenState.Waiting);
            var result = h.Run("905098332", "ub14eq0r");

            Check(result.Status == ConnectStatus.Launched, "终态应为已发起，实际 " + result.Status);
            Check(h.Pastes == 2, "应该粘两次（设备码、验证码），实际 " + h.Pastes);
            Check(h.Enters == 1, "回车只该代发一次，实际 " + h.Enters);
            Check(h.Holds.SequenceEqual(new[] { "905098332", "905098332", "ub14eq0r", "ub14eq0r" }),
                "剪贴板顺序不对：" + string.Join(",", h.Holds));
            Check(h.Frames.Any(f => f.Step == RelayStep.LoadedPassword && f.StepIndex == 2), "没提示到第 2 步");
            Check(h.Frames.All(f => f.Clipboard != "ub14eq0r" || f.Hint.Length > 0), "剪贴板说明缺一句话");
        }));

        await Test("接力·没读到验证码：到密码框就算已发起，不瞎等", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t == 0 };
            h.Screen.Enqueue(SunloginScreenState.PasswordPrompt);
            var result = h.Run("905098332", "");
            Check(result.Status == ConnectStatus.Launched, "无密码应判已发起，实际 " + result.Status);
            Check(h.Pastes == 1 && h.Enters == 1, "无密码只该粘一次、回车一次");
            Check(result.Message.Contains("没有读到验证码"), "没把「没验证码」说清楚：" + result.Message);
        }));

        await Test("接力·界面报错就判失败，不报成功", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t == 0 };
            h.Screen.Enqueue(SunloginScreenState.Failure);
            var result = h.Run("905098332", "ub14eq0r");
            Check(result.Status == ConnectStatus.Failed, "报错时应失败，实际 " + result.Status);
            Check(result.Message.Contains("核对"), "失败提示不含下一步该干什么：" + result.Message);
        }));

        await Test("接力·按了回车界面不前进＝识别码可能不对", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t == 0 };
            for (int i = 0; i < 40; i++) h.Screen.Enqueue(SunloginScreenState.MainPage);
            var result = h.Run("905098332", "ub14eq0r", progressSeconds: 1);
            Check(result.Status == ConnectStatus.Failed, "卡在主界面应判失败，实际 " + result.Status);
            Check(result.Message.Contains("没有前进"), "没说清是界面没前进：" + result.Message);
        }));

        await Test("接力·没人按键就超时，且提示可以重来", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = _ => false };
            for (int i = 0; i < 40; i++) h.Screen.Enqueue(SunloginScreenState.Unknown);
            var result = h.Run("905098332", "ub14eq0r", waitSeconds: 1);
            Check(result.Status == ConnectStatus.Uncertain, "没人按键应判等待超时，实际 " + result.Status);
            Check(result.Message.Contains("重来"), "超时提示没给退路：" + result.Message);
        }));

        await Test("接力·取消立刻停手，并说清已有会话不受影响", () => Run(() =>
        {
            using var cts = new CancellationTokenSource();
            var h = new RelayHarness { PressAt = t => { if (t == 2) cts.Cancel(); return false; }, Token = cts.Token };
            for (int i = 0; i < 40; i++) h.Screen.Enqueue(SunloginScreenState.Unknown);
            var result = h.Run("905098332", "ub14eq0r");
            Check(result.Status == ConnectStatus.Cancelled, "取消应判已停止，实际 " + result.Status);
            Check(result.Message.Contains("剪贴板已还原"), "没交代剪贴板怎么处理：" + result.Message);
        }));

        await Test("接力·前台不是向日葵就不粘、不回车（别把码糊到聊天窗口）", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t == 0, PasteOk = false };
            h.Screen.Enqueue(SunloginScreenState.MainPage);
            for (int i = 0; i < 40; i++) h.Screen.Enqueue(SunloginScreenState.Unknown);
            var result = h.Run("905098332", "ub14eq0r", waitSeconds: 1);
            Check(h.Enters == 0, "前台不对还代发了回车");
            Check(h.Frames.Any(f => f.Hint.Contains("没粘上")), "没告诉人「没粘上」");
            Check(result.Status == ConnectStatus.Uncertain, "应继续等而不是报成功，实际 " + result.Status);
        }));

        await Test("接力·重新装一次只装当前这一步该装的东西", () => Run(() =>
        {
            var h = new RelayHarness { PressAt = t => t == 0, StopAfterFrames = 2 };
            h.Screen.Enqueue(SunloginScreenState.PasswordPrompt);
            h.Run("905098332", "ub14eq0r");
            Check(h.LastSession != null, "会话没留下来");
            h.Holds.Clear();
            bool ok = h.LastSession!.Reinstall();
            Check(ok, "重新装失败");
            Check(h.Holds.Count == 1, "重新装该只装一次，实际 " + h.Holds.Count);
            Check(h.Holds[0] == "ub14eq0r", "第 2 步该重新装验证码，实际装了 " + h.Holds[0]);
        }));

        // ---------------- 开关默认值 ----------------
        await Test("默认走新通道、老点按默认关、等人 3 分钟", () => Run(() =>
        {
            var preferences = new Preferences();
            Check(!preferences.UseLegacyClickPath, "老点按路径默认没关");
            Check(preferences.EffectiveRelayWaitSeconds == 180, "等人上限不是 3 分钟");
            preferences.RelayWaitSeconds = 5;
            Check(preferences.EffectiveRelayWaitSeconds == 180, "越界的等人上限没有被夹回默认");
        }));

        // ---------------- 剪贴板接力（真机本机，只在剪贴板里没有图片时跑） ----------------
        await Test("剪贴板接力：装码→换密码→还原用户原文", () => Run(() =>
        {
            if (ClipboardReader.HasImage)
            {
                Console.WriteLine("  跳过：剪贴板里是图片，为免弄丢不碰（早上手边没东西时再跑 --probe-relay-clipboard）");
                return;
            }
            var original = ClipboardRelay.Peek();
            using var relay = new ClipboardRelay();
            Check(relay.Hold("905098332"), "写设备码失败");
            Check(ClipboardRelay.Peek() == "905098332", "设备码没读回来");
            Check(relay.Hold("ub14eq0r"), "换验证码失败");
            Check(ClipboardRelay.Peek() == "ub14eq0r", "验证码没读回来");
            relay.Release();
            Check((ClipboardRelay.Peek() ?? "") == (original ?? ""), "用户原文没还原");
        }));

        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---------------- 假环境：把状态机的所有外部动作换成假的 ----------------
    private sealed class RelayHarness
    {
        public readonly List<string> Holds = new();
        public readonly List<RelaySnapshot> Frames = new();
        public readonly Queue<SunloginScreenState> Screen = new();
        public int Pastes, Enters, Ticks;
        public bool PasteOk = true;
        public long Now;
        public Func<int, bool> PressAt = _ => false;
        public CancellationToken Token = CancellationToken.None;
        public int StopAfterFrames;
        public RelaySession? LastSession;

        public ConnectResult Run(string code, string password, int waitSeconds = 60, int progressSeconds = 10)
        {
            RelaySession? session = null;
            session = new RelaySession(code, password,
                hold: text => { Holds.Add(text); return true; },
                waitPress: _ => { Now += 200; return PressAt(Ticks++); },
                observe: () => (Screen.Count > 0 ? Screen.Dequeue() : SunloginScreenState.Unknown, "测试画面"),
                paste: () => { Pastes++; return PasteOk; },
                enter: () => Enters++,
                publish: frame =>
                {
                    Frames.Add(frame);
                    if (StopAfterFrames > 0 && Frames.Count >= StopAfterFrames) session?.Abort();
                },
                waitSeconds: waitSeconds, progressSeconds: progressSeconds,
                clock: () => Now, pauseMs: 0);
            LastSession = session;
            return session.Run(Token);
        }
    }

    // ---------------- 探针：ToDesk 命令行（只拼不发） ----------------
    public static int ProbeToDeskCli(string deviceCode, string password, string reportPath)
    {
        string? exe = null;
        try { exe = AppPaths.FindToDesk(); } catch { }
        var payload = new
        {
            deviceCode,
            passwordLength = password.Length,
            url = ToDeskLink.BuildUrl(deviceCode, password),
            urlEscapedWhenNeeded = ToDeskLink.BuildUrl(deviceCode, "a&b#c%d"),
            controlArguments = ToDeskLink.BuildControlArguments(deviceCode, password),
            controlDescription = ToDeskLink.Describe(deviceCode, password),
            protocolRegistry = ToDeskLink.RegisteredProtocolCommand(),
            executable = exe,
            executableExists = exe != null && File.Exists(exe)
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, json);
        return ToDeskLink.BuildUrl(deviceCode, password) == null ? 1 : 0;
    }

    // ---------------- 探针：Alt+V 钩子能不能真收到键 ----------------
    public static int ProbeAltVHook(string reportPath)
    {
        AltVHotkey? hotkey = null;
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            hotkey = new AltVHotkey();
            hotkey.Install();
            hotkey.Armed = true;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Console.Error.WriteLine("钩子线程没起来");
            return 1;
        }

        int presses = 0;
        hotkey!.Pressed += () => Interlocked.Increment(ref presses);
        Thread.Sleep(200);
        SendAltV();
        Thread.Sleep(400);
        SendAltV();
        Thread.Sleep(600);

        var captured = hotkey.Captured;
        var detail = hotkey.Detail;
        var installed = hotkey.Installed;
        var hook = hotkey.HookInstalled;
        dispatcher!.InvokeShutdown();
        hotkey.Dispose();

        var payload = new
        {
            installed,
            lowLevelHook = hook,
            detail,
            syntheticallyPressed = 2,
            capturedByHook = captured,
            raisedEvents = presses,
            verdict = captured >= 2 ? "Alt+V 能被接收并吞掉" : "没收全，早上换键或换装法"
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, json);
        return captured >= 2 ? 0 : 1;
    }

    // ---------------- 探针：剪贴板接力（会临时占用剪贴板，结束还原文本） ----------------
    public static int ProbeRelayClipboard(string reportPath)
    {
        if (ClipboardReader.HasImage)
        {
            var skip = new { skipped = true, reason = "剪贴板里是图片，为免弄丢不动它", hasImage = true };
            var skipJson = JsonSerializer.Serialize(skip, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Console.WriteLine(skipJson);
            if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, skipJson);
            return 0;
        }
        var original = ClipboardRelay.Peek();
        var steps = new List<string>();
        bool ok = true;
        try
        {
            using var relay = new ClipboardRelay();
            steps.Add("写入设备码=" + relay.Hold("905098332"));
            steps.Add("读回=" + ClipboardRelay.Peek());
            steps.Add("换验证码=" + relay.Hold("ub14eq0r"));
            steps.Add("读回=" + ClipboardRelay.Peek());
            relay.Release();
            steps.Add("还原后=" + (ClipboardRelay.Peek() ?? ""));
            ok = (ClipboardRelay.Peek() ?? "") == (original ?? "");
        }
        catch (Exception ex) { steps.Add("异常 " + ex.GetType().Name + ": " + ex.Message); ok = false; }
        var payload = new { originalHadText = original != null, steps, restored = ok };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, json);
        return ok ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    // 真实 INPUT 在 x64 上是 40 字节（联合体按 MOUSEINPUT 的 32 字节对齐），这里必须显式写 32，否则 SendInput 直接拒收
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>用系统级注入替人手按一次 Alt+V（钩子按「真按键」处理）。</summary>
    private static void SendAltV()
    {
        var inputs = new[]
        {
            Key(0x12), Key(0x56), Key(0x56, 2), Key(0x12, 2)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length) Console.WriteLine("SendInput 只送进去 " + sent + " 个");
    }

    private static Input Key(ushort vk, uint flags = 0) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = 0, Flags = flags } }
    };
}
