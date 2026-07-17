import Cocoa
import CoreServices
import Darwin
import os
import ServiceManagement
import Sparkle

/// パフォーマンス診断（Console.app で subsystem `jp.tomippe.diskmonitor` をフィルタ）。
private enum DiskMonitorLog {
    private static let log = Logger(subsystem: "jp.tomippe.diskmonitor", category: "perf")

    static func slowIfNeeded(_ label: String, started: CFAbsoluteTime, thresholdMs: Double = 50, extra: String = "") {
        let ms = (CFAbsoluteTimeGetCurrent() - started) * 1000
        guard ms >= thresholdMs else { return }
        let suffix = extra.isEmpty ? "" : " \(extra)"
        log.warning("\(label, privacy: .public) \(ms, privacy: .public)ms main=\(Thread.isMainThread, privacy: .public)\(suffix, privacy: .public)")
    }

    @discardableResult
    static func measure<T>(_ label: String, thresholdMs: Double = 50, extra: String = "", _ work: () -> T) -> T {
        let started = CFAbsoluteTimeGetCurrent()
        let result = work()
        slowIfNeeded(label, started: started, thresholdMs: thresholdMs, extra: extra)
        return result
    }
}

private final class FileSystemChangeMonitor {
    private var stream: FSEventStreamRef?
    private var monitoredPaths: [String] = []
    private let onChange: () -> Void

    init(onChange: @escaping () -> Void) {
        self.onChange = onChange
    }

    deinit {
        stop()
    }

    func update(paths: [String]) {
        let normalizedPaths = Array(Set(paths)).sorted()
        guard normalizedPaths != monitoredPaths else { return }
        stop()
        monitoredPaths = normalizedPaths
        guard !normalizedPaths.isEmpty else { return }

        var context = FSEventStreamContext(
            version: 0,
            info: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque()),
            retain: nil,
            release: nil,
            copyDescription: nil
        )
        let callback: FSEventStreamCallback = { _, info, _, _, _, _ in
            guard let info else { return }
            let monitor = Unmanaged<FileSystemChangeMonitor>.fromOpaque(info).takeUnretainedValue()
            monitor.onChange()
        }
        stream = FSEventStreamCreate(
            kCFAllocatorDefault,
            callback,
            &context,
            normalizedPaths as CFArray,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
            2.0,
            FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagUseCFTypes)
        )
        guard let stream else { return }
        FSEventStreamSetDispatchQueue(stream, DispatchQueue.main)
        FSEventStreamStart(stream)
    }

    private func stop() {
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
        self.stream = nil
    }
}

private enum VolumeKind: Int, Comparable {
    case ssd = 0
    case hdd = 1
    case optical = 2
    case externalUSB = 3
    case network = 4
    case other = 5

    static func < (lhs: VolumeKind, rhs: VolumeKind) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

private struct VolumeRow {
    let url: URL
    let name: String
    let icon: NSImage
    let availableBytes: Int64
    let isNonFreeMetric: Bool
    let isEjectable: Bool
    /// `NSWorkspace.getFileSystemInfo` に基づく。取り出し不可でもマウント解除のみ可能な場合に使う。
    let isUnmountable: Bool
    let isRootFileSystem: Bool
    let kind: VolumeKind

    /// サブメニューに取り出しまたはマウント解除を出すか（システムボリュームは除外）。
    var showsDetachSubmenu: Bool {
        !isRootFileSystem && (kind == .network || isEjectable || isUnmountable)
    }

    /// 取り出しの代わりにマウント解除として扱うか（ネットワークボリュームは常にマウント解除）。
    var detachUsesUnmountLabel: Bool {
        kind == .network || (!isEjectable && isUnmountable)
    }
}

/// ボリュームの取り出し・マウント解除（ネットワークは 10 秒タイムアウト後に強制解除）。
private enum VolumeDetach {
    private static let unmountForceTimeout: TimeInterval = 10

    static func detach(row: VolumeRow, completion: @escaping (String?) -> Void) {
        if row.detachUsesUnmountLabel {
            DispatchQueue.global(qos: .userInitiated).async {
                let message = unmountWithTimeoutAndForce(at: row.url)
                DispatchQueue.main.async { completion(message) }
            }
        } else {
            DispatchQueue.main.async {
                do {
                    try NSWorkspace.shared.unmountAndEjectDevice(at: row.url)
                    completion(nil)
                } catch {
                    completion(error.localizedDescription)
                }
            }
        }
    }

    private static func unmountWithTimeoutAndForce(at mountURL: URL) -> String? {
        let deadline = Date().addingTimeInterval(unmountForceTimeout)
        let normalDone = DispatchSemaphore(value: 0)
        var normalError: Error?

        DispatchQueue.main.async {
            do {
                try NSWorkspace.shared.unmountAndEjectDevice(at: mountURL)
            } catch {
                normalError = error
            }
            normalDone.signal()
        }

        while Date() < deadline {
            if !isMountPointActive(mountURL) { return nil }
            if normalDone.wait(timeout: .now() + 0.5) == .success {
                while Date() < deadline {
                    if !isMountPointActive(mountURL) { return nil }
                    Thread.sleep(forTimeInterval: 0.5)
                }
                break
            }
        }

        if !isMountPointActive(mountURL) { return nil }

        let forced = forceUnmountVolume(at: mountURL)
        Thread.sleep(forTimeInterval: 0.5)
        if !isMountPointActive(mountURL) { return nil }

        if forced.detail.isEmpty {
            return normalError?.localizedDescription ?? NSLocalizedString("error.force_unmount_failed", comment: "")
        }
        return forced.detail
    }

    private static func isMountPointActive(_ mountURL: URL) -> Bool {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: mountURL.path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            return false
        }
        return (try? mountURL.resourceValues(forKeys: [.volumeLocalizedNameKey]).volumeLocalizedName) != nil
    }

    private static func shellCommand(_ launchPath: String, _ arguments: [String]) -> (status: Int32, output: String) {
        let task = Process()
        let pipe = Pipe()
        task.executableURL = URL(fileURLWithPath: launchPath)
        task.arguments = arguments
        task.standardOutput = pipe
        task.standardError = pipe
        do {
            try task.run()
        } catch {
            return (127, error.localizedDescription)
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        let text = String(data: data, encoding: .utf8) ?? ""
        return (task.terminationStatus, text.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private static func volumeDeviceIdentifier(for mountURL: URL) -> String? {
        let result = shellCommand("/usr/sbin/diskutil", ["info", mountURL.path])
        guard result.status == 0 else { return nil }
        for line in result.output.split(separator: "\n") {
            let text = String(line)
            guard text.contains("Device Identifier:") else { continue }
            return text.split(separator: ":", maxSplits: 1).last.map {
                $0.trimmingCharacters(in: .whitespaces)
            }
        }
        return nil
    }

    private static func forceUnmountVolume(at mountURL: URL) -> (success: Bool, detail: String) {
        let path = mountURL.path
        var lastDetail = ""

        for args in [["unmount", path], ["unmount", "force", path]] {
            let result = shellCommand("/usr/sbin/diskutil", args)
            if result.status == 0, !isMountPointActive(mountURL) {
                return (true, "")
            }
            if !result.output.isEmpty { lastDetail = result.output }
        }

        if let device = volumeDeviceIdentifier(for: mountURL) {
            let result = shellCommand("/usr/sbin/diskutil", ["unmount", "force", device])
            if result.status == 0, !isMountPointActive(mountURL) {
                return (true, "")
            }
            if !result.output.isEmpty { lastDetail = result.output }
        }

        let umount = shellCommand("/sbin/umount", ["-f", path])
        if umount.status == 0, !isMountPointActive(mountURL) {
            return (true, "")
        }
        if !umount.output.isEmpty { lastDetail = umount.output }

        if authorizedForceUnmount(path), !isMountPointActive(mountURL) {
            return (true, "")
        }

        return (false, lastDetail.isEmpty ? NSLocalizedString("error.force_unmount_failed", comment: "") : lastDetail)
    }

    private static func authorizedForceUnmount(_ path: String) -> Bool {
        let quoted = "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let prompt = NSLocalizedString("auth.force_unmount.prompt", comment: "")
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        let script = "do shell script \"/usr/sbin/diskutil unmount force \(quoted)\" with administrator privileges with prompt \"\(prompt)\""
        guard let appleScript = NSAppleScript(source: script) else { return false }
        var error: NSDictionary?
        appleScript.executeAndReturnError(&error)
        if (error?[NSAppleScript.errorNumber] as? Int16) == -128 { return false }
        return error == nil
    }
}

/// ディレクトリの中身を遅延展開するためのサブメニュー。
private final class DirectoryMenu: NSMenu {
    var directoryURL: URL?
    /// ボリューム直下のサブメニューのみ設定。取り出し項目を先頭に出す。
    var volumeRow: VolumeRow?
    var dwellWorkItem: DispatchWorkItem?
    var loadWatchdogWorkItem: DispatchWorkItem?
    var loadGeneration = 0
    var loadAttempt = 0
    var loaded = false
    var sizeUpdateGeneration = 0
    var pendingUpdateWorkItem: DispatchWorkItem?
    var folderSizeMenuItem: NSMenuItem?
    var folderItemCount = 0
    var includeHiddenFiles = false
    var favoriteMode: DirectoryFavoriteMode = .browse
    /// 親項目と縦位置を揃える行（要約・お気に入り等のヘッダーより下の最初のコンテンツ）。
    weak var alignBesideParentItem: NSMenuItem?
    var alignTimer: Timer?
}

private struct DirectorySnapshot {
    let entries: [(url: URL, isDir: Bool, name: String, isAccessible: Bool, isHidden: Bool)]
    let totalCount: Int
    let error: Error?
}

private enum DirectoryFavoriteMode {
    case browse
    case favorite
}

private enum FavoriteStore {
    private static let key = "DiskMonitorFavoriteFolders"

    static func loadPaths() -> [String] {
        guard let paths = UserDefaults.standard.stringArray(forKey: key) else { return [] }
        var seen = Set<String>()
        return paths.filter { path in
            guard !seen.contains(path) else { return false }
            seen.insert(path)
            return true
        }
    }

    static func load() -> [URL] {
        loadPaths().map { URL(fileURLWithPath: $0, isDirectory: true) }
    }

    private static func savePaths(_ paths: [String]) {
        UserDefaults.standard.set(paths, forKey: key)
    }

    static func isAvailable(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) && isDir.boolValue
    }

    static func contains(_ url: URL) -> Bool {
        loadPaths().contains(url.path)
    }

    static func add(_ url: URL) {
        var paths = loadPaths()
        guard !paths.contains(url.path) else { return }
        paths.append(url.path)
        savePaths(paths)
    }

    static func remove(_ url: URL) {
        var paths = loadPaths()
        paths.removeAll { $0 == url.path }
        savePaths(paths)
    }
}

private let diskMonitorIntroURL = URL(string: "https://apps.tomippe.jp/disk-monitor/")!

class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var timer: Timer?
    private var launchAtLoginMenuItem: NSMenuItem?
    private let updaterController = SPUStandardUpdaterController(
        startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil
    )
    private var fileSystemChangeMonitor: FileSystemChangeMonitor?
    private var pendingFileSystemRefresh: DispatchWorkItem?
    private let trashRefreshQueue = DispatchQueue(label: "jp.tomippe.diskmonitor.trash", qos: .utility)
    private var volumeRefreshInFlight = false
    private var volumeRefreshPending = false
    private var volumeRefreshPendingIncludeTrash = false
    private var volumeRefreshStartedAt: Date?
    private let volumeRefreshStuckTimeout: TimeInterval = 30
    private let refreshInterval: TimeInterval = 5
    private let trashRefreshInterval: TimeInterval = 30
    private var trashRefreshTimer: Timer?
    private let menuNameWidth = 20
    private let capacityColumnTabStop: CGFloat = 235
    private let ejectColumnTabStop: CGFloat = 275
    private let directoryFolderSizeTimeout: TimeInterval = 2
    private let directoryListingTimeout: TimeInterval = 8
    private let directoryLoadDwell: TimeInterval = 0.5
    private let directoryLoadWatchdogGrace: TimeInterval = 2
    private let directoryLoadMaxAttempts = 2
    /// タイムアウト後も I/O が残り得るため **concurrent** 必須。serial だとハングした
    /// ネットワークボリューム取得が後続のルート容量・ローカル取得を永久に塞ぐ。
    private static let blockingWorkQueue = DispatchQueue(
        label: "jp.tomippe.diskmonitor.blocking-work",
        qos: .utility,
        attributes: .concurrent
    )
    /// メニューバー表示専用（ネットワーク列挙と競合させない）
    private static let rootStatusQueue = DispatchQueue(
        label: "jp.tomippe.diskmonitor.root-status",
        qos: .userInitiated
    )
    private let directoryListingQueue: OperationQueue = {
        let queue = OperationQueue()
        queue.name = "jp.tomippe.diskmonitor.directory-listing"
        queue.maxConcurrentOperationCount = 2
        queue.qualityOfService = .utility
        return queue
    }()
    private let directorySizeQueue: OperationQueue = {
        let queue = OperationQueue()
        queue.name = "jp.tomippe.diskmonitor.directory-size"
        queue.maxConcurrentOperationCount = 2
        queue.qualityOfService = .utility
        return queue
    }()
    private let directoryIconQueue: OperationQueue = {
        let queue = OperationQueue()
        queue.name = "jp.tomippe.diskmonitor.directory-icon"
        queue.maxConcurrentOperationCount = 4
        queue.qualityOfService = .utility
        return queue
    }()
    private var volumeRows: [VolumeRow] = []
    private var statusText = NSLocalizedString("status.loading", comment: "")
    private var trashSizeBytes: Int64 = 0
    private var isMenuVisible = false
    private var favoriteSizeUpdateGeneration = 0
    private var menuNeedsRebuild = true
    private var pendingRootMenuVisualUpdate: DispatchWorkItem?
    private static var finderTrashSizeDenied = false

    func applicationWillFinishLaunching(_: Notification) {
        MoveToApplicationsFolder.moveIfNecessary()
    }

    func applicationDidFinishLaunching(_: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let img = NSImage(systemSymbolName: "internaldrive", accessibilityDescription: NSLocalizedString("a11y.disk", comment: "")) {
            img.isTemplate = true
            statusItem.button?.image = img
            statusItem.button?.imagePosition = .imageLeading
        }
        statusItem.button?.title = " \(statusText)"

        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        rebuildMenu()

        fileSystemChangeMonitor = FileSystemChangeMonitor { [weak self] in
            self?.scheduleRefreshAfterFileSystemChange()
        }
        let workspaceCenter = NSWorkspace.shared.notificationCenter
        workspaceCenter.addObserver(
            self,
            selector: #selector(workspaceVolumeChanged(_:)),
            name: NSWorkspace.didMountNotification,
            object: nil
        )
        workspaceCenter.addObserver(
            self,
            selector: #selector(workspaceVolumeChanged(_:)),
            name: NSWorkspace.didUnmountNotification,
            object: nil
        )
        refreshRootStatusQuick()
        refreshVolumes(includeTrash: true)
        timer = makeRunLoopTimer(interval: refreshInterval, tolerance: 2) { [weak self] in
            self?.refreshRootStatusQuick()
            self?.refreshVolumes()
        }
        trashRefreshTimer = makeRunLoopTimer(interval: trashRefreshInterval, tolerance: 5) { [weak self] in
            self?.refreshTrashSize()
        }
    }

    private func makeRunLoopTimer(interval: TimeInterval, tolerance: TimeInterval, handler: @escaping () -> Void) -> Timer {
        let timer = Timer(timeInterval: interval, repeats: true) { _ in handler() }
        timer.tolerance = tolerance
        RunLoop.main.add(timer, forMode: .common)
        RunLoop.main.add(timer, forMode: .eventTracking)
        return timer
    }

    @objc private func workspaceVolumeChanged(_: Notification) {
        Self.clearDiskInfoCache()
        scheduleRefreshAfterFileSystemChange()
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        if let dirMenu = menu as? DirectoryMenu, !dirMenu.loaded {
            showDirectoryWaitingPlaceholder(dirMenu)
        }
    }

    func menuWillOpen(_ menu: NSMenu) {
        if let dirMenu = menu as? DirectoryMenu {
            scheduleDirectoryDwellLoad(dirMenu)
            scheduleAlignDirectorySubmenu(dirMenu)
            return
        }
        isMenuVisible = true
        if menuNeedsRebuild {
            rebuildMenu()
            menuNeedsRebuild = false
        }
        syncLaunchAtLoginItem()
        refreshRootStatusQuick()
        refreshVolumes(includeTrash: true)
    }

    func menuDidClose(_ menu: NSMenu) {
        if let dirMenu = menu as? DirectoryMenu {
            dirMenu.loadGeneration &+= 1
            dirMenu.dwellWorkItem?.cancel()
            dirMenu.dwellWorkItem = nil
            dirMenu.loadWatchdogWorkItem?.cancel()
            dirMenu.loadWatchdogWorkItem = nil
            dirMenu.pendingUpdateWorkItem?.cancel()
            dirMenu.pendingUpdateWorkItem = nil
            dirMenu.alignTimer?.invalidate()
            dirMenu.alignTimer = nil
            dirMenu.alignBesideParentItem = nil
            dirMenu.sizeUpdateGeneration &+= 1
            dirMenu.loaded = false
            dirMenu.loadAttempt = 0
            return
        }
        isMenuVisible = false
    }

    private func syncLaunchAtLoginItem() {
        guard #available(macOS 13.0, *) else { return }
        guard let item = launchAtLoginMenuItem else { return }
        switch SMAppService.mainApp.status {
        case .enabled:
            item.state = .on
        case .requiresApproval:
            item.state = .mixed
        default:
            item.state = .off
        }
    }

    @objc private func toggleLaunchAtLogin() {
        guard #available(macOS 13.0, *) else { return }
        Task {
            do {
                let service = SMAppService.mainApp
                if service.status == .enabled {
                    try await service.unregister()
                } else {
                    try service.register()
                }
                await MainActor.run { self.syncLaunchAtLoginItem() }
            } catch {
                await MainActor.run {
                    let alert = NSAlert()
                    alert.messageText = NSLocalizedString("alert.loginitem_failed_title", comment: "")
                    alert.informativeText = error.localizedDescription
                    alert.alertStyle = .warning
                    alert.runModal()
                }
            }
        }
    }

    private func refreshVolumes(includeTrash: Bool = false) {
        if volumeRefreshInFlight {
            if let started = volumeRefreshStartedAt,
               Date().timeIntervalSince(started) >= volumeRefreshStuckTimeout {
                volumeRefreshInFlight = false
            } else {
                volumeRefreshPending = true
                volumeRefreshPendingIncludeTrash = volumeRefreshPendingIncludeTrash || includeTrash
                return
            }
        }
        volumeRefreshInFlight = true
        volumeRefreshStartedAt = Date()
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let rows = Self.readVolumes()
            DispatchQueue.main.async {
                guard let self else { return }
                self.volumeRefreshInFlight = false
                self.volumeRefreshStartedAt = nil
                self.applyVolumeRows(rows)
                if includeTrash {
                    self.refreshTrashSize()
                }
                if self.volumeRefreshPending {
                    let pendingTrash = self.volumeRefreshPendingIncludeTrash
                    self.volumeRefreshPending = false
                    self.volumeRefreshPendingIncludeTrash = false
                    self.refreshVolumes(includeTrash: pendingTrash)
                }
            }
        }
    }

    /// メニューバー表示専用。全ボリューム取得と独立し、inFlight でも常に走る。
    private func refreshRootStatusQuick() {
        Self.rootStatusQueue.async { [weak self] in
            guard let snapshot = Self.readRootStatusSnapshot() else { return }
            DispatchQueue.main.async {
                self?.applyRootStatusSnapshot(snapshot)
            }
        }
    }

    private struct RootStatusSnapshot {
        let availableBytes: Int64
        let name: String
        let icon: NSImage
    }

    private func applyRootStatusSnapshot(_ snapshot: RootStatusSnapshot) {
        statusText = formatBytes(snapshot.availableBytes, concise: true)
        statusItem.button?.title = " \(statusText)"
        let icon = snapshot.icon
        icon.size = NSSize(width: 18, height: 18)
        icon.isTemplate = false
        statusItem.button?.image = icon
        statusItem.button?.imagePosition = .imageLeading
        statusItem.button?.toolTip = String(
            format: NSLocalizedString("status.tooltip", comment: ""),
            snapshot.name,
            formatBytes(snapshot.availableBytes, concise: false)
        )

        if let index = volumeRows.firstIndex(where: { $0.isRootFileSystem }) {
            let old = volumeRows[index]
            volumeRows[index] = VolumeRow(
                url: old.url,
                name: old.name,
                icon: snapshot.icon,
                availableBytes: snapshot.availableBytes,
                isNonFreeMetric: old.isNonFreeMetric,
                isEjectable: old.isEjectable,
                isUnmountable: old.isUnmountable,
                isRootFileSystem: old.isRootFileSystem,
                kind: old.kind
            )
            rebuildMenuIfOpen()
        }
    }

    private func refreshTrashSize() {
        trashRefreshQueue.async { [weak self] in
            let trashBytes = Self.calculateTrashSizeBytes()
            DispatchQueue.main.async {
                self?.applyTrashSize(trashBytes)
            }
        }
    }

    private func applyVolumeRows(_ rows: [VolumeRow]) {
        if rows.isEmpty {
            if volumeRows.isEmpty {
                updateStatus()
                rebuildMenuIfOpen()
                updateFileSystemChangeMonitoring()
            }
            return
        }

        // メインスレッドで withTimeout / mountedVolumeURLs を待たない（UI・Sparkle・再起動が固まる）。
        // 取得タイムアウトで欠けたボリュームは、前回分のうちローカル系だけ暫定保持する。
        var merged = rows
        let newURLs = Set(rows.map(\.url))
        for old in volumeRows where !newURLs.contains(old.url) && old.kind != .network {
            merged.append(old)
        }
        merged = Self.sortVolumeRows(merged)

        volumeRows = merged
        updateStatus()
        menuNeedsRebuild = true
        rebuildMenuIfOpen()
        updateFileSystemChangeMonitoring()
    }

    private func applyTrashSize(_ bytes: Int64) {
        trashSizeBytes = bytes
        menuNeedsRebuild = true
        rebuildMenuIfOpen()
    }

    /// メニュー表示中のフル再構築はサブメニュー展開状態を壊すため行わない。
    private func rebuildMenuIfOpen() {
        guard isMenuVisible else { return }
    }

    private func scheduleRefreshAfterFileSystemChange() {
        pendingFileSystemRefresh?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            self?.refreshVolumes()
        }
        pendingFileSystemRefresh = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: workItem)
    }

    private func updateFileSystemChangeMonitoring() {
        var paths = Set(
            volumeRows
                .filter { $0.kind != .network && !$0.isNonFreeMetric }
                .map { $0.url.path }
        )
        paths.insert("/")
        paths.insert("/Volumes")
        fileSystemChangeMonitor?.update(paths: paths.sorted())
    }

    private static func sortVolumeRows(_ rows: [VolumeRow]) -> [VolumeRow] {
        rows.sorted {
            if $0.kind != $1.kind { return $0.kind < $1.kind }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    private static func readVolumes() -> [VolumeRow] {
        let started = CFAbsoluteTimeGetCurrent()
        let keys: [URLResourceKey] = [
            .volumeNameKey,
            .volumeLocalizedNameKey,
            .volumeAvailableCapacityForImportantUsageKey,
            .volumeAvailableCapacityKey,
            .volumeTotalCapacityKey,
            .volumeIsEjectableKey,
            .volumeIsRemovableKey,
            .volumeIsInternalKey,
            .volumeIsLocalKey,
            .volumeIsReadOnlyKey,
            .volumeIsRootFileSystemKey,
            .effectiveIconKey,
        ]

        guard let urlsList = withTimeout(5.0, {
            FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes])
        }), let urls = urlsList else {
            return []
        }

        var rows: [VolumeRow] = []
        let sortedURLs = urls.sorted { lhs, rhs in
            if lhs.path == "/" { return true }
            if rhs.path == "/" { return false }
            return lhs.path.localizedCaseInsensitiveCompare(rhs.path) == .orderedAscending
        }
        for url in sortedURLs {
            guard let quick = resourceValues(for: url, keys: [.volumeIsLocalKey], timeout: 2.0) else { continue }
            let isNetwork = quick.volumeIsLocal == false
            let resourceTimeout: TimeInterval = isNetwork ? 2.0 : 8.0
            guard let values = resourceValues(for: url, keys: Set(keys), timeout: resourceTimeout) else { continue }
            // Finder の「使用可能」は importantUsage（パージ可能領域を含む）を表示する。
            // 外部・ネットワークボリュームでは importantUsage が 0 のため availableCapacity を使う。
            let available = values.volumeAvailableCapacityForImportantUsage.flatMap { $0 > 0 ? $0 : nil }
                ?? values.volumeAvailableCapacity.map(Int64.init)
                ?? (isNetwork ? nil : freeBytes(for: url))
            guard let available else { continue }

            let name = values.volumeLocalizedName ?? values.volumeName ?? url.lastPathComponent
            let isEjectable = values.volumeIsEjectable ?? false
            let isRoot = values.volumeIsRootFileSystem ?? (url.path == "/")
            // システムボリュームは eject 対象外のため、重い diskutil / getFileSystemInfo を省略する。
            let isUnmountable: Bool
            let info: [String: Any]
            if isRoot {
                isUnmountable = false
                info = [:]
            } else {
                isUnmountable = isNetwork
                    ? true
                    : pathReportsUnmountable(url.path)
                info = isNetwork ? [:] : diskInfo(for: url.path)
            }
            let icon = volumeIcon(for: url.path, effectiveIcon: values.effectiveIcon as? NSImage)
            let kind = classifyVolume(values: values, info: info)
            let nonFreeMetric = isNonFreeMetric(values: values, info: info)
            rows.append(
                VolumeRow(
                    url: url,
                    name: name,
                    icon: icon,
                    availableBytes: Int64(available),
                    isNonFreeMetric: nonFreeMetric,
                    isEjectable: isEjectable,
                    isUnmountable: isUnmountable,
                    isRootFileSystem: isRoot,
                    kind: kind
                )
            )
        }

        DiskMonitorLog.slowIfNeeded("readVolumes", started: started, thresholdMs: 100, extra: "count=\(rows.count)")
        return sortVolumeRows(rows)
    }

    private static func readRootStatusSnapshot() -> RootStatusSnapshot? {
        let root = URL(fileURLWithPath: "/")
        // Finder / メニュー一覧と同じ importantUsage を使う（systemFreeSize と混ぜると数 GB ちらつく）。
        // rootStatusQueue 上で直接取得し、ネットワーク列挙キューには載せない。
        let keys: Set<URLResourceKey> = [
            .volumeAvailableCapacityForImportantUsageKey,
            .volumeAvailableCapacityKey,
            .volumeLocalizedNameKey,
            .volumeNameKey,
            .effectiveIconKey,
        ]
        let values = try? root.resourceValues(forKeys: keys)
        let available = values?.volumeAvailableCapacityForImportantUsage.flatMap { $0 > 0 ? $0 : nil }
            ?? values?.volumeAvailableCapacity.map(Int64.init)
            ?? freeBytes(for: root)
        guard let available else { return nil }
        let name = values?.volumeLocalizedName ?? values?.volumeName ?? "Macintosh HD"
        let icon = values?.effectiveIcon as? NSImage
            ?? NSWorkspace.shared.icon(forFile: root.path)
        return RootStatusSnapshot(availableBytes: Int64(available), name: name, icon: icon)
    }

    private static func volumeIcon(for path: String, effectiveIcon: NSImage?) -> NSImage {
        if let effectiveIcon { return effectiveIcon }
        if let icon = withTimeout(2.0, { NSWorkspace.shared.icon(forFile: path) }) {
            return icon
        }
        return NSImage(systemSymbolName: "internaldrive", accessibilityDescription: nil)
            ?? NSImage(size: NSSize(width: 18, height: 18))
    }

    /// `NSWorkspace.getFileSystemInfoForPath` の isUnmountable に相当。
    private static func pathReportsUnmountable(_ path: String) -> Bool {
        withTimeout(2.0) {
            var removable = ObjCBool(false)
            var writable = ObjCBool(false)
            var unmountable = ObjCBool(false)
            var description: NSString?
            var fsType: NSString?
            guard NSWorkspace.shared.getFileSystemInfo(
                forPath: path,
                isRemovable: &removable,
                isWritable: &writable,
                isUnmountable: &unmountable,
                description: &description,
                type: &fsType
            ) else {
                return false
            }
            return unmountable.boolValue
        } ?? false
    }

    private static func classifyVolume(values: URLResourceValues, info: [String: Any]) -> VolumeKind {
        if (values.volumeIsLocal == false) {
            return .network
        }

        let fileSystem = (info["FilesystemType"] as? String)?.lowercased() ?? ""
        let bus = (info["BusProtocol"] as? String)?.lowercased() ?? ""

        if ["cd9660", "udf"].contains(fileSystem) || bus.contains("atapi") || bus.contains("optical") {
            return .optical
        }

        if values.volumeIsInternal == true {
            if let isSSD = info["SolidState"] as? Bool {
                return isSSD ? .ssd : .hdd
            }
            return .ssd
        }

        if (values.volumeIsRemovable == true || values.volumeIsEjectable == true) && values.volumeIsLocal == true {
            return .externalUSB
        }

        return .other
    }

    private static func withTimeout<T>(_ timeout: TimeInterval, _ work: @escaping () -> T) -> T? {
        let semaphore = DispatchSemaphore(value: 0)
        var result: T?
        blockingWorkQueue.async {
            result = work()
            semaphore.signal()
        }
        if semaphore.wait(timeout: .now() + timeout) == .timedOut {
            return nil
        }
        return result
    }

    /// Process をタイムアウト付きで実行する。
    /// 旧実装は `readDataToEndOfFile` を先に待つため、timeout→terminate 後もパイプ EOF 待ちで
    /// ユーティリティスレッドが永久に残り、数時間後に GCD / ディレクトリ一覧が枯渇していた。
    @discardableResult
    private static func runProcess(
        executable: String,
        arguments: [String],
        timeout: TimeInterval
    ) -> (status: Int32, stdout: Data)? {
        let started = CFAbsoluteTimeGetCurrent()
        let task = Process()
        let outPipe = Pipe()
        let errPipe = Pipe()
        task.executableURL = URL(fileURLWithPath: executable)
        task.arguments = arguments
        task.standardOutput = outPipe
        task.standardError = errPipe

        do {
            try task.run()
        } catch {
            return nil
        }

        let outputLock = NSLock()
        var stdoutData = Data()
        outPipe.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            guard !chunk.isEmpty else {
                handle.readabilityHandler = nil
                return
            }
            outputLock.lock()
            stdoutData.append(chunk)
            outputLock.unlock()
        }
        // stderr を捨てつつパイプ詰まらせない
        errPipe.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            if chunk.isEmpty {
                handle.readabilityHandler = nil
            }
        }

        let done = DispatchSemaphore(value: 0)
        DispatchQueue.global(qos: .utility).async {
            task.waitUntilExit()
            done.signal()
        }

        if done.wait(timeout: .now() + timeout) == .timedOut {
            forceTerminateProcess(task)
            outPipe.fileHandleForReading.readabilityHandler = nil
            errPipe.fileHandleForReading.readabilityHandler = nil
            try? outPipe.fileHandleForReading.close()
            try? errPipe.fileHandleForReading.close()
            _ = done.wait(timeout: .now() + 1.0)
            DiskMonitorLog.slowIfNeeded(
                "runProcess timeout",
                started: started,
                thresholdMs: 0,
                extra: "\(executable) \(arguments.joined(separator: " "))"
            )
            return nil
        }

        outPipe.fileHandleForReading.readabilityHandler = nil
        errPipe.fileHandleForReading.readabilityHandler = nil
        let remaining = outPipe.fileHandleForReading.readDataToEndOfFile()
        _ = errPipe.fileHandleForReading.readDataToEndOfFile()
        outputLock.lock()
        stdoutData.append(remaining)
        let data = stdoutData
        outputLock.unlock()
        return (task.terminationStatus, data)
    }

    private static func forceTerminateProcess(_ task: Process) {
        guard task.isRunning else { return }
        task.terminate()
        let deadline = Date().addingTimeInterval(0.2)
        while task.isRunning, Date() < deadline {
            Thread.sleep(forTimeInterval: 0.02)
        }
        if task.isRunning {
            kill(task.processIdentifier, SIGKILL)
        }
    }

    private static func isNonFreeMetric(values: URLResourceValues, info: [String: Any]) -> Bool {
        let bus = (info["BusProtocol"] as? String)?.lowercased() ?? ""
        if bus.contains("disk image") || bus.contains("image") {
            return true
        }
        if values.volumeIsReadOnly == true, values.volumeIsInternal != true {
            return true
        }
        return false
    }

    private static func resourceValues(
        for url: URL,
        keys: Set<URLResourceKey>,
        timeout: TimeInterval
    ) -> URLResourceValues? {
        let semaphore = DispatchSemaphore(value: 0)
        var result: URLResourceValues?
        blockingWorkQueue.async {
            result = try? url.resourceValues(forKeys: keys)
            semaphore.signal()
        }
        if semaphore.wait(timeout: .now() + timeout) == .timedOut {
            return nil
        }
        return result
    }

    private static let diskInfoCacheLock = NSLock()
    private static var diskInfoCache: [String: (info: [String: Any], cachedAt: Date, positive: Bool)] = [:]
    private static let diskInfoPositiveTTL: TimeInterval = 600
    private static let diskInfoNegativeTTL: TimeInterval = 60

    private static func clearDiskInfoCache() {
        diskInfoCacheLock.lock()
        diskInfoCache.removeAll()
        diskInfoCacheLock.unlock()
    }

    private static func diskInfo(for mountPath: String, timeout: TimeInterval = 2) -> [String: Any] {
        diskInfoCacheLock.lock()
        if let cached = diskInfoCache[mountPath] {
            let ttl = cached.positive ? diskInfoPositiveTTL : diskInfoNegativeTTL
            if Date().timeIntervalSince(cached.cachedAt) < ttl {
                let info = cached.info
                diskInfoCacheLock.unlock()
                return info
            }
        }
        diskInfoCacheLock.unlock()

        guard let result = runProcess(
            executable: "/usr/sbin/diskutil",
            arguments: ["info", "-plist", mountPath],
            timeout: timeout
        ), result.status == 0 else {
            diskInfoCacheLock.lock()
            diskInfoCache[mountPath] = ([:], Date(), false)
            diskInfoCacheLock.unlock()
            return [:]
        }
        guard let plist = try? PropertyListSerialization.propertyList(from: result.stdout, options: [], format: nil),
              let dict = plist as? [String: Any] else {
            diskInfoCacheLock.lock()
            diskInfoCache[mountPath] = ([:], Date(), false)
            diskInfoCacheLock.unlock()
            return [:]
        }
        diskInfoCacheLock.lock()
        diskInfoCache[mountPath] = (dict, Date(), true)
        diskInfoCacheLock.unlock()
        return dict
    }

    private static func freeBytes(for url: URL) -> Int64? {
        guard let attrsMap = withTimeout(2.0, {
            try? FileManager.default.attributesOfFileSystem(forPath: url.path)
        }), let attrs = attrsMap else {
            return nil
        }
        if let free = attrs[.systemFreeSize] as? NSNumber {
            return free.int64Value
        }
        if let free = attrs[.systemFreeSize] as? Int64 {
            return free
        }
        return nil
    }

    private func updateStatus() {
        guard let systemVolume = volumeRows.first(where: { $0.isRootFileSystem }) ?? volumeRows.first else {
            statusText = NSLocalizedString("status.unavailable", comment: "")
            statusItem.button?.title = " \(statusText)"
            return
        }

        let availableBytes = displayedAvailableBytes(for: systemVolume)
        statusText = formatBytes(availableBytes, concise: true)
        statusItem.button?.title = " \(statusText)"

        let icon = systemVolume.icon
        icon.size = NSSize(width: 18, height: 18)
        icon.isTemplate = false
        statusItem.button?.image = icon
        statusItem.button?.imagePosition = .imageLeading
        statusItem.button?.toolTip = String(format: NSLocalizedString("status.tooltip", comment: ""), systemVolume.name, formatBytes(availableBytes, concise: false))
    }

    private func rebuildMenu() {
        guard let menu = statusItem.menu else { return }
        let started = CFAbsoluteTimeGetCurrent()
        favoriteSizeUpdateGeneration &+= 1
        let favoriteGeneration = favoriteSizeUpdateGeneration
        let volumeCount = volumeRows.count
        let favoriteCount = FavoriteStore.loadPaths().count
        menu.removeAllItems()

        if volumeRows.isEmpty {
            let item = NSMenuItem(title: NSLocalizedString("menu.no_volumes", comment: ""), action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        } else {
            for row in volumeRows {
                menu.addItem(volumeMenuItem(for: row))
            }
        }

        let favorites = FavoriteStore.load()
        menu.addItem(.separator())
        if !favorites.isEmpty {
            for url in favorites {
                menu.addItem(favoriteMenuItem(for: url, generation: favoriteGeneration))
            }
            menu.addItem(.separator())
        }
        menu.addItem(trashMenuItem())
        menu.addItem(.separator())
        menu.addItem(sectionMenuItem(NSLocalizedString("menu.copy_status", comment: ""), #selector(copyStatus), "c", symbolName: "doc.on.doc"))
        menu.addItem(sectionMenuItem(NSLocalizedString("menu.refresh", comment: ""), #selector(refreshNow), "r", symbolName: "arrow.clockwise"))
        menu.addItem(diskUtilityMenuItem())
        menu.addItem(.separator())

        launchAtLoginMenuItem = nil
        if #available(macOS 13.0, *) {
            let loginItem = NSMenuItem(
                title: NSLocalizedString("menu.login_item", comment: ""),
                action: #selector(toggleLaunchAtLogin),
                keyEquivalent: ""
            )
            loginItem.target = self
            menu.addItem(loginItem)
            launchAtLoginMenuItem = loginItem
            syncLaunchAtLoginItem()
            menu.addItem(.separator())
        }
        menu.addItem(sectionMenuItem(
            NSLocalizedString("menu.about", comment: ""),
            #selector(showAboutPanel),
            "",
            symbolName: "info.circle"
        ))
        menu.addItem(sectionMenuItem(
            NSLocalizedString("menu.send_feedback", comment: ""),
            #selector(openFeedbackForm),
            "",
            symbolName: "star.bubble"
        ))
        menu.addItem(sparkleCheckForUpdatesMenuItem())
        menu.addItem(.separator())
        menu.addItem(TomippeRelaunch.restartMenuItem(
            appDisplayName: "Disk Monitor",
            target: self,
            action: #selector(restartApp)
        ))
        menu.addItem(TomippeRelaunch.quitMenuItem(
            appDisplayName: "Disk Monitor",
            target: self,
            action: #selector(quit),
            keyEquivalent: "q"
        ))
        DiskMonitorLog.slowIfNeeded(
            "rebuildMenu",
            started: started,
            thresholdMs: 30,
            extra: "volumes=\(volumeCount) favorites=\(favoriteCount)"
        )
    }

    private func scheduleRootMenuVisualUpdate() {
        pendingRootMenuVisualUpdate?.cancel()
        let work = DispatchWorkItem { [weak self] in
            self?.statusItem.menu?.update()
        }
        pendingRootMenuVisualUpdate = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2, execute: work)
    }

    private func favoriteMenuItem(for url: URL, generation: Int) -> NSMenuItem {
        let available = FavoriteStore.isAvailable(url)
        let name = ellipsized(Self.displayName(for: url), maxLength: menuNameWidth)
        let item = NSMenuItem(title: favoriteMenuTitle(name: name), action: #selector(openFileFromMenu(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = url
        item.attributedTitle = favoriteMenuAttributedTitle(name: name)
        item.image = directoryEntryPlaceholderIcon(isDirectory: true)
        if available {
            item.submenu = makeDirectoryMenu(for: url, favoriteMode: .favorite)
            scheduleFavoriteItemSize(item: item, url: url, name: name, generation: generation)
            DispatchQueue.global(qos: .utility).async {
                let icon = NSWorkspace.shared.icon(forFile: url.path)
                icon.size = NSSize(width: 16, height: 16)
                DispatchQueue.main.async {
                    item.image = icon
                }
            }
        } else {
            item.action = nil
            item.attributedTitle = favoriteMenuAttributedTitle(name: name, unavailable: true)
            item.submenu = unavailableFavoriteSubmenu(for: url)
        }
        return item
    }

    private func unavailableFavoriteSubmenu(for url: URL) -> NSMenu {
        let menu = NSMenu()
        menu.addItem(removeFromFavoritesMenuItem(for: url))
        return menu
    }

    private func favoriteMenuTitle(name: String, sizeText: String = "") -> String {
        "\(name)\t\(sizeText)\t"
    }

    private func favoriteMenuAttributedTitle(name: String, sizeText: String = "", unavailable: Bool = false) -> NSAttributedString {
        let title = favoriteMenuTitle(name: name, sizeText: sizeText)
        let baseFont = NSFont.menuFont(ofSize: 0)
        var attrs: [NSAttributedString.Key: Any] = [
            .font: baseFont,
            .paragraphStyle: volumeMenuParagraphStyle()
        ]
        if unavailable {
            attrs[.foregroundColor] = NSColor.disabledControlTextColor
        }
        let attributed = NSMutableAttributedString(string: title, attributes: attrs)
        guard !sizeText.isEmpty else { return attributed }

        let valueStart = name.count + 1
        let valueRange = NSRange(location: valueStart, length: sizeText.count)
        let italic = NSFontManager.shared.convert(baseFont, toHaveTrait: .italicFontMask)
        attributed.addAttribute(.font, value: italic, range: valueRange)
        if unavailable {
            attributed.addAttribute(.foregroundColor, value: NSColor.disabledControlTextColor, range: valueRange)
        }
        return attributed
    }

    private func scheduleFavoriteItemSize(item: NSMenuItem, url: URL, name: String, generation: Int) {
        directorySizeQueue.addOperation { [weak self, weak item] in
            guard let self else { return }
            let bytes = Self.directorySizeViaDu(at: url, timeout: self.directoryFolderSizeTimeout)
            let sizeText = bytes.map { self.formatBytes($0, concise: false) } ?? ""
            DispatchQueue.main.async { [weak item] in
                guard let item, self.favoriteSizeUpdateGeneration == generation else { return }
                item.title = self.favoriteMenuTitle(name: name, sizeText: sizeText)
                item.attributedTitle = self.favoriteMenuAttributedTitle(name: name, sizeText: sizeText)
                self.scheduleRootMenuVisualUpdate()
            }
        }
    }

    private func volumeMenuItem(for row: VolumeRow) -> NSMenuItem {
        let item = NSMenuItem(title: volumeMenuTitle(for: row), action: #selector(openVolumeFromMenu(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = row
        item.attributedTitle = volumeMenuAttributedTitle(for: row)
        let icon = row.icon.copy() as? NSImage ?? row.icon
        icon.size = NSSize(width: 16, height: 16)
        item.image = icon
        item.submenu = makeDirectoryMenu(for: row.url, volumeRow: row)
        return item
    }

    // MARK: - ディレクトリ展開（遅延）

    private func makeDirectoryMenu(for url: URL, volumeRow: VolumeRow? = nil, favoriteMode: DirectoryFavoriteMode = .browse) -> DirectoryMenu {
        let menu = DirectoryMenu()
        menu.directoryURL = url
        menu.volumeRow = volumeRow
        menu.favoriteMode = favoriteMode
        menu.delegate = self
        showDirectoryWaitingPlaceholder(menu)
        return menu
    }

    private func showDirectoryWaitingPlaceholder(_ menu: DirectoryMenu) {
        menu.removeAllItems()
        menu.alignBesideParentItem = nil
        let item = NSMenuItem(title: NSLocalizedString("menu.directory_loading", comment: ""), action: nil, keyEquivalent: "")
        item.isEnabled = false
        menu.addItem(item)
    }

    // MARK: - サブメニュー縦位置（Windows AlignBesideParent 相当）

    /// メニュー追跡中は `.eventTracking` でないとメインキューが動かないため Timer で揃える。
    private func scheduleAlignDirectorySubmenu(_ menu: DirectoryMenu, attempt: Int = 0) {
        menu.alignTimer?.invalidate()
        let delay: TimeInterval = attempt == 0 ? 0.02 : 0.04
        let timer = Timer(timeInterval: delay, repeats: false) { [weak self, weak menu] _ in
            guard let self, let menu else { return }
            menu.alignTimer = nil
            if self.alignDirectorySubmenuIfNeeded(menu) { return }
            if attempt < 6 {
                self.scheduleAlignDirectorySubmenu(menu, attempt: attempt + 1)
            }
        }
        RunLoop.main.add(timer, forMode: .eventTracking)
        RunLoop.main.add(timer, forMode: .common)
        menu.alignTimer = timer
    }

    /// 親項目の真横に `alignBesideParentItem` が来るようサブメニュー窓をずらす。成功で true。
    @discardableResult
    private func alignDirectorySubmenuIfNeeded(_ menu: DirectoryMenu) -> Bool {
        guard let alignItem = menu.alignBesideParentItem, alignItem.menu === menu else { return true }
        guard let parentItem = menu.supermenu?.items.first(where: { $0.submenu === menu }) else { return true }

        let parentFrame = parentItem.accessibilityFrame()
        let alignFrame = alignItem.accessibilityFrame()
        guard parentFrame.height > 1, alignFrame.height > 1 else { return false }

        let delta = parentFrame.midY - alignFrame.midY
        guard abs(delta) >= 0.5 else { return true }
        guard let window = popupMenuWindow(for: menu) else { return false }

        var frame = window.frame
        frame.origin.y += delta
        if let screen = window.screen ?? NSScreen.main {
            let visible = screen.visibleFrame
            if frame.maxY > visible.maxY {
                frame.origin.y = visible.maxY - frame.height
            }
            if frame.minY < visible.minY {
                frame.origin.y = visible.minY
            }
        }
        window.setFrame(frame, display: true)
        return true
    }

    private func popupMenuWindow(for menu: NSMenu) -> NSWindow? {
        let menuFrame = menu.accessibilityFrame()
        guard menuFrame.width > 1, menuFrame.height > 1 else { return nil }
        return NSApp.windows.first { window in
            guard window.isVisible else { return false }
            let name = NSStringFromClass(type(of: window))
            guard name.contains("NSPopupMenuWindow") else { return false }
            return window.frame.intersects(menuFrame)
        }
    }

    private static func optionKeyPressed() -> Bool {
        NSEvent.modifierFlags.contains(.option)
    }

    private func scheduleDirectoryDwellLoad(_ menu: DirectoryMenu) {
        menu.dwellWorkItem?.cancel()
        menu.loadWatchdogWorkItem?.cancel()
        guard !menu.loaded else { return }

        let generation = menu.loadGeneration
        let work = DispatchWorkItem { [weak self, weak menu] in
            self?.beginDirectoryLoad(menu, generation: generation)
        }
        menu.dwellWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + directoryLoadDwell, execute: work)
        scheduleDirectoryLoadWatchdog(menu, generation: generation)
    }

    private func beginDirectoryLoad(_ menu: DirectoryMenu?, generation: Int) {
        guard let menu, menu.loadGeneration == generation, let dir = menu.directoryURL else { return }
        let includeHidden = Self.optionKeyPressed()
        menu.includeHiddenFiles = includeHidden
        let volumeRow = menu.volumeRow
        directoryListingQueue.addOperation { [weak self, weak menu] in
            guard let self, let menu else { return }
            let snapshot = Self.readDirectorySnapshot(
                at: dir,
                timeout: self.directoryListingTimeout,
                includeHiddenFiles: includeHidden
            )
            DispatchQueue.main.async { [weak self, weak menu] in
                guard let self, let menu,
                      menu.loadGeneration == generation,
                      menu.directoryURL == dir else { return }
                menu.loadWatchdogWorkItem?.cancel()
                menu.loadWatchdogWorkItem = nil
                // update() → menuNeedsUpdate が !loaded でプレースホルダに戻さないよう先に立てる
                menu.loaded = true
                menu.loadAttempt = 0
                self.applyDirectorySnapshot(snapshot, to: menu, volumeRow: volumeRow)
                menu.update()
                // update 後に窓位置が変わることがあるので、揃えるのはその後。
                self.scheduleAlignDirectorySubmenu(menu)
            }
        }
    }

    private func scheduleDirectoryLoadWatchdog(_ menu: DirectoryMenu, generation: Int) {
        let delay = directoryLoadDwell + directoryListingTimeout + directoryLoadWatchdogGrace
        let watchdog = DispatchWorkItem { [weak self, weak menu] in
            guard let self, let menu,
                  menu.loadGeneration == generation,
                  !menu.loaded else { return }
            DiskMonitorLog.slowIfNeeded(
                "directoryLoadWatchdog",
                started: CFAbsoluteTimeGetCurrent(),
                thresholdMs: 0,
                extra: "\(menu.directoryURL?.path ?? "?") attempt=\(menu.loadAttempt + 1)"
            )
            self.recoverStuckDirectoryMenu(menu)
        }
        menu.loadWatchdogWorkItem = watchdog
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: watchdog)
    }

    private func recoverStuckDirectoryMenu(_ menu: DirectoryMenu) {
        menu.loadGeneration &+= 1
        menu.dwellWorkItem?.cancel()
        menu.dwellWorkItem = nil
        menu.loadWatchdogWorkItem?.cancel()
        menu.loadWatchdogWorkItem = nil
        menu.pendingUpdateWorkItem?.cancel()
        menu.pendingUpdateWorkItem = nil
        menu.alignTimer?.invalidate()
        menu.alignTimer = nil
        menu.sizeUpdateGeneration &+= 1
        menu.loaded = false

        if menu.loadAttempt < directoryLoadMaxAttempts {
            menu.loadAttempt += 1
            showDirectoryWaitingPlaceholder(menu)
            menu.update()
            scheduleDirectoryDwellLoad(menu)
            return
        }
        showDirectoryLoadRecovery(menu)
        menu.update()
        scheduleAlignDirectorySubmenu(menu)
    }

    private func showDirectoryLoadRecovery(_ menu: DirectoryMenu) {
        menu.removeAllItems()
        menu.alignBesideParentItem = nil
        let message = NSMenuItem(
            title: NSLocalizedString("menu.directory_load_stuck", comment: ""),
            action: nil,
            keyEquivalent: ""
        )
        message.isEnabled = false
        menu.addItem(message)
        menu.addItem(.separator())
        let retry = NSMenuItem(
            title: NSLocalizedString("menu.directory_reload", comment: ""),
            action: #selector(reloadDirectoryMenu(_:)),
            keyEquivalent: ""
        )
        retry.target = self
        retry.representedObject = menu
        menu.addItem(retry)
        menu.alignBesideParentItem = retry
    }

    @objc private func reloadDirectoryMenu(_ sender: NSMenuItem) {
        guard let menu = sender.representedObject as? DirectoryMenu else { return }
        menu.loadGeneration &+= 1
        menu.dwellWorkItem?.cancel()
        menu.dwellWorkItem = nil
        menu.loadWatchdogWorkItem?.cancel()
        menu.loadWatchdogWorkItem = nil
        menu.pendingUpdateWorkItem?.cancel()
        menu.pendingUpdateWorkItem = nil
        menu.alignTimer?.invalidate()
        menu.alignTimer = nil
        menu.sizeUpdateGeneration &+= 1
        menu.loaded = false
        menu.loadAttempt = 0
        showDirectoryWaitingPlaceholder(menu)
        menu.update()
        scheduleDirectoryDwellLoad(menu)
    }

    private static func readDirectorySnapshot(
        at dir: URL,
        timeout: TimeInterval,
        includeHiddenFiles: Bool = false
    ) -> DirectorySnapshot {
        let started = CFAbsoluteTimeGetCurrent()
        let keySet: Set<URLResourceKey> = [.isDirectoryKey, .isHiddenKey, .localizedNameKey, .nameKey]
        let keyList = Array(keySet)
        let listingOptions: FileManager.DirectoryEnumerationOptions = includeHiddenFiles ? [] : [.skipsHiddenFiles]
        // 列挙だけでなく属性取得・アクセス判定も含めてタイムアウトする（途中ハングで OperationQueue を塞がない）
        let timeoutMessage = NSLocalizedString("menu.directory_list_timeout", comment: "")
        guard let built = withTimeout(timeout, { () -> DirectorySnapshot in
            guard let contents = try? FileManager.default.contentsOfDirectory(
                at: dir,
                includingPropertiesForKeys: keyList,
                options: listingOptions
            ) else {
                return DirectorySnapshot(
                    entries: [],
                    totalCount: 0,
                    error: NSError(
                        domain: "DiskMonitor",
                        code: 2,
                        userInfo: [NSLocalizedDescriptionKey: timeoutMessage]
                    )
                )
            }
            let entries: [(url: URL, isDir: Bool, name: String, isAccessible: Bool, isHidden: Bool)] = contents.map { url in
                let vals = try? url.resourceValues(forKeys: keySet)
                let isDir = vals?.isDirectory ?? false
                let name = vals?.localizedName ?? vals?.name ?? url.lastPathComponent
                let accessible = isDirectoryEntryAccessible(at: url, isDirectory: isDir)
                let isHidden = vals?.isHidden == true || name.hasPrefix(".")
                return (url, isDir, name, accessible, isHidden)
            }.sorted { a, b in
                if a.isDir != b.isDir { return a.isDir && !b.isDir }
                return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
            }
            return DirectorySnapshot(entries: entries, totalCount: entries.count, error: nil)
        }) else {
            DiskMonitorLog.slowIfNeeded(
                "readDirectorySnapshot timeout",
                started: started,
                thresholdMs: 0,
                extra: dir.path
            )
            return DirectorySnapshot(
                entries: [],
                totalCount: 0,
                error: NSError(domain: "DiskMonitor", code: 1, userInfo: [NSLocalizedDescriptionKey: timeoutMessage])
            )
        }
        DiskMonitorLog.slowIfNeeded(
            "readDirectorySnapshot",
            started: started,
            thresholdMs: 100,
            extra: "\(dir.path) entries=\(built.entries.count) hidden=\(includeHiddenFiles)"
        )
        return built
    }

    private func applyDirectorySnapshot(_ snapshot: DirectorySnapshot, to menu: DirectoryMenu, volumeRow: VolumeRow?) {
        let started = CFAbsoluteTimeGetCurrent()
        menu.removeAllItems()
        menu.alignBesideParentItem = nil
        menu.sizeUpdateGeneration &+= 1
        menu.folderSizeMenuItem = nil
        guard let dir = menu.directoryURL else { return }

        let generation = menu.sizeUpdateGeneration
        let itemCount = snapshot.error == nil ? snapshot.totalCount : 0
        menu.folderItemCount = itemCount
        let summaryItem = NSMenuItem(
            title: directoryFolderSummaryTitle(itemCount: itemCount, includesHidden: menu.includeHiddenFiles),
            action: #selector(openFileFromMenu(_:)),
            keyEquivalent: ""
        )
        summaryItem.target = self
        summaryItem.representedObject = dir
        menu.folderSizeMenuItem = summaryItem
        menu.addItem(summaryItem)

        switch menu.favoriteMode {
        case .favorite:
            menu.addItem(removeFromFavoritesMenuItem(for: dir))
        case .browse:
            if menu.volumeRow == nil, !FavoriteStore.contains(dir) {
                menu.addItem(addToFavoritesMenuItem(for: dir))
            }
        }

        menu.addItem(.separator())
        scheduleDirectoryFolderSize(menu: menu, generation: generation, url: dir)

        if let row = volumeRow, row.showsDetachSubmenu {
            menu.addItem(detachMenuItem(for: row))
            menu.addItem(.separator())
        }

        if let error = snapshot.error {
            let format = NSLocalizedString("menu.directory_read_error", comment: "")
            let err = NSMenuItem(title: String(format: format, error.localizedDescription), action: nil, keyEquivalent: "")
            err.isEnabled = false
            menu.addItem(err)
            menu.alignBesideParentItem = err
            return
        }

        if snapshot.entries.isEmpty {
            let empty = NSMenuItem(
                title: NSLocalizedString("menu.directory_empty", comment: ""),
                action: nil,
                keyEquivalent: ""
            )
            empty.isEnabled = false
            menu.addItem(empty)
            menu.alignBesideParentItem = empty
            return
        }

        var iconEntries: [(url: URL, item: NSMenuItem)] = []
        var didSetAlign = false

        for entry in snapshot.entries {
            let item = NSMenuItem(title: entry.name, action: #selector(openFileFromMenu(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = entry.url
            item.image = directoryEntryPlaceholderIcon(isDirectory: entry.isDir)
            if entry.isHidden {
                item.attributedTitle = hiddenDirectoryEntryAttributedTitle(entry.name)
            }
            if entry.isAccessible {
                if entry.isDir, Self.isBrowsableDirectory(at: entry.url) {
                    item.submenu = makeDirectoryMenu(for: entry.url)
                }
            } else {
                item.isEnabled = false
                item.action = nil
            }
            menu.addItem(item)
            if !didSetAlign {
                menu.alignBesideParentItem = item
                didSetAlign = true
            }
            iconEntries.append((entry.url, item))
        }

        scheduleDirectoryEntryIcons(menu: menu, generation: generation, entries: iconEntries)
        DiskMonitorLog.slowIfNeeded(
            "applyDirectorySnapshot",
            started: started,
            thresholdMs: 50,
            extra: "\(dir.path) items=\(snapshot.entries.count)"
        )
    }

    private func detachMenuItem(for row: VolumeRow) -> NSMenuItem {
        let item = NSMenuItem(title: "", action: #selector(detachVolumeFromMenu(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = row
        let useUnmount = row.detachUsesUnmountLabel
        let titleKey = useUnmount ? "menu.unmount" : "menu.eject"
        let symbolName = (useUnmount && row.kind != .network) ? "externaldrive.badge.minus" : "eject.fill"
        item.title = NSLocalizedString(titleKey, comment: "")
        let a11y = NSLocalizedString(useUnmount ? "a11y.unmount" : "a11y.eject", comment: "")
        if let icon = NSImage(systemSymbolName: symbolName, accessibilityDescription: a11y)
            ?? NSImage(systemSymbolName: "eject.fill", accessibilityDescription: a11y) {
            icon.isTemplate = true
            icon.size = NSSize(width: 14, height: 14)
            item.image = icon
        }
        return item
    }

    private func directoryEntryPlaceholderIcon(isDirectory: Bool) -> NSImage {
        let symbol = isDirectory ? "folder" : "doc"
        let img = NSImage(systemSymbolName: symbol, accessibilityDescription: nil) ?? NSImage()
        img.isTemplate = !isDirectory
        img.size = NSSize(width: 16, height: 16)
        return img
    }

    private func scheduleDirectoryEntryIcons(
        menu: DirectoryMenu,
        generation: Int,
        entries: [(url: URL, item: NSMenuItem)]
    ) {
        for entry in entries {
            directoryIconQueue.addOperation { [weak self, weak menu, weak item = entry.item] in
                let icon = NSWorkspace.shared.icon(forFile: entry.url.path)
                icon.size = NSSize(width: 16, height: 16)
                DispatchQueue.main.async {
                    guard let self, let menu, let item,
                          menu.sizeUpdateGeneration == generation,
                          item.menu === menu else { return }
                    item.image = icon
                    self.scheduleDirectoryMenuVisualUpdate(menu)
                }
            }
        }
    }

    private func addToFavoritesMenuItem(for url: URL) -> NSMenuItem {
        let item = NSMenuItem(
            title: NSLocalizedString("menu.add_to_favorites", comment: ""),
            action: #selector(addToFavorites(_:)),
            keyEquivalent: ""
        )
        item.target = self
        item.representedObject = url
        return item
    }

    private func removeFromFavoritesMenuItem(for url: URL) -> NSMenuItem {
        let item = NSMenuItem(
            title: NSLocalizedString("menu.remove_from_favorites", comment: ""),
            action: #selector(removeFromFavorites(_:)),
            keyEquivalent: ""
        )
        item.target = self
        item.representedObject = url
        return item
    }

    @objc private func addToFavorites(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL,
              let menu = sender.menu as? DirectoryMenu,
              let dir = menu.directoryURL else { return }
        FavoriteStore.add(url)
        guard menu.loaded else { return }
        menu.sizeUpdateGeneration &+= 1
        let generation = menu.sizeUpdateGeneration
        let volumeRow = menu.volumeRow
        let includeHidden = menu.includeHiddenFiles
        directoryListingQueue.addOperation { [weak self, weak menu] in
            guard let self, let menu else { return }
            let snapshot = Self.readDirectorySnapshot(
                at: dir,
                timeout: self.directoryListingTimeout,
                includeHiddenFiles: includeHidden
            )
            DispatchQueue.main.async {
                guard menu.sizeUpdateGeneration == generation else { return }
                self.applyDirectorySnapshot(snapshot, to: menu, volumeRow: volumeRow)
                menu.update()
                self.scheduleAlignDirectorySubmenu(menu)
            }
        }
    }

    @objc private func removeFromFavorites(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        FavoriteStore.remove(url)
        menuNeedsRebuild = true
        guard isMenuVisible else { return }
        rebuildMenu()
        syncLaunchAtLoginItem()
    }

    private static func displayName(for url: URL) -> String {
        guard FavoriteStore.isAvailable(url) else {
            return url.lastPathComponent
        }
        let keys: Set<URLResourceKey> = [.localizedNameKey, .nameKey]
        let values = try? url.resourceValues(forKeys: keys)
        return values?.localizedName ?? values?.name ?? url.lastPathComponent
    }

    /// フォルダの OS 既定ファイルマネージャ（ForkLift 等は NSFileViewer で登録される）。
    private static func defaultFileManagerApplication() -> URL? {
        for domainName in ["NSGlobalDomain", "com.apple.globaldomain", ".GlobalPreferences"] {
            if let bundleId = UserDefaults.standard.persistentDomain(forName: domainName)?["NSFileViewer"] as? String,
               let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId) {
                return url
            }
        }
        if let bundleId = UserDefaults.standard.string(forKey: "NSFileViewer"),
           let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId) {
            return url
        }
        if let url = LSCopyDefaultApplicationURLForContentType("public.folder" as CFString, .all, nil)?.takeRetainedValue() as URL? {
            return url
        }
        return NSWorkspace.shared.urlForApplication(toOpen: URL(fileURLWithPath: NSHomeDirectory()))
    }

    private static func defaultApplicationForOpening(_ url: URL) -> URL? {
        if isApplicationBundle(at: url) {
            return NSWorkspace.shared.urlForApplication(toOpen: url)
        }
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue {
            return defaultFileManagerApplication() ?? NSWorkspace.shared.urlForApplication(toOpen: url)
        }
        return NSWorkspace.shared.urlForApplication(toOpen: url)
    }

    private static let openSmartBundleID = "jp.tomippe.opensmart"

    private static var openSmartApplicationURL: URL? {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: openSmartBundleID)
    }

    private static var isOpenSmartInstalled: Bool {
        openSmartApplicationURL != nil
    }

    private func openURL(_ url: URL, preferOpenSmart: Bool) {
        if preferOpenSmart, !Self.isApplicationBundle(at: url), let appURL = Self.openSmartApplicationURL {
            let config = NSWorkspace.OpenConfiguration()
            config.activates = true
            NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: config, completionHandler: nil)
            return
        }
        openWithDefaultFileManager(url)
    }

    private func openWithDefaultFileManager(_ url: URL) {
        if Self.isApplicationBundle(at: url) {
            _ = NSWorkspace.shared.open(url)
            return
        }
        if let appURL = Self.defaultApplicationForOpening(url) {
            let config = NSWorkspace.OpenConfiguration()
            config.activates = true
            NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: config, completionHandler: nil)
        } else {
            _ = NSWorkspace.shared.open(url)
        }
    }

    @objc private func openFileFromMenu(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        let useOpenSmart = Self.isOpenSmartInstalled && Self.optionKeyPressed()
        openURL(url, preferOpenSmart: useOpenSmart)
    }

    private static func isBrowsableDirectory(at url: URL) -> Bool {
        !isApplicationBundle(at: url)
    }

    private static func isApplicationBundle(at url: URL) -> Bool {
        url.pathExtension.lowercased() == "app"
    }

    private static func isDirectoryEntryAccessible(at url: URL, isDirectory: Bool) -> Bool {
        let fm = FileManager.default
        if isDirectory {
            return fm.isReadableFile(atPath: url.path) && fm.isExecutableFile(atPath: url.path)
        }
        return fm.isReadableFile(atPath: url.path)
    }

    private func directoryFolderSummaryTitle(itemCount: Int, sizeText: String? = nil, includesHidden: Bool = false) -> String {
        let hiddenSuffix = includesHidden
            ? NSLocalizedString("menu.directory_summary_includes_hidden", comment: "")
            : ""
        if let sizeText {
            return String(
                format: NSLocalizedString("menu.directory_summary_items_size", comment: ""),
                itemCount,
                sizeText
            ) + hiddenSuffix
        }
        return String(format: NSLocalizedString("menu.directory_summary_items", comment: ""), itemCount) + hiddenSuffix
    }

    private func hiddenDirectoryEntryAttributedTitle(_ name: String) -> NSAttributedString {
        NSAttributedString(
            string: name,
            attributes: [.foregroundColor: NSColor.secondaryLabelColor]
        )
    }

    private func directoryFolderSummaryAttributedTitle(
        itemCount: Int,
        sizeText: String,
        includesHidden: Bool = false
    ) -> NSAttributedString {
        let prefix = String(format: NSLocalizedString("menu.directory_summary_items", comment: ""), itemCount)
        let separator = NSLocalizedString("menu.directory_summary_separator", comment: "")
        let hiddenSuffix = includesHidden
            ? NSLocalizedString("menu.directory_summary_includes_hidden", comment: "")
            : ""
        let full = "\(prefix)\(separator)\(sizeText)\(hiddenSuffix)"
        let baseFont = NSFont.menuFont(ofSize: 0)
        let attributed = NSMutableAttributedString(string: full, attributes: [.font: baseFont])
        let sizeStart = prefix.count + separator.count
        let italic = NSFontManager.shared.convert(baseFont, toHaveTrait: .italicFontMask)
        attributed.addAttribute(.font, value: italic, range: NSRange(location: sizeStart, length: sizeText.count))
        return attributed
    }

    private func scheduleDirectoryMenuVisualUpdate(_ menu: DirectoryMenu) {
        menu.pendingUpdateWorkItem?.cancel()
        let work = DispatchWorkItem { [weak menu] in
            let started = CFAbsoluteTimeGetCurrent()
            menu?.update()
            if let path = menu?.directoryURL?.path {
                DiskMonitorLog.slowIfNeeded("directoryMenu.update", started: started, thresholdMs: 30, extra: path)
            }
        }
        menu.pendingUpdateWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2, execute: work)
    }

    private func scheduleDirectoryFolderSize(menu: DirectoryMenu, generation: Int, url: URL) {
        directorySizeQueue.addOperation { [weak self, weak menu] in
            guard let self, let menu else { return }
            let itemCount = menu.folderItemCount
            let bytes = Self.directorySizeViaDu(at: url, timeout: self.directoryFolderSizeTimeout)
            DispatchQueue.main.async {
                guard menu.sizeUpdateGeneration == generation,
                      let summaryItem = menu.folderSizeMenuItem,
                      summaryItem.menu === menu else { return }
                if let bytes {
                    let sizeText = self.formatBytes(bytes, concise: false)
                    summaryItem.title = self.directoryFolderSummaryTitle(
                        itemCount: itemCount,
                        sizeText: sizeText,
                        includesHidden: menu.includeHiddenFiles
                    )
                    summaryItem.attributedTitle = self.directoryFolderSummaryAttributedTitle(
                        itemCount: itemCount,
                        sizeText: sizeText,
                        includesHidden: menu.includeHiddenFiles
                    )
                } else {
                    summaryItem.title = self.directoryFolderSummaryTitle(
                        itemCount: itemCount,
                        includesHidden: menu.includeHiddenFiles
                    )
                    summaryItem.attributedTitle = nil
                }
                self.scheduleDirectoryMenuVisualUpdate(menu)
            }
        }
    }

    private static func directorySizeViaDu(at url: URL, timeout: TimeInterval) -> Int64? {
        guard let result = runProcess(
            executable: "/usr/bin/du",
            arguments: ["-sk", url.path],
            timeout: timeout
        ), result.status == 0,
           let line = String(data: result.stdout, encoding: .utf8)?.split(whereSeparator: \.isNewline).first,
           let kb = Int64(line.split(separator: "\t", maxSplits: 1).first ?? "") else {
            return nil
        }
        return kb * 1024
    }

    private func trashMenuItem() -> NSMenuItem {
        let icon = NSImage(systemSymbolName: "trash", accessibilityDescription: NSLocalizedString("menu.trash", comment: "")) ?? NSImage()
        icon.isTemplate = true

        let item = NSMenuItem(
            title: trashMenuTitle(),
            action: #selector(openTrashFromMenu),
            keyEquivalent: ""
        )
        item.target = self
        item.attributedTitle = trashMenuAttributedTitle()
        icon.size = NSSize(width: 16, height: 16)
        item.image = icon

        let submenu = NSMenu()
        let empty = NSMenuItem(
            title: NSLocalizedString("menu.empty_trash", comment: ""),
            action: #selector(emptyTrash),
            keyEquivalent: ""
        )
        empty.target = self
        submenu.addItem(empty)
        item.submenu = submenu
        return item
    }

    private func formatBytes(_ bytes: Int64, concise: Bool) -> String {
        let formatter = ByteCountFormatter()
        formatter.allowedUnits = [.useBytes, .useKB, .useMB, .useGB, .useTB, .usePB]
        formatter.countStyle = .file
        formatter.includesUnit = true
        formatter.isAdaptive = true
        formatter.zeroPadsFractionDigits = false
        formatter.includesCount = true
        if concise {
            formatter.allowsNonnumericFormatting = false
        }
        return formatter.string(fromByteCount: bytes)
    }

    private static let diskUtilityBundleID = "com.apple.DiskUtility"

    private static func diskUtilityURL() -> URL? {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: diskUtilityBundleID) {
            return url
        }
        let paths = [
            "/System/Applications/Utilities/Disk Utility.app",
            "/Applications/Utilities/Disk Utility.app",
        ]
        for path in paths where FileManager.default.fileExists(atPath: path) {
            return URL(fileURLWithPath: path)
        }
        return nil
    }

    private func diskUtilityMenuItem() -> NSMenuItem {
        let item = NSMenuItem(
            title: NSLocalizedString("menu.open_disk_utility", comment: ""),
            action: #selector(openDiskUtility),
            keyEquivalent: ""
        )
        item.target = self
        if let icon = NSImage(systemSymbolName: "internaldrive", accessibilityDescription: NSLocalizedString("menu.open_disk_utility", comment: "")) {
            icon.isTemplate = true
            icon.size = NSSize(width: 16, height: 16)
            item.image = icon
        }
        return item
    }

    @objc private func openDiskUtility() {
        if let url = Self.diskUtilityURL() {
            _ = NSWorkspace.shared.open(url)
            return
        }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-b", Self.diskUtilityBundleID]
        try? task.run()
    }

    @objc private func openVolumeFromMenu(_ sender: NSMenuItem) {
        guard let row = sender.representedObject as? VolumeRow else { return }
        let useOpenSmart = Self.isOpenSmartInstalled && Self.optionKeyPressed()
        openURL(row.url, preferOpenSmart: useOpenSmart)
    }

    @objc private func openTrashFromMenu() {
        let trashURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".Trash", isDirectory: true)
        let useOpenSmart = Self.isOpenSmartInstalled && Self.optionKeyPressed()
        openURL(trashURL, preferOpenSmart: useOpenSmart)
    }

    @objc private func detachVolumeFromMenu(_ sender: NSMenuItem) {
        guard let row = sender.representedObject as? VolumeRow else { return }
        detachVolume(row)
    }

    @objc private func emptyTrash() {
        let result = Self.emptyTrashWithFinder()
        if !result.cancelled, let errorMessage = result.errorMessage {
            let alert = NSAlert()
            alert.messageText = NSLocalizedString("alert.empty_trash_failed_title", comment: "")
            alert.informativeText = errorMessage
            alert.alertStyle = .warning
            alert.runModal()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            self?.refreshNow()
        }
    }

    private func detachVolume(_ row: VolumeRow) {
        VolumeDetach.detach(row: row) { [weak self] errorMessage in
            if let errorMessage {
                let alert = NSAlert()
                let failTitleKey = row.detachUsesUnmountLabel ? "alert.unmount_failed_title" : "alert.eject_failed_title"
                alert.messageText = NSLocalizedString(failTitleKey, comment: "")
                alert.informativeText = errorMessage
                alert.alertStyle = .warning
                alert.runModal()
            }
            self?.refreshVolumes()
        }
    }

    @objc private func copyStatus() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(statusText, forType: .string)
    }

    @objc private func refreshNow() {
        refreshVolumes()
    }

    @objc private func showAboutPanel() {
        TomippeAppAbout.show(
            appName: "Disk Monitor",
            introURL: diskMonitorIntroURL,
            checkForUpdates: { [weak self] in self?.updaterController.checkForUpdates(nil) }
        )
    }

    @objc private func openFeedbackForm() {
        TomippeFeedbackForm.open(appName: "Disk Monitor")
    }

    @objc private func restartApp() {
        TomippeRelaunch.relaunchCurrentApp()
    }

    @objc private func quit() {
        NSApplication.shared.terminate(nil)
    }

    private func mi(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    private func sectionMenuItem(_ title: String, _ action: Selector, _ key: String, symbolName: String) -> NSMenuItem {
        let item = mi(title, action, key)
        if let icon = NSImage(systemSymbolName: symbolName, accessibilityDescription: title) {
            icon.isTemplate = true
            icon.size = NSSize(width: 16, height: 16)
            item.image = icon
        }
        return item
    }

    private func sparkleCheckForUpdatesMenuItem() -> NSMenuItem {
        let title = NSLocalizedString("menu.check_for_updates", comment: "")
        let item = NSMenuItem(
            title: title,
            action: #selector(SPUStandardUpdaterController.checkForUpdates(_:)),
            keyEquivalent: ""
        )
        item.target = updaterController
        if let icon = NSImage(systemSymbolName: "arrow.down.circle", accessibilityDescription: title) {
            icon.isTemplate = true
            icon.size = NSSize(width: 16, height: 16)
            item.image = icon
        }
        return item
    }

    private func volumeMenuTitle(for row: VolumeRow) -> String {
        let name = ellipsized(row.name, maxLength: menuNameWidth)
        let free = formatBytes(displayedAvailableBytes(for: row), concise: false)
        return "\(name)\t\(free)\t"
    }

    private func volumeMenuAttributedTitle(for row: VolumeRow) -> NSAttributedString {
        let title = volumeMenuTitle(for: row)
        let baseFont = NSFont.menuFont(ofSize: 0)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: baseFont,
            .paragraphStyle: volumeMenuParagraphStyle()
        ]
        let attributed = NSMutableAttributedString(string: title, attributes: attrs)
        guard row.isNonFreeMetric else { return attributed }

        let name = ellipsized(row.name, maxLength: menuNameWidth)
        let free = formatBytes(displayedAvailableBytes(for: row), concise: false)
        let valueStart = name.count + 1
        let valueRange = NSRange(location: valueStart, length: free.count)
        let italic = NSFontManager.shared.convert(baseFont, toHaveTrait: .italicFontMask)
        attributed.addAttribute(.font, value: italic, range: valueRange)
        return attributed
    }

    private func displayedAvailableBytes(for row: VolumeRow) -> Int64 {
        // Finder の「使用可能」はゴミ箱分を差し引かないため、ここでも減算しない。
        return row.availableBytes
    }

    private func trashMenuTitle() -> String {
        let name = ellipsized(NSLocalizedString("menu.trash", comment: ""), maxLength: menuNameWidth)
        let size = formatBytes(trashSizeBytes, concise: false)
        return "\(name)\t\(size)\t"
    }

    private func trashMenuAttributedTitle() -> NSAttributedString {
        let title = trashMenuTitle()
        let baseFont = NSFont.menuFont(ofSize: 0)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: baseFont,
            .paragraphStyle: volumeMenuParagraphStyle()
        ]
        let attributed = NSMutableAttributedString(string: title, attributes: attrs)
        let name = ellipsized(NSLocalizedString("menu.trash", comment: ""), maxLength: menuNameWidth)
        let size = formatBytes(trashSizeBytes, concise: false)
        let valueStart = name.count + 1
        let valueRange = NSRange(location: valueStart, length: size.count)
        let italic = NSFontManager.shared.convert(baseFont, toHaveTrait: .italicFontMask)
        attributed.addAttribute(.font, value: italic, range: valueRange)
        return attributed
    }

    private func volumeMenuParagraphStyle() -> NSParagraphStyle {
        let style = NSMutableParagraphStyle()
        style.tabStops = [
            NSTextTab(textAlignment: .right, location: capacityColumnTabStop, options: [:]),
            NSTextTab(textAlignment: .right, location: ejectColumnTabStop, options: [:])
        ]
        style.defaultTabInterval = ejectColumnTabStop
        return style
    }

    private func ellipsized(_ text: String, maxLength: Int) -> String {
        if text.count <= maxLength { return text }
        let prefixLength = max(maxLength - 3, 1)
        return "\(text.prefix(prefixLength))..."
    }

    private static func calculateTrashSizeBytes() -> Int64 {
        // Finder の「項目ごとの physical size 合計」は実ファイルを都度読むためリアルタイムかつ正確で、
        // 全ボリューム＋iCloud Drive のゴミ箱を Finder が集約してくれる。さらに（許可済みの）
        // Automation だけで動き、フルディスクアクセスを必要としない。
        // FileManager 直接走査は ~/.Trash の列挙にフルディスクアクセスが要る（未付与だと読めない）ため、
        // Automation が拒否されている場合のフォールバックとしてのみ使う。
        // ※ Finder の「コンテナの physical size」はキャッシュで stale だが、ここでは使っていない。
        if let bytes = calculateTrashSizeBytesWithFinder() {
            return bytes
        }
        if let bytes = calculateTrashSizeBytesWithFileManager() {
            return bytes
        }
        return 0
    }

    private static func calculateTrashSizeBytesWithFileManager() -> Int64? {
        let fm = FileManager.default
        let dirs = trashDirectories()
        var total: Int64 = 0
        var anyAccessible = false
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .totalFileSizeKey, .isDirectoryKey]

        for dir in dirs {
            guard fm.fileExists(atPath: dir.path) else { continue }
            guard (try? fm.contentsOfDirectory(atPath: dir.path)) != nil else { continue }
            anyAccessible = true
            guard let enumerator = fm.enumerator(
                at: dir,
                includingPropertiesForKeys: keys,
                options: [],
                errorHandler: nil
            ) else { continue }
            for case let fileURL as URL in enumerator {
                guard let vals = try? fileURL.resourceValues(forKeys: Set(keys)),
                      vals.isDirectory != true else { continue }
                let size = vals.totalFileAllocatedSize ?? vals.totalFileSize ?? 0
                total += Int64(size)
            }
        }
        return anyAccessible ? total : nil
    }

    private static let trashSizeFinderTimeout: TimeInterval = 8

    private static func calculateTrashSizeBytesWithFinder() -> Int64? {
        guard !finderTrashSizeDenied else { return nil }
        let started = CFAbsoluteTimeGetCurrent()
        // physical size of trash returns Finder's cached value (stale after file moves).
        // Summing each item individually via index forces a fresh per-item lookup.
        // try blocks inside the script absorb transient errors (e.g. item removed mid-loop).
        // NSAppleScript はキャンセル不能で 100s 超ブロックし得るため、殺せる osascript 経由にする。
        let script = """
        tell application "Finder"
            set total to 0
            set n to count of (items of trash)
            if n > 0 then
                repeat with i from 1 to n
                    try
                        set total to total + (physical size of item i of trash)
                    end try
                end repeat
                if total is 0 then
                    try
                        set total to physical size of trash
                    end try
                end if
            end if
            return total
        end tell
        """
        guard let result = runProcess(
            executable: "/usr/bin/osascript",
            arguments: ["-e", script],
            timeout: trashSizeFinderTimeout
        ) else {
            DiskMonitorLog.slowIfNeeded("trashSizeFinder timeout", started: started, thresholdMs: 0)
            return nil
        }
        let output = String(data: result.stdout, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if result.status != 0 {
            // -1743: not authorized to send Apple events
            if output.localizedCaseInsensitiveContains("not allowed")
                || output.localizedCaseInsensitiveContains("not authorized")
                || output.contains("-1743") {
                finderTrashSizeDenied = true
            }
            return nil
        }
        let bytes = Int64(output)
        DiskMonitorLog.slowIfNeeded("trashSizeFinder", started: started, thresholdMs: 200)
        return bytes
    }

    private static func emptyTrashWithFinder() -> (cancelled: Bool, errorMessage: String?) {
        let script = """
        tell application "Finder"
            activate
            empty trash
        end tell
        """
        guard let appleScript = NSAppleScript(source: script) else {
            return (false, NSLocalizedString("alert.empty_trash_failed_message", comment: ""))
        }
        var error: NSDictionary?
        appleScript.executeAndReturnError(&error)
        guard let error else {
            return (false, nil)
        }
        let errorNumber = error[NSAppleScript.errorNumber] as? Int
        let cancelled = errorNumber == -128
        let message = error[NSAppleScript.errorMessage] as? String
        return (cancelled, message ?? NSLocalizedString("alert.empty_trash_failed_message", comment: ""))
    }

    private static func trashDirectories() -> [URL] {
        let fm = FileManager.default
        var dirs: [URL] = []
        var seen = Set<String>()

        let homeTrash = fm.homeDirectoryForCurrentUser.appendingPathComponent(".Trash", isDirectory: true)
        dirs.append(homeTrash)

        // iCloud Drive のゴミ箱。Finder の「ゴミ箱」はローカルと iCloud を合算するため、
        // ここを含めないと FileManager 集計が iCloud 分を取りこぼす。
        let iCloudTrash = fm.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Mobile Documents/.Trash", isDirectory: true)
        dirs.append(iCloudTrash)

        let uid = getuid()
        let rootTrashes = URL(fileURLWithPath: "/.Trashes/\(uid)", isDirectory: true)
        dirs.append(rootTrashes)

        // ネットワークボリューム上の .Trashes へ fileExists すると応答が遅くなる。
        // Finder 集計（Automation）が全ボリューム分を扱うため、ここではローカル系のみ走査する。

        return dirs.filter { dir in
            let key = dir.path
            guard !seen.contains(key) else { return false }
            seen.insert(key)
            var isDir: ObjCBool = false
            return fm.fileExists(atPath: key, isDirectory: &isDir) && isDir.boolValue
        }
    }
}

@main
struct DiskMonitorApp {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
