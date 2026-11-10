using System.Text.Json;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>
/// 本周关联令牌：每次连接分配一个 4 位顺序号（0001 起），本周内唯一，跨周归零。
///
/// 老实红线（经理审定第 2 条）：
///   · 这是「第几次连接」的顺序号，不是按日期/周次派生出来的码 —— 不允许任何人反推算法。
///   · 令牌只由灰Desk 本地生成并显示，靠人工念给客服/小灰匠；小灰匠一律不推导、不自造令牌。
///
/// 落盘：%AppData%/GrayDesk20/week-token.json = {"weekStart":"yyyy-MM-dd","counter":N}
/// 周起点按周一 00:00（本地时区）计。写盘失败时退回内存计数，仍保证本周内递增不重复。
/// </summary>
internal static class WeekToken
{
    private static readonly object Gate = new();
    private static State _state = new();
    private static bool _loaded;
    private static bool _overflowLogged;

    private sealed class State
    {
        public string WeekStart { get; set; } = "";
        public int Counter { get; set; }
    }

    private static string FilePath => Path.Combine(Log.Root, "week-token.json");

    /// <summary>本周起点（周一 00:00，本地时区）。</summary>
    internal static DateTime WeekStartOf(DateTime local)
    {
        var d = local.Date;
        int offset = ((int)d.DayOfWeek + 6) % 7; // 周一=0 … 周日=6
        return d.AddDays(-offset);
    }

    /// <summary>取下一个令牌（4 位补零）。调用一次 = 分配一个号码。</summary>
    internal static string Next()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var currentWeek = WeekStartOf(DateTime.Now).ToString("yyyy-MM-dd");
            if (!string.Equals(_state.WeekStart, currentWeek, StringComparison.Ordinal))
            {
                // 跨周（或首次运行）：本周从 0001 重新起
                _state.WeekStart = currentWeek;
                _state.Counter = 0;
            }
            _state.Counter = Math.Max(0, _state.Counter) + 1;
            if (_state.Counter > 9999 && !_overflowLogged)
            {
                _overflowLogged = true;
                Log.Write("week-token 超出 4 位（本周已过 9999 次连接），后续按实际位数显示，仍保证唯一");
            }
            Save();
            return Format(_state.Counter);
        }
    }

    /// <summary>当前周起点（供界面提示本周范围用）。</summary>
    internal static string CurrentWeekStartText() => WeekStartOf(DateTime.Now).ToString("yyyy-MM-dd");

    private static string Format(int n) => n <= 9999 ? n.ToString("D4") : n.ToString();

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
            if (loaded != null && !string.IsNullOrWhiteSpace(loaded.WeekStart)) _state = loaded;
        }
        catch (Exception ex) { Log.Write("week-token 读取失败 " + ex.GetType().Name); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex) { Log.Write("week-token 写入失败 " + ex.GetType().Name); }
    }
}
