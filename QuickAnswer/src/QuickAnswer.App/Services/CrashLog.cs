using System.Text;

namespace QuickAnswer.App.Services;

/// <summary>
/// 极简错误日志。写 %LOCALAPPDATA%\QuickAnswer\error.log。
///
/// 存在的理由：WinUI 3 的 XAML 解析异常会被包成一句毫无上下文的
/// "Failed to assign to property ..."，不落盘根本查不出是哪一行。
/// 用户把 error.log 发过来就能定位。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickAnswer",
            "error.log");

    public static void Write(string where, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"位置: {where}");

            if (ex is not null)
            {
                sb.AppendLine($"类型: {ex.GetType().FullName}");
                sb.AppendLine($"消息: {ex.Message}");
                sb.AppendLine($"HResult: 0x{ex.HResult:X8}");
                sb.AppendLine("堆栈:");
                sb.AppendLine(ex.StackTrace);
            }
            else
            {
                sb.AppendLine("(无异常对象)");
            }
            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身失败就放弃，别再往外抛
        }
    }
}
