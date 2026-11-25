import Foundation
#if os(Windows)
import SwitcherPlatform
#endif

// Identity metadata labels a local login; Codex still authenticates with the service.
public struct CredentialIdentity: CodexIdentityReading {
    public init() {}

    public func readIdentity(profileHome: URL) async throws -> AccountIdentity {
        try Task.checkCancellation()
        guard let identity = try Self.read(from: profileHome) else {
            throw CodexClientError.identityUnavailable
        }
        return identity
    }

    static func read(from home: URL) throws -> AccountIdentity? {
        let url = home.appending(path: "auth.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        #if os(Windows)
        guard switcher_check_path(url.path) == 0 else { throw CodexClientError.identityUnavailable }
        #endif
        return try decode(Data(contentsOf: url))
    }

    static func decode(_ data: Data) throws -> AccountIdentity? {
        do {
            let credential = try JSONDecoder().decode(Credential.self, from: data)
            guard let tokens = credential.tokens else { return nil }
            let claims = try tokens.id_token.map(decodeClaims)
            guard let accountID = nonempty(tokens.account_id) ?? nonempty(claims?.auth?.chatgpt_account_id) else {
                return nil
            }
            return AccountIdentity(accountID: accountID, email: nonempty(claims?.email))
        } catch {
            // Decoder diagnostics may contain credential contents. Expose only the failure category.
            throw CodexClientError.identityUnavailable
        }
    }

    private static func nonempty(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return value
    }

    // Saved token claims take precedence over registry labels during targeted renewal.
    static func reauthorizationMatches(saved: Data, received: Data) throws -> Bool {
        func fields(_ bytes: Data) throws -> (String?, String?, String?) {
            let tokens = try JSONDecoder().decode(Credential.self, from: bytes).tokens
            let id = try tokens?.id_token.map(decodeClaims)
            let access = tokens?.access_token.flatMap { try? decodeClaims($0) }
            return (nonempty(access?.auth?.chatgpt_user_id) ?? nonempty(access?.auth?.user_id) ?? nonempty(id?.auth?.chatgpt_user_id) ?? nonempty(id?.auth?.user_id) ?? nonempty(id?.sub),
                    nonempty(access?.auth?.chatgpt_account_id) ?? nonempty(id?.auth?.chatgpt_account_id) ?? nonempty(tokens?.account_id),
                    nonempty(access?.auth?.organization_id) ?? nonempty(id?.auth?.organization_id))
        }
        let old = try fields(saved), new = try fields(received)
        guard let workspace = old.1, workspace == new.1 else { return false }
        if let organization = old.2, let receivedOrganization = new.2, organization != receivedOrganization { return false }
        if let user = old.0, let receivedUser = new.0 { return user == receivedUser }
        // Legacy credentials without a user ID may renew unchanged identity, never change email.
        guard let a = try decode(saved), let b = try decode(received) else { return false }
        return a.matches(b)
    }

    private static func decodeClaims(_ token: String) throws -> Claims {
        let parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { throw CodexClientError.identityUnavailable }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: payload) else { throw CodexClientError.identityUnavailable }
        return try JSONDecoder().decode(Claims.self, from: data)
    }

    private struct Credential: Decodable { let tokens: Tokens? }
    private struct Tokens: Decodable {
        let account_id: String?
        let id_token: String?
        let access_token: String?
    }
    private struct Claims: Decodable {
        let email: String?
        let sub: String?
        let auth: Auth?
        enum CodingKeys: String, CodingKey {
            case email, sub
            case auth = "https://api.openai.com/auth"
        }
    }
    private struct Auth: Decodable {
        let chatgpt_account_id: String?
        let chatgpt_user_id: String?
        let user_id: String?
        let organization_id: String?
    }
}
