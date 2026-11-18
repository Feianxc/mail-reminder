import Foundation
import Security

struct BindingCredential: Codable {
    let token: String
    let nodeID: String
    let roomID: String
}

enum SecureStore {
    private static let service = "online.feian.mailreminder.ios.binding"
    private static let account = "mobile-token"

    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func load() throws -> BindingCredential? {
        var search = query
        search[kSecReturnData as String] = true
        search[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(search as CFDictionary, &item)
        #if DEBUG
        NSLog("MailReminder Keychain load OSStatus=%d", status)
        #endif
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data else { throw KeychainFailure(status: status) }
        return try JSONDecoder().decode(BindingCredential.self, from: data)
    }

    static func save(_ credential: BindingCredential) throws {
        let data = try JSONEncoder().encode(credential)
        let values: [String: Any] = [kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            values.forEach { item[$0.key] = $0.value }
            let added = SecItemAdd(item as CFDictionary, nil)
            guard added == errSecSuccess else { throw KeychainFailure(status: added) }
        } else if status != errSecSuccess {
            throw KeychainFailure(status: status)
        }
    }

    static func clear() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainFailure(status: status) }
    }
}

private struct KeychainFailure: LocalizedError {
    let status: OSStatus
    var errorDescription: String? { "无法访问本机安全存储，请解锁手机后重试。" }
}
