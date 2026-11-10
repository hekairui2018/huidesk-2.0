using System.Diagnostics;
using System.Drawing;
using RemoteConnectHelper.V2.Ocr;

namespace RemoteConnectHelper.V2.Infrastructure;

// Runs once per desktop launch. Diagnostic/worker invocations do not start this task.
internal static class OcrBootstrap
{
    private static Task<bool>? _ready;
    internal static void Start() => _ready ??= Task.Run(Prepare);
    internal static async Task WaitAsync(CancellationToken token)
    {
        if (_ready is { } ready && !await ready.WaitAsync(token))
            throw new InvalidOperationException("PP-OCR后台修复未通过，请打开OCR自检与修复查看结果");
    }
    internal static bool Prepare()
    {
        using var mutex = new Mutex(false, "Local\\GrayDesk20-OcrPreparation");
        bool owns = false;
        try
        {
            try { owns = mutex.WaitOne(TimeSpan.FromMinutes(4)); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new TimeoutException("OCR后台修复仍在进行");
            Log.Write("OCR启动后台检查开始");
            bool passed = CheckAndRepair(
                () => Run(Environment.ProcessPath!, ["--probe-ocr-health"], 40),
                () => {
                    string payload = Path.Combine(AppContext.BaseDirectory, "Tools", "OcrRepair");
                    string script = Path.Combine(payload, "repair.ps1");
                    if (!File.Exists(script)) return false;
                    string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                    return Run(ps, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                        "-PayloadRoot", payload, "-SourceExe", Environment.ProcessPath!, "-Background"], 200);
                },
                () => OcrMaintenance.ApplyVerifiedCompatibility(reload: true));
            Log.Write(passed ? "OCR启动后台检查通过" : "OCR启动后台修复未通过；保留本地诊断结果");
            return passed;
        }
        catch (Exception ex) { Log.Write("OCR后台准备失败 " + ex.GetType().Name); return false; }
        finally { if (owns) mutex.ReleaseMutex(); }
    }
    internal static bool CheckAndRepair(Func<bool> probe, Func<bool> repair, Action reload)
    {
        if (probe()) return true;
        Log.Write("OCR启动检查失败，后台修复兼容环境");
        if (!repair()) return false;
        reload();
        return probe(); // Never declare success merely because files were copied.
    }
    private static bool Run(string executable, string[] args, int seconds)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        info.Environment["GRAYDESK_OCR_BACKGROUND"] = "1";
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("后台OCR进程未启动");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (!process.WaitForExit(seconds * 1000)) { process.Kill(true); process.WaitForExit(); return false; }
        return process.ExitCode == 0;
    }
    internal static async Task<int> ProbeAsync()
    {
        using var image = new Bitmap(700, 140);
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.White);
            using var font = new Font("Arial", 32);
            graphics.DrawString("ToDesk 123456789", font, Brushes.Black, 15, 25);
        }
        using var stream = new MemoryStream(); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var provider = new IsolatedRapidOcrProvider { RequestTimeout = TimeSpan.FromSeconds(30) };
        var document = await provider.ReadAsync(stream.ToArray(), 0, false, CancellationToken.None);
        return string.Concat(document.Blocks.Select(b => b.Text)).Replace(" ", "").Contains("123456789") ? 0 : 1;
    }
}
