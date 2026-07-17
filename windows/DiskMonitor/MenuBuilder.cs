using System.IO;
using System.Windows.Media;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using IODriveType = System.IO.DriveType;

namespace DiskMonitor;

internal static class MenuBuilder
{
    private static readonly TimeSpan FolderSizeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// AppBar chip order: pinned/default volumes → favorites → trash →
    /// then unpinned volumes (fill remaining width). Overflow goes to "…".
    /// </summary>
    public static List<MenuItemSpec> BuildPrimaryItems()
    {
        var items = new List<MenuItemSpec>();

        var volumes = VolumeService.ListVolumes();
        if (volumes.Count == 0)
        {
            items.Add(new MenuItemSpec { Title = L.Get("menu.no_volumes"), Enabled = false });
        }
        else
        {
            foreach (var vol in volumes.Where(TopBarStore.IsShownOnTopBar))
                items.Add(CreateVolumeItem(vol));
        }

        var favorites = FavoriteStore.LoadPaths();
        if (favorites.Count > 0)
        {
            items.Add(Sep()); // left of favorites
            foreach (var fav in favorites)
                items.Add(CreateFavoriteItem(fav));
        }

        if (TopBarStore.IsTrashShownOnTopBar())
        {
            items.Add(Sep()); // left of trash
            items.Add(CreateTrashItem());
            items.Add(Sep()); // right of trash
        }

        // Unpinned volumes after trash — shown only while bar width remains.
        foreach (var vol in volumes.Where(v => !TopBarStore.IsShownOnTopBar(v)))
            items.Add(CreateVolumeItem(vol));

        return items;
    }

    public static IReadOnlyList<MenuItemSpec> BuildRoot(
        Action refresh,
        Action quit,
        Func<string> statusText)
    {
        var items = new List<MenuItemSpec>();
        var volumes = VolumeService.ListVolumes();

        if (volumes.Count == 0)
        {
            items.Add(new MenuItemSpec { Title = L.Get("menu.no_volumes"), Enabled = false });
        }
        else
        {
            foreach (var vol in volumes)
                items.Add(CreateVolumeItem(vol));
        }

        items.Add(Sep());

        var favorites = FavoriteStore.LoadPaths();
        if (favorites.Count > 0)
        {
            foreach (var fav in favorites)
                items.Add(CreateFavoriteItem(fav));
            items.Add(Sep());
        }

        items.Add(CreateTrashItem());
        items.Add(Sep());
        items.Add(new MenuItemSpec
        {
            Glyph = AppGlyph.More,
            Title = L.Get("menu.more"),
            HasSubmenu = true,
            PopulateSubmenu = child => child.SetItems(BuildMoreItems(refresh, quit, statusText))
        });

        return items;
    }

    /// <summary>"…" menu: chips that did not fit + former 「その他」.</summary>
    public static IReadOnlyList<MenuItemSpec> BuildOverflowAndMore(
        IReadOnlyList<MenuItemSpec> layoutOverflow,
        Action refresh,
        Action quit,
        Func<string> statusText)
    {
        var items = new List<MenuItemSpec>();

        // Top-bar overflow (volumes / favorites / trash that didn't fit).
        if (layoutOverflow.Count > 0)
            items.AddRange(layoutOverflow.Where(i => !i.IsSeparator));

        // Intentionally hidden from the bar — keep reachable under "…".
        if (!TopBarStore.IsTrashShownOnTopBar())
        {
            if (items.Count > 0 && !items[^1].IsSeparator)
                items.Add(Sep());
            items.Add(CreateTrashItem());
        }

        var more = BuildMoreItems(refresh, quit, statusText);
        // Horizontal separator between top-bar overflow section and 「その他」.
        if (items.Count > 0 && more.Count > 0 && !items[^1].IsSeparator)
            items.Add(Sep());

        items.AddRange(more);
        if (items.Count > 0 && !items[0].IsSeparator)
            items[0].AlignBesideParent = true;
        return items;
    }

    private static MenuItemSpec CreateVolumeItem(VolumeInfo vol)
    {
        var path = vol.RootPath;
        // Capacity unknown → no trailing label (never "取得失敗"); chip / menu still work.
        var free = vol.AvailableBytes is long bytes
            ? VolumeService.FormatBytes(bytes, concise: true)
            : null;
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Disk,
            IconPath = path,
            Title = vol.Name,
            Trailing = free,
            Enabled = true,
            HasSubmenu = true,
            OnClick = () => MenuActions.OpenPath(path),
            PopulateSubmenu = child => PopulateDirectory(child, path, volumeRoot: path, isFavoriteRoot: false)
        };
    }

    private static MenuItemSpec CreateFavoriteItem(string fav)
    {
        var available = FavoriteStore.IsAvailable(fav);
        var name = Path.GetFileName(fav.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : fav;
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Folder,
            IconPath = available ? fav : null,
            Title = DisplayName(name, isDirectory: Directory.Exists(fav)),
            Enabled = available,
            TrailingItalic = true,
            TrailingTag = available ? "favsize:" + fav : null,
            HasSubmenu = true,
            OnClick = available ? () => MenuActions.OpenPath(fav) : null,
            PopulateSubmenu = available
                ? child => PopulateDirectory(child, fav, volumeRoot: null, isFavoriteRoot: true)
                : child =>
                {
                    var remove = RemoveFavoriteItem(fav);
                    remove.AlignBesideParent = true;
                    child.SetItems([remove]);
                }
        };
    }

    private static MenuItemSpec CreateTrashItem()
    {
        // Peek only — SHQueryRecycleBin on the UI thread hitches the AppBar.
        var trashBytes = MenuActions.PeekRecycleBinBytes();
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Recycle,
            Title = L.Get("menu.trash"),
            // Always show capacity (incl. 0 B) — AppBar + menus. Refreshed async via TrailingTag.
            Trailing = VolumeService.FormatBytes(trashBytes, concise: false),
            TrailingItalic = true,
            TrailingTag = "trashsize",
            HasSubmenu = true,
            OnClick = MenuActions.OpenRecycleBin,
            PopulateSubmenu = PopulateTrashSubmenu
        };
    }

    private static MenuItemSpec CreateTopBarToggleItem(VolumeInfo vol)
    {
        var shown = TopBarStore.IsShownOnTopBar(vol);
        var path = vol.RootPath;
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Star,
            Title = L.Get(shown ? "menu.remove_from_top_bar" : "menu.show_on_top_bar"),
            OnClick = () =>
            {
                TopBarStore.SetShownOnTopBar(path, !shown);
                MenuSession.CloseAll();
            }
        };
    }

    private static MenuItemSpec CreateTrashTopBarToggleItem()
    {
        var shown = TopBarStore.IsTrashShownOnTopBar();
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Star,
            Title = L.Get(shown ? "menu.remove_from_top_bar" : "menu.show_on_top_bar"),
            OnClick = () =>
            {
                TopBarStore.SetTrashShownOnTopBar(!shown);
                MenuSession.CloseAll();
            }
        };
    }

    public static IReadOnlyList<MenuItemSpec> BuildMoreItems(
        Action refresh,
        Action quit,
        Func<string> statusText)
    {
        var loginOn = MenuActions.IsOpenAtLogin();
        return
        [
            new MenuItemSpec
            {
                Glyph = AppGlyph.Copy,
                Title = L.Get("menu.copy_status"),
                AlignBesideParent = true,
                OnClick = () => MenuActions.CopyText(statusText())
            },
            new MenuItemSpec
            {
                Glyph = AppGlyph.Refresh,
                Title = L.Get("menu.refresh"),
                OnClick = refresh
            },
            new MenuItemSpec
            {
                Glyph = AppGlyph.Disk,
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
                Glyph = AppGlyph.Info,
                Title = L.Get("menu.about"),
                OnClick = MenuActions.ShowAbout
            },
            new MenuItemSpec
            {
                Glyph = AppGlyph.Feedback,
                Title = L.Get("menu.send_feedback"),
                OnClick = FeedbackForm.Open
            },
            Sep(),
            new MenuItemSpec
            {
                Glyph = AppGlyph.Restart,
                Title = string.Format(L.Get("menu.restart_format"), L.Get("app.name")),
                OnClick = MenuActions.RestartApp
            },
            new MenuItemSpec
            {
                Glyph = AppGlyph.Quit,
                Title = string.Format(L.Get("menu.quit_format"), L.Get("app.name")),
                OnClick = quit
            }
        ];
    }

    private static void PopulateTrashSubmenu(VolumeMenuWindow child)
    {
        // Actions sit above the parent row; the slot beside ごみ箱 stays empty.
        child.SetItems([
            CreateTrashTopBarToggleItem(),
            Sep(),
            new MenuItemSpec
            {
                Title = L.Get("menu.empty_trash"),
                OnClick = () =>
                {
                    MenuActions.EmptyRecycleBin();
                    MenuSession.CloseAll();
                }
            },
            new MenuItemSpec
            {
                IsAlignSpacer = true,
                AlignBesideParent = true,
                Enabled = false
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
        var includeHidden = AltKeyState.TakeCapturedOrRead();

        menu.BindDirectoryRelist(
            includeHidden,
            () => PopulateDirectory(menu, directoryPath, volumeRoot, isFavoriteRoot));

        // Listing on a background thread — switching folders cancels via generation/token.
        _ = Task.Run(async () =>
        {
            DirectorySnapshot snap;
            try
            {
                snap = await DirectoryService.ListAsync(directoryPath, includeHidden, token)
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
                    snap, directoryPath, volumeRoot, isFavoriteRoot, includeHidden, out var summaryTag);
                menu.SetItems(items);

                ScheduleEntryIcons(menu, generation, items);

                if (snap.Error is null && summaryTag is not null)
                    ScheduleFolderSize(menu, generation, directoryPath, snap.TotalCount, includeHidden, summaryTag);
            });
        }, token);
    }

    private static List<MenuItemSpec> BuildDirectoryItems(
        DirectorySnapshot snap,
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        bool includeHidden,
        out string? summaryTag)
    {
        summaryTag = null;
        var items = new List<MenuItemSpec>();

        // Mac keeps summary + favorite actions even when the folder itself can't be listed.
        summaryTag = "summary:" + directoryPath;
        var summaryTitle = string.Format(
            L.Get("menu.directory_summary_items"),
            snap.Error is null ? snap.TotalCount : 0);
        if (includeHidden && snap.Error is null)
            summaryTitle += L.Get("menu.directory_summary_includes_hidden");
        items.Add(new MenuItemSpec
        {
            Tag = summaryTag,
            Title = summaryTitle,
            Enabled = snap.Error is null,
            OnClick = snap.Error is null ? () => MenuActions.OpenPath(directoryPath) : null
        });

        if (isFavoriteRoot)
            items.Add(RemoveFavoriteItem(directoryPath));
        else if (volumeRoot is null && !FavoriteStore.Contains(directoryPath))
            items.Add(AddFavoriteItem(directoryPath));

        // Drive root: pin / unpin on AppBar (top bar).
        if (volumeRoot is not null
            && string.Equals(
                Path.GetFullPath(directoryPath).TrimEnd('\\'),
                Path.GetFullPath(volumeRoot).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            var vol = VolumeService.ListVolumes().FirstOrDefault(v =>
                string.Equals(v.RootPath, volumeRoot, StringComparison.OrdinalIgnoreCase));
            if (vol is not null)
                items.Add(CreateTopBarToggleItem(vol));
        }

        items.Add(Sep());

        if (snap.Error is not null)
        {
            items.Add(new MenuItemSpec
            {
                Title = string.Format(L.Get("menu.directory_read_error"), snap.Error),
                Enabled = false,
                AlignBesideParent = true
            });
            summaryTag = null;
            return items;
        }

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
                    Glyph = AppGlyph.Eject,
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
            items.Add(new MenuItemSpec
            {
                Title = L.Get("menu.directory_empty"),
                Enabled = false,
                AlignBesideParent = true
            });
            return items;
        }

        var alignFirstEntry = true;
        foreach (var entry in snap.Entries)
        {
            var path = entry.Path;
            var isDir = entry.IsDirectory;
            // Glyph placeholders — real shell icons load async (Mac scheduleDirectoryEntryIcons).
            var glyph = isDir ? AppGlyph.Folder : AppGlyph.Document;
            var align = alignFirstEntry;
            alignFirstEntry = false;

            if (isDir && !entry.IsAccessible)
            {
                // Gray + favorites-only submenu (Mac: disabled look; keep add-favorite action).
                var showAdd = !FavoriteStore.Contains(path);
                items.Add(new MenuItemSpec
                {
                    Glyph = glyph,
                    IconPath = path,
                    Title = DisplayName(entry.Name, isDirectory: true),
                    Enabled = false,
                    Muted = entry.IsHidden,
                    AlignBesideParent = align,
                    HasSubmenu = showAdd,
                    PopulateSubmenu = showAdd
                        ? nested =>
                        {
                            var add = AddFavoriteItem(path);
                            add.AlignBesideParent = true;
                            nested.SetItems([add]);
                        }
                        : null
                });
                continue;
            }

            items.Add(new MenuItemSpec
            {
                Glyph = glyph,
                IconPath = path,
                Title = DisplayName(entry.Name, isDir),
                Enabled = entry.IsAccessible,
                Muted = entry.IsHidden,
                AlignBesideParent = align,
                HasSubmenu = entry.IsAccessible && isDir,
                OnClick = entry.IsAccessible ? () => MenuActions.OpenPath(path) : null,
                PopulateSubmenu = entry.IsAccessible && isDir
                    ? nested => PopulateDirectory(nested, path, volumeRoot: null, isFavoriteRoot: false)
                    : null
            });
        }

        return items;
    }

    /// <summary>Explorer-style: hide .exe / .lnk / .url extensions in menu labels.</summary>
    private static string DisplayName(string name, bool isDirectory)
    {
        if (isDirectory || string.IsNullOrEmpty(name)) return name;
        var ext = Path.GetExtension(name);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? name : stem;
        }

        return name;
    }

    /// <summary>Root menu: icons + favorite/trash sizes — all off the UI thread.</summary>
    public static void ScheduleRootAsync(VolumeMenuWindow menu, IReadOnlyList<MenuItemSpec> items)
    {
        ScheduleIcons(menu, generation: null, items, menu.LoadToken);

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.TrailingTag)) continue;

            if (item.TrailingTag.StartsWith("favsize:", StringComparison.Ordinal))
            {
                var path = item.TrailingTag["favsize:".Length..];
                var tag = item.TrailingTag;
                _ = Task.Run(async () =>
                {
                    long? bytes;
                    try
                    {
                        bytes = await DirectoryService.GetFolderSizeAsync(path, FolderSizeTimeout)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (bytes is null) return;
                    var sizeText = VolumeService.FormatBytes(bytes.Value, concise: false);
                    await menu.Dispatcher.InvokeAsync(() =>
                    {
                        if (!menu.IsVisible) return;
                        menu.UpdateTaggedTrailing(tag, sizeText);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                });
            }
            else if (item.TrailingTag == "trashsize")
            {
                var tag = item.TrailingTag;
                _ = Task.Run(async () =>
                {
                    long bytes;
                    try
                    {
                        bytes = MenuActions.RecycleBinBytes(forceRefresh: true);
                    }
                    catch
                    {
                        return;
                    }

                    var sizeText = VolumeService.FormatBytes(bytes, concise: false);
                    await menu.Dispatcher.InvokeAsync(() =>
                    {
                        if (!menu.IsVisible) return;
                        menu.UpdateTaggedTrailing(tag, sizeText);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                });
            }
        }
    }

    private static void ScheduleEntryIcons(
        VolumeMenuWindow menu,
        int generation,
        IReadOnlyList<MenuItemSpec> items) =>
        ScheduleIcons(menu, generation, items, menu.LoadToken);

    private static void ScheduleIcons(
        VolumeMenuWindow menu,
        int? generation,
        IReadOnlyList<MenuItemSpec> items,
        CancellationToken token)
    {
        var paths = items
            .Where(i => !string.IsNullOrEmpty(i.IconPath))
            .Select(i => i.IconPath!)
            .ToList();
        if (paths.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var path in paths)
            {
                if (token.IsCancellationRequested) return;

                ImageSource? icon;
                try
                {
                    icon = await ShellIconLoader.GetAsync(path, 18, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    continue;
                }

                if (icon is null) continue;

                var capturedPath = path;
                var capturedIcon = icon;
                try
                {
                    await menu.Dispatcher.InvokeAsync(() =>
                    {
                        if (generation is int g)
                        {
                            if (!menu.IsLoadCurrent(g)) return;
                        }
                        else if (!menu.IsVisible)
                        {
                            return;
                        }

                        menu.UpdateIcon(capturedPath, capturedIcon);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private static MenuItemSpec AddFavoriteItem(string path) => new()
    {
        Glyph = AppGlyph.Star,
        // Same wording as volume/trash top-bar toggles (favorites appear on the AppBar).
        Title = L.Get("menu.show_on_top_bar"),
        OnClick = () =>
        {
            FavoriteStore.Add(path);
            MenuSession.CloseAll();
        }
    };

    private static MenuItemSpec RemoveFavoriteItem(string path) => new()
    {
        Glyph = AppGlyph.Star,
        Title = L.Get("menu.remove_from_top_bar"),
        OnClick = () =>
        {
            FavoriteStore.Remove(path);
            MenuSession.CloseAll();
        }
    };

    private static void ScheduleFolderSize(
        VolumeMenuWindow menu,
        int generation,
        string directoryPath,
        int itemCount,
        bool includeHidden,
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
            // Mac: "{N} items - {size}（隠しファイルを含む）" — suffix after capacity.
            var titlePrefix = string.Format(L.Get("menu.directory_summary_items"), itemCount) + " - ";
            var hiddenSuffix = includeHidden
                ? L.Get("menu.directory_summary_includes_hidden")
                : null;

            await menu.Dispatcher.InvokeAsync(() =>
            {
                if (!menu.IsLoadCurrent(generation)) return;
                menu.UpdateTaggedTitle(summaryTag, titlePrefix, sizeText, hiddenSuffix);
            });
        }, token);
    }

    private static MenuItemSpec Sep() => new() { IsSeparator = true };
}
