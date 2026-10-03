import Cocoa
import SwiftUI
import Charts
import UniformTypeIdentifiers

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
// smoothed line chart fed by `ccusage daily --json --by-agent` (full-history
// fetch so model discovery follows everything ccusage can find; the chart
// itself slices the last 7 days). If ccusage is missing, a first-run banner
// offers a one-click install (brew/npm). The 工具 dropdown's "支持、无数据"
// section is parsed live from `ccusage --help`, so it tracks the installed
// ccusage. 总量 only by default; every other agent/model appears exclusively
// when checked in the dropdown. The list mirrors the chart selection exactly.

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
    // data-engine (ccusage) setup state for the first-run banner
    @Published var engineMissing = false
    @Published var engineInstalling = false
    @Published var engineError: String?
    @Published var hasBrew = false
    @Published var hasNpm = false
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

let PATHFIX = "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"; "

// run a shell snippet with the usual bins prepended (launchd/login-window
// environments carry a bare system PATH; ccusage lives in homebrew)
func shell(_ script: String) -> (code: Int32, out: String, err: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/zsh")
    p.arguments = ["-c", PATHFIX + script]
    let outPipe = Pipe(), errPipe = Pipe()
    p.standardOutput = outPipe
    p.standardError = errPipe
    do { try p.run() } catch { return (-1, "", "\(error)") }
    let out = outPipe.fileHandleForReading.readDataToEndOfFile()
    let err = errPipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    return (p.terminationStatus,
            String(data: out, encoding: .utf8) ?? "",
            String(data: err, encoding: .utf8) ?? "")
}

// ccusage raw daily JSON (subset we consume)
struct CCModelBreakdown: Decodable {
    let modelName: String?
    let inputTokens: Int?
    let outputTokens: Int?
    let cacheCreationTokens: Int?
    let cacheReadTokens: Int?
}
struct CCAgent: Decodable {
    let agent: String?
    let totalTokens: Int?
    let modelBreakdowns: [CCModelBreakdown]?
}
struct CCDay: Decodable {
    let date: String?
    let period: String?
    let agents: [CCAgent]?
}
struct CCRoot: Decodable { let daily: [CCDay]? }

// optional deepseek ledger, merged the same way the old `usage` glue did
struct DshLedger: Decodable {
    struct Event: Decodable { let day: String; let model: String?; let tokens: Int }
    let events: [Event]?
}

// was: `~/.local/bin/usage 3650 --chart` (python glue). The app now calls
// ccusage directly and aggregates here — one less installed piece. Full
// window: chart slices the last 7, model discovery covers everything.
func runJSON() -> Payload? {
    if ProcessInfo.processInfo.environment["USAGEBAR_FAKE_NO_ENGINE"] == "1" { return nil }
    let df = DateFormatter()
    df.dateFormat = "yyyy-MM-dd"
    df.locale = Locale(identifier: "en_US_POSIX")
    let cal = Calendar.current
    let today = cal.startOfDay(for: Date())
    let start = cal.date(byAdding: .day, value: -3649, to: today)!
    let r = shell("ccusage daily --since \(df.string(from: start)) --offline --json --by-agent")
    guard r.code == 0,
          let root = try? JSONDecoder().decode(CCRoot.self, from: Data(r.out.utf8)) else { return nil }

    var agentsByDay: [String: [String: Int]] = [:]
    var modelsByDay: [String: [String: Int]] = [:]
    for d in root.daily ?? [] {
        guard let k = d.period ?? d.date else { continue }
        var ag = agentsByDay[k] ?? [:]
        var md = modelsByDay[k] ?? [:]
        for a in d.agents ?? [] {
            ag[a.agent ?? "?", default: 0] += a.totalTokens ?? 0
            for m in a.modelBreakdowns ?? [] {
                let mt = (m.inputTokens ?? 0) + (m.outputTokens ?? 0)
                    + (m.cacheCreationTokens ?? 0) + (m.cacheReadTokens ?? 0)
                md[m.modelName ?? "?", default: 0] += mt
            }
        }
        agentsByDay[k] = ag
        modelsByDay[k] = md
    }
    if let home = ProcessInfo.processInfo.environment["HOME"],
       let data = try? Data(contentsOf: URL(fileURLWithPath: home + "/.dsh/.dshw-usage.json")),
       let ledger = try? JSONDecoder().decode(DshLedger.self, from: data) {
        let since = df.string(from: start)
        for e in ledger.events ?? [] where e.day >= since {
            var ag = agentsByDay[e.day] ?? [:]
            ag["dsh", default: 0] += e.tokens
            agentsByDay[e.day] = ag
            var md = modelsByDay[e.day] ?? [:]
            md[e.model ?? "deepseek", default: 0] += e.tokens
            modelsByDay[e.day] = md
        }
    }

    var days: [DayPoint] = []
    var cur = start
    while cur <= today {
        let k = df.string(from: cur)
        days.append(DayPoint(date: k, agents: agentsByDay[k] ?? [:], models: modelsByDay[k] ?? [:]))
        cur = cal.date(byAdding: .day, value: 1, to: cur)!
    }
    let total = days.last.map { $0.agents.values.reduce(0, +) } ?? 0
    return Payload(days: days, total: total)
}

func ccusageInstalled() -> Bool { shell("command -v ccusage").code == 0 }

// ccusage --help enumerates every agent CLI it can parse; each such subcommand
// line ends with "usage commands". Parsed live so the 支持、无数据 section
// follows the installed ccusage version instead of a hardcoded list.
func parseCCusageAgents() -> [String] {
    let r = shell("ccusage --help")
    guard r.code == 0 else { return [] }
    return r.out.split(separator: "\n")
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

// live drag-reorder for the series list: hovering the dragged row over
// another row moves it before that row; the drop itself just ends the session
struct SeriesDropDelegate: DropDelegate {
    let item: String
    @Binding var dragged: String?
    let move: (String, String) -> Void

    func dropEntered(info: DropInfo) {
        guard let d = dragged, d != item else { return }
        move(d, item)
    }

    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }

    func performDrop(info: DropInfo) -> Bool {
        withAnimation(.easeOut(duration: 0.15)) { dragged = nil }
        return true
    }
}

// MARK: - Popover

struct ContentView: View {
    @ObservedObject var store: Store
    var onDaily: () -> Void
    var onRefresh: () -> Void
    var onQuit: () -> Void
    var onInstall: (String) -> Void
    @State private var selected: DayPoint?
    @State private var mode: ChartMode
    @State private var autostart = false
    @AppStorage("usagebar.visible.agents") private var storedAgents = ""
    @AppStorage("usagebar.visible.models") private var storedModels = ""
    @AppStorage("usagebar.colors") private var storedColors = ""
    @AppStorage("usagebar.order.agents") private var storedAgentOrder = ""
    @AppStorage("usagebar.order.models") private var storedModelOrder = ""
    @State private var dragged: String?
    @State private var hovered: String?

    init(store: Store, onDaily: @escaping () -> Void, onRefresh: @escaping () -> Void,
         onQuit: @escaping () -> Void, onInstall: @escaping (String) -> Void) {
        self.store = store
        self.onDaily = onDaily
        self.onRefresh = onRefresh
        self.onQuit = onQuit
        self.onInstall = onInstall
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
            if store.engineMissing {
                engineSetup
            } else {
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
            }
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

    // first-run setup: ccusage missing → offer a one-click install so a fresh
    // download needs no terminal at all
    var engineSetup: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("数据引擎 ccusage 未安装", systemImage: "exclamationmark.triangle")
                .font(.headline)
            Text("它负责读取各 AI CLI 的本地日志（完全本地，不联网上传）。装好后菜单栏即显示用量。")
                .font(.callout)
                .foregroundStyle(.secondary)
            if store.engineInstalling {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("安装中…首次会连 Node 一起装，可能需要几分钟")
                        .font(.callout)
                }
            } else {
                HStack(spacing: 8) {
                    if store.hasBrew {
                        Button("用 Homebrew 安装") { onInstall("brew") }.handCursor()
                    }
                    if store.hasNpm {
                        Button("用 npm 安装") { onInstall("npm") }.handCursor()
                    }
                }
                if let e = store.engineError {
                    Text(e)
                        .font(.caption)
                        .foregroundStyle(.red)
                        .lineLimit(3)
                }
                Text("也可手动执行：brew install ccusage（或 npm i -g ccusage）")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))
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
            Button("恢复默认（仅显示总量）") {
                setVisible([])
                storedAgentOrder = ""
                storedModelOrder = ""
            }
        } label: {
            Image(systemName: "slider.horizontal.3")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .handCursor()
        .help("选择显示的曲线；颜色点列表里的色块修改")
    }

    // the list mirrors the chart selection exactly (series). A user-dragged
    // order (persisted per mode) wins; not-yet-ordered series follow the
    // default sort (总量 first, then 7-day usage)
    var listSeries: [String] {
        let saved = storedOrder.split(separator: ",").map(String.init).filter { series.contains($0) }
        let t = windowTotals
        let rest = series.sorted { a, b in
            if (a == "总量") != (b == "总量") { return a == "总量" }
            return (t[a] ?? 0) > (t[b] ?? 0)
        }.filter { !saved.contains($0) }
        return saved + rest
    }

    private var storedOrder: String { mode == .agent ? storedAgentOrder : storedModelOrder }

    func persistOrder(_ arr: [String]) {
        if mode == .agent { storedAgentOrder = arr.joined(separator: ",") }
        else { storedModelOrder = arr.joined(separator: ",") }
    }

    // direction-aware move (dragging down inserts AFTER the target) —
    // inserting always-before makes the layout shift under the cursor and
    // the drop target flicker; animated so rows slide like dnd-kit does
    func reorder(_ d: String, onto target: String) {
        var arr = listSeries
        guard let from = arr.firstIndex(of: d), let to = arr.firstIndex(of: target), from != to else { return }
        withAnimation(.snappy(duration: 0.22)) {
            if to > from { arr.move(fromOffsets: IndexSet(integer: from), toOffset: to + 1) }
            else { arr.move(fromOffsets: IndexSet(integer: from), toOffset: to) }
            persistOrder(arr)
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
        let height = min(CGFloat(rows.count) * 21 + 24, 200)
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
                .padding(.horizontal, 6)
                ForEach(rows, id: \.self) { name in
                    seriesRow(name, dp: dp)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 2)
        }
        .frame(height: height)
        .scrollIndicators(.visible)
        .scrollBounceBehavior(.basedOnSize))
    }

    // one series row, extracted so the modifier chain stays type-checkable
    func seriesRow(_ name: String, dp: DayPoint) -> some View {
        rowContent(name, dp: dp)
            .font(.caption2)
            .monospacedDigit()
            .foregroundStyle(name == "总量" ? .primary : .secondary)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(
                RoundedRectangle(cornerRadius: 5)
                    .fill(.quaternary)
                    .opacity(hovered == name && dragged != name ? 1 : 0)
            )
            .opacity(dragged == name ? 0.35 : 1)
            .zIndex(dragged == name ? 1 : 0)
            .onDrag {
                dragged = name
                return NSItemProvider(object: name as NSString)
            } preview: {
                dragPreview(name, dp: dp)
            }
            .onDrop(of: [.text], delegate: SeriesDropDelegate(item: name, dragged: $dragged, move: { d, t in
                reorder(d, onto: t)
            }))
            .onHover { inside in
                hovered = inside ? name : (hovered == name ? nil : hovered)
                if inside { NSCursor.openHand.push() } else { NSCursor.pop() }
            }
            .help("拖动调整顺序；色块修改颜色")
    }

    private func rowContent(_ name: String, dp: DayPoint) -> some View {
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
    }

    // the card that follows the cursor while dragging (dnd-kit style):
    // material background + drop shadow. Pure SwiftUI dot — the ColorDot
    // NSViewRepresentable misplaces its dot inside drag-preview snapshots.
    private func dragPreview(_ name: String, dp: DayPoint) -> some View {
        HStack(spacing: 6) {
            Circle()
                .fill(color(name))
                .frame(width: 7, height: 7)
                .frame(width: 16, height: 16)
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
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 7))
        .shadow(color: .black.opacity(0.25), radius: 6, y: 3)
    }

    var chart: some View {
        Chart {
            // per-day dashed gridlines under the data, same dash rhythm as
            // the 总量 line. Keyed by offset — a second ForEach keyed by
            // \.element.id collides with the hover-rule ForEach and gets culled.
            ForEach(Array(store.points.enumerated()), id: \.offset) { idx, _ in
                RuleMark(x: .value("网格", idx))
                    .foregroundStyle(.secondary.opacity(0.35))
                    .lineStyle(StrokeStyle(lineWidth: 1, dash: [4, 3]))
            }
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
                    // hover indicator: solid and brighter so it reads above
                    // the dashed per-day gridlines
                    RuleMark(x: .value("日期", idx))
                        .foregroundStyle(.primary.opacity(0.35))
                        .lineStyle(StrokeStyle(lineWidth: 1.5))
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
                onQuit: { NSApp.terminate(nil) },
                onInstall: { self.installEngine($0) }
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
        // `usage` glue CLI is optional now; fall back to plain ccusage
        p.arguments = ["-e", "tell application \"Terminal\" to do script \"command -v usage >/dev/null && usage 7 || ccusage daily\""]
        try? p.run()
    }

    // detect which package manager can install the data engine
    func detectInstallers() {
        DispatchQueue.global(qos: .utility).async { [self] in
            let brew = shell("command -v brew").code == 0
            let npm = shell("command -v npm").code == 0
            DispatchQueue.main.async { [self] in
                store.hasBrew = brew
                store.hasNpm = npm
            }
        }
    }

    func installEngine(_ tool: String) {
        guard !store.engineInstalling else { return }
        store.engineInstalling = true
        store.engineError = nil
        DispatchQueue.global(qos: .userInitiated).async { [self] in
            let r = shell(tool == "brew" ? "brew install ccusage" : "npm install -g ccusage")
            DispatchQueue.main.async { [self] in
                store.engineInstalling = false
                if r.code == 0 {
                    store.engineMissing = false
                    refresh()
                } else {
                    store.engineError = r.err.split(separator: "\n").suffix(3).joined(separator: "\n")
                }
            }
        }
    }

    func refresh() {
        guard !busy else { return }
        busy = true
        let needAgents = store.supportedAgents.isEmpty
        DispatchQueue.global(qos: .utility).async { [self] in
            // debug/verification hook: pretend the engine is missing
            if ProcessInfo.processInfo.environment["USAGEBAR_FAKE_NO_ENGINE"] == "1" {
                DispatchQueue.main.async { [self] in
                    busy = false
                    item.button?.title = "n/a"
                    store.engineMissing = true
                    detectInstallers()
                }
                return
            }
            let payload = runJSON()
            let agents = needAgents ? parseCCusageAgents() : []
            DispatchQueue.main.async { [self] in
                busy = false
                guard let payload else {
                    item.button?.title = "n/a"
                    store.engineMissing = !ccusageInstalled()
                    if store.engineMissing { detectInstallers() }
                    return
                }
                store.engineMissing = false
                store.engineError = nil
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
