using System.Text.Json;

namespace RemoteConnectHelper.V2.Infrastructure;

public sealed class Preferences
{
    public List<RemoteConnectHelper.V2.Discovery.SoftwareEntry>? SoftwareEntries {get;set;}
    public string? WallpaperPath {get;set;}
    public string? LastWallpaperFolder {get;set;}
    public double[]? WallpaperPickerBounds {get;set;}
    public string Theme {get;set;}="Light";
    public bool AutoConnect { get; set; } = true;
    public bool WatchClipboard { get; set; } = true;
    public bool ClipboardConsent { get; set; }
    public bool ShowFloatingBall { get; set; } = true;
    public string? LastImageFolder {get;set;}
    public double[]? ImagePickerBounds {get;set;}
    public double FloatingOpacity { get; set; } = .85;
    public double EffectiveFloatingOpacity => double.IsFinite(FloatingOpacity)?Math.Clamp(FloatingOpacity,.1,1):.85;

    /// <summary>
    /// 步E：老「OCR 找框＋点按」连接路径。默认关（新通道：向日葵剪贴板接力 / ToDesk 命令行）。
    /// 留着这一个开关，是为了某个客户端版本把两条新路都堵死时，客服能自己一键退回，不必等出包。
    /// </summary>
    public bool UseLegacyClickPath { get; set; }
    /// <summary>向日葵等人上限（秒）：人可能去接电话，所以给 3 分钟，超时只提示、不关窗。</summary>
    public int RelayWaitSeconds { get; set; } = 180;
    public int EffectiveRelayWaitSeconds => RelayWaitSeconds is >= 30 and <= 600 ? RelayWaitSeconds : 180;
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Log.Root, "preferences.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Log.Root); File.WriteAllText(Path.Combine(Log.Root, "preferences.json"), JsonSerializer.Serialize(this)); }
        catch { }
    }
}
