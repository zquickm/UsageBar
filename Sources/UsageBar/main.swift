import Cocoa
import SwiftUI
import Charts

// Classic System Events login item — the mechanism that actually shows up in
// 系统设置 → 登录项 → 登录时打开. SMAppService was tried first but BTM rejects
// adhoc-signed apps (register() "succeeds", status stays .notFound).
enum LoginItem {
    private static var name: String {
        URL(fileURLWithPath: Bundle.main.bundlePath).deletingPathExtension().lastPathComponent
    }

    static func set(_ on: Bool) {
        let path = Bundle.main.bundlePath
        let src = on
            ? "tell application \"System Events\" to make login item at end with properties {path:\"\(path)\", hidden:false}"
            : "tell application \"System Events\" to delete login item \"\(name)\""
        DispatchQueue.global(qos: .utility).async { runOSA(src) }
    }

    static func isEnabled(_ done: @escaping (Bool) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            let out = runOSA("tell application \"System Events\" to get the name of every login item")
            DispatchQueue.main.async { done(out.contains(name)) }
        }
    }

    @discardableResult
    private static func runOSA(_ src: String) -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", src]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = Pipe()
        do { try p.run() } catch { return "" }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(data: data, encoding: .utf8) ?? ""
    }
}

// UsageBar — menu bar token display. Click ⚡ for a popover with a 7-day
// smoothed line chart fed by `~/.local/bin/usage 3650 --chart` (full-history
// fetch so model discovery follows everything ccusage can find; the chart
// itself slices the last 7 days). The 工具 dropdown's "支持、无数据" section is
// parsed live from `ccusage --help`, so it tracks the installed ccusage.
// 总量 only by default; every other agent/model appears exclusively when
// checked in the dropdown. The list mirrors the chart selection exactly.

struct DayPoint: Codable, Identifiable {
    let date: String
    let agents: [String: Int]
    let models: [String: Int]
    var id: String { date }
}

struct Payload: Codable {
    let days: [DayPoint]
    let total: Int
}

final class Store: ObservableObject {
    @Published var title = "…"
    @Published var total = 0
    @Published var points: [DayPoint] = []
    @Published var knownModels: Set<String> = []  // whole fetch window
    @Published var supportedAgents: [String] = []  // parsed from ccusage --help
}

func human(_ n: Int) -> String {
    let d = Double(n)
    if d >= 1e8 { return zhTrim(String(format: "%.2f亿", d / 1e8)) }
    if d >= 1e4 { return zhTrim(String(format: "%.1f万", d / 1e4)) }
    return "\(n)"
}

func zhTrim(_ s: String) -> String {
    s.replacingOccurrences(of: #"\.?0+(?=[亿万]$)"#, with: "", options: .regularExpression)
}

extension Color {
    init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = UInt64(s, radix: 16) else { return nil }
        self.init(red: Double((v >> 16) & 0xFF) / 255,
                  green: Double((v >> 8) & 0xFF) / 255,
                  blue: Double(v & 0xFF) / 255)
    }
}

func runJSON() -> Payload? {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/zsh")
    // launchd/login-window environments carry the bare system PATH; ccusage
    // lives in homebrew, so prepend the usual bins or `usage` dies at login.
    // Full-history fetch: chart slices the last 7, discovery covers everything
    p.arguments = ["-c", "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"; ~/.local/bin/usage 3650 --chart"]
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = Pipe()
    do { try p.run() } catch { return nil }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    guard p.terminationStatus == 0 else { return nil }
    return try? JSONDecoder().decode(Payload.self, from: data)
}

// ccusage --help enumerates every agent CLI it can parse; each such subcommand
// line ends with "usage commands". Parsed live so the 支持、无数据 section
// follows the installed ccusage version instead of a hardcoded list.
func parseCCusageAgents() -> [String] {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/zsh")
    p.arguments = ["-c", "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"; ccusage --help"]
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = Pipe()
    do { try p.run() } catch { return [] }
    let out = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    p.waitUntilExit()
    guard p.terminationStatus == 0 else { return [] }
    return out.split(separator: "\n")
        .filter { $0.contains("usage commands") }
        .compactMap { $0.split(whereSeparator: \.isWhitespace).first.map(String.init) }
}

// MARK: - Series, defaults & palette

enum ChartMode: String, CaseIterable {
    case agent = "按工具"
    case model = "按模型"
}

let agentOrder = ["总量", "zcode", "codex", "dsh"]
let agentColor: [String: Color] = ["zcode": .blue, "codex": .purple, "dsh": .orange]
let modelOrderKnown = ["总量", "GLM-5.3-Flash", "GLM-5.3", "deepseek-flash"]

func hexOfNS(_ c: NSColor) -> String {
    let srgb = c.usingColorSpace(.sRGB) ?? c
    return String(format: "#%02X%02X%02X",
                  Int(round(srgb.redComponent * 255)),
                  Int(round(srgb.greenComponent * 255)),
                  Int(round(srgb.blueComponent * 255)))
}

// A tiny colored dot that opens the system color panel on click and reports
// live changes (wheel / RGB / hex all come from NSColorPanel).
struct ColorDot: NSViewRepresentable {
    let color: Color
    let size: CGFloat
    let onPick: (NSColor) -> Void

    func makeNSView(context: Context) -> DotView {
        let v = DotView()
        v.dotColor = NSColor(color)
        v.dotSize = size
        v.onPick = onPick
        return v
    }

    func updateNSView(_ v: DotView, context: Context) {
        v.dotColor = NSColor(color)
    }

    final class DotView: NSView {
        var dotColor: NSColor = .black { didSet { needsDisplay = true } }
        var dotSize: CGFloat = 7
        var onPick: ((NSColor) -> Void)?

        override func draw(_ dirtyRect: NSRect) {
            let r = NSRect(x: bounds.midX - dotSize / 2, y: bounds.midY - dotSize / 2,
                           width: dotSize, height: dotSize)
            dotColor.setFill()
            NSBezierPath(ovalIn: r).fill()
        }

        override func resetCursorRects() {
            addCursorRect(bounds, cursor: .pointingHand)
        }

        private var hoverArea: NSTrackingArea?

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            if let a = hoverArea { removeTrackingArea(a) }
            let a = NSTrackingArea(rect: bounds,
                                   options: [.cursorUpdate, .activeAlways],
                                   owner: self, userInfo: nil)
            addTrackingArea(a)
            hoverArea = a
        }

        override func cursorUpdate(with event: NSEvent) {
            NSCursor.pointingHand.set()
        }

        override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
            true  // single click works without activating the window first
        }

        override func mouseDown(with event: NSEvent) {
            // switch the popover to manual-close while the color panel is up
            NotificationCenter.default.post(name: .usagebarColorPanelOpening, object: nil)
            let panel = NSColorPanel.shared
            panel.setTarget(self)
            panel.setAction(#selector(colorChanged(_:)))
            panel.color = dotColor
            panel.makeKeyAndOrderFront(nil)
        }

        @objc func colorChanged(_ sender: NSColorPanel) {
            let c = sender.color.usingColorSpace(.sRGB) ?? sender.color
            dotColor = c
            onPick?(c)
        }
    }
}

func agentFallbackColor(_ name: String) -> Color {
    let l = name.lowercased()
    if l.contains("claude") { return .red }
    if l.contains("gemini") { return .teal }
    if l.contains("grok") { return .pink }
    if l.contains("droid") { return .brown }
    if l.contains("opencode") { return .indigo }
    if l.contains("cursor") { return .cyan }
    return .gray
}

func modelColor(_ name: String) -> Color {
    let l = name.lowercased()
    if l.hasPrefix("glm") { return .blue }
    if l.contains("deepseek") { return .orange }
    if l.hasPrefix("gpt") { return .purple }
    if l.contains("claude") { return .red }
    if l.contains("gemini") { return .teal }
    if l.contains("kimi") { return .pink }
    return .gray
}

extension View {
    // pointing-hand cursor over clickable SwiftUI controls (macOS default is arrow)
    func handCursor() -> some View {
        onHover { inside in
            if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
        }
    }
}

extension Notification.Name {
    static let usagebarColorPanelOpening = Notification.Name("usagebarColorPanelOpening")
}

// MARK: - Popover

struct ContentView: View {
    @ObservedObject var store: Store
    var onDaily: () -> Void
    var onRefresh: () -> Void
    var onQuit: () -> Void
    @State private var selected: DayPoint?
    @State private var mode: ChartMode
    @State private var autostart = false
    @AppStorage("usagebar.visible.agents") private var storedAgents = ""
    @AppStorage("usagebar.visible.models") private var storedModels = ""
    @AppStorage("usagebar.colors") private var storedColors = ""

    init(store: Store, onDaily: @escaping () -> Void, onRefresh: @escaping () -> Void, onQuit: @escaping () -> Void) {
        self.store = store
        self.onDaily = onDaily
        self.onRefresh = onRefresh
        self.onQuit = onQuit
        _mode = State(initialValue: ProcessInfo.processInfo.environment["USAGEBAR_MODE"] == "model" ? .model : .agent)
    }

    static func cnDate(_ iso: String) -> String {
        let parts = iso.split(separator: "-")
        guard parts.count == 3, let m = Int(parts[1]), let d = Int(parts[2]) else { return iso }
        return "\(m).\(String(format: "%02d", d))"
    }

    var xLabels: [String] { store.points.map { Self.cnDate($0.date) } }

    var discovered: Set<String> {
        if mode == .model {
            // full fetch window so unused models still show in the dropdown
            var names = store.knownModels
            names.insert("总量")
            return names
        }
        var names = Set(store.points.flatMap { $0.agents.keys })
        names.insert("总量")
        names.formUnion(["zcode", "codex", "dsh"])
        return names
    }

    func value(_ p: DayPoint, _ name: String) -> Int {
        if name == "总量" {
            return (mode == .agent ? p.agents.values : p.models.values).reduce(0, +)
        }
        return mode == .agent ? (p.agents[name] ?? 0) : (p.models[name] ?? 0)
    }

    var windowTotals: [String: Int] {
        var t = [String: Int]()
        for p in store.points {
            for n in discovered { t[n, default: 0] += value(p, n) }
        }
        return t
    }

    func todayValue(_ name: String) -> Int {
        store.points.last.map { value($0, name) } ?? 0
    }

    var autoDefault: Set<String> {
        // nothing is auto-selected except 总量 — every agent/model appears
        // only when the user checks it in the dropdown
        ["总量"]
    }

    var visible: Set<String> {
        let stored = mode == .agent ? storedAgents : storedModels
        if stored.isEmpty { return autoDefault }
        return Set(stored.split(separator: ",").map(String.init))
    }

    func setVisible(_ names: Set<String>) {
        let s = names.joined(separator: ",")
        if mode == .agent { storedAgents = s } else { storedModels = s }
    }

    var series: [String] {
        let names = visible.intersection(discovered)
        let known = mode == .agent ? agentOrder : modelOrderKnown
        let ordered = known.filter { names.contains($0) }
            + names.filter { !known.contains($0) }.sorted()
        return ordered.isEmpty ? ["总量"] : ordered
    }

    var colorOverrides: [String: String] {
        guard !storedColors.isEmpty else { return [:] }
        return Dictionary(uniqueKeysWithValues: storedColors.split(separator: ";").compactMap { part in
            let kv = part.split(separator: "=")
            guard kv.count == 2 else { return nil }
            return (String(kv[0]), String(kv[1]))
        })
    }

    func setColor(_ name: String, _ hex: String) {
        var c = colorOverrides
        c[name] = hex
        storedColors = c.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: ";")
    }

    func clearColor(_ name: String) {
        var c = colorOverrides
        c.removeValue(forKey: name)
        storedColors = c.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: ";")
    }

    func color(_ name: String) -> Color {
        if let hex = colorOverrides[name], let c = Color(hex: hex) { return c }
        if name == "总量" { return .primary }  // black in light mode, white in dark
        if mode == .agent { return agentColor[name] ?? agentFallbackColor(name) }
        return modelColor(name)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Picker("模式", selection: $mode) {
                    ForEach(ChartMode.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .handCursor()
                seriesMenu
            }
            chart
            Divider()
            valueList
            HStack {
                Button("终端日报", action: onDaily).handCursor()
                Spacer()
                Toggle("开机自启", isOn: $autostart)
                    .toggleStyle(.checkbox)
                    .help("登录时自动启动（系统设置 → 登录项里可见）")
                    .onChange(of: autostart) { _, on in LoginItem.set(on) }
                Button("刷新", action: onRefresh).handCursor()
                Button("退出", action: onQuit).handCursor()
            }.buttonStyle(.borderless)
        }
        .padding(14)
        .frame(width: 380)
        .onAppear { LoginItem.isEnabled { autostart = $0 } }
    }

    var seriesMenu: some View {
        Menu {
            Section(mode == .agent ? "显示工具" : "显示模型") {
                ForEach(discovered.sorted(), id: \.self) { name in
                    Toggle("\(name)  \(human(windowTotals[name] ?? 0))", isOn: Binding(
                        get: { visible.contains(name) },
                        set: { on in
                            var v = visible
                            if on { v.insert(name) } else { v.remove(name) }
                            setVisible(v)
                        }
                    ))
                }
            }
            if mode == .agent {
                let silent = store.supportedAgents.filter { !discovered.contains($0) }
                if !silent.isEmpty {
                    Section("支持、无数据（来自 ccusage）") {
                        ForEach(silent, id: \.self) { Text($0).foregroundStyle(.secondary) }
                    }
                }
            }
            Button("恢复默认（仅显示总量）") { setVisible([]) }
        } label: {
            Image(systemName: "slider.horizontal.3")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .handCursor()
        .help("选择显示的曲线；颜色点列表里的色块修改")
    }

    // the list mirrors the chart selection exactly (series); 总量 pinned
    // first when visible, the rest by 7-day usage
    var listSeries: [String] {
        let t = windowTotals
        return series.sorted { a, b in
            if (a == "总量") != (b == "总量") { return a == "总量" }
            return (t[a] ?? 0) > (t[b] ?? 0)
        }
    }

    // the list follows the chart hover: whichever day the cursor is on,
    // that day's per-series usage is listed (defaults to today)
    var displayPoint: DayPoint? {
        selected ?? store.points.last
    }

    var valueList: some View {
        let rows = listSeries
        guard let dp = displayPoint else { return AnyView(EmptyView()) }
        let dateLabel = Self.cnDate(dp.date)
        let height = min(CGFloat(rows.count) * 17 + 24, 200)
        return AnyView(ScrollView {
            VStack(spacing: 3) {
                HStack(spacing: 6) {
                    Text("\(dateLabel) 用量")
                        .font(.caption2.bold())
                        .foregroundStyle(.secondary)
                    Spacer()
                    Text("7天")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .frame(width: 92, alignment: .trailing)
                }
                ForEach(rows, id: \.self) { name in
                    HStack(spacing: 6) {
                        // small dot; click → system color panel (wheel / RGB / hex)
                        ColorDot(color: color(name), size: 7) { ns in
                            setColor(name, hexOfNS(ns))
                        }
                        .frame(width: 16, height: 16)
                        .help("点击修改颜色（色轮 / RGB / 十六进制）")
                        Text(name)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .frame(width: 108, alignment: .leading)
                        Spacer()
                        Text(human(value(dp, name)))
                            .frame(width: 84, alignment: .trailing)
                            .foregroundStyle(.primary)
                        Text(human(windowTotals[name] ?? 0))
                            .frame(width: 92, alignment: .trailing)
                    }
                    .font(.caption2)
                    .monospacedDigit()
                    .foregroundStyle(name == "总量" ? .primary : .secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 2)
        }
        .frame(height: height)
        .scrollIndicators(.visible)
        .scrollBounceBehavior(.basedOnSize))
    }

    var chart: some View {
        Chart {
            ForEach(series, id: \.self) { name in
                ForEach(Array(store.points.enumerated()), id: \.element.id) { idx, p in
                    LineMark(x: .value("日期", idx), y: .value("token", value(p, name)))
                        .interpolationMethod(.catmullRom(alpha: 1))
                        .foregroundStyle(by: .value("系列", name))
                        .lineStyle(name == "总量"
                                   ? StrokeStyle(lineWidth: 2, dash: [5, 3])
                                   : StrokeStyle(lineWidth: 2.5))
                }
            }
            ForEach(Array(store.points.enumerated()), id: \.element.id) { idx, p in
                if selected?.date == p.date {
                    RuleMark(x: .value("日期", idx))
                        .foregroundStyle(.secondary.opacity(0.25))
                }
            }
        }
        .chartForegroundStyleScale(domain: series, range: series.map(color))
        .chartLegend(.hidden)
        .chartXAxis(.hidden)
        .chartYAxis(.hidden)
        .chartXScale(domain: -0.4 ... Double(max(store.points.count - 1, 1)) + 0.4)
        .chartPlotStyle { $0.padding(.bottom, 18) }
        .chartOverlay { proxy in
            GeometryReader { geo in
                ForEach(Array(xLabels.indices), id: \.self) { i in
                    if let px = proxy.position(forX: Double(i)) {
                        let x = max(16, min(geo.size.width - 16, px))
                        Text(xLabels[i])
                            .font(.caption2)
                            .foregroundStyle(Color.secondary)
                            .fixedSize()
                            .position(x: x, y: geo.size.height - 7)
                    }
                }
                Rectangle()
                    .fill(.clear)
                    .contentShape(Rectangle())
                    .onContinuousHover { phase in
                        switch phase {
                        case .active(let loc):
                            guard let xv = proxy.value(atX: loc.x, as: Double.self),
                                  !store.points.isEmpty else { return }
                            let idx = max(0, min(store.points.count - 1, Int(xv.rounded())))
                            selected = store.points[idx]
                        case .ended:
                            selected = nil
                        @unknown default:
                            break
                        }
                    }
            }
        }
        .frame(height: 172)
    }
}

// MARK: - App

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let popover = NSPopover()
    let store = Store()
    var busy = false

    func applicationDidFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.accessory)
        // one-time: add to 登录时打开 (System Events login item). First call
        // triggers the one-time Automation consent prompt.
        let ud = UserDefaults.standard
        if !ud.bool(forKey: "usagebar.autostart.tried") {
            ud.set(true, forKey: "usagebar.autostart.tried")
            LoginItem.isEnabled { on in if !on { LoginItem.set(true) } }
        }
        // monochrome template bolt: white on dark menu bar, black on light
        if let bolt = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: "用量") {
            bolt.isTemplate = true
            item.button?.image = bolt
        }
        item.button?.title = "…"
        item.button?.action = #selector(togglePopover)
        item.button?.target = self
        // native click-outside-to-close; temporarily manual while color panel is open
        popover.behavior = .transient
        popover.contentViewController = NSHostingController(
            rootView: ContentView(
                store: store,
                onDaily: { self.openDaily() },
                onRefresh: { self.refresh() },
                onQuit: { NSApp.terminate(nil) }
            ))
        NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 && self.popover.isShown {  // Esc
                self.popover.performClose(nil)
                return nil
            }
            return e
        }
        // click anywhere outside this app closes the popover (accessory apps
        // never really "resign active", so didResignActive alone won't fire)
        NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) { _ in
            if self.popover.isShown {
                self.popover.performClose(nil)
            }
        }
        NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification, object: nil, queue: .main
        ) { _ in
            DispatchQueue.main.async {
                if self.popover.isShown { self.popover.performClose(nil) }
            }
        }
        // detach the color panel from any dot view before it goes away
        NotificationCenter.default.addObserver(
            forName: NSPopover.willCloseNotification, object: popover, queue: .main
        ) { _ in
            DispatchQueue.main.async {
                NSColorPanel.shared.setTarget(nil)
            }
        }
        // color panel open → manual close so picking a color doesn't dismiss
        // the popover; panel closed → back to native click-outside behavior
        NotificationCenter.default.addObserver(
            forName: .usagebarColorPanelOpening, object: nil, queue: .main
        ) { _ in
            DispatchQueue.main.async { self.popover.behavior = .applicationDefined }
        }
        NotificationCenter.default.addObserver(
            self, selector: #selector(colorPanelWillClose(_:)),
            name: NSWindow.willCloseNotification, object: nil
        )
        refresh()
        Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            DispatchQueue.main.async { self.refresh() }
        }
        if ProcessInfo.processInfo.environment["USAGEBAR_AUTOSHOW"] == "1" {
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.showPopover() }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        NSColorPanel.shared.setTarget(nil)
    }

    @objc func colorPanelWillClose(_ note: Notification) {
        if (note.object as? NSWindow) === NSColorPanel.shared {
            popover.behavior = .transient
        }
    }

    @objc func togglePopover() {
        if popover.isShown { popover.performClose(nil) } else { showPopover() }
    }

    func showPopover() {
        refresh()
        guard let button = item.button else { return }
        // make the popover the key window — transient close-on-outside-click
        // only works when the app is active (this is what CC Switch et al. do)
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }

    @objc func openDaily() {
        popover.performClose(nil)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", "tell application \"Terminal\" to do script \"usage 7\""]
        try? p.run()
    }

    func refresh() {
        guard !busy else { return }
        busy = true
        let needAgents = store.supportedAgents.isEmpty
        DispatchQueue.global(qos: .utility).async { [self] in
            let payload = runJSON()
            let agents = needAgents ? parseCCusageAgents() : []
            DispatchQueue.main.async { [self] in
                busy = false
                guard let payload else { item.button?.title = "n/a"; return }
                store.total = payload.total
                store.points = Array(payload.days.suffix(7))  // chart window
                store.knownModels = Set(payload.days.flatMap { $0.models.keys })
                if !agents.isEmpty { store.supportedAgents = agents }
                store.title = human(payload.total)
                item.button?.title = store.title
                NSLog("UsageBar: title=%@ points=%d", store.title, store.points.count)
            }
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
