using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class AboutPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public AboutPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
        // 页为 NavigationCacheMode=Required 的常驻实例，订阅一次即可（无需退订）。
        AppState.UpdateStateChanged += OnUpdateStateChanged;
        Render();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            Render();
        });
    }

    private void OnUpdateStateChanged() => _dispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        AboutVersionText.Text = Loc.Tf("About_Version_Format", UpdateService.CurrentVersion);

        var info = AppState.LatestUpdate;
        if (AppState.IsCheckingForUpdates)
        {
            UpdateStatusText.Text = Loc.T("Update_Status_Checking");
        }
        else if (info is { HasUpdate: true })
        {
            UpdateStatusText.Text = string.Join("  ",
                Loc.Tf("About_Update_Available_Format", info.Version),
                Loc.Tf("Update_Dialog_Size_Format", UpdateFlow.FormatBytes(info.FileSize)),
                Loc.Tf("Update_Dialog_Time_Format", UpdateFlow.FormatTime(info.UpdatedAt)));
            UpdateNotesText.Text = info.Notes;
        }
        else if (info is { Available: true })
        {
            UpdateStatusText.Text = Loc.Tf("About_Update_UpToDate_Format", info.Version);
        }
        else if (AppState.UpdateCheckError is { } err)
        {
            UpdateStatusText.Text = Loc.Tf("AppState_Update_CheckFailed_Format", err);
        }
        else
        {
            UpdateStatusText.Text = Loc.T("Update_Status_Idle");
        }

        UpdateNotesText.Visibility = info is { HasUpdate: true } && !string.IsNullOrWhiteSpace(info.Notes)
            ? Visibility.Visible
            : Visibility.Collapsed;
        InstallUpdateButton.Visibility = info is { HasUpdate: true } ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = !AppState.IsCheckingForUpdates;
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionState.Offline)
        {
            AppState.ShowStatus(Loc.T("Update_Offline_Unavailable"), InfoBarSeverity.Warning);
            return;
        }
        await AppState.CheckForUpdatesAsync(isAutomatic: false);
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.LatestUpdate is { HasUpdate: true } info)
        {
            await UpdateFlow.StartAsync(info, () => MainWindow.Instance?.Close());
        }
    }
}
