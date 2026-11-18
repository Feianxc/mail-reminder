import SwiftUI

@main
@MainActor
struct MailReminderApp: App {
    @StateObject private var model = ReminderModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
                .preferredColorScheme(.dark)
                .onAppear { model.setActive(scenePhase == .active) }
                .onChange(of: scenePhase) { phase in
                    model.setActive(phase == .active)
                }
        }
    }
}
