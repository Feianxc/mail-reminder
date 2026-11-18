import Foundation

func check(_ value: @autoclosure () -> Bool, _ message: String) {
    guard value() else { fatalError(message) }
    print("PASS \(message)")
}

for count in [0, 1, 3] {
    var state = ClientState()
    for index in 0..<count {
        let id = String(format: "%032x", index + 1)
        _ = state.enqueueReceipt(id: id, seen: false)
        state.incoming.append(IncomingReminder(id: id, senderName: "中文同事", receivedAt: Date(), isTest: false, seen: false))
    }
    let encoded = try JSONEncoder().encode(state)
    let json = try JSONSerialization.jsonObject(with: encoded) as! [String: Any]
    check((json["pendingReceipts"] as? [Any])?.count == count, "\(count) receipt entries remain an array")
    check((json["incoming"] as? [Any])?.count == count, "\(count) reminders remain an array")
    let restored = try JSONDecoder().decode(ClientState.self, from: encoded)
    check(restored.pendingReceipts.count == count && restored.incoming.count == count, "\(count) persisted reminders round-trip")
    let pollJSON = try JSONSerialization.data(withJSONObject: ["cursor": count, "packets": Array(repeating: "packet", count: count)])
    let poll = try JSONDecoder().decode(PollResponse.self, from: pollJSON)
    check(poll.packets.count == count, "\(count) poll packets decode")
}

let id = String(repeating: "a", count: 32)
var pending = ClientState()
_ = pending.enqueueReceipt(id: id, seen: false)
let inFlight = pending.pendingReceipts[0]
_ = pending.enqueueReceipt(id: id, seen: true)
pending.removeDelivered(inFlight)
check(pending.pendingReceipts.count == 1 && pending.pendingReceipts[0].seen, "manual confirmation survives an earlier software receipt response")
pending.removeDelivered(pending.pendingReceipts[0])
check(pending.pendingReceipts.isEmpty, "delivered manual confirmation leaves the queue")
check(MailConstants.validName("张三-研发") && !MailConstants.validName("张三\n"), "Chinese names and control characters")
check(MailConstants.message == "收到一封加急邮件，请立刻处理", "urgent message uses the requested Chinese wording")
print("PASS 16 iOS model and protocol checks")
