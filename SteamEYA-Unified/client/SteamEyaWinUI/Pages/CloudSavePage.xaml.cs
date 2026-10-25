using System.Collections.ObjectModel;
using System.IO.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
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

    private static readonly (string Key, string Name)[] BuiltinGames =
    [
        ("pvz", "植物大战僵尸"),
        ("pvzhybrid", "植物大战僵尸杂交版新版"),
        ("minecraft", "我的世界")
    ];

    private string _game = "pvz";

    /// <summary>用户自定义的游戏（名字来自服务端目录，存档目录只存本机）。</summary>
    private List<CustomSaveGame> _customGames = [];

    /// <summary>服务端下发的自定义游戏（别人加过且已上传过的），带创建者填的存档目录。</summary>
    private List<CloudSaveGameDto> _remoteGames = [];

    /// <summary>重建下拉框期间屏蔽 SelectionChanged，避免清空时触发一次无用加载。</summary>
    private bool _rebuildingGames;

    public CloudSavePage()
    {
        InitializeComponent();
        MyList.ItemsSource = _mySaves;
        SharedList.ItemsSource = _sharedSaves;
        BuildGameCombo();
        UpdateAutoBackupState();

        Loaded += async (_, _) =>
        {
            await LoadRemoteGamesAsync();
            await ReloadAsync();
        };
    }

    /// <summary>拉服务端已公开的自定义游戏，补进下拉框（别人传过存档的也能直接用）。</summary>
    private async Task LoadRemoteGamesAsync()
    {
        try
        {
            var dto = await _service.GamesAsync();
            if (dto.Ok && dto.Games is { Count: > 0 } games)
            {
                _remoteGames = games;
                BuildGameCombo();
                UpdateAutoBackupState();
            }
        }
        catch (Exception ex)
        {
            // 离线/未登录时不影响本机内置游戏与本地自定义游戏。
            AppLog.Debug($"拉取云存档游戏列表失败：{ex.Message}");
        }
    }

    /// <summary>重建游戏下拉框：内置 3 个在前，自定义在后；尽量保持当前选中项。</summary>
    private void BuildGameCombo(string? selectKey = null)
    {
        _customGames = CustomSaveStore.Load().ToList();
        var target = selectKey ?? _game;

        _rebuildingGames = true;
        try
        {
            GameCombo.Items.Clear();

            foreach (var (key, name) in BuiltinGames)
            {
                GameCombo.Items.Add(new ComboBoxItem { Content = name, Tag = key });
            }

            foreach (var custom in _customGames)
            {
                GameCombo.Items.Add(new ComboBoxItem { Content = custom.Name, Tag = custom.Key });
            }

            // 别人上传过、已对所有人公开的自定义游戏也列出来，直接就能下载还原。
            var localKeys = _customGames.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var remote in _remoteGames)
            {
                if (remote.Builtin || localKeys.Contains(remote.Key))
                {
                    continue;
                }

                GameCombo.Items.Add(new ComboBoxItem { Content = remote.Name, Tag = remote.Key });
            }

            var index = 0;
            for (var i = 0; i < GameCombo.Items.Count; i++)
            {
                if (GameCombo.Items[i] is ComboBoxItem { Tag: string tag } &&
                    string.Equals(tag, target, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            GameCombo.SelectedIndex = index;
        }
        finally
        {
            _rebuildingGames = false;
        }

        if (GameCombo.SelectedItem is ComboBoxItem { Tag: string selected })
        {
            _game = selected;
        }
    }

    private string GameName(string game)
    {
        if (CustomSaveStore.Find(game) is { } custom)
        {
            return custom.Name;
        }

        // 别人上传过的自定义游戏：本地清单里没有，名字从服务端列表取。
        var remote = _remoteGames.FirstOrDefault(g => string.Equals(g.Key, game, StringComparison.Ordinal));
        if (remote is not null && !string.IsNullOrWhiteSpace(remote.Name))
        {
            return remote.Name;
        }

        foreach (var (key, name) in BuiltinGames)
        {
            if (string.Equals(key, game, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return game;
    }

    /// <summary>
    /// 该游戏的本地存档目录（可自动备份 / 自动还原）。
    /// 自定义游戏用用户填的路径；我的世界这类没有固定目录的返回 null。
    /// </summary>
    private string? FixedSaveDir(string game)
    {
        // 本地清单优先：自己填的路径能改，服务端那份是别人填的。
        if (CustomSaveStore.Find(game) is { } custom)
        {
            return string.IsNullOrWhiteSpace(custom.SaveDir) ? null : custom.SaveDir;
        }

        // 别人共享的游戏：用服务端下发的路径，这样还原到约定位置。
        var remote = _remoteGames.FirstOrDefault(g =>
            !g.Builtin && string.Equals(g.Key, game, StringComparison.Ordinal));
        if (remote is not null)
        {
            return string.IsNullOrWhiteSpace(remote.SaveDir) ? null : remote.SaveDir;
        }

        return game switch
        {
            "pvz" => PvzSaveDir,
            "pvzhybrid" => PvzHybridSaveDir,
            _ => null
        };
    }

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
        if (_rebuildingGames)
        {
            return;
        }

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

    // ---------------- 自定义游戏 ----------------

    /// <summary>次要文字画刷；取不到就退回灰色，别让一个资源键把弹窗打断。</summary>
    private static Microsoft.UI.Xaml.Media.Brush SecondaryTextBrush()
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var value) &&
            value is Microsoft.UI.Xaml.Media.Brush brush)
        {
            return brush;
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }

    /// <summary>
    /// 添加自定义游戏：填名字 + 本机存档目录。名字会登记到服务端（同名直接复用），
    /// 目录只保存在本机，用于「自动备份 / 还原」。
    /// </summary>
    private async void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "游戏名",
            PlaceholderText = "例如：泰拉瑞亚"
        };

        var dirBox = new TextBox
        {
            Header = "存档目录",
            PlaceholderText = @"C:\Users\你的名字\Documents\My Games\Terraria"
        };

        var browseBtn = new Button
        {
            Content = "浏览…",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        browseBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                // 无打包 WinUI：选择器必须显式绑定窗口句柄。
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    dirBox.Text = folder.Path;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"选择存档目录失败：{ex.Message}");
            }
        };

        var dirRow = new Grid { ColumnSpacing = 8 };
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dirRow.Children.Add(dirBox);
        Grid.SetColumn(browseBtn, 1);
        dirRow.Children.Add(browseBtn);

        var tip = new TextBlock
        {
            Text = "存档目录只保存在本机，用于「自动备份」。游戏名会同步到云端，其他人也能看到这款游戏的共享存档。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryTextBrush()
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(nameBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(tip);

        var listHost = new StackPanel { Spacing = 6 };
        var listTitle = new TextBlock
        {
            Text = "已添加的自定义游戏",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
            Foreground = SecondaryTextBrush()
        };

        // 删除按钮直接改本地清单，列表与下拉框同步重建。
        void RenderCustomList()
        {
            listHost.Children.Clear();

            var customs = CustomSaveStore.Load().ToList();
            listTitle.Visibility = customs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            foreach (var custom in customs)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{custom.Name} · {custom.SaveDir}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                };
                row.Children.Add(text);

                var removeBtn = new Button { Content = "删除", FontSize = 12 };
                removeBtn.Click += (_, _) =>
                {
                    CustomSaveStore.Remove(custom.Key);
                    RenderCustomList();

                    // BuildGameCombo 屏蔽了 SelectionChanged，这里要自己补一次加载。
                    BuildGameCombo();
                    UpdateAutoBackupState();
                    _ = ReloadAsync();
                };
                Grid.SetColumn(removeBtn, 1);
                row.Children.Add(removeBtn);

                listHost.Children.Add(row);
            }
        }

        panel.Children.Add(listTitle);
        panel.Children.Add(listHost);
        RenderCustomList();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加自定义游戏",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var dir = dirBox.Text.Trim().Trim('"');

        if (name.Length == 0 || dir.Length == 0)
        {
            ShowInfo("请填写游戏名和存档目录", InfoBarSeverity.Warning);
            return;
        }

        // 目录不存在不阻断：用户可能还没装游戏，路径先存着，备份时会提示。
        if (!Directory.Exists(dir))
        {
            ShowInfo($"目录不存在，已按原样保存：{dir}", InfoBarSeverity.Warning);
        }

        AddGameBtn.IsEnabled = false;
        try
        {
            var existing = CustomSaveStore.FindByName(name);
            var key = existing?.Key ?? string.Empty;
            var localOnly = existing?.LocalOnly ?? false;

            if (string.IsNullOrWhiteSpace(key) || localOnly)
            {
                // 先问服务端要 key：同名会复用同一条，避免同一款游戏被拆成多个 key。
                try
                {
                    var reg = await _service.RegisterGameAsync(name, dir);
                    if (reg.Ok && !string.IsNullOrWhiteSpace(reg.Key))
                    {
                        key = reg.Key;
                        localOnly = false;
                    }
                    else
                    {
                        key = $"local_{Guid.NewGuid():N}"[..14];
                        localOnly = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"注册自定义游戏失败：{ex.Message}");
                    key = $"local_{Guid.NewGuid():N}"[..14];
                    localOnly = true;
                }
            }

            CustomSaveStore.Upsert(new CustomSaveGame
            {
                Key = key,
                Name = name,
                SaveDir = dir,
                LocalOnly = localOnly
            });

            BuildGameCombo(key);
            UpdateAutoBackupState();
            await ReloadAsync();

            ShowInfo(
                localOnly
                    ? $"已添加「{name}」（仅本机可用，云端暂未同步）"
                    : $"已添加「{name}」",
                localOnly ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("添加自定义云存档游戏失败。", ex);
            ShowInfo($"添加失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            AddGameBtn.IsEnabled = true;
        }
    }

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
            var result = await _service.UploadAsync(_game, tempZip, null, null, "auto", FixedSaveDir(_game));
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

        // 路径可能来自服务端（别人填的），清空前必须确认不是盘根/系统目录。
        if (!IsSafeToClear(destDir))
        {
            ShowInfo($"拒绝还原到该目录（路径过于宽泛）：{destDir}", InfoBarSeverity.Error);
            return;
        }

        if (!await ConfirmRestoreAsync(destDir))
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
    private async Task<bool> ConfirmRestoreAsync(string destDir)
    {
        var exists = Directory.Exists(destDir);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = exists ? "将把存档解压到下面这个目录：" : "该目录还不存在，将会自动创建，然后把存档解压到：",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = destDir,
            IsTextSelectionEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = "目录内现有的文件和子目录会先被清空，再写入存档，避免新旧存档混在一起。建议先备份现有存档。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = SecondaryTextBrush()
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "还原存档",
            Content = body,
            PrimaryButtonText = "清空并还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 清空前的安全闸：拒绝盘根、系统目录、用户主目录本身。
    /// 存档路径可能由别人填在服务端，不能无条件递归删除。
    /// </summary>
    private static bool IsSafeToClear(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(dir))
            {
                return false;
            }

            full = Path.GetFullPath(dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        var root = (Path.GetPathRoot(full) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 盘根（C:\）没有可清空的意义，直接拒绝。
        if (full.Length == 0 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var protectedDir in ProtectedClearTargets())
        {
            if (string.IsNullOrWhiteSpace(protectedDir))
            {
                continue;
            }

            var p = protectedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>不允许被整体清空的系统目录（它们的子目录仍然可以）。</summary>
    private static IEnumerable<string> ProtectedClearTargets()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
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
            var result = await _service.UploadAsync(_game, file.Path, null, null, "drag", FixedSaveDir(_game));
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