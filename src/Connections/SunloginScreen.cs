using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.V2.Connections;

/// <summary>观察型 OCR 看到的向日葵界面状态。只判「界面走到哪一步」，不取坐标、不点按。</summary>
public enum SunloginScreenState { Unknown, MainPage, PasswordPrompt, Waiting, Connected, Failure }

/// <summary>
/// 步E：向日葵「已发起」的判据。
/// 按了回车不等于连上——识别码错时界面不前进、错误提示会看得见。所以要用界面文字当证据：
/// 进密码框 / 进等待界面 / 报错 / 还停在主界面，四种情况各给一句话。
/// 这是「观察型 OCR」，与认码、名片识别同属允许保留的一类；找框点按那类已经退役。
/// </summary>
public static class SunloginScreen
{
    private static readonly string[] FailureWords =
    {
        "不在线", "不存在", "验证码错误", "密码错误", "连接失败", "无法连接", "已离线",
        "设备离线", "识别码错误", "验证失败", "无人接听", "拒绝连接", "已断开", "连接已关闭"
    };

    // 只有在「不是主界面」时才认「验证码」这种弱字样，避免把主界面的「验证码（可为空）」当成密码框
    private static readonly string[] PasswordStrong = { "输入连接密码", "请输入密码", "输入密码", "连接密码" };
    private static readonly string[] PasswordWeak = { "请输入验证码", "输入验证码" };

    private static readonly string[] MainWords = { "伙伴识别码", "远程协助他人", "协助他人", "远程控制设备", "本机识别码" };
    private static readonly string[] WaitingWords = { "正在连接", "连接中", "正在建立", "等待对方", "等待授权", "正在验证" };
    private static readonly string[] ConnectedWords = { "已连接", "连接成功", "会话已连接" };

    /// <summary>按优先级判：报错 → 密码框 → 等待 → 主界面 → 已连上 → 认不出。</summary>
    public static (SunloginScreenState State, string Reason) Classify(OcrDoc? document)
    {
        var flat = document?.FlatText ?? "";
        if (flat.Length == 0) return (SunloginScreenState.Unknown, "没读到界面文字");

        var failure = FailureWords.FirstOrDefault(flat.Contains);
        if (failure != null) return (SunloginScreenState.Failure, failure);

        var strong = PasswordStrong.FirstOrDefault(flat.Contains);
        if (strong != null) return (SunloginScreenState.PasswordPrompt, strong);

        bool mainVisible = MainWords.Any(flat.Contains);
        if (!mainVisible)
        {
            var weak = PasswordWeak.FirstOrDefault(flat.Contains);
            if (weak != null) return (SunloginScreenState.PasswordPrompt, weak);
            var waiting = WaitingWords.FirstOrDefault(flat.Contains);
            if (waiting != null) return (SunloginScreenState.Waiting, waiting);
        }
        else
        {
            // 主界面在，但可能盖着一条「正在连接」的进度提示；此时仍算进了等待
            var waiting = WaitingWords.FirstOrDefault(flat.Contains);
            if (waiting != null) return (SunloginScreenState.Waiting, waiting);
            return (SunloginScreenState.MainPage, "界面还在输入设备码这一步");
        }

        var connected = ConnectedWords.FirstOrDefault(flat.Contains);
        if (connected != null) return (SunloginScreenState.Connected, connected);

        return (SunloginScreenState.Unknown, "界面既不像主界面也不像等待");
    }
}
