import Foundation
import Combine

@MainActor
final class ReminderModel: ObservableObject {
    @Published private(set) var state = ClientState()
    @Published private(set) var isBound = false
    @Published private(set) var isActive = false
    @Published private(set) var isBusy = false
    @Published private(set) var connectionText = "尚未绑定"
    @Published private(set) var isConnected = false
    @Published private(set) var receiptStatus = ""
    @Published private(set) var banner: IncomingReminder?
    @Published var errorMessage: String?
    @Published var noticeMessage: String?
    @Published var theme: MailTheme {
        didSet { defaults.set(theme.rawValue, forKey: "mail.theme") }
    }
    @Published var reduceMotion: Bool {
        didSet { defaults.set(reduceMotion, forKey: "mail.reduceMotion") }
    }

    private let defaults: UserDefaults
    private let api = APIClient()
    private var credential: BindingCredential?
    private var bindingGeneration = UUID()
    private var connectionRun = UUID()
    private var receiptRun = UUID()
    private var pollTask: Task<Void, Never>?
    private var receiptTask: Task<Void, Never>?
    private var bannerTask: Task<Void, Never>?
    private var changingBinding = false
    private var sendAllowedAt = Date.distantPast
    private static let stateKey = "mail.clientState.v1"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        theme = MailTheme(rawValue: defaults.string(forKey: "mail.theme") ?? "tech") ?? .tech
        reduceMotion = defaults.bool(forKey: "mail.reduceMotion")
        if let data = defaults.data(forKey: Self.stateKey),
           let saved = try? JSONDecoder().decode(ClientState.self, from: data) {
            state = saved
            state.processed = Array(saved.processed.suffix(256))
            state.incoming = Array(saved.incoming.prefix(MailConstants.historyLimit))
            state.pendingReceipts = Array(saved.pendingReceipts.prefix(MailConstants.receiptLimit))
            state.receiptCache = Array(saved.receiptCache.suffix(32))
        }
        do {
            if let savedCredential = try SecureStore.load() {
                if savedCredential.nodeID == state.nodeID && savedCredential.roomID == state.roomID {
                    credential = savedCredential
                    isBound = true
                    connectionText = "等待进入前台"
                } else {
                    // A reinstallation or interrupted binding never reuses another room's state.
                    try SecureStore.clear()
                    resetState()
                }
            } else {
                resetState()
            }
        } catch {
            errorMessage = error.localizedDescription
            connectionText = "安全存储暂不可用"
        }
    }

    var latestReceipt: ReceiptSummary? {
        guard let sent = state.lastSent else { return nil }
        return state.receiptCache.first { $0.id == sent.id }
    }

    func setActive(_ active: Bool) {
        guard active != isActive else { return }
        isActive = active
        if active {
            startConnection()
        } else {
            stopConnection()
            dismissBanner()
            connectionText = isBound ? "已暂停，回到前台后接收" : "尚未绑定"
            persist()
        }
    }

    func pair(code: String, name: String) async -> Bool {
        guard !isBusy else { return false }
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard code.count == 8, code.utf8.allSatisfy({ (48...57).contains($0) }) else {
            errorMessage = "请输入桌面端生成的八位数字绑定码。"
            return false
        }
        guard MailConstants.validName(trimmedName) else {
            errorMessage = "请输入有效名称，最多 64 个字符，支持中文。"
            return false
        }
        isBusy = true
        changingBinding = true
        bindingGeneration = UUID()
        stopConnection()
        dismissBanner()
        defer {
            isBusy = false
            changingBinding = false
            startConnection()
        }
        do {
            let response = try await api.pair(code: code, name: trimmedName, previousToken: credential?.token)
            let newCredential = BindingCredential(token: response.token, nodeID: response.nodeId, roomID: response.roomId)
            do {
                try SecureStore.save(newCredential)
            } catch {
                // Pairing revokes the previous token on the server. Never restore it locally.
                try? await api.unbind(token: newCredential.token)
                try? SecureStore.clear()
                credential = nil
                isBound = false
                resetState()
                throw error
            }
            credential = newCredential
            state = ClientState()
            state.nodeID = response.nodeId
            state.roomID = response.roomId
            state.subnet = response.subnet
            state.name = response.name
            state.cursor = response.cursor
            sendAllowedAt = .distantPast
            receiptStatus = ""
            isBound = true
            persist()
            noticeMessage = "已绑定到 \(response.subnet)"
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    func rename(_ name: String) async -> Bool {
        guard !isBusy, let credential = credential else { return false }
        let newName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard MailConstants.validName(newName) else {
            errorMessage = "请输入有效名称，最多 64 个字符，支持中文。"
            return false
        }
        isBusy = true
        let generation = bindingGeneration
        defer { isBusy = false }
        do {
            let savedName = try await api.rename(token: credential.token, name: newName)
            guard bindingGeneration == generation else { return false }
            state.name = savedName
            persist()
            noticeMessage = "名称已同步到同组桌面端。"
            return true
        } catch {
            handleActionError(error, generation: generation)
            return false
        }
    }

    func unbind() async -> Bool {
        guard !isBusy, let credential = credential else { return false }
        isBusy = true
        changingBinding = true
        bindingGeneration = UUID()
        stopConnection()
        defer {
            isBusy = false
            changingBinding = false
            startConnection()
        }
        do {
            do { try await api.unbind(token: credential.token) }
            catch let error as APIError where error.unauthorized { /* Already revoked. */ }
            try SecureStore.clear()
            self.credential = nil
            isBound = false
            dismissBanner()
            resetState()
            noticeMessage = "已解除绑定。"
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    func send(isTest: Bool) async {
        guard !isBusy, isActive, let credential = credential else { return }
        guard Date() >= sendAllowedAt else {
            errorMessage = "请间隔五秒后再次发送。"
            return
        }
        isBusy = true
        sendAllowedAt = Date().addingTimeInterval(5)
        let generation = bindingGeneration
        defer { isBusy = false }
        do {
            let result = try await api.send(token: credential.token, isTest: isTest)
            guard bindingGeneration == generation else { return }
            // Polling may deliver receipts before this response. Keep the cache by alert ID.
            state.lastSent = SentReminder(id: result.alertId, sentAt: Date(), isTest: isTest)
            persist()
            noticeMessage = isTest ? "联机测试已发送，等待软件回执。" : "加急邮件提醒已发送。"
        } catch {
            // A timed-out POST may already have sent the reminder. Never automatically repeat it.
            handleActionError(error, generation: generation)
        }
    }

    func markSeen(_ reminder: IncomingReminder) {
        guard isBound, state.incoming.contains(where: { $0.id == reminder.id }) else { return }
        guard state.enqueueReceipt(id: reminder.id, seen: true) else {
            errorMessage = "待发回执较多，请连接服务后再点知道了。"
            return
        }
        if let index = state.incoming.firstIndex(where: { $0.id == reminder.id }) {
            state.incoming[index].seen = true
        }
        if banner?.id == reminder.id { dismissBanner() }
        persist()
        startReceiptWorker()
    }

    func dismissBanner() {
        bannerTask?.cancel()
        bannerTask = nil
        banner = nil
    }

    private func startConnection() {
        guard isActive, isBound, !changingBinding, let credential = credential else {
            if !isBound { connectionText = "尚未绑定" }
            return
        }
        startReceiptWorker()
        guard pollTask == nil else { return }
        let generation = bindingGeneration
        let run = UUID()
        connectionRun = run
        connectionText = "正在连接"
        pollTask = Task { [weak self] in
            guard let self = self else { return }
            await self.pollLoop(credential: credential, generation: generation, run: run)
            if self.connectionRun == run { self.pollTask = nil }
        }
    }

    private func stopConnection() {
        connectionRun = UUID()
        receiptRun = UUID()
        pollTask?.cancel()
        receiptTask?.cancel()
        pollTask = nil
        receiptTask = nil
        isConnected = false
    }

    private func pollLoop(credential: BindingCredential, generation: UUID, run: UUID) async {
        var retrySeconds: UInt64 = 1
        while canReceive(generation: generation, run: run) {
            do {
                let response = try await api.poll(token: credential.token, cursor: state.cursor)
                guard canReceive(generation: generation, run: run), !Task.isCancelled else { return }
                let consumed = consume(response)
                isConnected = true
                connectionText = "前台接收中"
                retrySeconds = 1
                startReceiptWorker()
                if !consumed || response.packets.isEmpty {
                    try await Task.sleep(nanoseconds: 200_000_000)
                }
            } catch {
                guard canReceive(generation: generation, run: run), !Task.isCancelled else { return }
                if let failure = error as? APIError, failure.unauthorized {
                    invalidateBinding()
                    return
                }
                isConnected = false
                connectionText = "连接中断，\(retrySeconds) 秒后重试"
                do { try await Task.sleep(nanoseconds: retrySeconds * 1_000_000_000) }
                catch { return }
                retrySeconds = min(30, retrySeconds * 2)
            }
        }
    }

    private func canReceive(generation: UUID, run: UUID) -> Bool {
        isActive && isBound && !changingBinding && bindingGeneration == generation && connectionRun == run
    }

    private func consume(_ response: PollResponse) -> Bool {
        for raw in response.packets {
            guard let packet = WirePayload.decode(raw), packet.SenderId != state.nodeID,
                  let id = packet.Id else { continue }
            if packet.kind == "alert" {
                let previous = state.incoming.first { $0.id == id }
                guard state.enqueueReceipt(id: id, seen: previous?.seen ?? false) else {
                    // Keep the previous cursor. Persisted IDs suppress repeats while the queue drains.
                    persist()
                    return false
                }
                if !state.processed.contains(id) {
                    state.processed.append(id)
                    state.processed = Array(state.processed.suffix(256))
                    let reminder = IncomingReminder(id: id, senderName: packet.Name,
                        receivedAt: Date(), isTest: packet.IsTest, seen: false)
                    state.incoming.insert(reminder, at: 0)
                    state.incoming = Array(state.incoming.prefix(MailConstants.historyLimit))
                    showBanner(reminder)
                }
            } else if packet.TargetId == state.nodeID {
                state.recordReceipt(id: id, sender: packet.SenderId, seen: packet.kind == "seen")
            }
        }
        // Cursor and receipt queue are persisted together after handling the complete batch.
        state.cursor = response.cursor
        persist()
        return true
    }

    private func startReceiptWorker() {
        guard isActive, isBound, !changingBinding, receiptTask == nil,
              !state.pendingReceipts.isEmpty, let credential = credential else { return }
        let generation = bindingGeneration
        let run = UUID()
        receiptRun = run
        receiptTask = Task { [weak self] in
            guard let self = self else { return }
            await self.flushReceipts(credential: credential, generation: generation, run: run)
            if self.receiptRun == run { self.receiptTask = nil }
        }
    }

    private func flushReceipts(credential: BindingCredential, generation: UUID, run: UUID) async {
        var retrySeconds: UInt64 = 1
        while isActive && isBound && !changingBinding && bindingGeneration == generation && receiptRun == run {
            guard !Task.isCancelled, let receipt = state.pendingReceipts.first else { return }
            if receipt.createdAt < Date().addingTimeInterval(-30 * 60) {
                state.pendingReceipts.removeFirst()
                receiptStatus = "部分回执已超过服务保存期限。"
                persist()
                continue
            }
            do {
                try await api.receipt(token: credential.token, receipt: receipt)
                guard bindingGeneration == generation, receiptRun == run, !Task.isCancelled else { return }
                state.removeDelivered(receipt)
                receiptStatus = state.pendingReceipts.isEmpty ? "回执已同步" : "正在同步回执"
                persist()
                retrySeconds = 1
            } catch {
                guard bindingGeneration == generation, receiptRun == run, !Task.isCancelled else { return }
                if let failure = error as? APIError {
                    if failure.unauthorized { invalidateBinding(); return }
                    if case .response(let status, _) = failure, status == 404 || status == 410 {
                        state.removeDelivered(receipt)
                        receiptStatus = "该提醒的回执已超过服务保存期限。"
                        persist()
                        continue
                    }
                }
                receiptStatus = "回执待同步，联网后自动重试"
                do { try await Task.sleep(nanoseconds: retrySeconds * 1_000_000_000) }
                catch { return }
                retrySeconds = min(30, retrySeconds * 2)
            }
        }
    }

    private func showBanner(_ reminder: IncomingReminder) {
        bannerTask?.cancel()
        banner = reminder
        bannerTask = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: MailConstants.reminderDuration) }
            catch { return }
            guard let self = self, self.banner?.id == reminder.id else { return }
            // Closing after five seconds records no human acknowledgment.
            self.banner = nil
            self.bannerTask = nil
        }
    }

    private func handleActionError(_ error: Error, generation: UUID) {
        guard bindingGeneration == generation else { return }
        if let failure = error as? APIError, failure.unauthorized { invalidateBinding() }
        else { errorMessage = error.localizedDescription }
    }

    private func invalidateBinding() {
        stopConnection()
        bindingGeneration = UUID()
        try? SecureStore.clear()
        credential = nil
        isBound = false
        dismissBanner()
        resetState()
        connectionText = "绑定已失效"
        errorMessage = "绑定已失效，请在桌面端生成新绑定码。"
    }

    private func resetState() {
        let name = state.name
        state = ClientState()
        if MailConstants.validName(name) { state.name = name }
        receiptStatus = ""
        persist()
    }

    private func persist() {
        do { defaults.set(try JSONEncoder().encode(state), forKey: Self.stateKey) }
        catch { errorMessage = "本机状态未能保存，请重新打开应用后检查绑定。" }
    }
}
