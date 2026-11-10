using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.UI;

namespace RemoteConnectHelper.V2.Diagnostics;

public static class CommandLine
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if(args is ["--probe-notification-image",var notificationImage,var notificationOutput])return NotificationProbe.Image(notificationImage,notificationOutput);
            if(args is ["--verify-notification-fixture",var fixtureNotificationImage,var fixtureNotificationOutput])return NotificationProbe.Fixture(fixtureNotificationImage,fixtureNotificationOutput);
            if(args is ["--probe-notification-live",var liveNotificationOutput])return NotificationProbe.Live(liveNotificationOutput);
            if(args is ["--probe-ocr-health"])return OcrBootstrap.ProbeAsync().GetAwaiter().GetResult();
            if(args is ["--prepare-ocr"])return OcrBootstrap.Prepare()?0:1;
            if(args is ["--verify-main-window-selection",var mainOutput])return MainWindowSelectionTests.Run(mainOutput);
            if(args is ["--verify-startup-recovery",var startupOutput])return StartupRecoveryTests.Run(startupOutput);
            if(args is ["--verify-update-status-window",var updateWindowOutput])return UpdateStatusWindowTests.Run(updateWindowOutput);
            if(args is ["--verify-online-update",var updateOutput])return OnlineUpdateTests.RunAsync(updateOutput).GetAwaiter().GetResult();
            if(args is ["--verify-input-protection",var inputOutput])return InputProtectionTests.Run(inputOutput);
            if(args is ["--probe-live-input",var client,var probeOutput])return LiveInputProbe.Run(client,probeOutput);
            if(args is ["--probe-todesk-self",var selfOutput])return ToDeskSelfProbe.Run(selfOutput);
            if(args is ["--probe-complete-sunlogin",var localCode,var completeOutput])return CompleteSunloginProbe.Run(localCode,completeOutput);
            if(args is ["--verify-window-readback",var readbackOutput])return WindowCodeTests.Run(readbackOutput);
            if(args is ["--ocr-worker",var owner]&&int.TryParse(owner,out int parentPid)&&parentPid>0)return OcrWorker.Run(parentPid:parentPid);
            if(args is ["--ocr-worker"])return OcrWorker.Run();
            if(args is ["--ocr-worker-test-exit"])return OcrWorker.Run("exit");
            if(args is ["--ocr-worker-test-hang"])return OcrWorker.Run("hang");
            if(args is ["--verify-connection-status",var statusOutput])return ConnectionStateTests.Run(statusOutput);
            if(args is ["--verify-explicit-connect",var explicitOutput])return ExplicitConnectTests.Run(explicitOutput);
            if(args is ["--verify-ocr-isolation",var isolationOutput])return OcrIsolationTests.RunAsync(isolationOutput).GetAwaiter().GetResult();
            if (args is ["--verify-independent-blocks",var blocksOutput]) return IndependentBlocksTests.Run(blocksOutput);
            if (args is ["--verify-software-ui",var softwareOutput]) return DesktopInteractionTests.RunSoftware(softwareOutput);
            if (args is ["--verify-theme",var themeOutput]) return DesktopInteractionTests.RunTheme(themeOutput);
            if (args is ["--verify-help-ui",var helpOutput]) return DesktopInteractionTests.RunHelp(helpOutput);
            if (args is ["--verify-file-picker",var pickerOutput]) return FilePickerTests.Run(pickerOutput);
            if (args is ["--desktop-test", var desktopOutput]) return DesktopInteractionTests.Run(desktopOutput);
            if (args is ["--desktop-test", var testOutput, var baselineV1]) return DesktopInteractionTests.Run(testOutput,baselineV1);
            if (args is ["--verify-desktop-state"]) return DesktopStateTests.RunAsync().GetAwaiter().GetResult();
            if (args is ["--desktop-demo"]) return DesktopDemo.Run();
            if (args is ["--verify-clipboard", var clipboardReport]) return ClipboardReadOnlyTest.Run(clipboardReport);
            if (args is ["--discover", var discoveryReport]) {File.WriteAllText(discoveryReport,JsonSerializer.Serialize(Discovery.RemoteSoftwareDiscovery.Discover(),new JsonSerializerOptions{WriteIndented=true}));return 0;}
            if (args is ["--verify-orientation",var orientationOutput]) return OrientationTests.RunAsync(orientationOutput).GetAwaiter().GetResult();
            if (args is ["--verify-graydesk"]) return GrayDeskTests.RunAsync().GetAwaiter().GetResult();
            // 步E：连接方式升级（ToDesk 命令行 / 向日葵 Alt+V 接力）的离线自检与真机探针
            if (args is ["--verify-step-e"]) return StepETests.RunAsync().GetAwaiter().GetResult();
            if (args is ["--probe-todesk-cli", var tdCode, var tdPassword]) return StepETests.ProbeToDeskCli(tdCode, tdPassword, "");
            if (args is ["--probe-todesk-cli", var tdCode2, var tdPassword2, var tdReport]) return StepETests.ProbeToDeskCli(tdCode2, tdPassword2, tdReport);
            if (args is ["--probe-altv-hook", var altvReport]) return StepETests.ProbeAltVHook(altvReport);
            if (args is ["--probe-relay-clipboard", var relayClipReport]) return StepETests.ProbeRelayClipboard(relayClipReport);
            if (args is ["--verify"]) return RegressionSuite.RunAsync().GetAwaiter().GetResult();
            if (args is ["--selftest", var image])
            {
                using var pipeline = new RecognitionPipeline();
                var result = pipeline.AnalyzeAsync(File.ReadAllBytes(image), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args is ["--customer-info", var customerImage])
            {
                // 读名片：与界面「读名片」走同一条链路（同一个 OCR 管线 + 同一套解析规则）。
                using var pipeline = new RecognitionPipeline();
                var document = pipeline.ReadDocumentAsync(File.ReadAllBytes(customerImage), CancellationToken.None).GetAwaiter().GetResult();
                var parsed = Parsing.CustomerInfoRecognizer.Parse(document);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phone = parsed.Info.Phone,
                    name = parsed.Info.Name,
                    evidence = parsed.Evidence,
                    issues = parsed.Issues,
                    blocks = document.Blocks.Select(b => new { text = b.Text, x = Math.Round(b.Bounds.X), y = Math.Round(b.Bounds.Y), w = Math.Round(b.Bounds.Width), h = Math.Round(b.Bounds.Height) })
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return 0;
            }
            if (args is ["--batch", var folder, var output]) return BatchAsync(folder, output).GetAwaiter().GetResult();
            if (args is ["--evaluate", var images, var manifest, var results]) return Benchmark.RunAsync(images, manifest, results).GetAwaiter().GetResult();
            if (args is ["--render", var png]) { Render(png); return 0; }
            if (args is ["--render", var sample, var rendered]) { Render(rendered, sample); return 0; }
            if (args is ["--verify-ui", var uiSample, var uiPng]) { Render(uiPng, uiSample, true); return 0; }
            if (args.Length >= 2 && args[0] == "--probe-upload-status") return ProbeUploadStatus(args[1], args.Length > 2 ? args[2] : null);
            if (args.Length >= 2 && args[0] == "--render-connect-card")
            {
                // 离屏渲染「本次连接」卡（不 Show、不开热键、不联网）：用来核版式与文案。
                int cardHeight = args.Length > 2 && int.TryParse(args[2], out int parsedHeight) ? parsedHeight : 500;
                RenderConnectCard(args[1], cardHeight, args.Length > 3 ? args[3] : "sent", args.Length > 4 ? args[4] : "Light");
                return 0;
            }
            Console.Error.WriteLine("--verify | --selftest <image> | --batch <folder> <output.json> | --evaluate <folder> <manifest.json> <output-folder> | --render <output.png>");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task<int> BatchAsync(string folder, string output)
    {
        using var pipeline = new RecognitionPipeline();
        var rows = new List<object>();
        int errors = 0;
        foreach (string file in Directory.EnumerateFiles(folder).Where(p => new[] { ".jpg", ".png", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Order())
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var r = await pipeline.AnalyzeAsync(await File.ReadAllBytesAsync(file), CancellationToken.None);
                rows.Add(new { Image = Path.GetFileName(file), App = r.AppName, Code = r.DeviceCode, Password = r.Password, Decision = r.Decision.ToString(),
                    Variant = r.Document.Variant, Engine = r.Document.Engine, Ms = clock.ElapsedMilliseconds, Blocks = r.Document.Blocks.Count });
                Console.WriteLine($"{Path.GetFileName(file)}: {r.AppName}, code={r.DeviceCode}, pwd={r.Password}, {r.Decision}, {clock.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { errors++; rows.Add(new { Image = Path.GetFileName(file), Error = ex.Message }); Console.WriteLine("FAIL " + Path.GetFileName(file) + " " + ex.Message); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        return rows.Count > 0 && errors == 0 ? 0 : 1;
    }
    private static void Render(string output, string? sample = null, bool verify = false)    {
        // 仅离屏渲染，不调用 Show/Run，不注册热键、托盘或剪贴板监听。
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AssistantController { AutoConnect = false };
        if (sample != null) Task.Run(() => controller.RecognizeAsync(File.ReadAllBytes(sample))).GetAwaiter().GetResult();
        var window = new ShellWindow(controller, new Preferences { AutoConnect = false }) { CanClose = true };
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1008, 702)); root.Arrange(new Rect(0, 0, 1008, 702)); root.UpdateLayout();
        if (verify)
        {
            var code = (System.Windows.Controls.TextBox)window.FindName("CodeBox");
            var password = (System.Windows.Controls.TextBox)window.FindName("PasswordBox");
            if (!code.IsEnabled || !password.IsEnabled || code.IsReadOnly || password.IsReadOnly) throw new InvalidOperationException("Recognized fields are not editable");
            var candidates = (System.Windows.Controls.WrapPanel)window.FindName("CodeCandidates");
            foreach (System.Windows.Controls.Button candidate in candidates.Children)
            {
                candidate.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (code.Text != candidate.Content.ToString()) throw new InvalidOperationException("Candidate selection did not update device code");
                var chosen = controller.Result!.DeviceCodes.First(c => c.Value == code.Text);
                string expectedPassword = controller.Result.Passwords.Where(p => p.DeviceCandidateId == chosen.Id).MaxBy(p => p.RuleScore)?.Value ?? "";
                if (password.Text != expectedPassword) throw new InvalidOperationException("Candidate password paired incorrectly");
            }
            if (candidates.Children.Count > 0) ((System.Windows.Controls.Button)candidates.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            foreach (var name in new[] { "ConnectButton", "StopButton" })
            {
                var button = (FrameworkElement)window.FindName(name);
                var position = button.TranslatePoint(new System.Windows.Point(0, 0), root);
                if (position.Y < 0 || position.Y + button.ActualHeight > root.ActualHeight) throw new InvalidOperationException("Action button outside viewport");
                for (DependencyObject? parent = VisualTreeHelper.GetParent(button); parent != null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is System.Windows.Controls.ScrollViewer) throw new InvalidOperationException("Action button scrolls away");
            }
            string expectedCode = code.Text;
            code.Text = "123456789"; password.Text = "Aa1234";
            if (code.Text != "123456789" || password.Text != "Aa1234") throw new InvalidOperationException("Manual edit failed");
            code.Text = expectedCode; password.Text = controller.Result?.Password ?? "";
            // Restore the recognized overlays after the intentional edit check.
            if (candidates.Children.Count > 0) ((System.Windows.Controls.Button)candidates.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Console.WriteLine("PASS offscreen fields/edit/candidate pairing/persistent action buttons; native focus and input not tested");
        }
        var bitmap = new RenderTargetBitmap(1060, 742, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 246, 251)), null, new Rect(0, 0, 1060, 742));
        bitmap.Render(visual); bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        window.Close(); controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown();
    }

    /// <summary>探针：走一遍真实上报链路，把状态序列打出来（配合 GRAYDESK_DIAGNOSTIC_ROOT 用临时目录，不碰真 outbox/台账）。</summary>
    private static int ProbeUploadStatus(string mode, string? url)
    {
        var states = new List<string>();
        using var done = new ManualResetEventSlim();
        void OnStatus(UploadStatus status)
        {
            lock (states) states.Add($"{status.State}: {status.Detail}");
            if (status.State != UploadState.Sending) done.Set();
        }
        if (mode == "sent" && !string.IsNullOrEmpty(url)) ConnectLogReporter.EndpointOverride = url;
        if (mode == "queued" || mode == "retry") ConnectLogReporter.EndpointOverride = "http://127.0.0.1:59999/graydesk/connect-log";
        ConnectLogReporter.Report(RemoteConnectHelper.V2.Domain.RemoteApp.Sunlogin, "900000001", "success",
            phone: "13800138000", customerName: "上报探针", weekToken: "0099", onStatus: OnStatus);
        bool finished = done.Wait(TimeSpan.FromSeconds(12));
        Thread.Sleep(150);
        var queuedPending = ConnectLogReporter.PendingCount();
        if (mode == "retry")
        {
            // 网络恢复后点状态点补传：把端点换回可用地址，走 FlushNow（和界面「重试」同一条路）
            done.Reset();
            ConnectLogReporter.EndpointOverride = url;
            ConnectLogReporter.FlushNow(OnStatus);
            done.Wait(TimeSpan.FromSeconds(12));
            finished = done.IsSet;
            Thread.Sleep(150);
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            mode,
            root = Log.Root,
            finished,
            outbox_after_first = queuedPending,
            outbox_pending = ConnectLogReporter.PendingCount(),
            states
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return finished ? 0 : 1;
    }

    /// <summary>离屏渲染确认卡（步E）：把「本次连接」三块摆成真实状态，只出图，不 Show、不联网、不写台账。</summary>
    private static void RenderConnectCard(string output, int height, string state, string theme = "Light")
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeManager.Apply(theme);
        var controller = new AssistantController { AutoConnect = false };
        switch (state)
        {
            case "sent":
                controller.SeedConnectOutcome("0042", ConnectReportState.Sent, "已写入台账", "13800138000", "王工 · 苏州明泰");
                break;
            case "queued":
                controller.SeedConnectOutcome("0043", ConnectReportState.Queued, "网络不通，已排队待补传（本地 6 条）", "13800138000", "王工 · 苏州明泰", pending: 6);
                break;
            case "failed":
                controller.SeedConnectOutcome("0044", ConnectReportState.Failed, "上报失败，本次记录已留本地");
                break;
            case "manual":
                controller.SeedConnectOutcome("0045", ConnectReportState.Sent, "已写入台账", "138-0013-8000", "李工", manualCustomer: true);
                break;
            case "issue":
                // 最坏情况：名字超长 + 存疑提示要换行，用来看卡片会不会被撑破
                controller.SeedConnectOutcome("0046", ConnectReportState.Queued, "网络不通，已排队待补传（本地 12 条）",
                    "13800138000", "苏州明泰光电科技有限公司 王工", pending: 12,
                    issue: "读到的号码疑似远程码/验证码，已排除，请手动确认客户号码");
                break;
            // 步E：接力区的两种样子（第 1 步等粘设备码 / 第 2 步等粘验证码）
            case "relay1":
                controller.SeedRelay(new RemoteConnectHelper.V2.Connections.RelaySnapshot(true,
                    RemoteConnectHelper.V2.Connections.RelayStep.LoadedCode, 1, 2,
                    "点一下向日葵的「伙伴识别码」框，按 Alt+V —— 我来粘设备码",
                    "剪贴板里是设备码，粘完我会换成验证码", true, ""));
                break;
            case "relay2":
                controller.SeedRelay(new RemoteConnectHelper.V2.Connections.RelaySnapshot(true,
                    RemoteConnectHelper.V2.Connections.RelayStep.LoadedPassword, 2, 2,
                    "密码框出来了：点一下密码框，按 Alt+V —— 我来粘验证码",
                    "剪贴板里是验证码", true, ""));
                break;
            default: break; // none：未连接的样子
        }
        const int width = 320;
        var card = new ConnectionCard(controller);
        var root = (FrameworkElement)card.Content;
        root.Measure(new System.Windows.Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 246, 251)), null, new Rect(0, 0, width, height));
        bitmap.Render(visual); bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var stream = File.Create(output)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        var reportState = (System.Windows.Controls.TextBlock)card.FindName("ReportStateText")!;
        var tokenText = (System.Windows.Controls.TextBlock)card.FindName("WeekTokenText")!;
        var record = (FrameworkElement)card.FindName("SessionRecordBox")!;
        var recordTop = record.TranslatePoint(new System.Windows.Point(0, 0), root);
        var scroll = (System.Windows.Controls.ScrollViewer)card.FindName("EditorScroll")!;
        Console.WriteLine($"card={state} token={tokenText.Text} report={reportState.Text} size={width}x{height} " +
            $"record={recordTop.Y:F0}..{recordTop.Y + record.ActualHeight:F0} content={scroll.ExtentHeight:F0} -> {Path.GetFullPath(output)}");
        card.Close(); controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown();
    }
}
