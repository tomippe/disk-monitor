import Cocoa
import CoreServices
import ServiceManagement
import Sparkle

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

// MARK: - アプリが Applications 外から起動されたとき移動を促す（直接配布版のみ）
private enum MoveToApplicationsFolder {
    private static let alertSuppressKey = "moveToApplicationsFolderAlertSuppress"

    static func moveIfNecessary() {
        if ProcessInfo.processInfo.environment["APP_SANDBOX_CONTAINER_ID"] != nil {
            return
        }

        let fm = FileManager.default
        let bundleURL = Bundle.main.bundleURL

        guard !isInApplicationsFolder(bundleURL) else { return }
        guard !UserDefaults.standard.bool(forKey: alertSuppressKey) else { return }
        guard let applicationsURL = preferredInstallDirectory() else { return }

        let bundleName = bundleURL.lastPathComponent
        let destinationURL = applicationsURL.appendingPathComponent(bundleName)

        let needAuth = (!fm.isWritableFile(atPath: applicationsURL.path)) ||
            (fm.fileExists(atPath: destinationURL.path) && !fm.isWritableFile(atPath: destinationURL.path))

        if !NSApp.isActive {
            NSApp.activate(ignoringOtherApps: true)
        }

        let alert = NSAlert()
        alert.messageText = NSLocalizedString("move.title", comment: "Move app prompt")
        var informativeText = NSLocalizedString("move.message", comment: "")
        if needAuth {
            informativeText += " " + NSLocalizedString("move.needs_password", comment: "")
        } else if isInDownloadsFolder(bundleURL) {
            informativeText += " " + NSLocalizedString("move.downloads_hint", comment: "")
        }
        alert.informativeText = informativeText
        alert.addButton(withTitle: NSLocalizedString("move.button_move", comment: ""))
        alert.addButton(withTitle: NSLocalizedString("move.button_stay", comment: ""))
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = NSLocalizedString("move.dont_ask", comment: "")

        guard alert.runModal() == .alertFirstButtonReturn else {
            if alert.suppressionButton?.state == .on {
                UserDefaults.standard.set(true, forKey: alertSuppressKey)
            }
            return
        }

        if needAuth {
            let result = authorizedInstall(from: bundleURL, to: destinationURL)
            guard !result.cancelled else { moveIfNecessary(); return }
            guard result.success else {
                NSApplication.shared.terminate(nil)
                return
            }
        } else {
            switch installBundle(from: bundleURL, to: destinationURL) {
            case .installed:
                break
            case .openedExisting:
                return
            case .failed:
                showErrorAlert()
                return
            }
        }

        _ = try? fm.trashItem(at: bundleURL, resultingItemURL: nil)

        relaunch(at: destinationURL.path) {
            DispatchQueue.main.async { exit(0) }
        }
    }

    private enum InstallResult {
        case installed
        case openedExisting
        case failed
    }

    private static func isInApplicationsFolder(_ url: URL) -> Bool {
        guard let apps = systemApplicationsDirectory() else { return false }
        return url.standardizedFileURL.path.hasPrefix(apps.path)
    }

    private static func systemApplicationsDirectory() -> URL? {
        FileManager.default.urls(for: .applicationDirectory, in: .localDomainMask).first
    }

    private static func isInDownloadsFolder(_ url: URL) -> Bool {
        let path = url.path
        let downloadDirs = FileManager.default.urls(for: .downloadsDirectory, in: .allDomainsMask)
        return downloadDirs.contains { path.hasPrefix($0.path) }
    }

    private static func preferredInstallDirectory() -> URL? {
        systemApplicationsDirectory()
    }

    private static func installBundle(from sourceURL: URL, to destinationURL: URL) -> InstallResult {
        let fm = FileManager.default
        if fm.fileExists(atPath: destinationURL.path) {
            if isApplicationAtURLRunning(destinationURL) {
                NSWorkspace.shared.open(destinationURL)
                return .openedExisting
            }
            _ = try? fm.trashItem(at: destinationURL, resultingItemURL: nil)
        }
        return runDitto(from: sourceURL, to: destinationURL) ? .installed : .failed
    }

    private static func runDitto(from sourceURL: URL, to destinationURL: URL) -> Bool {
        sourceURL.withUnsafeFileSystemRepresentation { sourcePath in
            destinationURL.withUnsafeFileSystemRepresentation { destPath in
                guard let sourcePath, let destPath else { return false }
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
                process.arguments = [
                    "--norsrc",
                    String(cString: sourcePath),
                    String(cString: destPath),
                ]
                do {
                    try process.run()
                    process.waitUntilExit()
                    return process.terminationStatus == 0
                } catch {
                    return false
                }
            } ?? false
        } ?? false
    }

    private static func isApplicationAtURLRunning(_ url: URL) -> Bool {
        let target = url.standardized
        return NSWorkspace.shared.runningApplications.contains {
            $0.bundleURL?.standardized == target
        }
    }

    private static func authorizedInstall(from sourceURL: URL, to destinationURL: URL) -> (cancelled: Bool, success: Bool) {
        guard destinationURL.pathExtension == "app",
              !destinationURL.path.trimmingCharacters(in: .whitespaces).isEmpty,
              !sourceURL.path.trimmingCharacters(in: .whitespaces).isEmpty else {
            return (false, false)
        }
        return sourceURL.withUnsafeFileSystemRepresentation { sourcePath -> (Bool, Bool) in
            destinationURL.withUnsafeFileSystemRepresentation { destPath -> (Bool, Bool) in
                guard let src = sourcePath, let dst = destPath else { return (false, false) }
                let deleteCmd = "rm -rf '\(String(cString: dst))'"
                let copyCmd = "/usr/bin/ditto --norsrc '\(String(cString: src))' '\(String(cString: dst))'"
                let script = "do shell script \"\(deleteCmd) && \(copyCmd)\" with administrator privileges"
                guard let appleScript = NSAppleScript(source: script) else { return (false, false) }
                var error: NSDictionary?
                appleScript.executeAndReturnError(&error)
                let cancelled = (error?[NSAppleScript.errorNumber] as? Int16) == -128
                return (cancelled, error == nil)
            }
        }
    }

    private static func relaunch(at path: String, completionCallback: @escaping () -> Void) {
        let pid = ProcessInfo.processInfo.processIdentifier
        let quotedPath = "'\(path.replacingOccurrences(of: "'", with: "'\\''"))'"
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        task.arguments = ["-d", "-r", "com.apple.quarantine", path]
        task.terminationHandler = { _ in
            let waitScript = "(while /bin/kill -0 \(pid) >&/dev/null; do /bin/sleep 0.1; done; /usr/bin/open \(quotedPath)) &"
            let openTask = Process()
            openTask.executableURL = URL(fileURLWithPath: "/bin/sh")
            openTask.arguments = ["-c", waitScript]
            try? openTask.run()
            completionCallback()
        }
        try? task.run()
    }

    private static func showErrorAlert() {
        let alert = NSAlert()
        alert.messageText = NSLocalizedString("move.error_title", comment: "")
        alert.informativeText = NSLocalizedString("move.error_message", comment: "")
        alert.addButton(withTitle: NSLocalizedString("move.ok", comment: ""))
        alert.runModal()
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
        !isRootFileSystem && (isEjectable || (!isEjectable && isUnmountable))
    }

    /// 取り出しの代わりにマウント解除として扱うか。
    var detachUsesUnmountLabel: Bool {
        !isEjectable && isUnmountable
    }
}

private final class VolumeMenuItemView: NSView {
    private let nameLabel = NSTextField(labelWithString: "")
    private let freeLabel = NSTextField(labelWithString: "")
    private var ejectButton: NSButton?
    private let onOpenInFinder: () -> Void
    private let onEject: (() -> Void)?
    private var trackingAreaRef: NSTrackingArea?
    private var isHovered = false
    private var highlightTimer: Timer?

    init(frame frameRect: NSRect, row: VolumeRow, freeText: String, onOpenInFinder: @escaping () -> Void, onEject: (() -> Void)?) {
        self.onOpenInFinder = onOpenInFinder
        self.onEject = onEject
        super.init(frame: frameRect)

        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true

        let iconView = NSImageView(image: row.icon)
        iconView.translatesAutoresizingMaskIntoConstraints = false
        iconView.imageScaling = .scaleProportionallyUpOrDown
        iconView.setContentHuggingPriority(.required, for: .horizontal)
        iconView.widthAnchor.constraint(equalToConstant: 16).isActive = true
        iconView.heightAnchor.constraint(equalToConstant: 16).isActive = true

        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        nameLabel.stringValue = row.name
        nameLabel.lineBreakMode = .byTruncatingMiddle
        nameLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        freeLabel.translatesAutoresizingMaskIntoConstraints = false
        freeLabel.stringValue = freeText
        freeLabel.alignment = .right
        freeLabel.font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        freeLabel.setContentHuggingPriority(.required, for: .horizontal)
        freeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        freeLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 72).isActive = true

        let stack = NSStackView()
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        addSubview(stack)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        stack.addArrangedSubview(iconView)
        stack.addArrangedSubview(nameLabel)
        stack.addArrangedSubview(spacer)
        stack.addArrangedSubview(freeLabel)

        let ejectSlot = NSView()
        ejectSlot.translatesAutoresizingMaskIntoConstraints = false
        ejectSlot.widthAnchor.constraint(equalToConstant: 16).isActive = true
        ejectSlot.heightAnchor.constraint(equalToConstant: 16).isActive = true
        stack.addArrangedSubview(ejectSlot)

        if onEject != nil {
            let detachSymbol = row.detachUsesUnmountLabel ? "externaldrive.badge.minus" : "eject.fill"
            let detachA11y = row.detachUsesUnmountLabel
                ? NSLocalizedString("a11y.unmount", comment: "")
                : NSLocalizedString("a11y.eject", comment: "")
            let button = NSButton(
                image: NSImage(systemSymbolName: detachSymbol, accessibilityDescription: detachA11y)
                    ?? NSImage(systemSymbolName: "eject.fill", accessibilityDescription: detachA11y)
                    ?? NSImage(),
                target: self,
                action: #selector(handleEject)
            )
            button.translatesAutoresizingMaskIntoConstraints = false
            button.isBordered = false
            button.bezelStyle = .regularSquare
            button.imageScaling = .scaleProportionallyUpOrDown
            button.contentTintColor = .labelColor
            button.setButtonType(.momentaryChange)
            button.focusRingType = .none
            button.image?.size = NSSize(width: 10, height: 10)
            button.frame = NSRect(x: 0, y: 0, width: 16, height: 16)
            ejectSlot.addSubview(button)
            button.centerXAnchor.constraint(equalTo: ejectSlot.centerXAnchor).isActive = true
            button.centerYAnchor.constraint(equalTo: ejectSlot.centerYAnchor).isActive = true
            button.widthAnchor.constraint(equalToConstant: 12).isActive = true
            button.heightAnchor.constraint(equalToConstant: 12).isActive = true
            ejectButton = button
        }

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            stack.topAnchor.constraint(equalTo: topAnchor, constant: 3),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -3),
            widthAnchor.constraint(greaterThanOrEqualToConstant: 320)
        ])

        let click = NSClickGestureRecognizer(target: self, action: #selector(handleOpen))
        addGestureRecognizer(click)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    deinit {
        highlightTimer?.invalidate()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        highlightTimer?.invalidate()
        let timer = Timer(timeInterval: 0.03, repeats: true) { [weak self] _ in
            guard let self else { return }
            let highlighted = self.enclosingMenuItem?.isHighlighted ?? false
            if highlighted != self.isHovered {
                self.isHovered = highlighted
                self.needsDisplay = true
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        RunLoop.main.add(timer, forMode: .eventTracking)
        highlightTimer = timer
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingAreaRef {
            removeTrackingArea(trackingAreaRef)
        }
        let trackingArea = NSTrackingArea(
            rect: bounds,
            options: [.activeInActiveApp, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(trackingArea)
        trackingAreaRef = trackingArea
    }

    override func mouseEntered(with _: NSEvent) {
        // NSMenu 内では mouseEntered が不安定なため、isHighlighted 監視を主とする。
    }

    override func mouseExited(with _: NSEvent) {
        // NSMenu 内では mouseExited が不安定なため、isHighlighted 監視を主とする。
    }

    override func draw(_ dirtyRect: NSRect) {
        if isHovered {
            NSColor.controlAccentColor.setFill()
            dirtyRect.fill()
            nameLabel.textColor = .alternateSelectedControlTextColor
            freeLabel.textColor = .alternateSelectedControlTextColor
            ejectButton?.contentTintColor = .alternateSelectedControlTextColor
        } else {
            NSColor.clear.setFill()
            dirtyRect.fill()
            nameLabel.textColor = .labelColor
            freeLabel.textColor = .labelColor
            ejectButton?.contentTintColor = .labelColor
        }
        super.draw(dirtyRect)
    }

    @objc private func handleOpen(_ recognizer: NSClickGestureRecognizer) {
        let point = recognizer.location(in: self)
        if let button = ejectButton, button.frame.contains(point) {
            return
        }
        onOpenInFinder()
    }

    @objc private func handleEject() {
        onEject?()
    }
}

class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var timer: Timer?
    private var launchAtLoginMenuItem: NSMenuItem?
    private let updaterController = SPUStandardUpdaterController(
        startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil
    )
    private var fileSystemChangeMonitor: FileSystemChangeMonitor?
    private var pendingFileSystemRefresh: DispatchWorkItem?
    private let refreshInterval: TimeInterval = 5
    private let menuNameWidth = 20
    private let capacityColumnTabStop: CGFloat = 235
    private let ejectColumnTabStop: CGFloat = 275
    private var volumeRows: [VolumeRow] = []
    private var statusText = NSLocalizedString("status.loading", comment: "")
    private var trashSizeBytes: Int64 = 0
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
        refreshVolumes()
        timer = Timer.scheduledTimer(withTimeInterval: refreshInterval, repeats: true) { [weak self] _ in
            self?.refreshVolumes()
        }
        timer?.tolerance = 2
    }

    func menuWillOpen(_: NSMenu) {
        rebuildMenu()
        syncLaunchAtLoginItem()
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

    private func refreshVolumes() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let rows = Self.readVolumes()
            DispatchQueue.main.async {
                self?.volumeRows = rows
                self?.trashSizeBytes = Self.calculateTrashSizeBytes()
                self?.updateStatus()
                self?.rebuildMenu()
                self?.updateFileSystemChangeMonitoring()
            }
        }
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
        let paths = volumeRows
            .filter { $0.kind != .network && !$0.isNonFreeMetric }
            .map { $0.url.path }
        fileSystemChangeMonitor?.update(paths: paths)
    }

    private static func readVolumes() -> [VolumeRow] {
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

        guard let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) else {
            return []
        }

        var rows: [VolumeRow] = []
        for url in urls {
            guard let values = try? url.resourceValues(forKeys: Set(keys)) else { continue }
            // Finder の「使用可能」は importantUsage（パージ可能領域を含む）を表示する。
            // 外部・ネットワークボリュームでは importantUsage が 0 のため availableCapacity を使う。
            let available = values.volumeAvailableCapacityForImportantUsage.flatMap { $0 > 0 ? $0 : nil }
                ?? values.volumeAvailableCapacity.map(Int64.init)
                ?? freeBytes(for: url)
            guard let available else { continue }

            let name = values.volumeLocalizedName ?? values.volumeName ?? url.lastPathComponent
            let isEjectable = values.volumeIsEjectable ?? false
            let isUnmountable = pathReportsUnmountable(url.path)
            let isRoot = values.volumeIsRootFileSystem ?? (url.path == "/")
            let icon = (values.effectiveIcon as? NSImage)
                ?? NSWorkspace.shared.icon(forFile: url.path)

            let info = diskInfo(for: url.path)
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

        return rows.sorted {
            if $0.kind != $1.kind { return $0.kind < $1.kind }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    /// `NSWorkspace.getFileSystemInfoForPath` の isUnmountable に相当。
    private static func pathReportsUnmountable(_ path: String) -> Bool {
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

    private static func diskInfo(for mountPath: String) -> [String: Any] {
        let task = Process()
        let pipe = Pipe()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/diskutil")
        task.arguments = ["info", "-plist", mountPath]
        task.standardOutput = pipe
        task.standardError = Pipe()

        do {
            try task.run()
        } catch {
            return [:]
        }

        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        guard task.terminationStatus == 0 else { return [:] }
        guard let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil),
              let dict = plist as? [String: Any] else {
            return [:]
        }
        return dict
    }

    private static func freeBytes(for url: URL) -> Int64? {
        let attrs = try? FileManager.default.attributesOfFileSystem(forPath: url.path)
        if let free = attrs?[.systemFreeSize] as? NSNumber {
            return free.int64Value
        }
        if let free = attrs?[.systemFreeSize] as? Int64 {
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

        menu.addItem(.separator())
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
        let checkUpdateItem = NSMenuItem(
            title: NSLocalizedString("menu.check_for_updates", comment: ""),
            action: #selector(SPUStandardUpdaterController.checkForUpdates(_:)),
            keyEquivalent: ""
        )
        checkUpdateItem.target = updaterController
        menu.addItem(checkUpdateItem)
        menu.addItem(.separator())
        menu.addItem(mi(NSLocalizedString("menu.quit", comment: ""), #selector(quit), "q"))
    }

    private func volumeMenuItem(for row: VolumeRow) -> NSMenuItem {
        let item = NSMenuItem(title: volumeMenuTitle(for: row), action: #selector(openVolumeFromMenu(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = row
        item.attributedTitle = volumeMenuAttributedTitle(for: row)
        let icon = row.icon.copy() as? NSImage ?? row.icon
        icon.size = NSSize(width: 16, height: 16)
        item.image = icon

        if row.showsDetachSubmenu {
            let submenu = NSMenu()
            let detachItem = NSMenuItem(title: "", action: #selector(detachVolumeFromMenu(_:)), keyEquivalent: "")
            detachItem.target = self
            detachItem.representedObject = row
            let useUnmount = row.detachUsesUnmountLabel
            let titleKey = useUnmount ? "menu.unmount" : "menu.eject"
            let symbolName = useUnmount ? "externaldrive.badge.minus" : "eject.fill"
            detachItem.title = NSLocalizedString(titleKey, comment: "")
            let a11y = NSLocalizedString(useUnmount ? "a11y.unmount" : "a11y.eject", comment: "")
            if let icon = NSImage(systemSymbolName: symbolName, accessibilityDescription: a11y)
                ?? NSImage(systemSymbolName: "eject.fill", accessibilityDescription: a11y) {
                icon.isTemplate = true
                icon.size = NSSize(width: 14, height: 14)
                detachItem.image = icon
            }
            submenu.addItem(detachItem)
            item.submenu = submenu
        }
        return item
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

    private func openInFinder(_ row: VolumeRow) {
        _ = NSWorkspace.shared.open(row.url)
    }

    @objc private func openVolumeFromMenu(_ sender: NSMenuItem) {
        guard let row = sender.representedObject as? VolumeRow else { return }
        openInFinder(row)
    }

    private func openTrash() {
        let trashURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".Trash", isDirectory: true)
        _ = NSWorkspace.shared.open(trashURL)
    }

    @objc private func openTrashFromMenu() {
        openTrash()
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
        do {
            try NSWorkspace.shared.unmountAndEjectDevice(at: row.url)
        } catch {
            let alert = NSAlert()
            let failTitleKey = row.isEjectable ? "alert.eject_failed_title" : "alert.unmount_failed_title"
            alert.messageText = NSLocalizedString(failTitleKey, comment: "")
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
            alert.runModal()
        }
        refreshVolumes()
    }

    @objc private func copyStatus() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(statusText, forType: .string)
    }

    @objc private func refreshNow() {
        refreshVolumes()
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

    private static func calculateTrashSizeBytesWithFinder() -> Int64? {
        guard !finderTrashSizeDenied else { return nil }
        // physical size of trash returns Finder's cached value (stale after file moves).
        // Summing each item individually via index forces a fresh per-item lookup.
        // try blocks inside the script absorb transient errors (e.g. item removed mid-loop).
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
        guard let appleScript = NSAppleScript(source: script) else { return nil }
        var error: NSDictionary?
        let result = appleScript.executeAndReturnError(&error)
        if let error {
            // -1743: automation not authorized — disable permanently
            // other errors are transient; don't disable
            let errorNumber = error[NSAppleScript.errorNumber] as? Int
            if errorNumber == -1743 {
                finderTrashSizeDenied = true
            }
            return nil
        }
        if let stringValue = result.stringValue,
           let bytes = Int64(stringValue) {
            return bytes
        }
        let intValue = result.int32Value
        return intValue >= 0 ? Int64(intValue) : nil
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

        if let mounted = fm.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: []) {
            for volume in mounted {
                let volumeTrash = volume.appendingPathComponent(".Trashes/\(uid)", isDirectory: true)
                dirs.append(volumeTrash)
            }
        }

        return dirs.filter { dir in
            let key = dir.path
            guard !seen.contains(key) else { return false }
            seen.insert(key)
            var isDir: ObjCBool = false
            return fm.fileExists(atPath: key, isDirectory: &isDir) && isDir.boolValue
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
