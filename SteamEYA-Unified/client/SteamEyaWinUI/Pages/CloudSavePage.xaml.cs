using System.Collections.ObjectModel;
using System.IO.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

/// <summary>云存档页：备份/还原植物大战僵尸（含杂交版新版）与我的世界存档，并支持共享。</summary>
public sealed partial class CloudSavePage : Page
{
    // 植物大战僵尸存档目录（自动备份与还原的目标）。
    private const string PvzSaveDir = @"C:\ProgramData\PopCap Games\PlantsVsZombies\pvzHE\yourdata";

    // 植物大战僵尸杂交版新版（Godot 内核）：存档在用户 AppData 下的 Godot 用户数据目录。
    private static readonly string PvzHybridSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Godot", "app_userdata", "植物大战僵尸杂交版", "Csharp");

    private readonly CloudSaveService _service = new();
    private readonly ObservableCollection<CloudSaveItem> _mySaves = [];
    private readonly ObservableCollection<CloudSaveItem> _sharedSaves = [];

    private string _game = "pvz";

    public CloudSavePage()
    {
        InitializeComponent();
        MyList.ItemsSource = _mySaves;
        SharedList.ItemsSource = _sharedSaves;
        GameCombo.SelectedIndex = 0;
        UpdateAutoBackupState();

        Loaded += async (_, _) => await ReloadAsync();
    }

    private static string GameName(string game) => game switch
    {
        "minecraft" => "我的世界",
        "pvzhybrid" => "植物大战僵尸杂交版新版",
        _ => "植物大战僵尸"
    };

    /// <summary>该游戏的固定本地存档目录（可自动备份 / 自动还原）；我的世界没有固定目录，返回 null。</summary>
    private static string? FixedSaveDir(string game) => game switch
    {
        "pvz" => PvzSaveDir,
        "pvzhybrid" => PvzHybridSaveDir,
        _ => null
    };

    // ---------------- 数据加载 ----------------
    private async Task ReloadAsync()
    {
        try
        {
            var mine = await _service.ListAsync(_game);
            _mySaves.Clear();
            foreach (var item in mine.Saves ?? [])
            {
                _mySaves.Add(item);
            }
            MyEmptyText.Visibility = _mySaves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var shared = await _service.ListSharedAsync(_game);
            _sharedSaves.Clear();
            foreach (var item in shared.Saves ?? [])
            {
                _sharedSaves.Add(item);
            }
            SharedEmptyText.Visibility = _sharedSaves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (mine.Ok == false && !string.IsNullOrEmpty(mine.Msg))
            {
                ShowInfo(mine.Msg!, InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"加载存档失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            UpdateSelectionButtons();
        }
    }

    private void OnGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _game = tag;
        }
        UpdateAutoBackupState();
        _ = ReloadAsync();
    }

    /// <summary>“自动备份”仅支持有固定存档目录的游戏（植物大战僵尸 / 杂交版新版）；我的世界置灰并给出提示。</summary>
    private void UpdateAutoBackupState()
    {
        if (FixedSaveDir(_game) is not null)
        {
            AutoBackupBtn.IsEnabled = true;
            ToolTipService.SetToolTip(AutoBackupBtn, null);
        }
        else
        {
            AutoBackupBtn.IsEnabled = false;
            ToolTipService.SetToolTip(AutoBackupBtn, "该功能暂不可用 试着拖入存档压缩包上传吧");
        }
    }

    private void OnMySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

    private void UpdateSelectionButtons()
    {
        var count = MyList.SelectedItems.Count;
        DownloadBtn.IsEnabled = count > 0;
        ShareBtn.IsEnabled = count > 0;
    }

    // ---------------- 自动备份 ----------------
    private async void OnAutoBackup(object sender, RoutedEventArgs e)
    {
        var saveDir = FixedSaveDir(_game);
        if (saveDir is null)
        {
            ShowInfo("该功能暂不可用 试着拖入存档压缩包上传吧", InfoBarSeverity.Warning);
            return;
        }
        if (!Directory.Exists(saveDir))
        {
            ShowInfo($"未找到存档目录：{saveDir}", InfoBarSeverity.Warning);
            return;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"eya_pvz_{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(saveDir, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            var result = await _service.UploadAsync(_game, tempZip, null, null, "auto");
            if (result.Ok)
            {
                ShowInfo("自动备份成功", InfoBarSeverity.Success);
                await ReloadAsync();
            }
            else
            {
                ShowInfo(result.Msg ?? "上传失败", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"自动备份失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    // ---------------- 下载 / 还原 ----------------
    private async void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        var targets = MyList.SelectedItems.Cast<CloudSaveItem>().ToList();
        foreach (var item in targets)
        {
            await RestoreAsync(item);
        }
    }

    private async void OnMenuDownloadTo(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await DownloadToFolderAsync(item);
        }
    }

    private async void OnMenuRestore(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await RestoreAsync(item);
        }
    }

    private async void OnMenuShare(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await ShareAsync([item.Id]);
        }
    }

    private async void OnMenuUnshare(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await ShareAsync([item.Id], shared: false);
        }
    }

    private async void OnMenuSharedDownloadTo(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await DownloadToFolderAsync(item);
        }
    }

    private async void OnMenuSharedRestore(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await RestoreAsync(item);
        }
    }

    /// <summary>
    /// 右键菜单用代码构建：避免在 DataTemplate 内对 ContextFlyout 做 x:Bind 触发 XAML 编译问题。
    /// </summary>
    private void OnMyItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CloudSaveItem item)
        {
            return;
        }
        var flyout = new MenuFlyout();
        flyout.Items.Add(BuildMenuItem("备  注", item, OnMenuRemark));
        flyout.Items.Add(BuildMenuItem("下载到指定文件夹", item, OnMenuDownloadTo));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildMenuItem("还原此存档", item, OnMenuRestore));
        // 已共享的存档，菜单项改为「取消共享」。
        flyout.Items.Add(item.Shared
            ? BuildMenuItem("取消共享", item, OnMenuUnshare)
            : BuildMenuItem("共享存档", item, OnMenuShare));
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private void OnSharedItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CloudSaveItem item)
        {
            return;
        }
        var flyout = new MenuFlyout();
        flyout.Items.Add(BuildMenuItem("下载到指定文件夹", item, OnMenuSharedDownloadTo));
        flyout.Items.Add(BuildMenuItem("还原此存档", item, OnMenuSharedRestore));
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private static MenuFlyoutItem BuildMenuItem(string text, CloudSaveItem item, RoutedEventHandler handler)
    {
        var menuItem = new MenuFlyoutItem { Text = text, Tag = item };
        menuItem.Click += handler;
        return menuItem;
    }

    /// <summary>从菜单项取回其绑定的存档条目（优先 Tag，其次 DataContext）。</summary>
    private static CloudSaveItem? GetMenuItem(object sender)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Tag is CloudSaveItem tagged)
            {
                return tagged;
            }
            if (element.DataContext is CloudSaveItem contexted)
            {
                return contexted;
            }
        }
        return null;
    }

    /// <summary>
    /// 下载并解压到目标目录（植物大战僵尸为固定目录；我的世界需选择目录）。
    /// 还原是「替换」而非「合并」：直接解压会把新旧存档混在一起（旧存档残留的文件不会被清掉），
    /// 所以先弹窗确认，确认后清空目标目录再解压。
    /// </summary>
    private async Task RestoreAsync(CloudSaveItem item)
    {
        var destDir = await ResolveRestoreDirAsync(item);
        if (destDir is null)
        {
            return;
        }

        if (!await ConfirmRestoreAsync())
        {
            return;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"eya_save_{Guid.NewGuid():N}.zip");
        try
        {
            var (ok, msg) = await _service.DownloadAsync(item.Id, tempZip);
            if (!ok)
            {
                ShowInfo(msg ?? "下载失败", InfoBarSeverity.Error);
                return;
            }
            Directory.CreateDirectory(destDir);
            ClearDirectoryContents(destDir);
            ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
            ShowInfo($"已还原到：{destDir}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"还原失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    private async Task<string?> ResolveRestoreDirAsync(CloudSaveItem item)
    {
        if (FixedSaveDir(item.Game) is { } fixedDir)
        {
            return fixedDir;
        }
        // 我的世界没有固定目录：让用户选择还原目标文件夹。
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            ShowInfo("已取消（我的世界需选择还原目标文件夹）", InfoBarSeverity.Informational);
        }
        return folder;
    }

    /// <summary>还原前的二次确认（默认停在「取消」上，避免误触覆盖）。</summary>
    private async Task<bool> ConfirmRestoreAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "还原存档",
            Content = "确定还原存档后将会覆盖现有存档，建议将现有存档备份后再进行还原存档喵~",
            PrimaryButtonText = "继续还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>清空目录内的全部文件与子目录（保留目录本身），防止新旧存档叠加。</summary>
    private static void ClearDirectoryContents(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
        {
            return;
        }
        foreach (var file in info.EnumerateFiles())
        {
            file.Delete();
        }
        foreach (var sub in info.EnumerateDirectories())
        {
            sub.Delete(recursive: true);
        }
    }

    private async Task DownloadToFolderAsync(CloudSaveItem item)
    {
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return;
        }
        var safeName = string.Concat(item.Name.Split(Path.GetInvalidFileNameChars()));
        var dest = Path.Combine(folder, $"{safeName}.zip");
        try
        {
            var (ok, msg) = await _service.DownloadAsync(item.Id, dest);
            ShowInfo(ok ? $"已下载到：{dest}" : (msg ?? "下载失败"),
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInfo($"下载失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 共享 ----------------
    private async void OnShareSelected(object sender, RoutedEventArgs e)
    {
        var ids = MyList.SelectedItems.Cast<CloudSaveItem>().Select(i => i.Id).ToArray();
        if (ids.Length == 0)
        {
            return;
        }
        await ShareAsync(ids);
    }

    /// <summary>共享 / 取消共享（shared: false 即取消）。</summary>
    private async Task ShareAsync(int[] ids, bool shared = true)
    {
        var action = shared ? "共享" : "取消共享";
        try
        {
            var result = await _service.ShareAsync(ids, shared);
            if (result.Ok)
            {
                ShowInfo(shared ? "已共享给所有用户" : "已取消共享", InfoBarSeverity.Success);
                await ReloadAsync();
            }
            else
            {
                ShowInfo(result.Msg ?? $"{action}失败", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"{action}失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 备注 ----------------
    private async void OnMenuRemark(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { } item)
        {
            return;
        }

        var input = new TextBox { Text = item.Remark, AcceptsReturn = false, PlaceholderText = "给这个存档写点备注…" };
        var dialog = new ContentDialog
        {
            Title = "存档备注",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await _service.SetRemarkAsync(item.Id, input.Text);
            if (result.Ok)
            {
                ShowInfo("备注已保存", InfoBarSeverity.Success);
                await ReloadAsync();
            }
            else
            {
                ShowInfo(result.Msg ?? "保存失败", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"保存失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 拖拽上传（仅单个 .zip） ----------------
    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "上传单个 .zip 存档";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count != 1)
        {
            ShowInfo("一次只能拖入一个压缩文件（且不能是文件夹）", InfoBarSeverity.Warning);
            return;
        }
        if (items[0] is not StorageFile file)
        {
            ShowInfo("不能拖入文件夹，请拖入单个 .zip 压缩包", InfoBarSeverity.Warning);
            return;
        }
        if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo("仅支持 .zip 压缩包", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var result = await _service.UploadAsync(_game, file.Path, null, null, "drag");
            if (result.Ok)
            {
                ShowInfo($"上传成功（{GameName(_game)}）", InfoBarSeverity.Success);
                await ReloadAsync();
            }
            else
            {
                ShowInfo(result.Msg ?? "上传失败", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"上传失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 工具 ----------------
    private static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*"); // FolderPicker 需要至少一个过滤项
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略临时文件清理失败
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }
}