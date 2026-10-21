using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamEyaWinUI.Pages;

/// <summary>在线设备列表项（昵称 + IP，双击发起协助）。</summary>
public sealed class AssistPeerVm
{
    public AssistPeerVm(string name, string ip, int port, string hint)
    {
        Name = name;
        Ip = ip;
        Port = port;
        Hint = hint;
        Endpoint = $"{ip}:{port}";
    }

    public string Name { get; }

    public string Ip { get; }

    public int Port { get; }

    public string Endpoint { get; }

    /// <summary>右侧提示文案（"双击发起" / "未开启远程协助"）。</summary>
    public string Hint { get; }
}

/// <summary>
/// 协助申请列表项。状态由 <see cref="Apply"/> 统一推导，保证"按钮只可能是 同意 / 失效"两种形态。
/// </summary>
public sealed class AssistRequestVm : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _detailText = string.Empty;
    private string _statusText = string.Empty;
    private string _buttonText = string.Empty;
    private bool _canApprove;

    public AssistRequestVm(string requestId)
    {
        RequestId = requestId;
    }

    public string RequestId { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>只可能是"同意"或"失效"。</summary>
    public string ButtonText
    {
        get => _buttonText;
        private set => Set(ref _buttonText, value);
    }

    /// <summary>只有待处理的邀请可点。</summary>
    public bool CanApprove
    {
        get => _canApprove;
        private set => Set(ref _canApprove, value);
    }

    public void Apply(string title, string detail, string status, string button, bool canApprove)
    {
        Title = title;
        DetailText = detail;
        StatusText = status;
        ButtonText = button;
        CanApprove = canApprove;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
