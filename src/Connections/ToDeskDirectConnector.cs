using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Connections;

public sealed record ToDeskLaunch(bool Started, ToDeskRoute Route, string Detail, string Url, string Arguments);

/// <summary>
/// 步E·ToDesk 直连：URL 主 → 协议处理器 → <c>-control</c> 备。
/// 只负责「发出去」；连没连上由 <see cref="SessionObserver"/> 判（会话窗＋标题带码＋断开按钮），
/// 一代那套「点连接后全屏 OCR 轮询找红字」不再进入连接环节。
/// 「失败」在这里只表示三条路都没能把进程/协议叫起来；起得来但对方没响应，会由后续观察判成「尚未确认」。
/// </summary>
public static class ToDeskDirectConnector
{
    public static ToDeskLaunch Launch(string exePath, string deviceCode, string password)
    {
        var url = ToDeskLink.BuildUrl(deviceCode, password) ?? "";
        var arguments = ToDeskLink.Describe(deviceCode, password);
        var problems = new List<string>();

        if (url.Length > 0)
        {
            if (ToDeskLink.TryLaunchUrlArgument(exePath, url, out var urlDetail))
                return new(true, ToDeskRoute.UrlArgument, urlDetail, url, arguments);
            problems.Add("URL 传参：" + urlDetail);

            if (ToDeskLink.TryLaunchProtocol(url, out var shellDetail))
                return new(true, ToDeskRoute.ProtocolShell, shellDetail, url, arguments);
            problems.Add("协议处理器：" + shellDetail);
        }
        else problems.Add("设备码无效，URL 未构造");

        if (ToDeskLink.TryLaunchControl(exePath, ToDeskLink.BuildControlArguments(deviceCode, password), out var controlDetail))
            return new(true, ToDeskRoute.ControlCommand, controlDetail, url, arguments);
        problems.Add("-control：" + controlDetail);

        return new(false, ToDeskRoute.None, string.Join("；", problems), url, arguments);
    }

    /// <summary>真机清单用：把三条路要发的原文打出来，不发。</summary>
    public static string Explain(string deviceCode, string password)
    {
        var url = ToDeskLink.BuildUrl(deviceCode, password) ?? "(设备码无效，未构造)";
        var arguments = ToDeskLink.Describe(deviceCode, password);
        return $"URL: {url}\n- 参数: {arguments}\n- 协议注册: {ToDeskLink.RegisteredProtocolCommand() ?? "(未注册)"}";
    }

    public static async Task<ConnectResult> ConnectAsync(string exePath, string deviceCode, string password, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var launch = await Task.Run(() => Launch(exePath, deviceCode, password), token).ConfigureAwait(false);
        Log.Write($"ToDesk 命令行 route={launch.Route} started={launch.Started} detail={launch.Detail}");
        if (!launch.Started)
            return ConnectResult.Fail("ToDesk 命令行没叫起来，请手动在客户端点连接；详情已写入日志");

        // 让客户端有一点时间把窗口拉起来，再交给会话观察器；这里不判成功。
        await Task.Delay(600, token).ConfigureAwait(false);
        return ConnectResult.Ok(launch.Route switch
        {
            ToDeskRoute.UrlArgument => "已用命令行把设备码发给 ToDesk（URL），正在确认会话",
            ToDeskRoute.ProtocolShell => "已通过 ToDesk:// 协议发起，正在确认会话",
            ToDeskRoute.ControlCommand => "已用 -control 发起，正在确认会话",
            _ => "已发起 ToDesk 连接，正在确认会话"
        });
    }
}
