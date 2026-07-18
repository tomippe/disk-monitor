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

    public static IReadOnlyList<MenuItemSpec> BuildRoot(Action refresh, Action quit)
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
            PopulateSubmenu = child => child.SetItems(BuildMoreItems(refresh, quit))
        });

        return items;
    }

    /// <summary>"…" menu: chips that did not fit + former 「その他」.</summary>
    public static IReadOnlyList<MenuItemSpec> BuildOverflowAndMore(
        IReadOnlyList<MenuItemSpec> layoutOverflow,
        Action refresh,
        Action quit)
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

        var more = BuildMoreItems(refresh, quit);
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
        // Empty DVD / unreadable volume: gray parent; submenu stays for top-bar / eject.
        var ready = vol.IsReady;
        var network = vol.DriveType == IODriveType.Network;
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Disk,
            // Network shell icons stall the STA loader — keep the glyph only.
            IconPath = network ? null : path,
            Title = vol.Name,
            Trailing = free,
            Enabled = ready,
            Muted = !ready,
            HasSubmenu = true,
            OnClick = ready ? () => MenuActions.OpenPath(path) : null,
            // Capture VolumeInfo so opening the submenu never re-enumerates drives on the UI thread.
            PopulateSubmenu = child => PopulateDirectory(
                child, path, volumeRoot: path, isFavoriteRoot: false, volume: vol)
        };
    }

    private static MenuItemSpec CreateFavoriteItem(string fav)
    {
        var available = FavoriteStore.IsAvailable(fav);
        var name = Path.GetFileName(fav.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : fav;
        var network = VolumeService.IsNetworkPath(fav);
        return new MenuItemSpec
        {
            Glyph = AppGlyph.Folder,
            // Skip shell icons for network favorites — STA SHGetFileInfo stalls hard.
            IconPath = available && !network ? fav : null,
            Title = DisplayName(name, isDirectory: true),
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

    public static IReadOnlyList<MenuItemSpec> BuildMoreItems(Action refresh, Action quit)
    {
        var loginOn = MenuActions.IsOpenAtLogin();
        return
        [
            new MenuItemSpec
            {
                Glyph = AppGlyph.Copy,
                Title = L.Get("menu.copy_status"),
                AlignBesideParent = true,
                OnClick = () => MenuActions.CopyText(VolumeService.FormatAllVolumesStatus())
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
        bool isFavoriteRoot,
        VolumeInfo? volume = null)
    {
        var generation = menu.BeginLoad();
        var token = menu.LoadToken;
        var includeHidden = AltKeyState.TakeCapturedOrRead();

        // Prefer the snapshot captured when the chip/row was built (no DriveInfo on UI).
        volume ??= volumeRoot is not null ? VolumeService.TryGetCachedVolume(volumeRoot) : null;

        menu.BindDirectoryRelist(
            includeHidden,
            () => PopulateDirectory(menu, directoryPath, volumeRoot, isFavoriteRoot, volume));

        // Empty DVD / media not ready — skip listing; keep top-bar / eject only.
        if (volumeRoot is not null
            && volume is { IsReady: false }
            && IsSameDirectoryPath(directoryPath, volumeRoot))
        {
            menu.SetItems(BuildDirectoryActionsOnly(directoryPath, volumeRoot, isFavoriteRoot, volume));
            return;
        }

        // Gray "読み込み中…" + top-bar / eject — must stay cheap (no DriveInfo / SHGetFileInfo).
        menu.SetItems(BuildDirectoryLoadingItems(directoryPath, volumeRoot, isFavoriteRoot, volume));

        // List + build MenuItemSpec off the UI thread — switching folders cancels via generation/token.
        _ = Task.Run(() =>
        {
            DirectorySnapshot snap;
            try
            {
                snap = DirectoryService.List(directoryPath, includeHidden, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;

            var items = BuildDirectoryItems(
                snap, directoryPath, volumeRoot, isFavoriteRoot, includeHidden, volume, out var summaryTag);

            menu.Dispatcher.BeginInvoke(() =>
            {
                if (!menu.IsLoadCurrent(generation)) return;

                menu.SetItems(items);

                ScheduleEntryIcons(menu, generation, items);

                if (snap.Error is null && summaryTag is not null)
                    ScheduleFolderSize(menu, generation, directoryPath, snap.TotalCount, includeHidden, summaryTag);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, token);
    }

    /// <summary>
    /// Header rows shown while the directory listing is still loading
    /// (muted summary placeholder, top-bar / favorite toggle, eject).
    /// </summary>
    private static List<MenuItemSpec> BuildDirectoryLoadingItems(
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        VolumeInfo? volume)
    {
        var items = BuildDirectoryHeaderItems(
            directoryPath,
            volumeRoot,
            isFavoriteRoot,
            volume,
            summaryTitle: L.Get("menu.directory_loading"),
            summaryEnabled: false,
            summaryOnClick: null,
            summaryTag: null,
            includeEject: true);
        // Keep header actions above the parent row while the listing is empty.
        items.Add(new MenuItemSpec
        {
            IsAlignSpacer = true,
            AlignBesideParent = true,
            Enabled = false
        });
        return items;
    }

    /// <summary>
    /// Unreadable folder / empty media: top-bar / favorite / eject only (no summary, no error row).
    /// </summary>
    private static List<MenuItemSpec> BuildDirectoryActionsOnly(
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        VolumeInfo? volume)
    {
        var items = BuildDirectoryActionItems(
            directoryPath, volumeRoot, isFavoriteRoot, volume, includeEject: true);
        if (items.Count > 0)
        {
            items[0].AlignBesideParent = true;
            return items;
        }

        items.Add(new MenuItemSpec
        {
            IsAlignSpacer = true,
            AlignBesideParent = true,
            Enabled = false
        });
        return items;
    }

    /// <summary>
    /// Summary + favorite / top-bar toggles + optional eject — shared by loading and loaded menus.
    /// </summary>
    private static List<MenuItemSpec> BuildDirectoryHeaderItems(
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        VolumeInfo? volume,
        string summaryTitle,
        bool summaryEnabled,
        Action? summaryOnClick,
        string? summaryTag,
        bool includeEject)
    {
        var items = new List<MenuItemSpec>
        {
            new()
            {
                Tag = summaryTag,
                Title = summaryTitle,
                Enabled = summaryEnabled,
                Muted = !summaryEnabled,
                OnClick = summaryOnClick
            }
        };
        items.AddRange(BuildDirectoryActionItems(
            directoryPath, volumeRoot, isFavoriteRoot, volume, includeEject));
        return items;
    }

    /// <summary>Top-bar / favorite / eject — independent of directory listing success.</summary>
    private static List<MenuItemSpec> BuildDirectoryActionItems(
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        VolumeInfo? volume,
        bool includeEject)
    {
        var items = new List<MenuItemSpec>();

        if (isFavoriteRoot)
            items.Add(RemoveFavoriteItem(directoryPath));
        else if (volumeRoot is null && !FavoriteStore.Contains(directoryPath))
            items.Add(AddFavoriteItem(directoryPath));

        var driveRoot = volumeRoot is not null && IsSameDirectoryPath(directoryPath, volumeRoot);
        var vol = driveRoot ? volume : null;
        if (vol is not null)
            items.Add(CreateTopBarToggleItem(vol));

        // Empty DVD / no media: Eject is not available — omit the row entirely.
        var canEject = includeEject
            && vol is { IsReady: true }
            && vol.DriveType is IODriveType.Removable or IODriveType.CDRom;

        if (canEject)
        {
            if (items.Count > 0)
                items.Add(Sep());
            var root = volumeRoot!;
            items.Add(new MenuItemSpec
            {
                Glyph = AppGlyph.Eject,
                Title = L.Get("menu.eject"),
                OnClick = () =>
                {
                    MenuActions.EjectDrive(root);
                    MenuSession.CloseAll();
                }
            });
        }

        // Never leave a trailing separator (caller adds one before listing content if needed).
        TrimTrailingSeparators(items);
        return items;
    }

    private static void TrimTrailingSeparators(List<MenuItemSpec> items)
    {
        while (items.Count > 0 && items[^1].IsSeparator)
            items.RemoveAt(items.Count - 1);
    }

    /// <summary>Insert a separator before folder listing rows when the header does not already end with one.</summary>
    private static void EnsureSeparatorBeforeContent(List<MenuItemSpec> items)
    {
        if (items.Count == 0 || items[^1].IsSeparator) return;
        items.Add(Sep());
    }

    /// <summary>Compare paths without GetFullPath when possible (GetFullPath can hit slow volumes).</summary>
    private static bool IsSameDirectoryPath(string a, string b)
    {
        static string Norm(string p) => p.TrimEnd('\\', '/');
        if (string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<MenuItemSpec> BuildDirectoryItems(
        DirectorySnapshot snap,
        string directoryPath,
        string? volumeRoot,
        bool isFavoriteRoot,
        bool includeHidden,
        VolumeInfo? volume,
        out string? summaryTag)
    {
        summaryTag = null;

        // Unreadable — actions only (top-bar / favorite / eject). No "0 items" / error row.
        if (snap.Error is not null)
            return BuildDirectoryActionsOnly(directoryPath, volumeRoot, isFavoriteRoot, volume);

        summaryTag = "summary:" + directoryPath;
        var summaryTitle = string.Format(L.Get("menu.directory_summary_items"), snap.TotalCount);
        if (includeHidden)
            summaryTitle += L.Get("menu.directory_summary_includes_hidden");

        var items = BuildDirectoryHeaderItems(
            directoryPath,
            volumeRoot,
            isFavoriteRoot,
            volume,
            summaryTitle,
            summaryEnabled: true,
            summaryOnClick: () => MenuActions.OpenPath(directoryPath),
            summaryTag,
            includeEject: true);

        EnsureSeparatorBeforeContent(items);

        if (snap.Entries.Count == 0)
        {
            items.Add(new MenuItemSpec
            {
                Title = L.Get("menu.directory_empty"),
                Enabled = false,
                Muted = true,
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
                    Muted = true,
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
        // Shell icons for network paths block the STA icon thread (and feel like UI hitches).
        var paths = items
            .Where(i => !string.IsNullOrEmpty(i.IconPath) && !VolumeService.IsNetworkPath(i.IconPath!))
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
        // Walking a network share for size freezes the process for a long time — skip.
        if (VolumeService.IsNetworkPath(directoryPath))
            return;

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
