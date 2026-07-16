using System.IO;
using System.Windows.Media;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using WpfColor = System.Windows.Media.Color;
using IODriveType = System.IO.DriveType;

namespace DiskMonitor;

internal static class MenuBuilder
{
    private const double IconSize = 14;
    private static readonly TimeSpan FolderSizeTimeout = TimeSpan.FromSeconds(8);

    public static IReadOnlyList<MenuItemSpec> BuildRoot(
        Action refresh,
        Action quit,
        Func<string> statusText)
    {
        var brush = FgBrush();
        var items = new List<MenuItemSpec>();
        var volumes = VolumeService.ListVolumes();

        if (volumes.Count == 0)
        {
            items.Add(new MenuItemSpec { Title = L.Get("menu.no_volumes"), Enabled = false });
        }
        else
        {
            foreach (var vol in volumes)
            {
                var path = vol.RootPath;
                var free = vol.IsReady
                    ? VolumeService.FormatBytes(vol.AvailableBytes, concise: true)
                    : L.Get("status.unavailable");
                items.Add(new MenuItemSpec
                {
                    Icon = ShellVolumeHelper.GetDriveIcon(path, 18),
                    Title = vol.Name,
                    Trailing = free,
                    Enabled = vol.IsReady,
                    HasSubmenu = vol.IsReady,
                    OnClick = vol.IsReady ? () => MenuActions.OpenPath(path) : null,
                    PopulateSubmenu = vol.IsReady
                        ? child => PopulateDirectory(child, path, volumeRoot: path, isFavoriteRoot: false)
                        : null
                });
            }
        }

        items.Add(Sep());

        var favorites = FavoriteStore.LoadPaths();
        if (favorites.Count > 0)
        {
            foreach (var fav in favorites)
            {
                var available = FavoriteStore.IsAvailable(fav);
                var name = Path.GetFileName(fav.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : fav;
                items.Add(new MenuItemSpec
                {
                    Icon = available
                        ? DirectoryService.GetEntryIcon(fav, 18) ?? MenuIcons.Folder(brush, IconSize)
                        : MenuIcons.Folder(brush, IconSize),
                    Title = name,
                    Enabled = available,
                    HasSubmenu = available,
                    OnClick = available ? () => MenuActions.OpenPath(fav) : null,
                    PopulateSubmenu = available
                        ? child => PopulateDirectory(child, fav, volumeRoot: null, isFavoriteRoot: true)
                        : null
                });
            }
            items.Add(Sep());
        }

        var trashBytes = MenuActions.RecycleBinBytes();
        items.Add(new MenuItemSpec
        {
            Icon = MenuIcons.Recycle(brush, IconSize),
            Title = L.Get("menu.trash"),
            Trailing = trashBytes > 0 ? VolumeService.FormatBytes(trashBytes, concise: true) : null,
            HasSubmenu = true,
            OnClick = MenuActions.OpenRecycleBin,
            PopulateSubmenu = PopulateTrashSubmenu
        });

        items.Add(Sep());
        items.Add(new MenuItemSpec
        {
            Icon = MenuIcons.More(brush, IconSize),
            Title = L.Get("menu.more"),
            HasSubmenu = true,
            PopulateSubmenu = child => child.SetItems(BuildMoreItems(brush, refresh, quit, statusText))
        });

        return items;
    }

    private static IReadOnlyList<MenuItemSpec> BuildMoreItems(
        SolidColorBrush brush,
        Action refresh,
        Action quit,
        Func<string> statusText)
    {
        var loginOn = MenuActions.IsOpenAtLogin();
        return
        [
            new MenuItemSpec
            {
                Icon = MenuIcons.Copy(brush, IconSize),
                Title = L.Get("menu.copy_status"),
                OnClick = () => MenuActions.CopyText(statusText())
            },
            new MenuItemSpec
            {
                Icon = MenuIcons.Refresh(brush, IconSize),
                Title = L.Get("menu.refresh"),
                OnClick = refresh
            },
            new MenuItemSpec
            {
                Icon = MenuIcons.Disk(brush, IconSize),
                Title = L.Get("menu.open_disk_utility"),
                OnClick = MenuActions.OpenDiskManagement
            },
            Sep(),
            new MenuItemSpec
            {
                Title = L.Get("menu.login_item"),
                Trailing = loginOn ? "✓" : null,
                OnClick = () => MenuActions.SetOpenAtLogin(!MenuActions.IsOpenAtLogin())
            },
            Sep(),
            new MenuItemSpec
            {
                Icon = MenuIcons.Info(brush, IconSize),
                Title = L.Get("menu.about"),
                OnClick = MenuActions.ShowAbout
            },
            new MenuItemSpec
            {
                Icon = MenuIcons.Feedback(brush, IconSize),
                Title = L.Get("menu.send_feedback"),
                OnClick = FeedbackForm.Open
            },
            new MenuItemSpec
            {
                Icon = MenuIcons.Update(brush, IconSize),
                Title = L.Get("menu.check_for_updates"),
                OnClick = () => MenuActions.OpenUrl(MenuActions.IntroUrl)
            },
            Sep(),
            new MenuItemSpec
            {
                Icon = MenuIcons.Restart(brush, IconSize),
                Title = string.Format(L.Get("menu.restart_format"), L.Get("app.name")),
                OnClick = MenuActions.RestartApp
            },
            new MenuItemSpec
            {
                Icon = MenuIcons.Quit(brush, IconSize),
                Title = string.Format(L.Get("menu.quit_format"), L.Get("app.name")),
                OnClick = quit
            }
        ];
    }

    private static void PopulateTrashSubmenu(VolumeMenuWindow child)
    {
        child.SetItems([
            new MenuItemSpec
            {
                Title = L.Get("menu.empty_trash"),
                OnClick = () =>
                {
                    MenuActions.EmptyRecycleBin();
                    MenuSession.CloseAll();
                }
            }
        ]);
    }

    private static void PopulateDirectory(
        VolumeMenuWindow menu,
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot)
    {
        var generation = menu.BeginLoad();
        var token = menu.LoadToken;
        var brush = FgBrush();

        // Listing on a background thread — switching folders cancels via generation/token.
        _ = Task.Run(async () =>
        {
            DirectorySnapshot snap;
            try
            {
                snap = await DirectoryService.ListAsync(directoryPath, includeHidden: false, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await menu.Dispatcher.InvokeAsync(() =>
            {
                if (!menu.IsLoadCurrent(generation)) return;

                var items = BuildDirectoryItems(
                    snap, directoryPath, volumeRoot, isFavoriteRoot, brush, out var summaryTag);
                menu.SetItems(items);

                if (snap.Error is null && summaryTag is not null)
                    ScheduleFolderSize(menu, generation, directoryPath, snap.TotalCount, summaryTag);
            });
        }, token);
    }

    private static List<MenuItemSpec> BuildDirectoryItems(
        DirectorySnapshot snap,
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        SolidColorBrush brush,
        out string? summaryTag)
    {
        summaryTag = null;
        var items = new List<MenuItemSpec>();

        if (snap.Error is not null)
        {
            items.Add(new MenuItemSpec
            {
                Title = string.Format(L.Get("menu.directory_read_error"), snap.Error),
                Enabled = false
            });
            return items;
        }

        summaryTag = "summary:" + directoryPath;
        items.Add(new MenuItemSpec
        {
            Tag = summaryTag,
            Title = string.Format(L.Get("menu.directory_summary_items"), snap.TotalCount),
            OnClick = () => MenuActions.OpenPath(directoryPath)
        });

        if (isFavoriteRoot)
        {
            items.Add(new MenuItemSpec
            {
                Icon = MenuIcons.Star(brush, IconSize),
                Title = L.Get("menu.remove_from_favorites"),
                OnClick = () =>
                {
                    FavoriteStore.Remove(directoryPath);
                    MenuSession.CloseAll();
                }
            });
        }
        else if (volumeRoot is null && !FavoriteStore.Contains(directoryPath))
        {
            items.Add(new MenuItemSpec
            {
                Icon = MenuIcons.Star(brush, IconSize),
                Title = L.Get("menu.add_to_favorites"),
                OnClick = () =>
                {
                    FavoriteStore.Add(directoryPath);
                    MenuSession.CloseAll();
                }
            });
        }

        items.Add(Sep());

        if (volumeRoot is not null
            && string.Equals(
                Path.GetFullPath(directoryPath).TrimEnd('\\'),
                Path.GetFullPath(volumeRoot).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            var vol = VolumeService.ListVolumes().FirstOrDefault(v =>
                string.Equals(v.RootPath, volumeRoot, StringComparison.OrdinalIgnoreCase));
            if (vol is not null && vol.DriveType is IODriveType.Removable or IODriveType.CDRom)
            {
                items.Add(new MenuItemSpec
                {
                    Icon = MenuIcons.Eject(brush, IconSize),
                    Title = L.Get("menu.eject"),
                    OnClick = () =>
                    {
                        MenuActions.EjectDrive(volumeRoot);
                        MenuSession.CloseAll();
                    }
                });
                items.Add(Sep());
            }
        }

        if (snap.Entries.Count == 0)
        {
            items.Add(new MenuItemSpec { Title = L.Get("menu.directory_empty"), Enabled = false });
            return items;
        }

        foreach (var entry in snap.Entries)
        {
            var path = entry.Path;
            var isDir = entry.IsDirectory;
            // Fast placeholders — SHGetFileInfo per entry freezes large folders.
            var icon = isDir
                ? MenuIcons.Folder(brush, IconSize)
                : null;
            items.Add(new MenuItemSpec
            {
                Icon = icon,
                Title = entry.Name,
                Enabled = entry.IsAccessible,
                HasSubmenu = entry.IsAccessible && isDir,
                OnClick = entry.IsAccessible ? () => MenuActions.OpenPath(path) : null,
                PopulateSubmenu = entry.IsAccessible && isDir
                    ? nested => PopulateDirectory(nested, path, volumeRoot: null, isFavoriteRoot: false)
                    : null
            });
        }

        return items;
    }

    private static void ScheduleFolderSize(
        VolumeMenuWindow menu,
        int generation,
        string directoryPath,
        int itemCount,
        string summaryTag)
    {
        var token = menu.LoadToken;
        _ = Task.Run(async () =>
        {
            long? bytes;
            try
            {
                bytes = await DirectoryService.GetFolderSizeAsync(directoryPath, FolderSizeTimeout, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (bytes is null) return;

            var sizeText = VolumeService.FormatBytes(bytes.Value, concise: false);
            var title = string.Format(L.Get("menu.directory_summary_items_size"), itemCount, sizeText);

            await menu.Dispatcher.InvokeAsync(() =>
            {
                if (!menu.IsLoadCurrent(generation)) return;
                menu.UpdateTaggedTitle(summaryTag, title);
            });
        }, token);
    }

    private static MenuItemSpec Sep() => new() { IsSeparator = true };

    private static SolidColorBrush FgBrush()
    {
        var dark = IsDarkTheme();
        var c = dark ? WpfColor.FromRgb(0xF3, 0xF3, 0xF3) : WpfColor.FromRgb(0x1A, 0x1A, 0x1A);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            if (value is int i) return i == 0;
        }
        catch { /* ignore */ }
        return false;
    }
}
