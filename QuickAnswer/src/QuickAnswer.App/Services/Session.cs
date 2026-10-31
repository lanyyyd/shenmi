using QuickAnswer.Core.Models;

namespace QuickAnswer.App.Services;

/// <summary>当前登录态。只在内存里，持久化由 AppConfig 负责。</summary>
public sealed class Session
{
    public bool IsSignedIn { get; private set; }
    public UserDto? User { get; private set; }
    public string? Token { get; private set; }

    public string DisplayName =>
        User?.Nickname is { Length: > 0 } n ? n
        : User?.Username is { Length: > 0 } u ? u
        : "未登录";

    public void SignIn(UserDto user, string token)
    {
        User = user;
        Token = token;
        IsSignedIn = true;
    }

    public void SignOut()
    {
        User = null;
        Token = null;
        IsSignedIn = false;
    }
}
