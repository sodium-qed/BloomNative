import SwiftUI
import Observation
import AppKit
import MetalKit

@MainActor @Observable final class BloomController {
    var language = AppLanguage.selected { didSet { UserDefaults.standard.set(language.rawValue, forKey: "appLanguage"); languageDidChange?() } }
    @ObservationIgnored var languageDidChange: (() -> Void)?
    func text(_ key: String) -> String { BloomStrings.text(key, language: language) }
    var enabled = false
    var automatic = true { didSet { UserDefaults.standard.set(automatic, forKey: "automatic") } }
    var manualAngle = 110.0 { didSet { UserDefaults.standard.set(manualAngle, forKey: "manualAngle") } }
    var angle: Double?
    var sensorStatus = "正在检测铰链传感器…"
    var breathing = true { didSet { UserDefaults.standard.set(breathing, forKey: "breathing") } }
    var closedAngle = 10.0
    var openAngle = 120.0 { didSet { UserDefaults.standard.set(openAngle, forKey: "openAngle") } }
    var response = 0.22 { didSet { UserDefaults.standard.set(response, forKey: "response") } }
    var errorMessage: String?
    @ObservationIgnored private let sensor = LidSensor()
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var windows: [NSWindow] = []
    @ObservationIgnored private var views: [BloomMetalView] = []
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var locked = false
    @ObservationIgnored var hideControls: (() -> Void)?
    @ObservationIgnored private let systemWallpaper = SystemWallpaperSync()
    @ObservationIgnored private var wallpaperWork: DispatchWorkItem?
    @ObservationIgnored private var lastRequestedFrame = -1
    @ObservationIgnored weak var preview: BloomMetalView?
    var progress: Double { bloomProgress(angle: automatic ? (angle ?? openAngle) : manualAngle, closed: closedAngle, open: openAngle) }
    init() {
        let defaults = UserDefaults.standard
        defaults.register(defaults: ["automatic": true, "manualAngle": 110.0, "breathing": true, "openAngle": 120.0, "response": 0.22])
        automatic = defaults.bool(forKey: "automatic")
        manualAngle = defaults.double(forKey: "manualAngle")
        breathing = defaults.bool(forKey: "breathing")
        openAngle = defaults.double(forKey: "openAngle")
        response = defaults.double(forKey: "response")
        angle = sensor.read(); sensorStatus = sensor.status
        timer = Timer.scheduledTimer(withTimeInterval: 1.0/30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.update() }
        }
        systemWallpaper.onError = { [weak self] message in self?.errorMessage = self.map { $0.text("系统壁纸同步失败：") + message } }
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.activeSpaceDidChangeNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated {
            guard let self, self.enabled else { return }
            self.windows.forEach { $0.orderBack(nil) }
            self.syncSystemWallpaper()
        } })
        observers.append(nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.pause(true) } })
        observers.append(nc.addObserver(forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.pause(true) } })
        observers.append(nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.wake() } })
        observers.append(nc.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.wake() } })
        observers.append(NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { if self?.enabled == true { self?.startDesktop() } } })
        observers.append(DistributedNotificationCenter.default().addObserver(forName: NSNotification.Name("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.locked = true; self?.pause(true) } })
        observers.append(DistributedNotificationCenter.default().addObserver(forName: NSNotification.Name("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.locked = false; self?.wake() } })
    }
    func update() {
        if !locked { if let reading = sensor.read() { angle = reading }; sensorStatus = sensor.status }
        for view in views + [preview].compactMap({$0}) {
            view.renderer?.target = Float(progress)
            view.renderer?.breath = breathing && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 1 : 0
            view.renderer?.response = response
            let fps = ProcessInfo.processInfo.isLowPowerModeEnabled ? 30 : 60
            if view.preferredFramesPerSecond != fps { view.preferredFramesPerSecond = fps }
        }
        if enabled && !locked {
            let visibleProgress = views.first?.renderer?.progress ?? progress
            let frame = Int((visibleProgress * Double((views.first?.renderer?.frames.count ?? 300)-1)).rounded())
            if frame != lastRequestedFrame {
                lastRequestedFrame = frame
                wallpaperWork?.cancel()
                let work = DispatchWorkItem { [weak self] in self?.syncSystemWallpaper() }
                wallpaperWork = work
                DispatchQueue.main.asyncAfter(deadline: .now()+0.35,execute: work)
            }
        }
        if let preview { preview.isPaused = locked || NSApp.isHidden || preview.window?.isVisible != true || preview.window?.isMiniaturized == true }
    }
    func pause(_ paused: Bool) { (views + [preview].compactMap({$0})).forEach { $0.isPaused = paused } }
    func wake() { sensor.reconnect(); (views + [preview].compactMap({$0})).forEach { $0.renderer?.reset(progress: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? progress : 0); $0.isPaused = locked }; update() }
    func replay() { (views + [preview].compactMap({$0})).forEach { $0.renderer?.reset(playOriginal: true) } }
    func setDesktop(_ value: Bool) {
        enabled = value; UserDefaults.standard.set(value,forKey: "desktopEnabled")
        if value { systemWallpaper.captureOriginals(); startDesktop() }
        else { wallpaperWork?.cancel(); systemWallpaper.restore(); stopDesktop() }
    }
    private func syncSystemWallpaper() {
        guard enabled else { return }
        let requests = zip(windows,views).compactMap { window, view -> (NSScreen,BloomRenderer,Double)? in
            guard let screen = window.screen,let renderer = view.renderer else { return nil }
            return (screen,renderer,renderer.progress)
        }
        systemWallpaper.sync(requests)
    }
    func shutdown() { wallpaperWork?.cancel(); systemWallpaper.restore(); stopDesktop() }
    private func startDesktop() {
        systemWallpaper.captureOriginals()
        lastRequestedFrame = -1
        stopDesktop()
        for screen in NSScreen.screens {
            let window = WallpaperWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false, screen: screen)
            window.setFrame(screen.frame, display: false)
            window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)) + 1)
            window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
            window.ignoresMouseEvents = true; window.isReleasedWhenClosed = false
            window.canHide = false; window.hidesOnDeactivate = false
            window.isExcludedFromWindowsMenu = true
            let view = BloomMetalView(frame: NSRect(origin: .zero, size: screen.frame.size))
            if let failure = view.failure { errorMessage = failure; enabled = false; stopDesktop(); return }
            window.contentView = view; windows.append(window); views.append(view)
            window.orderBack(nil)
        }
        replay(); update(); syncSystemWallpaper()
    }
    private func stopDesktop() { views.forEach { $0.isPaused = true }; windows.forEach { $0.close() }; windows = []; views = [] }
    func installSaver() {
        do {
            let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Screen Savers")
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let destination = directory.appendingPathComponent("Bloom Native.saver")
            if FileManager.default.fileExists(atPath: destination.path) {
                errorMessage = text("屏保已存在。请在系统设置中选择 Bloom Native；若要更新，请先移走旧版。")
            } else {
                let embedded = Bundle.main.resourceURL?.appendingPathComponent("Bloom Native.saver")
                let source = embedded.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil } ?? Bundle.main.bundleURL.deletingLastPathComponent().appendingPathComponent("Bloom Native.saver")
                try FileManager.default.copyItem(at: source, to: destination)
            }
            openSaverSettings()
        } catch { errorMessage = error.localizedDescription }
    }
    func openSaverSettings() {
        if #available(macOS 26, *) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Wallpaper-Settings.extension")!)
        } else {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.ScreenSaver-Settings.extension")!)
        }
    }
}
struct MetalPreview: NSViewRepresentable {
    let controller: BloomController
    func makeNSView(context: Context) -> BloomMetalView {
        let view = BloomMetalView(); controller.preview = view
        if let failure = view.failure { DispatchQueue.main.async { controller.errorMessage = failure } }
        return view
    }
    func updateNSView(_ nsView: BloomMetalView, context: Context) {
        nsView.renderer?.target = Float(controller.progress)
        nsView.renderer?.breath = controller.breathing ? 1 : 0
    }
}
struct BloomContent: View {
    @Bindable var controller: BloomController
    var body: some View {
        HStack(spacing: 0) {
            ScrollView { VStack(alignment: .leading, spacing: 22) {
                HStack { Image(systemName: "fanblades.fill").font(.title).foregroundStyle(.blue); Text("Bloom").font(.system(size: 30, weight: .semibold, design: .rounded)) }
                Text(controller.text("随开合，慢慢绽放。")).foregroundStyle(.secondary)
                Picker(controller.text("语言"), selection: $controller.language) {
                    ForEach(AppLanguage.allCases) { language in Text(language.title).tag(language) }
                }.pickerStyle(.segmented)
                Divider()
                Toggle(controller.text("启用动态桌面"), isOn: Binding(get: {controller.enabled}, set: {controller.setDesktop($0)}))
                    .toggleStyle(.switch)
                Toggle(controller.text("跟随屏幕开合"), isOn: $controller.automatic).toggleStyle(.switch)
                VStack(alignment: .leading, spacing: 7) {
                    Label(controller.text(controller.sensorStatus), systemImage: controller.angle == nil ? "info.circle" : "sensor.fill")
                        .font(.caption).foregroundStyle(.secondary)
                    if let angle = controller.angle { Text("\(controller.text("当前角度"))  \(angle, specifier: "%.0f")°").font(.system(.title3, design: .monospaced)) }
                    else { Text(controller.text("此机型无法读取时，使用唤醒展开或手动预览。")).font(.caption).foregroundStyle(.secondary) }
                }
                if !controller.automatic {
                    VStack(alignment: .leading) { Text("\(controller.text("预览角度"))  \(controller.manualAngle, specifier: "%.0f")°").font(.caption); Slider(value: $controller.manualAngle, in: 0...150).accessibilityLabel(controller.text("预览开合角度")) }
                }
                DisclosureGroup(controller.text("展开与动效")) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("\(controller.text("完全展开角度"))  \(controller.openAngle, specifier: "%.0f")°").font(.caption)
                        Slider(value: $controller.openAngle, in: 60...150).accessibilityLabel(controller.text("完全展开角度"))
                        Text("\(controller.text("平滑跟随"))  \(controller.response, specifier: "%.2f") \(controller.text("秒"))").font(.caption)
                        Slider(value: $controller.response, in: 0.08...0.7).accessibilityLabel(controller.text("平滑跟随时间"))
                        Toggle(controller.text("轻微呼吸"), isOn: $controller.breathing)
                    }.padding(.top, 10)
                }
                Button(controller.text("重播展开"), systemImage: "play.fill") { controller.replay() }.buttonStyle(.bordered)
                Button(controller.text("隐藏控制面板"), systemImage: "rectangle.compress.vertical") { controller.hideControls?() }.buttonStyle(.bordered)
                Text(controller.text("关闭或隐藏窗口后，壁纸继续运行。菜单栏 Bloom 可重新打开。")).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Text(controller.text("屏保 · 安静地流动")).font(.headline)
                Text(controller.text("安装后在系统屏保设置中选择 Bloom Native。锁屏的密码界面由 macOS 管理，动画在屏保运行时显示。")).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Button(controller.text("安装屏保并打开设置"), systemImage: "moon.stars") { controller.installSaver() }
                Link(controller.text("原动画：Microsoft / Six N. Five"), destination: URL(string: "https://sixnfive.com/projects/windows-11/")!).font(.caption2)
            }.padding(26) }.scrollIndicators(.hidden).frame(width: 320).background(.background)
            VStack(alignment: .leading, spacing: 14) {
                HStack { Text("BLUE BLOOM").font(.system(.caption, design: .monospaced)).tracking(3); Spacer(); Text(controller.enabled ? controller.text("桌面运行中") : controller.text("实时预览")).font(.caption).foregroundStyle(.secondary) }
                MetalPreview(controller: controller).clipShape(.rect(cornerRadius: 18))
                    .overlay(alignment: .bottomLeading) {
                        VStack(alignment: .leading, spacing: 5) { Text(controller.text("一朵花，一次新的开始。")).font(.title2.weight(.medium)); Text(controller.text("Windows 11 Bloom · 原作者展开动画")).font(.caption).foregroundStyle(.white.opacity(0.65)) }.padding(25).allowsHitTesting(false)
                    }
                Text(controller.text("原始动画逐帧跟随 · 300 帧 / 5 秒 · GPU 平滑播放")).font(.caption).foregroundStyle(.secondary)
            }.padding(26).background(Color(red: 0.04, green: 0.06, blue: 0.11))
        }.frame(minWidth: 980, minHeight: 660).preferredColorScheme(.dark)
        .alert("Bloom", isPresented: Binding(get: {controller.errorMessage != nil}, set: { if !$0 {controller.errorMessage = nil} })) { Button(controller.text("好")) { controller.errorMessage = nil } } message: { Text(controller.errorMessage ?? "") }
    }
}
@MainActor final class SystemWallpaperSync {
    private let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Bloom Native/WallpaperFrames-v3")
    private var originals: [String: [String: Any]] = UserDefaults.standard.dictionary(forKey: "originalWallpapers") as? [String: [String: Any]] ?? [:]
    private var generation = 0
    private var busy = false
    private var pending: [(NSScreen, BloomRenderer, Double)]?
    // Only immutable GPU resources are used on the worker; live renderer state is
    // captured as a progress value on the main thread, never read by the worker.
    private struct SnapshotWork: @unchecked Sendable {
        let screenID: String
        let size: CGSize
        let renderer: BloomRenderer
        let progress: Double
    }
    var onError: ((String) -> Void)?
    private func key(_ screen: NSScreen) -> String { String((screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0) }
    func captureOriginals() {
        for screen in NSScreen.screens {
            guard let url = NSWorkspace.shared.desktopImageURL(for: screen), !url.path.hasPrefix(directory.path) else { continue }
            let options = NSWorkspace.shared.desktopImageOptions(for: screen) ?? [:]
            var saved: [String: Any] = ["url": url.absoluteString]
            if let scaling = options[.imageScaling] as? NSNumber { saved["scaling"] = scaling }
            if let clipping = options[.allowClipping] as? NSNumber { saved["clipping"] = clipping }
            if let color = options[.fillColor] as? NSColor,let data = try? NSKeyedArchiver.archivedData(withRootObject: color,requiringSecureCoding: true) { saved["color"] = data }
            originals[key(screen)] = saved
        }
        UserDefaults.standard.set(originals, forKey: "originalWallpapers")
    }
    func sync(_ requests: [(NSScreen, BloomRenderer, Double)]) {
        guard !requests.isEmpty else { return }
        if busy { pending = requests; return }
        busy = true
        let revision = generation
        let directory = directory
        let work = requests.map { screen, renderer, progress in
            SnapshotWork(screenID: key(screen),size: screen.frame.size,renderer: renderer,progress: progress)
        }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var results: [(String, URL)] = []
            var failure: String?
            do {
                try FileManager.default.createDirectory(at: directory,withIntermediateDirectories: true)
                for job in work {
                    let screenID = job.screenID, size = job.size, renderer = job.renderer, progress = job.progress
                    // Resolution follows source detail; no pointless retina upscaling.
                    let width = min(renderer.frames.luma.width, Int(size.width * 2))
                    let height = max(1, Int(Double(width) * size.height / size.width))
                    let frame = Int((progress * Double(renderer.frames.count-1)).rounded())
                    let url = directory.appendingPathComponent("display-\(screenID)-\(width)x\(height)-frame-\(frame).jpg")
                    if !FileManager.default.fileExists(atPath: url.path) {
                        let data = try renderer.wallpaperJPEG(width: width,height: height,progress: Double(frame)/Double(renderer.frames.count-1))
                        try data.write(to: url,options: .atomic)
                    }
                    results.append((screenID,url))
                }
            } catch { failure = error.localizedDescription }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                if self.generation == revision {
                    if let failure { self.onError?(failure) }
                    for (screenID, url) in results {
                        guard let screen = NSScreen.screens.first(where: { self.key($0) == screenID }) else { continue }
                        do {
                            try NSWorkspace.shared.setDesktopImageURL(url,for: screen,options: [.imageScaling: NSImageScaling.scaleAxesIndependently.rawValue,.allowClipping: false])
                            NSLog("Bloom system wallpaper synced: %@", url.lastPathComponent)
                        } catch { self.onError?(error.localizedDescription) }
                    }
                }
                self.busy = false
                if let pending = self.pending { self.pending = nil; self.sync(pending) }
            }
        }
    }
    func restore() {
        generation += 1; pending = nil
        for screen in NSScreen.screens {
            guard let current = NSWorkspace.shared.desktopImageURL(for: screen), current.path.hasPrefix(directory.path),
                  let saved = originals[key(screen)],let string = saved["url"] as? String,let url = URL(string: string) else { continue }
            var options: [NSWorkspace.DesktopImageOptionKey: Any] = [:]
            if let scaling = saved["scaling"] { options[.imageScaling] = scaling }
            if let clipping = saved["clipping"] { options[.allowClipping] = clipping }
            if let data = saved["color"] as? Data,let color = try? NSKeyedUnarchiver.unarchivedObject(ofClass: NSColor.self,from: data) { options[.fillColor] = color }
            do { try NSWorkspace.shared.setDesktopImageURL(url,for: screen,options: options) }
            catch { NSLog("Bloom original wallpaper restoration: %@",error.localizedDescription) }
        }
    }
}

final class WallpaperWindow: NSWindow {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor final class BloomApplicationDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate {
    let controller = BloomController()
    private var panel: NSWindow!
    private var statusItem: NSStatusItem!
    func applicationDidFinishLaunching(_ notification: Notification) {
        panel = NSWindow(contentRect: NSRect(x: 0,y: 0,width: 1060,height: 730), styleMask: [.titled,.closable,.miniaturizable,.resizable], backing: .buffered, defer: false)
        panel.title = "Bloom Native"; panel.isReleasedWhenClosed = false
        panel.contentMinSize = NSSize(width: 980,height: 660)
        panel.level = .normal; panel.collectionBehavior = [.managed]
        panel.delegate = self; panel.center()
        panel.contentView = NSHostingView(rootView: BloomContent(controller: controller))
        controller.hideControls = { [weak self] in self?.panel.orderOut(nil) }
        controller.languageDidChange = { [weak self] in self?.buildMainMenu() }
        buildMainMenu()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "fanblades.fill",accessibilityDescription: "Bloom Native")
        let statusMenu = NSMenu(); statusMenu.delegate = self
        statusItem.menu = statusMenu
        showPanel()
        if UserDefaults.standard.bool(forKey: "desktopEnabled") { controller.setDesktop(true) }
    }
    private func buildMainMenu() {
        let mainMenu = NSMenu()
        let appItem = NSMenuItem(); mainMenu.addItem(appItem)
        let appMenu = NSMenu(title: "Bloom Native"); appItem.submenu = appMenu
        let show = appMenu.addItem(withTitle: controller.text("显示控制面板"),action: #selector(showPanel),keyEquivalent: ","); show.target = self
        let hide = appMenu.addItem(withTitle: controller.text("隐藏 Bloom Native"),action: #selector(NSApplication.hide(_:)),keyEquivalent: "h"); hide.target = NSApp
        appMenu.addItem(.separator())
        let quit = appMenu.addItem(withTitle: controller.text("退出 Bloom Native"),action: #selector(NSApplication.terminate(_:)),keyEquivalent: "q"); quit.target = NSApp
        let windowItem = NSMenuItem(); mainMenu.addItem(windowItem)
        let windowMenu = NSMenu(title: controller.text("窗口")); windowItem.submenu = windowMenu
        let minimize = windowMenu.addItem(withTitle: controller.text("最小化"),action: #selector(NSWindow.performMiniaturize(_:)),keyEquivalent: "m"); minimize.target = panel
        let close = windowMenu.addItem(withTitle: controller.text("关闭控制面板"),action: #selector(NSWindow.performClose(_:)),keyEquivalent: "w"); close.target = panel
        NSApp.mainMenu = mainMenu; NSApp.windowsMenu = windowMenu
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let show = menu.addItem(withTitle: controller.text("显示控制面板"),action: #selector(showPanel),keyEquivalent: ""); show.target = self
        let hide = menu.addItem(withTitle: controller.text("隐藏控制面板"),action: #selector(hidePanel),keyEquivalent: ""); hide.target = self
        menu.addItem(.separator())
        let toggle = menu.addItem(withTitle: controller.text("动态壁纸"),action: #selector(toggleDesktop),keyEquivalent: ""); toggle.target = self; toggle.state = controller.enabled ? .on : .off
        let replay = menu.addItem(withTitle: controller.text("重播 Bloom"),action: #selector(replayBloom),keyEquivalent: ""); replay.target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: controller.text("退出 Bloom Native"),action: #selector(NSApplication.terminate(_:)),keyEquivalent: "q").target = NSApp
    }
    @objc func showPanel() {
        NSApp.unhide(nil)
        if panel.isMiniaturized { panel.deminiaturize(nil) }
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    @objc private func hidePanel() { panel.orderOut(nil) }
    @objc private func toggleDesktop() { controller.setDesktop(!controller.enabled) }
    @objc private func replayBloom() { controller.replay() }
    func windowShouldClose(_ sender: NSWindow) -> Bool { sender.orderOut(nil); return false }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showPanel(); return true }
    func applicationWillTerminate(_ notification: Notification) { controller.shutdown() }
}
@main struct BloomMain {
    @MainActor static func main() {
        let application = NSApplication.shared
        let delegate = BloomApplicationDelegate()
        application.delegate = delegate
        application.setActivationPolicy(.regular)
        application.run()
        withExtendedLifetime(delegate) {}
    }
}
