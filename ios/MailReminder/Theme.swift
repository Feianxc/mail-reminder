import SwiftUI

enum MailTheme: String, CaseIterable, Codable, Identifiable {
    case tech
    case cosmos

    var id: String { rawValue }
    var title: String { self == .tech ? "高科技" : "宇宙星空" }
    var palette: MailPalette { MailPalette(theme: self) }
}

struct MailPalette {
    let background: Color
    let surface: Color
    let soft: Color
    let line: Color
    let text: Color
    let muted: Color
    let accent: Color
    let signal: Color
    let success: Color
    let danger: Color
    let action: [Color]

    init(theme: MailTheme) {
        let cosmos = theme == .cosmos
        background = Color(hex: cosmos ? 0x060714 : 0x0A0C10)
        surface = Color(hex: cosmos ? 0x0C0D24 : 0x101319)
        soft = Color(hex: cosmos ? 0x23213D : 0x171B23)
        line = Color(hex: cosmos ? 0x48465E : 0x262C37)
        text = Color(hex: cosmos ? 0xF4F2FF : 0xEEF1F6)
        muted = Color(hex: cosmos ? 0xA6A7CF : 0x8B95A7)
        accent = Color(hex: cosmos ? 0xCDC4FF : 0x7FD8FF)
        signal = Color(hex: cosmos ? 0xFFD29A : 0xFF8A3D)
        success = Color(hex: cosmos ? 0x8DE8D2 : 0x57E3B0)
        danger = Color(hex: cosmos ? 0xFF8FA6 : 0xFF6B6B)
        action = cosmos ? [Color(hex: 0xFFE9C2), Color(hex: 0xFFB07A)]
            : [Color(hex: 0xFFB45A), Color(hex: 0xFF6A2C)]
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(.sRGB, red: Double((hex >> 16) & 255) / 255,
                  green: Double((hex >> 8) & 255) / 255,
                  blue: Double(hex & 255) / 255, opacity: 1)
    }
}

struct MailBackdrop: View {
    let theme: MailTheme

    var body: some View {
        ZStack {
            theme.palette.background
            if theme == .cosmos {
                RadialGradient(colors: [Color(hex: 0x39305E).opacity(0.62), .clear],
                    center: .topTrailing, startRadius: 20, endRadius: 500)
                RadialGradient(colors: [Color(hex: 0x292C55).opacity(0.42), .clear],
                    center: .bottomLeading, startRadius: 0, endRadius: 400)
                Canvas { context, size in
                    for index in 0..<100 {
                        let x = CGFloat((index * 47 + 19) % 997) / 997 * size.width
                        let y = CGFloat((index * 131 + 79) % 991) / 991 * size.height
                        let diameter: CGFloat = index % 11 == 0 ? 2.2 : 1
                        let rect = CGRect(x: x, y: y, width: diameter, height: diameter)
                        context.fill(Path(ellipseIn: rect), with: .color(.white.opacity(index % 3 == 0 ? 0.5 : 0.2)))
                    }
                }
            } else {
                Canvas { context, size in
                    var path = Path()
                    for x in stride(from: CGFloat(0), through: size.width, by: 32) {
                        path.move(to: CGPoint(x: x, y: 0))
                        path.addLine(to: CGPoint(x: x, y: size.height))
                    }
                    for y in stride(from: CGFloat(0), through: size.height, by: 32) {
                        path.move(to: CGPoint(x: 0, y: y))
                        path.addLine(to: CGPoint(x: size.width, y: y))
                    }
                    context.stroke(path, with: .color(theme.palette.line.opacity(0.28)), lineWidth: 0.5)
                }
            }
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

struct MailPanel: ViewModifier {
    let theme: MailTheme

    func body(content: Content) -> some View {
        content
            .padding(20)
            .background(theme.palette.surface.opacity(theme == .cosmos ? 0.88 : 0.96))
            .clipShape(RoundedRectangle(cornerRadius: theme == .cosmos ? 24 : 14, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: theme == .cosmos ? 24 : 14, style: .continuous)
                    .strokeBorder(theme.palette.line.opacity(0.85), lineWidth: 1)
            }
    }
}

extension View {
    func mailPanel(_ theme: MailTheme) -> some View { modifier(MailPanel(theme: theme)) }
}

struct MailPressStyle: ButtonStyle {
    let reduceMotion: Bool
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.82 : 1)
            .scaleEffect(configuration.isPressed && !reduceMotion && !systemReduceMotion ? 0.975 : 1)
            .animation(reduceMotion || systemReduceMotion ? nil : .easeOut(duration: 0.16), value: configuration.isPressed)
    }
}

struct EclipseDecoration: View {
    let palette: MailPalette
    let motionEnabled: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !motionEnabled)) { timeline in
            let angle = motionEnabled ? timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 80) / 80 * 360 : 0
            ZStack {
                Circle()
                    .stroke(palette.signal.opacity(0.6), lineWidth: 3)
                    .blur(radius: 15)
                    .padding(27)
                Circle()
                    .stroke(AngularGradient(colors: [palette.accent.opacity(0.1), palette.signal,
                        palette.accent.opacity(0.15), palette.signal.opacity(0.6)], center: .center), lineWidth: 2)
                    .padding(28)
                Circle()
                    .stroke(palette.accent.opacity(0.16), lineWidth: 1)
                    .padding(9)
                Circle()
                    .trim(from: 0.04, to: 0.94)
                    .stroke(palette.accent.opacity(0.4), style: StrokeStyle(lineWidth: 0.8, dash: [18, 9, 3, 9]))
                    .rotationEffect(.degrees(angle))
                Circle()
                    .fill(palette.signal)
                    .frame(width: 6, height: 6)
                    .shadow(color: palette.signal, radius: 7)
                    .offset(y: -146)
                    .rotationEffect(.degrees(angle + 22))
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
