using System.Text.Json.Serialization;

namespace QuickAnswer.Core.Models;

/// <summary>题型。AI 返回的 type 优先于本地正则判定。</summary>
public enum QuestionType
{
    Unknown = 0,
    Choice,   // 单选
    Multi,    // 多选
    Judge,    // 判断
    Fill,     // 填空
    Short,    // 简答
}

public sealed class OptionItem
{
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>该选项在屏幕上的矩形（物理像素，虚拟桌面坐标）。用于隐蔽模式画三角。</summary>
    [JsonIgnore]
    public ScreenRect? Rect { get; set; }

    public override string ToString() => $"{Key}. {Text}";
}

/// <summary>矩形，物理像素，虚拟桌面坐标系（左上角可能是负值）。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public int CenterY => Y + Height / 2;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>一次 OCR 的结构化结果。</summary>
public sealed class ParsedQuestion
{
    public QuestionType Type { get; set; } = QuestionType.Unknown;
    public string Stem { get; set; } = "";
    public List<OptionItem> Options { get; set; } = new();
    public string RawOcr { get; set; } = "";

    /// <summary>题干所在行的矩形，用于填空题在下方写答案。</summary>
    [JsonIgnore]
    public ScreenRect? StemRect { get; set; }

    /// <summary>整块题目区域的矩形。</summary>
    [JsonIgnore]
    public ScreenRect? BlockRect { get; set; }

    public bool HasOptions => Options.Count >= 2;
}

/// <summary>最终呈现给用户的答案。</summary>
public sealed class AnswerResult
{
    public QuestionType Type { get; set; } = QuestionType.Unknown;

    /// <summary>选择题的答案字母（已按本地选项顺序重映射）。多选用逗号分隔，如 "A,C"。</summary>
    public string Letter { get; set; } = "";

    /// <summary>答案文本。填空/简答的正文；选择题的正确选项原文。</summary>
    public string Text { get; set; } = "";

    public double Confidence { get; set; }
    public string Source { get; set; } = "";     // "题库" / "AI"
    public string Detail { get; set; } = "";     // "相似度 96% · 12ms" / "gpt-4o-mini · 1.3s"
    public long ElapsedMs { get; set; }
    public bool LowConfidence { get; set; }

    /// <summary>命中的本地选项（隐蔽模式据此画三角）。多选可能有多个。</summary>
    [JsonIgnore]
    public List<OptionItem> TargetOptions { get; set; } = new();

    /// <summary>
    /// 是不是「得自己把答案打进答题框」的题：填空、简答。
    ///
    /// 判断/单选/多选不算 —— 它们的产物是一个字母，用户点选项就行，
    /// 自动复制只会把剪贴板搞脏（还要连带毁掉用户自己复制的东西）。
    ///
    /// Type 未知时（视觉模型没给 type、或者本地没判出来）退化成看形状：
    /// 没有字母、但有正文，那基本就是自由作答。
    /// </summary>
    [JsonIgnore]
    public bool IsSubjective => Type switch
    {
        QuestionType.Fill or QuestionType.Short => true,
        QuestionType.Choice or QuestionType.Multi or QuestionType.Judge => false,
        _ => (Letter ?? "").Trim().Length == 0 && (Text ?? "").Trim().Length > 0,
    };

    /// <summary>
    /// 该放进剪贴板的那段字。
    ///
    /// 主观题只给正文（去掉「答案：」这类抬头），因为用户要的是答案本身；
    /// 客观题优先给字母 —— 外面粘贴到聊天框里一个 "A" 比一整句选项原文更好用。
    /// </summary>
    [JsonIgnore]
    public string CopyText
    {
        get
        {
            var text = StripPrefix(Text);
            var letter = (Letter ?? "").Trim();

            if (IsSubjective)
                return text.Length > 0 ? text : letter;

            return letter.Length > 0 ? letter : text;
        }
    }

    /// <summary>模型偶尔会把「答案：xxx」「答: xxx」一起写进 answer_text，贴过去很碍眼。</summary>
    private static string StripPrefix(string? raw)
    {
        var s = (raw ?? "").Trim();
        foreach (var prefix in new[] { "答案：", "答案:", "答：", "答:", "参考答案：", "参考答案:" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal))
                return s[prefix.Length..].Trim();
        }

        return s;
    }

    public static AnswerResult Fail(string reason) => new() { Source = "", Text = "", Detail = reason };
}

// ---------------- 网络契约 ----------------

public sealed class SearchRequest
{
    [JsonPropertyName("stem")] public string Stem { get; set; } = "";
    [JsonPropertyName("options")] public List<OptionItem> Options { get; set; } = new();
    [JsonPropertyName("type")] public string Type { get; set; } = "choice";
    [JsonPropertyName("raw_ocr")] public string RawOcr { get; set; } = "";
}

public sealed class SearchResponse
{
    [JsonPropertyName("hit")] public bool Hit { get; set; }
    [JsonPropertyName("question_id")] public long QuestionId { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("answer_letter")] public string? AnswerLetter { get; set; }
    [JsonPropertyName("answer_text")] public string? AnswerText { get; set; }
    [JsonPropertyName("option_map")] public Dictionary<string, string>? OptionMap { get; set; }
    [JsonPropertyName("confidence")] public string? Confidence { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ApiAnswerDto
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("answer_letter")] public string? AnswerLetter { get; set; }
    [JsonPropertyName("answer_text")] public string? AnswerText { get; set; }
    [JsonPropertyName("explain")] public string? Explain { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

public sealed class SolveResponse
{
    [JsonPropertyName("result")] public ApiAnswerDto? Result { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

public sealed class ContributeRequest
{
    [JsonPropertyName("stem")] public string Stem { get; set; } = "";
    [JsonPropertyName("options")] public List<OptionItem> Options { get; set; } = new();
    [JsonPropertyName("type")] public string Type { get; set; } = "choice";
    [JsonPropertyName("answer_letter")] public string? AnswerLetter { get; set; }
    [JsonPropertyName("answer_text")] public string AnswerText { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "ai";
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("raw_ocr")] public string RawOcr { get; set; } = "";
}

public sealed class ModelInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    public override string ToString() => Name;
}

public sealed class LoginResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("user")] public UserDto? User { get; set; }
}

public sealed class UserDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
    [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; }
    [JsonPropertyName("level")] public int Level { get; set; }
}

/// <summary>后端统一返回的错误体 {"detail": "..."}。</summary>
public sealed class ErrorDto
{
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}
