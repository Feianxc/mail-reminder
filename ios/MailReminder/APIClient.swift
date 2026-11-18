import Foundation

enum APIError: LocalizedError {
    case response(Int, String)
    case invalidResponse
    case invalidBinding

    var errorDescription: String? {
        switch self {
        case .response(_, let message): return message
        case .invalidResponse: return "服务器返回的数据不完整，请稍后重试。"
        case .invalidBinding: return "绑定信息不完整，请重新生成绑定码。"
        }
    }

    var unauthorized: Bool {
        if case .response(401, _) = self { return true }
        return false
    }
}

private struct ServerFailure: Decodable {
    let message: String?
}

private final class RedirectBlocker: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

final class APIClient {
    private let session: URLSession
    private let redirectBlocker = RedirectBlocker()

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 40
        configuration.timeoutIntervalForResource = 45
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        session = URLSession(configuration: configuration)
    }

    func pair(code: String, name: String, previousToken: String?) async throws -> PairResponse {
        let response: PairResponse = try await post("api/mobile/pair", token: nil,
            body: PairRequest(code: code, name: name, previousToken: previousToken))
        guard response.token.count >= 24, response.token.count <= 1024,
              !response.roomId.isEmpty, response.roomId.count <= 128,
              response.nodeId.hasPrefix("mobile-"), response.nodeId.count <= 128,
              MailConstants.validName(response.name),
              !response.subnet.isEmpty, response.subnet.count <= 64,
              response.cursor >= 0 else { throw APIError.invalidBinding }
        return response
    }

    func poll(token: String, cursor: Int64) async throws -> PollResponse {
        let response: PollResponse = try await request("api/mobile/poll?cursor=\(max(0, cursor))", token: token)
        guard response.cursor >= 0, response.packets.count <= 128 else { throw APIError.invalidResponse }
        return response
    }

    func send(token: String, isTest: Bool) async throws -> AlertResponse {
        let response: AlertResponse = try await post("api/mobile/alert", token: token,
                                                     body: AlertRequest(isTest: isTest))
        guard MailConstants.validAlertID(response.alertId) else { throw APIError.invalidResponse }
        return response
    }

    func receipt(token: String, receipt: PendingReceipt) async throws {
        let response: SuccessResponse = try await post("api/mobile/ack", token: token,
            body: ReceiptRequest(alertId: receipt.id, seen: receipt.seen))
        guard response.ok else { throw APIError.invalidResponse }
    }

    func rename(token: String, name: String) async throws -> String {
        let response: SuccessResponse = try await post("api/mobile/name", token: token, body: NameRequest(name: name))
        guard response.ok else { throw APIError.invalidResponse }
        return response.name ?? name
    }

    func unbind(token: String) async throws {
        let response: SuccessResponse = try await post("api/mobile/unbind", token: token, body: EmptyRequest())
        guard response.ok else { throw APIError.invalidResponse }
    }

    private func post<Body: Encodable, Response: Decodable>(
        _ path: String, token: String?, body: Body
    ) async throws -> Response {
        try await request(path, token: token, body: JSONEncoder().encode(body))
    }

    private func request<Response: Decodable>(
        _ path: String, token: String?, body: Data? = nil
    ) async throws -> Response {
        // All API paths are constants. Redirects never forward the bearer token.
        guard let url = URL(string: path, relativeTo: MailConstants.baseURL)?.absoluteURL,
              url.scheme == "https", url.host == "blog.feian.online",
              url.path.hasPrefix("/mail-reminder/api/") else { throw APIError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = body == nil ? "GET" : "POST"
        request.timeoutInterval = body == nil ? 40 : 15
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(MailConstants.userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        if body != nil { request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type") }
        if let token = token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let (data, response) = try await session.data(for: request, delegate: redirectBlocker)
        try Task.checkCancellation()
        guard let http = response as? HTTPURLResponse,
              data.count <= 2 * 1024 * 1024 else { throw APIError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            let failure = try? JSONDecoder().decode(ServerFailure.self, from: data)
            let fallback = http.statusCode == 401 ? "绑定已失效，请重新绑定。" : "连接服务失败，请稍后重试。"
            throw APIError.response(http.statusCode, failure?.message ?? fallback)
        }
        do { return try JSONDecoder().decode(Response.self, from: data) }
        catch { throw APIError.invalidResponse }
    }
}
