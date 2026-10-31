using System.Text.Json;
using System.Text.Json.Serialization;
using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Storage;

/// <summary>
/// 本地配置。存放于 %LOCALAPPDATA%\QuickAnswer\config.json。
///
/// 说明：登录 token 是明文存的。这里权衡过 —— DPAPI 能加一层，但要额外引一个包、
/// 且在同一用户下运行的任何程序都能解开，实际防护收益很低。真正的防线是服务端
/// JWT 的 7 天有效期。如果后续要发给别人用，再补 DPAPI。
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- 服务器与账号 ----
    /// <summary>内置服务器地址。老版本配置里的 127.0.0.1 会在加载时自动迁移到这个地址。</summary>
    public const string DefaultServerUrl = "https://chat.teatt.top";

    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string? Token { get; set; }
    public string? Username { get; set; }
    public string? Nickname { get; set; }
    public string? ModelId { get; set; }

    // ---- 识别 ----
    public ScreenRect? Region { get; set; }

    /// <summary>检测间隔（毫秒）。200–5000。</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>换题判定：新文本与上一题的相似度低于此值即认为换题。</summary>
    public double ChangeThreshold { get; set; } = 0.70;

    /// <summary>题库命中阈值：低于此值改问 AI，宁慢不给错。</summary>
    public double HitThreshold { get; set; } = 0.85;

    /// <summary>OCR 前的放大倍数。小字识别率靠它，不要低于 1.5。</summary>
    public double OcrScale { get; set; } = 2.0;

    // ---- 呈现 ----
    /// <summary>true = 隐蔽模式（在题目上直接标注）；false = 悬浮窗。</summary>
    public bool StealthMode { get; set; }

    public double OverlayOpacity { get; set; } = 0.30;

    public bool ClickThrough { get; set; } = true;

    /// <summary>窗口从屏幕捕获中排除。关掉它才能被 OBS/极域录到。</summary>
    public bool ProtectFromCapture { get; set; } = true;

    public int OverlayLeft { get; set; } = -1;   // -1 = 用默认右下角
    public int OverlayTop { get; set; } = -1;

    // ---- 运行状态（不参与界面展示，但值得记住）----
    public bool AutoLoop { get; set; }
    public int TodayCount { get; set; }
    public int TodayHit { get; set; }
    public string TodayDate { get; set; } = "";

    // ---------------------------------------------------------------- 读写

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickAnswer");

    public static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts);
                if (cfg is not null)
                {
                    // 早期版本的默认值，用户没改过就别让他一直连本机
                    if (string.IsNullOrWhiteSpace(cfg.ServerUrl)
                        || cfg.ServerUrl.Contains("127.0.0.1", StringComparison.Ordinal)
                        || cfg.ServerUrl.Contains("localhost", StringComparison.Ordinal))
                    {
                        cfg.ServerUrl = DefaultServerUrl;
                    }
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时不要让程序起不来，退回默认值
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch
        {
        }
    }

    /// <summary>跨天重置每日统计。</summary>
    public void RollDayIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (TodayDate != today)
        {
            TodayDate = today;
            TodayCount = 0;
            TodayHit = 0;
        }
    }
}
