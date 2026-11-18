import Foundation

enum MailConstants {
    static let baseURL = URL(string: "https://blog.feian.online/mail-reminder/")!
    static let userAgent = "MailReminderiOS/1.3.0"
    static let message = "收到一封加急邮件，请立刻处理"
    static let reminderDuration: UInt64 = 5_000_000_000
    static let receiptLimit = 128
    static let historyLimit = 64

    static func validName(_ value: String) -> Bool {
        !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && value.utf16.count <= 64
            && !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }

    static func validAlertID(_ value: String) -> Bool {
        value.count == 32 && value.unicodeScalars.allSatisfy {
            (48...57).contains($0.value) || (65...70).contains($0.value) || (97...102).contains($0.value)
        }
    }
}

struct PairRequest: Encodable {
    let code: String
    let name: String
    let previousToken: String?
}

struct PairResponse: Decodable {
    let token: String
    let roomId: String
    let subnet: String
    let nodeId: String
    let name: String
    let cursor: Int64
}

struct PollResponse: Decodable {
    let cursor: Int64
    let packets: [String]
}

struct EmptyRequest: Encodable {}
struct SuccessResponse: Decodable {
    let ok: Bool
    let name: String?
}
struct AlertRequest: Encodable { let isTest: Bool }
struct AlertResponse: Decodable { let alertId: String }
struct NameRequest: Encodable { let name: String }
struct ReceiptRequest: Encodable {
    let alertId: String
    let seen: Bool
}

struct WireEnvelope: Decodable {
    let Payload: String
    let Signature: String
}

struct WirePayload: Decodable {
    let Version: Int
    let kind: String
    let SenderId: String
    let Name: String
    let Id: String?
    let TargetId: String?
    let Time: Int64
    let AlertTime: Int64
    let IsTest: Bool

    private enum CodingKeys: String, CodingKey {
        case Version, SenderId, Name, Id, TargetId, Time, AlertTime, IsTest
        case kind = "Type"
    }

    static func decode(_ packet: String, now: Date = Date()) -> WirePayload? {
        guard packet.utf8.count <= 8192,
              let envelope = try? JSONDecoder().decode(WireEnvelope.self, from: Data(packet.utf8)),
              !envelope.Signature.isEmpty,
              let payload = try? JSONDecoder().decode(WirePayload.self, from: Data(envelope.Payload.utf8)),
              payload.Version == 1,
              !payload.SenderId.isEmpty, payload.SenderId.utf8.count <= 128,
              MailConstants.validName(payload.Name),
              abs(Double(payload.Time) - now.timeIntervalSince1970 * 1000) <= 120_000
        else { return nil }

        // Mobile devices receive authenticated server data over pinned-origin HTTPS.
        // The room HMAC key remains on the desktop and server.
        guard ["alert", "ack", "seen"].contains(payload.kind),
              let id = payload.Id, MailConstants.validAlertID(id) else { return nil }
        if payload.kind == "alert"
            && abs(Double(payload.AlertTime) - now.timeIntervalSince1970 * 1000) > 120_000 {
            return nil
        }
        return payload
    }
}

struct IncomingReminder: Codable, Identifiable, Equatable {
    let id: String
    let senderName: String
    let receivedAt: Date
    let isTest: Bool
    var seen: Bool

    var title: String { isTest ? "联机测试提醒" : MailConstants.message }
}

struct PendingReceipt: Codable, Equatable, Identifiable {
    let id: String
    var seen: Bool
    let createdAt: Date
}

struct ReceiptSummary: Codable, Identifiable {
    let id: String
    var receivedBy: [String] = []
    var seenBy: [String] = []

    mutating func record(sender: String, seen: Bool) {
        if !receivedBy.contains(sender) && receivedBy.count < 512 { receivedBy.append(sender) }
        if seen && !seenBy.contains(sender) && seenBy.count < 512 { seenBy.append(sender) }
    }
}

struct SentReminder: Codable {
    let id: String
    let sentAt: Date
    let isTest: Bool
}

struct ClientState: Codable {
    var nodeID = ""
    var roomID = ""
    var subnet = ""
    var name = "我的 iPhone"
    var cursor: Int64 = 0
    var processed: [String] = []
    var pendingReceipts: [PendingReceipt] = []
    var incoming: [IncomingReminder] = []
    var receiptCache: [ReceiptSummary] = []
    var lastSent: SentReminder?

    mutating func enqueueReceipt(id: String, seen: Bool) -> Bool {
        if let index = pendingReceipts.firstIndex(where: { $0.id == id }) {
            pendingReceipts[index].seen = pendingReceipts[index].seen || seen
            return true
        }
        guard pendingReceipts.count < MailConstants.receiptLimit else { return false }
        pendingReceipts.append(PendingReceipt(id: id, seen: seen, createdAt: Date()))
        return true
    }

    mutating func removeDelivered(_ receipt: PendingReceipt) {
        // A user may upgrade ACK to SEEN while the ACK request is awaiting its response.
        pendingReceipts.removeAll { $0.id == receipt.id && (receipt.seen || !$0.seen) }
    }

    mutating func recordReceipt(id: String, sender: String, seen: Bool) {
        if let index = receiptCache.firstIndex(where: { $0.id == id }) {
            receiptCache[index].record(sender: sender, seen: seen)
        } else {
            var summary = ReceiptSummary(id: id)
            summary.record(sender: sender, seen: seen)
            receiptCache.append(summary)
            receiptCache = Array(receiptCache.suffix(32))
        }
    }
}
