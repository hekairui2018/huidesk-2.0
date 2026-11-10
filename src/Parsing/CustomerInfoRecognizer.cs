using System.Text;
using System.Text.RegularExpressions;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Parsing;

/// <summary>客户锚（号码 = 手机号/微信号）与客户名称。空串 = 认不准，留空待认领。</summary>
public sealed record CustomerInfo(string Phone, string Name)
{
    public static readonly CustomerInfo Empty = new("", "");
    public bool HasPhone => Phone.Length > 0;
    public bool HasName => Name.Length > 0;
    public bool IsEmpty => !HasPhone && !HasName;
}

public sealed record CustomerInfoResult(CustomerInfo Info, IReadOnlyList<string> Evidence, IReadOnlyList<string> Issues)
{
    public static readonly CustomerInfoResult Empty = new(CustomerInfo.Empty, [], []);
    public bool IsEmpty => Info.IsEmpty;
}

/// <summary>
/// 复用认码那条 PP-OCR 管线（同一个 IOcrProvider 进程）读出的 OcrDocument，做名片解析：
/// 只认「手机号 / 微信号」和「客户名称」，认不准一律留空待认领。
///
/// 铁律（老实红线）：
///   1. 绝不把远程码/设备码/验证码当客户号码——同行或邻行有远程语境词直接排除。
///   2. 多个号码候选分不清时留空，不挑一个凑数。
///   3. 名称只在有明显优势时采纳，候选接近就留空。
///
/// 为什么按行聚合：RapidOCR 按词输出，一个 11 位手机号常被切成 "138" "0000" "0000" 三块，
/// 必须按 Y 聚行、行内按 X 拼接后再匹配，否则永远读不到完整号码。
/// </summary>
public static class CustomerInfoRecognizer
{
    // ---- 标签词 ----
    private static readonly Regex PhoneLabel = new(
        @"手机号|手机号码|手机|电话|联系电话|联系方式|Mobile|Tel(?:ephone)?|Phone",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WechatLabel = new(
        @"微信号|WeChat\s*ID|wxid", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RemoteLabel = new(
        @"识别码|设备码|设备代码|远程码|验证码|临时密码|安全密码|连接码|本机识别|伙伴ID|设备ID",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RemarkLabel = new(
        @"^备注|备注[:：]", RegexOptions.Compiled);

    // 名片之外的界面词，不能当名称
    private static readonly Regex UiNoise = new(
        @"^(微信|WeChat|Weixin|扫一扫|添加|添加好友|加好友|通过验证|发送消息|发消息|语音通话|视频通话|朋友圈|视频号|备注|标签|地区|来源|更多信息|更多|微信号|手机号|手机号码|电话号码|投诉|确定|取消|返回|搜索|通讯录|发现|聊天信息|设置|个人名片|名片|二维码|昵称|名字|群聊|联系人|新的朋友|公众号|转账|红包|表情|图片|位置|收藏|卡包|看一看|购物|游戏|小程序|朋友权限|置顶聊天|消息免打扰|星标朋友|加入黑名单|删除|清空聊天记录|保存到通讯录|分享|复制|转发|编辑|完成|关闭|详情|基本信息|个人信息|详细资料|更多资料|个人资料|基本资料|资料|朋友圈权限|共同群聊|来源途径)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MobileExact = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

    // 远程软件品牌/远程界面标志：出现即认为这不是名片，名称不许拿界面文字凑
    private static readonly Regex RemoteBrand = new(
        @"向日葵|Sunlogin|AweSun|(?<![a-z])ToDesk(?![a-z])|AnyDesk|TeamViewer|RustDesk|UltraViewer|UU远程|GameViewer|远程控制|远程协助|远程桌面",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 微信号：wxid_xxx，或 6-20 位字母开头的字母数字_-（人民币以外的自定义微信号）
    private static readonly Regex WechatIdPattern = new(@"^(?:wxid_[A-Za-z0-9_\-]{3,}|[A-Za-z][A-Za-z0-9_\-]{5,19})$", RegexOptions.Compiled);

    // 强中文名（2-20 字，无数字），用于名称候选
    private static readonly Regex ChineseNamePattern = new(@"^[\p{IsCJKUnifiedIdeographs}A-Za-z·•\.\s]{2,20}$", RegexOptions.Compiled);

    /// <summary>解析一份已经 OCR 出来的文档；不触发任何 OCR。</summary>
    public static CustomerInfoResult Parse(OcrDocument? document)
    {
        if (document?.Blocks is not { Count: > 0 }) return CustomerInfoResult.Empty;
        if (document.Width <= 0 || document.Height <= 0) return CustomerInfoResult.Empty;

        var lines = BuildLines(document);
        if (lines.Count == 0) return CustomerInfoResult.Empty;

        var evidence = new List<string>();
        var issues = new List<string>();

        var phone = ExtractPhone(lines, evidence, issues);
        var name = ExtractName(lines, document.Height, phone, evidence, issues);

        if (phone.Length == 0 && name.Length == 0)
            issues.Add("未识别到客户号码和名称，可手动填写或重截名片");

        return new CustomerInfoResult(new CustomerInfo(phone, name), evidence, issues);
    }

    // ==================== 号码 ====================

    private static string ExtractPhone(List<Line> lines, List<string> evidence, List<string> issues)
    {
        var hits = new List<(string Value, int LineIndex, bool Labelled, bool RemoteContext, bool IsWechat)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (text.Length == 0) continue;

            bool remote = RemoteLabel.IsMatch(text);
            bool phoneLabel = PhoneLabel.IsMatch(text);
            bool wechatLabel = WechatLabel.IsMatch(text);

            foreach (var mobile in FindMobiles(text))
                hits.Add((mobile, i, phoneLabel, remote || NearMatch(lines, i, RemoteLabel), false));

            // 微信号：有「微信号」标签，或 raw 形如 wxid_xxx
            if (wechatLabel || WechatIdPattern.IsMatch(text.Trim()))
            {
                var id = ExtractWechatId(text);
                if (id.Length > 0 && !phoneLabel)
                    hits.Add((id, i, wechatLabel, remote || NearMatch(lines, i, RemoteLabel), true));
            }
        }

        if (hits.Count == 0) return "";

        // 1) 远程语境里的号码一律排除（防止把设备码/验证码当客户号码）
        var clean = hits.Where(h => !h.RemoteContext).ToList();
        if (clean.Count == 0)
        {
            issues.Add("读到的号码疑似远程码/验证码，已排除，请手动确认客户号码");
            return "";
        }

        // 2) 优先手机号（能打电话才是好锚），再退微信号
        var labelledPhone = clean.Where(h => h.Labelled && !h.IsWechat).ToList();
        if (labelledPhone.Count > 0)
        {
            var pick = labelledPhone[0];
            evidence.Add("手机号（带标签）：" + pick.Value);
            return pick.Value;
        }
        var labelledWechat = clean.Where(h => h.Labelled && h.IsWechat).ToList();
        if (labelledWechat.Count > 0)
        {
            evidence.Add("微信号（带标签）：" + labelledWechat[0].Value);
            return labelledWechat[0].Value;
        }

        // 3) 无标签但全图唯一手机号，且没有任何远程语境 → 采纳但标注依据
        var distinctMobiles = clean.Where(h => !h.IsWechat).Select(h => h.Value).Distinct().ToList();
        if (distinctMobiles.Count == 1 && !hits.Any(h => h.RemoteContext))
        {
            evidence.Add("全图唯一手机号：" + distinctMobiles[0]);
            return distinctMobiles[0];
        }

        // 4) 分不清 → 留空（不挑一个凑数）
        issues.Add("有 " + clean.Select(h => h.Value).Distinct().Count() + " 个号码候选分不清，请手动确认");
        return "";
    }

    private static IEnumerable<string> FindMobiles(string text)
    {
        // 先去掉分隔符（空格、横线、间隔点），保留可读断点
        var compact = Regex.Replace(text, @"[\s\-·—–_]+", "");
        foreach (Match m in MobileExact.Matches(compact))
            yield return m.Value;

        // 直读不到再做形近折叠（仅对"数字占多数"的片段，避免把英文词折叠成号码）
        foreach (Match token in Regex.Matches(compact, @"[0-9０-９A-Za-z|!！〇○]{10,13}"))
        {
            var raw = token.Value;
            int digits = raw.Count(char.IsAsciiDigit);
            if (digits < 6 || digits * 2 < raw.Length) continue; // 数字不过半不折叠
            var folded = FoldDigits(raw);
            foreach (Match m in MobileExact.Matches(folded))
                if (!compact.Contains(m.Value)) yield return m.Value; // 已直读到的不要重复返回
        }
    }

    private static string FoldDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '０' => '0', '１' => '1', '２' => '2', '３' => '3', '４' => '4',
                '５' => '5', '６' => '6', '７' => '7', '８' => '8', '９' => '9',
                'O' or 'o' or '〇' or '○' => '0',
                'l' or 'I' or 'i' or '|' or '！' or '!' => '1',
                'Z' or 'z' => '2',
                'S' or 's' => '5',
                'B' => '8',
                _ => ch
            });
        }
        return sb.ToString();
    }

    private static string ExtractWechatId(string text)
    {
        // 标签后的主体：微信号：abc123 / 微信号 abc123 / wxid_xxx
        var m = Regex.Match(text, @"(?:微信号|WeChat\s*ID)[:：\s]*([A-Za-z0-9_\-]{3,32})", RegexOptions.IgnoreCase);
        if (m.Success && !Regex.IsMatch(m.Groups[1].Value, @"^\d+$")) return m.Groups[1].Value;
        m = Regex.Match(text, @"wxid_[A-Za-z0-9_\-]{3,}", RegexOptions.IgnoreCase);
        if (m.Success) return m.Value;
        return "";
    }

    // ==================== 名称 ====================

    private static string ExtractName(List<Line> lines, int documentHeight, string phone, List<string> evidence, List<string> issues)
    {
        // 远程码界面（有品牌词或识别码/验证码标签）不是名片：界面上的品牌名、按钮字都不是客户名称。
        var allText = string.Join("\n", lines.Select(l => l.Text));
        if ((RemoteBrand.IsMatch(allText) || RemoteLabel.IsMatch(allText)) && !PhoneLabel.IsMatch(allText))
        {
            if (!string.IsNullOrEmpty(phone) || RemoteBrand.IsMatch(allText))
                issues.Add("像是远程码界面，未把界面文字当客户名称");
            return "";
        }
        // 1) 「备注」标签优先级最高 —— 客服自己改过的名字比微信昵称更可用
        for (int i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (!RemarkLabel.IsMatch(text)) continue;
            var value = Regex.Replace(text, @"^备注[:：]?\s*", "").Trim();
            if (IsNameCandidate(value, phone))
            {
                evidence.Add("备注名：" + value);
                return value;
            }
        }

        // 2) 昵称候选：字号最大 + 最靠上，排除界面词/标签词/号码
        var scored = new List<(string Value, double Score, int Index)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var value = line.Text.Trim();
            if (!IsNameCandidate(value, phone)) continue;
            // 顶部栏标题（微信「详细资料」「聊天信息」等）不是名字
            if (line.CenterY < documentHeight * 0.10) continue;

            double height = line.Blocks.Max(b => b.Bounds.Height);
            if (height <= 0) continue;
            // 靠上加权：名片昵称基本在最上方 1/3（头像右侧第一行，字号最大）
            double yBonus = line.CenterY < documentHeight * 0.45 ? 1.35 : 1.0;
            scored.Add((value, height * yBonus, i));
        }

        if (scored.Count == 0) return "";

        var best = scored.OrderByDescending(s => s.Score).ToList();
        // 3) 只在明显优势时采纳：最高分必须显著高于次高，否则留空
        if (best.Count > 1 && best[1].Score > 0 && best[0].Score < best[1].Score * 1.20)
        {
            issues.Add("名称候选不明确（可能是「" + best[0].Value + "」或「" + best[1].Value + "」），已留空");
            return "";
        }

        evidence.Add("名称（字号最大）：" + best[0].Value);
        return best[0].Value;
    }

    private static bool IsNameCandidate(string value, string phone)
    {
        if (value.Length < 2 || value.Length > 20) return false;
        if (UiNoise.IsMatch(value)) return false;
        if (PhoneLabel.IsMatch(value) || WechatLabel.IsMatch(value) || RemoteLabel.IsMatch(value) || RemarkLabel.IsMatch(value)) return false;
        if (value == phone) return false;
        if (MobileExact.IsMatch(value)) return false;
        // 至少含一个中文或字母，且数字占比 < 30%（"1380000"、"2026-11-10" 这类不是名字）
        int letters = value.Count(c => char.IsLetter(c));
        int digitsInName = value.Count(char.IsAsciiDigit);
        if (letters < 2) return false;
        if (digitsInName > 0 && digitsInName * 3 > value.Length) return false;
        return ChineseNamePattern.IsMatch(value);
    }

    // ==================== 行聚合 ====================

    private sealed class Line
    {
        public List<OcrTextBlock> Blocks { get; } = [];
        public double CenterY => Blocks.Count == 0 ? 0 : Blocks.Average(b => b.Bounds.CenterY);
        public double Height => Blocks.Count == 0 ? 0 : Blocks.Max(b => b.Bounds.Height);
        public double X => Blocks.Count == 0 ? 0 : Blocks.Min(b => b.Bounds.X);
        public string Text { get; private set; } = "";

        public void Add(OcrTextBlock block) { Blocks.Add(block); Text = Join(Blocks); }

        private static string Join(List<OcrTextBlock> blocks)
        {
            var ordered = blocks.OrderBy(b => b.Bounds.X).ToList();
            var sb = new StringBuilder();
            double lastRight = double.NaN;
            foreach (var b in ordered)
            {
                var text = b.Text;
                if (text.Length == 0) continue;
                if (sb.Length > 0 && !double.IsNaN(lastRight))
                {
                    double gap = b.Bounds.X - lastRight;
                    double h = Math.Max(1, Math.Min(b.Bounds.Height, blocks.Max(x => x.Bounds.Height)));
                    // 间距小于 0.3 倍字高 = 同一串被切开的词，直连；否则留空格（中文词间自然断开）
                    if (gap > h * 0.3) sb.Append(' ');
                }
                sb.Append(text);
                lastRight = b.Bounds.Right;
            }
            return sb.ToString();
        }
    }

    private static List<Line> BuildLines(OcrDocument document)
    {
        var lines = new List<Line>();
        foreach (var block in document.Blocks
                     .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                     .OrderBy(b => b.Bounds.CenterY).ThenBy(b => b.Bounds.X))
        {
            Line? target = null;
            double bestDelta = double.MaxValue;
            foreach (var line in lines)
            {
                double delta = Math.Abs(line.CenterY - block.Bounds.CenterY);
                double tolerance = Math.Max(6, Math.Min(line.Height, block.Bounds.Height) * 0.6);
                if (delta <= tolerance && delta < bestDelta) { target = line; bestDelta = delta; }
            }
            if (target == null) { var line = new Line(); line.Add(block); lines.Add(line); }
            else target.Add(block);
        }
        return lines.OrderBy(l => l.CenterY).ToList();
    }

    private static bool NearMatch(List<Line> lines, int index, Regex pattern)
    {
        for (int d = -1; d <= 1; d++)
        {
            int j = index + d;
            if (j < 0 || j >= lines.Count || j == index) continue;
            if (pattern.IsMatch(lines[j].Text)) return true;
        }
        return false;
    }
}
