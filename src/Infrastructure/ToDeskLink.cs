using System.Diagnostics;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>ToDesk 这一趟到底从哪条路进去的（写日志、界面上区分「已发起」的依据）。</summary>
public enum ToDeskRoute { None, UrlArgument, ProtocolShell, ControlCommand }

/// <summary>
/// 步E：ToDesk 命令行直连。
/// 一条 URL（ToDesk://connect&amp;码&amp;密码）就是官方给浏览器用的「快速发起远程连接」，
/// 注册表里 ToDesk 协议处理器正是 <c>"ToDesk.exe" "%1"</c>，所以我们把同一条 URL 直接当参数喂给 exe，
/// 等价于协议调用但不经过 shell（不受命令行注入影响）。
/// <c>-control -id -passwd</c> 作备选：官方文档注明只对设备列表里已解锁的设备生效，临时客户码可能静默失败。
/// 这里只负责「怎么拼、怎么起」，连上没连上交给 SessionObserver 判。
/// </summary>
public static class ToDeskLink
{
    public const string UrlPrefix = "ToDesk://connect";

    /// <summary>去掉空格/连字符后必须全是数字；带字母或空串一律视为无效码，不做「猜」。</summary>
    private static string Digits(string? deviceCode)
    {
        var stripped = new string((deviceCode ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return stripped.Length > 0 && stripped.All(char.IsAsciiDigit) ? stripped : "";
    }

    /// <summary>URL 主通道。密码要转义，否则密码里的 &amp;/#/% 会把整条 URL 截断。</summary>
    public static string? BuildUrl(string? deviceCode, string? password)
    {
        var code = Digits(deviceCode);
        if (code.Length == 0) return null;
        return string.IsNullOrEmpty(password)
            ? $"{UrlPrefix}&{code}"
            : $"{UrlPrefix}&{code}&{Uri.EscapeDataString(password)}";
    }

    /// <summary>备选通道：-control 的参数表（用于日志与 -help 对照，不直接拼成一条字符串去起进程）。</summary>
    public static IReadOnlyList<string> BuildControlArguments(string? deviceCode, string? password)
    {
        var code = Digits(deviceCode);
        if (code.Length == 0) return Array.Empty<string>();
        var args = new List<string> { "-control", "-id", code };
        if (!string.IsNullOrEmpty(password)) { args.Add("-passwd"); args.Add(password); }
        return args;
    }

    /// <summary>给人看的一行参数（日志/报告用）。</summary>
    public static string Describe(string deviceCode, string password)
        => string.Join(" ", BuildControlArguments(deviceCode, password).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

    /// <summary>把 URL 当参数直接交给 exe（等价于协议处理器，但不经过 shell）。</summary>
    public static bool TryLaunchUrlArgument(string exePath, string url, out string detail)
    {
        detail = "";
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            info.ArgumentList.Add(url);
            using var process = Process.Start(info);
            detail = process == null ? "进程没有起来" : $"已起 ToDesk 进程 pid={process.Id}";
            return process != null;
        }
        catch (Exception ex)
        {
            detail = ($"exe 直接传参失败 {ex.GetType().Name}: {ex.Message}").Trim();
            return false;
        }
    }

    /// <summary>exe 不认参数时的退路：交给 Windows 的 ToDesk:// 协议处理器（注册表指向同一个 exe）。</summary>
    public static bool TryLaunchProtocol(string url, out string detail)
    {
        detail = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            detail = process == null ? "协议处理器没有回话" : $"已交给协议处理器 pid={process.Id}";
            return true;
        }
        catch (Exception ex)
        {
            detail = ($"协议处理器失败 {ex.GetType().Name}: {ex.Message}").Trim();
            return false;
        }
    }

    /// <summary>备选通道：-control -id -passwd。</summary>
    public static bool TryLaunchControl(string exePath, IReadOnlyList<string> arguments, out string detail)
    {
        detail = "";
        if (arguments.Count == 0) { detail = "参数为空"; return false; }
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            detail = process == null ? "进程没有起来" : $"已起 ToDesk 进程 pid={process.Id}";
            return process != null;
        }
        catch (Exception ex)
        {
            detail = ($"-control 失败 {ex.GetType().Name}: {ex.Message}").Trim();
            return false;
        }
    }

    /// <summary>本机 ToDesk 协议注册在哪个 exe 上（只读注册表，用于报告/真机清单核对）。</summary>
    public static string? RegisteredProtocolCommand()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"ToDesk\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }
}
