import SwiftUI
import UIKit

@MainActor
struct ContentView: View {
    @EnvironmentObject private var model: ReminderModel
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @State private var showingSettings = false
    @State private var showingBinding = false

    private var palette: MailPalette { model.theme.palette }
    private var reduceMotion: Bool { model.reduceMotion || systemReduceMotion }

    var body: some View {
        ZStack {
            MailBackdrop(theme: model.theme)
            ScrollView {
                VStack(spacing: model.theme == .cosmos ? 24 : 18) {
                    header
                    connectionStatus
                    if let notice = model.noticeMessage {
                        HStack(alignment: .top, spacing: 12) {
                            Text(notice).font(.subheadline).frame(maxWidth: .infinity, alignment: .leading)
                            Button { model.noticeMessage = nil } label: { Image(systemName: "xmark") }
                                .accessibilityLabel("关闭状态信息")
                        }
                        .foregroundStyle(palette.accent)
                    }
                    if model.isBound {
                        if model.theme == .tech { techSendButton }
                        else { cosmosSendButton }
                        receiptPanel
                        testButton
                        incomingHistory
                    } else {
                        welcomePanel
                    }
                    foregroundExplanation
                }
                .padding(.horizontal, 22)
                .padding(.top, 20)
                .padding(.bottom, 28)
                .frame(maxWidth: 620)
                .frame(maxWidth: .infinity)
            }
        }
        .foregroundStyle(palette.text)
        .tint(palette.signal)
        .overlay(alignment: .top) {
            if let reminder = model.banner {
                ReminderBanner(reminder: reminder)
                    .padding(.horizontal, 16)
                    .padding(.top, 8)
                    .transition(reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity))
                    .zIndex(10)
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: 0.22), value: model.banner?.id)
        .onChange(of: model.banner?.id) { id in
            if id != nil { UINotificationFeedbackGenerator().notificationOccurred(.warning) }
        }
        .sheet(isPresented: $showingSettings) { SettingsView() }
        .sheet(isPresented: $showingBinding) { BindingView() }
        .alert("操作未完成", isPresented: Binding(
            get: { model.errorMessage != nil && !showingSettings && !showingBinding },
            set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("知道了", role: .cancel) { model.errorMessage = nil }
        } message: { Text(model.errorMessage ?? "请稍后重试。") }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: model.theme == .cosmos ? "sparkle" : "envelope.badge")
                .font(.title2)
                .foregroundStyle(palette.signal)
            Text("邮件提醒助手").font(.system(.title3, weight: .semibold))
            Spacer(minLength: 8)
            Button { showingSettings = true } label: {
                Image(systemName: "slider.horizontal.3")
                    .font(.title3)
                    .frame(width: 44, height: 44)
                    .background(palette.soft, in: RoundedRectangle(cornerRadius: 12))
            }
            .accessibilityLabel("设置")
            .accessibilityIdentifier("settingsButton")
        }
    }

    private var connectionStatus: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 5) {
                    Text(model.isBound ? model.state.name : "连接你的工作网段")
                        .font(.headline)
                        .textSelection(.enabled)
                    if model.isBound {
                        Text(model.state.subnet)
                            .font(.system(.caption, design: .monospaced))
                            .foregroundStyle(palette.muted)
                    }
                }
                Spacer()
                Image(systemName: model.isConnected ? "network" : "wifi.slash")
                    .foregroundStyle(model.isConnected ? palette.success : palette.muted)
                    .font(.title3)
                    .accessibilityHidden(true)
            }
            Label(model.connectionText, systemImage: model.isConnected ? "checkmark.circle.fill" : "clock")
                .font(.caption)
                .foregroundStyle(model.isConnected ? palette.success : palette.muted)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .mailPanel(model.theme)
    }

    private var techSendButton: some View {
        Button { Task { await model.send(isTest: false) } } label: {
            VStack(alignment: .leading, spacing: 22) {
                HStack {
                    Text("加急邮件").font(.system(.caption, design: .monospaced, weight: .medium))
                        .foregroundStyle(palette.signal)
                    Spacer()
                    Image(systemName: "arrow.up.right").foregroundStyle(palette.muted)
                }
                Image(systemName: "envelope.badge.fill")
                    .font(.system(size: 52, weight: .light))
                    .foregroundStyle(LinearGradient(colors: palette.action, startPoint: .topLeading, endPoint: .bottomTrailing))
                Text(MailConstants.message)
                    .font(.system(.title2, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                    .multilineTextAlignment(.leading)
                HStack {
                    Text("发送给同组在线设备").font(.subheadline).foregroundStyle(palette.muted)
                    Spacer(minLength: 8)
                    if model.isBusy { ProgressView().tint(palette.signal) }
                    else { Image(systemName: "paperplane.fill").foregroundStyle(palette.signal) }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(24)
            .background {
                RoundedRectangle(cornerRadius: 16)
                    .fill(LinearGradient(colors: [Color(hex: 0x24201D), palette.surface], startPoint: .topLeading, endPoint: .bottomTrailing))
            }
            .overlay { RoundedRectangle(cornerRadius: 16).strokeBorder(palette.signal.opacity(0.5), lineWidth: 1) }
        }
        .buttonStyle(MailPressStyle(reduceMotion: reduceMotion))
        .disabled(model.isBusy || !model.isActive)
        .accessibilityIdentifier("sendReminder")
    }

    private var cosmosSendButton: some View {
        VStack(spacing: 6) {
            Button { Task { await model.send(isTest: false) } } label: {
                ZStack {
                    EclipseDecoration(palette: palette, motionEnabled: model.isActive && !reduceMotion)
                    Circle()
                        .fill(RadialGradient(colors: [Color(hex: 0x14112B), palette.background],
                            center: .topLeading, startRadius: 0, endRadius: 220))
                        .padding(30)
                        .shadow(color: palette.signal.opacity(0.4), radius: 14)
                    VStack(spacing: 14) {
                        Image(systemName: "envelope.badge")
                            .font(.system(size: 30, weight: .light))
                            .foregroundStyle(palette.signal)
                        Text(MailConstants.message)
                            .font(.system(.title3, design: .rounded, weight: .medium))
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                        if model.isBusy { ProgressView().tint(palette.signal) }
                        else { Text("发送提醒").font(.caption).foregroundStyle(palette.muted) }
                    }
                    .padding(58)
                }
                .frame(maxWidth: 316)
                .aspectRatio(1, contentMode: .fit)
            }
            .buttonStyle(MailPressStyle(reduceMotion: reduceMotion))
            .disabled(model.isBusy || !model.isActive)
            .accessibilityIdentifier("sendReminder")
            Text("发送给同组在线设备").font(.subheadline).foregroundStyle(palette.muted)
        }
        .frame(maxWidth: .infinity)
    }

    private var receiptPanel: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .firstTextBaseline) {
                Text("本次回执").font(.headline)
                Spacer()
                if let sent = model.state.lastSent {
                    Text(sent.sentAt, style: .time).font(.caption).foregroundStyle(palette.muted)
                }
            }
            if let sent = model.state.lastSent {
                Text(sent.isTest ? "联机测试提醒" : "加急邮件提醒")
                    .font(.subheadline).foregroundStyle(palette.muted)
                HStack(spacing: 24) {
                    receiptCount(model.latestReceipt?.receivedBy.count ?? 0, title: "软件收到", color: palette.accent)
                    Rectangle().fill(palette.line).frame(width: 1)
                    receiptCount(model.latestReceipt?.seenBy.count ?? 0, title: "已知晓", color: palette.signal)
                }
                .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("发送后显示软件收到与人工知晓的数量。")
                    .font(.subheadline).foregroundStyle(palette.muted)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .mailPanel(model.theme)
    }

    private func receiptCount(_ count: Int, title: String, color: Color) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("\(count)").font(.system(size: 34, weight: .light, design: model.theme == .cosmos ? .serif : .monospaced))
                .foregroundStyle(color)
            Text(title).font(.caption).foregroundStyle(palette.muted)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    private var testButton: some View {
        Button { Task { await model.send(isTest: true) } } label: {
            Label("联机测试", systemImage: "waveform.path.ecg")
                .font(.subheadline)
                .frame(maxWidth: .infinity, minHeight: 48)
                .background(palette.soft, in: RoundedRectangle(cornerRadius: model.theme == .cosmos ? 18 : 10))
        }
        .buttonStyle(MailPressStyle(reduceMotion: reduceMotion))
        .disabled(model.isBusy || !model.isActive)
        .accessibilityIdentifier("sendTest")
    }

    private var incomingHistory: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("收到的提醒").font(.headline)
                Spacer()
                if !model.state.pendingReceipts.isEmpty {
                    Text("\(model.state.pendingReceipts.count) 条回执待同步")
                        .font(.caption).foregroundStyle(palette.signal)
                }
            }
            if model.state.incoming.isEmpty {
                Text("保持应用在前台，同组提醒会显示在这里。")
                    .font(.subheadline).foregroundStyle(palette.muted)
            } else {
                ForEach(model.state.incoming) { reminder in
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(alignment: .top) {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(reminder.senderName).font(.subheadline.weight(.semibold))
                                Text(reminder.title).font(.caption).foregroundStyle(palette.muted)
                            }
                            Spacer(minLength: 10)
                            Text(reminder.receivedAt, style: .time).font(.caption2).foregroundStyle(palette.muted)
                        }
                        if reminder.seen {
                            Label("已知晓", systemImage: "checkmark.circle.fill")
                                .font(.caption).foregroundStyle(palette.success)
                        } else {
                            Button("知道了") { model.markSeen(reminder) }
                                .font(.subheadline.weight(.medium))
                                .frame(minHeight: 44)
                                .accessibilityIdentifier("seen-\(reminder.id)")
                        }
                    }
                    if reminder.id != model.state.incoming.last?.id {
                        Rectangle().fill(palette.line).frame(height: 1)
                    }
                }
            }
            if !model.receiptStatus.isEmpty {
                Text(model.receiptStatus).font(.caption2).foregroundStyle(palette.muted)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .mailPanel(model.theme)
    }

    private var welcomePanel: some View {
        VStack(alignment: .leading, spacing: 20) {
            Image(systemName: "iphone.and.arrow.forward").font(.system(size: 42, weight: .light)).foregroundStyle(palette.signal)
            Text("与桌面端同步提醒").font(.title2.weight(.semibold))
            Text("在电脑端打开手机绑定，输入八位绑定码，加入自己的工作网段。")
                .font(.subheadline).foregroundStyle(palette.muted)
            Button { showingBinding = true } label: {
                Text("绑定桌面端").font(.headline).frame(maxWidth: .infinity, minHeight: 50)
                    .foregroundStyle(palette.background)
                    .background(LinearGradient(colors: palette.action, startPoint: .leading, endPoint: .trailing),
                        in: RoundedRectangle(cornerRadius: 12))
            }
            .accessibilityIdentifier("openBinding")
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .mailPanel(model.theme)
    }

    private var foregroundExplanation: some View {
        Label("当前版本在前台接收。切到后台或锁屏后会暂停，回到应用后自动重连。", systemImage: "info.circle")
            .font(.caption)
            .foregroundStyle(palette.muted)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

@MainActor
private struct ReminderBanner: View {
    @EnvironmentObject private var model: ReminderModel
    let reminder: IncomingReminder

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top) {
                Label(reminder.isTest ? "联机测试" : "加急邮件", systemImage: "envelope.badge.fill")
                    .font(.subheadline.weight(.semibold)).foregroundStyle(model.theme.palette.signal)
                Spacer()
                Button { model.dismissBanner() } label: {
                    Image(systemName: "xmark").frame(width: 32, height: 32)
                }
                .accessibilityLabel("关闭提醒")
            }
            Text(reminder.title).font(.title3.weight(.semibold))
            Text("来自 \(reminder.senderName)").font(.subheadline).foregroundStyle(model.theme.palette.muted)
            Button { model.markSeen(reminder) } label: {
                Text("知道了").font(.headline)
                    .frame(maxWidth: .infinity, minHeight: 46)
                    .foregroundStyle(model.theme.palette.background)
                    .background(model.theme.palette.signal, in: RoundedRectangle(cornerRadius: 11))
            }
            .accessibilityIdentifier("bannerSeen")
        }
        .mailPanel(model.theme)
        .shadow(color: .black.opacity(0.65), radius: 22, y: 10)
        .accessibilityElement(children: .contain)
    }
}

@MainActor
private struct BindingView: View {
    @EnvironmentObject private var model: ReminderModel
    @Environment(\.dismiss) private var dismiss
    @State private var code = ""
    @State private var name = ""

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("八位数字绑定码", text: $code)
                        .keyboardType(.numberPad)
                        .textContentType(.oneTimeCode)
                        .font(.system(.title2, design: .monospaced))
                        .accessibilityIdentifier("pairCode")
                        .onChange(of: code) { value in
                            code = String(value.filter { $0.isASCII && $0.isNumber }.prefix(8))
                        }
                    TextField("手机显示名称", text: $name)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .accessibilityIdentifier("pairName")
                } header: { Text("桌面端绑定") }
                footer: { Text("在电脑端的手机绑定窗口生成绑定码。绑定码五分钟内有效，仅可使用一次。名称支持中文。") }

                if model.isBound {
                    Section {
                        Text("当前网段 \(model.state.subnet)")
                        Text("换绑成功后，原网段绑定会同时解除。")
                            .foregroundStyle(.secondary)
                    }
                }
                Section {
                    Button {
                        Task {
                            if await model.pair(code: code, name: name) { dismiss() }
                        }
                    } label: {
                        HStack {
                            Text(model.isBound ? "更换绑定" : "确认绑定")
                            Spacer()
                            if model.isBusy { ProgressView() }
                        }
                    }
                    .disabled(model.isBusy || code.count != 8 || !MailConstants.validName(name))
                    .accessibilityIdentifier("confirmBinding")
                }
                if let error = model.errorMessage {
                    Section { Text(error).foregroundStyle(model.theme.palette.danger) }
                }
            }
            .scrollContentBackground(.hidden)
            .background(model.theme.palette.background)
            .navigationTitle(model.isBound ? "更换网段绑定" : "绑定桌面端")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("关闭") { dismiss() }.disabled(model.isBusy) } }
            .tint(model.theme.palette.signal)
            .onAppear { name = model.state.name; model.errorMessage = nil }
            .onDisappear { model.errorMessage = nil }
            .interactiveDismissDisabled(model.isBusy)
        }
        .preferredColorScheme(.dark)
    }
}

@MainActor
private struct SettingsView: View {
    @EnvironmentObject private var model: ReminderModel
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var showingBinding = false
    @State private var confirmingUnbind = false

    var body: some View {
        NavigationStack {
            Form {
                Section("外观") {
                    Picker("主题", selection: $model.theme) {
                        ForEach(MailTheme.allCases) { theme in Text(theme.title).tag(theme) }
                    }
                    .pickerStyle(.segmented)
                    .accessibilityIdentifier("themePicker")
                    Toggle("减少动态效果", isOn: $model.reduceMotion)
                        .accessibilityIdentifier("reduceMotion")
                }
                Section {
                    TextField("手机显示名称", text: $name)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    Button("保存并同步名称") {
                        Task { _ = await model.rename(name) }
                    }
                    .disabled(!model.isBound || model.isBusy || !MailConstants.validName(name) || name == model.state.name)
                    .accessibilityIdentifier("saveName")
                } header: { Text("我的名称") }
                footer: { Text("支持中文。保存后，同组桌面端将收到新的显示名称。") }

                Section("网段绑定") {
                    if model.isBound {
                        LabeledContent("网段", value: model.state.subnet)
                        LabeledContent("接收状态", value: model.connectionText)
                    }
                    Button(model.isBound ? "更换绑定" : "绑定桌面端") { showingBinding = true }
                        .disabled(model.isBusy)
                    if model.isBound {
                        Button("解除绑定", role: .destructive) { confirmingUnbind = true }
                            .disabled(model.isBusy)
                    }
                }
                Section("接收与回执") {
                    Text("保持应用在前台时接收同组提醒。切换后台或锁屏后，iOS 会暂停接收。")
                    Text("提醒展示五秒后自动关闭，也可在记录中点击知道了。自动关闭只结束展示，人工点击后才发送知晓回执。")
                    Text("断网回执会保留在本机，回到前台联网后重试。服务端回执记录保存约三十分钟。")
                }
                .font(.footnote)
                .foregroundStyle(.secondary)
                Section { LabeledContent("版本", value: "1.3.0") }
                if let notice = model.noticeMessage {
                    Section { Text(notice).foregroundStyle(model.theme.palette.success) }
                }
                if let error = model.errorMessage {
                    Section { Text(error).foregroundStyle(model.theme.palette.danger) }
                }
            }
            .scrollContentBackground(.hidden)
            .background(model.theme.palette.background)
            .navigationTitle("设置")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
            .tint(model.theme.palette.signal)
            .onAppear { name = model.state.name }
            .onDisappear { model.errorMessage = nil }
            .onChange(of: model.state.name) { name = $0 }
            .sheet(isPresented: $showingBinding) { BindingView() }
            .confirmationDialog("解除当前网段绑定？", isPresented: $confirmingUnbind, titleVisibility: .visible) {
                Button("解除绑定", role: .destructive) { Task { _ = await model.unbind() } }
                Button("取消", role: .cancel) {}
            }
        }
        .preferredColorScheme(.dark)
    }
}
