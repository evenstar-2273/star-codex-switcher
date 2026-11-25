import Foundation
import Testing
@testable import SwitcherCore

struct AccountReauthorizationTests {
    private func identityBytes(email: String, user: String = "user", workspace: String = "workspace", organization: String = "org") throws -> Data {
        let claims: [String: Any] = ["sub": user, "email": email, "https://api.openai.com/auth":
            ["chatgpt_user_id": user, "chatgpt_account_id": workspace, "organization_id": organization]]
        let payload = try JSONSerialization.data(withJSONObject: claims).base64EncodedString()
            .replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        return try JSONSerialization.data(withJSONObject: ["tokens": ["id_token": "e30.\(payload).sig", "access_token": "e30.\(payload).sig", "account_id": workspace]])
    }

    @Test func renewedEmailUsesSavedIdentityAndNeverTakesAnotherRecord() async throws {
        let fixture = try RenewalFixture(); defer { fixture.clean() }
        let original = fixture.profile("workspace", name: "Keep my label")
        let home = try await fixture.stage(original, token: "old")
        try identityBytes(email: "renewal@example.test").write(to: home.appending(path: "auth.json"))
        try await fixture.store.addProfile(original)
        let other = fixture.profile("different-workspace")
        _ = try await fixture.save(other, token: "other")
        let renewal = AccountProfile(id: UUID(), displayName: "new", email: "new@example.test", accountID: "workspace", createdAt: Date())
        let temporary = try await fixture.stage(renewal, token: "fresh")
        let fresh = try identityBytes(email: "new@example.test")
        try fresh.write(to: temporary.appending(path: "auth.json"))
        try await fixture.store.addProfile(renewal, replacingAccountID: original.id)
        var registry = try await fixture.store.loadRegistry()
        #expect(registry.accounts.count == 2)
        #expect(registry.accounts[0].id == original.id && registry.accounts[0].displayName == "Keep my label")
        #expect(registry.accounts[0].email == "new@example.test")
        #expect(try Data(contentsOf: home.appending(path: "auth.json")) == fresh)
        // Same user/workspace but the next email belongs to a different saved record.
        let conflict = fixture.profile("workspace")
        let conflictHome = try await fixture.stage(conflict, token: "conflict")
        try identityBytes(email: "renewal@example.test").write(to: conflictHome.appending(path: "auth.json"))
        await #expect(throws: (any Error).self) { try await fixture.store.addProfile(conflict, replacingAccountID: original.id) }
        registry = try await fixture.store.loadRegistry()
        #expect(registry.accounts.count == 2 && registry.accounts[0].email == "new@example.test")
        #expect(try Data(contentsOf: home.appending(path: "auth.json")) == fresh)
        #expect(try !CredentialIdentity.reauthorizationMatches(saved: fresh, received: identityBytes(email: "new@example.test", user: "someone-else")))
        #expect(try !CredentialIdentity.reauthorizationMatches(saved: fresh, received: identityBytes(email: "new@example.test", organization: "other-org")))
    }

    @Test func renewingSignedOutAccountKeepsIdentityMetadataAndDoesNotSignInDesktop() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let original = fixture.profile("personal", name: "My saved label")
        let oldHome = try await fixture.save(original, token: "old")
        let renewal = fixture.profile("personal")
        let temporary = try await fixture.stage(renewal, token: "renewed")
        let renewedBytes = try Data(contentsOf: temporary.appending(path: "auth.json"))
        try await fixture.store.addProfile(renewal)
        try await fixture.store.discardUnregisteredProfile(id: renewal.id)
        let registry = try await fixture.store.loadRegistry()
        #expect(registry.accounts == [original])
        #expect(registry.activeAccountID == nil)
        #expect(try Data(contentsOf: oldHome.appending(path: "auth.json")) == renewedBytes)
        #expect(!FileManager.default.fileExists(atPath: temporary.path))
        #expect(!FileManager.default.fileExists(atPath: fixture.live.path))
        let reopened = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        #expect(try await reopened.loadRegistry().accounts == [original])
    }

    @Test func oldLiveCredentialCannotOverwriteRenewalAfterRefreshSaveOrRestart() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let original = fixture.profile("personal")
        let saved = try await fixture.save(original, token: "old")
        let old = try fixture.bytes("personal", token: "old")
        try old.write(to: fixture.live)
        _ = try await fixture.store.syncActiveCredential()
        let renewal = fixture.profile("personal")
        let temporary = try await fixture.stage(renewal, token: "renewed-without-timestamps")
        let fresh = try Data(contentsOf: temporary.appending(path: "auth.json"))
        try await fixture.store.addProfile(renewal)
        try await fixture.store.discardUnregisteredProfile(id: renewal.id)
        #expect(try Data(contentsOf: fixture.live) == old)
        let reopened = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        #expect(try await reopened.syncActiveCredential().activeAccountID == original.id)
        try await reopened.saveCurrentCredential()
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == fresh)
        let markers = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: saved.appending(path: "reauthorization-live.json"))) as? [[String: String]])
        #expect(markers.count == 1)
        #expect(markers[0].count == 2)
        #expect(markers[0].values.allSatisfy { $0.count == 64 && $0.allSatisfy { $0.isHexDigit } })
        // An actual later external login change must still synchronize, without timestamp guesses.
        let external = try fixture.bytes("personal", token: "external-new-login")
        try external.write(to: fixture.live)
        _ = try await reopened.syncActiveCredential()
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == external)
    }

    @Test func sameEmailInDifferentWorkspaceDoesNotReplaceExistingAccount() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let personal = fixture.profile("personal")
        let saved = try await fixture.save(personal, token: "personal")
        let old = try Data(contentsOf: saved.appending(path: "auth.json"))
        let workspace = fixture.profile("workspace")
        _ = try await fixture.save(workspace, token: "workspace")
        #expect(try await fixture.store.loadRegistry().accounts.count == 2)
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == old)
    }

    @Test func interruptedCredentialWriteDoesNotMakeTheRecoveryMarkerSuppressLiveSync() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let original = fixture.profile("personal")
        let saved = try await fixture.save(original, token: "saved-before-renewal")
        let priorSaved = try Data(contentsOf: saved.appending(path: "auth.json"))
        let live = try fixture.bytes("personal", token: "active-before-renewal")
        try live.write(to: fixture.live)
        let renewal = fixture.profile("personal")
        _ = try await fixture.stage(renewal, token: "new-authorization")
        try await fixture.store.addProfile(renewal)
        // Reproduce the persisted state after marker write but before an unsuccessful auth write.
        try priorSaved.write(to: saved.appending(path: "auth.json"))
        let restarted = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        _ = try await restarted.syncActiveCredential()
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == live)
        #expect(try await restarted.loadRegistry().accounts.count == 1)
    }

    @Test func renewalDoesNotChangeAnotherActiveAccount() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let target = fixture.profile("personal")
        _ = try await fixture.save(target, token: "old")
        let other = try fixture.bytes("workspace", token: "active-workspace")
        try other.write(to: fixture.live)
        let before = try await fixture.store.syncActiveCredential()
        let renewal = fixture.profile("personal")
        _ = try await fixture.stage(renewal, token: "renewed")
        try await fixture.store.addProfile(renewal)
        #expect(try await fixture.store.loadRegistry() == before)
        #expect(try Data(contentsOf: fixture.live) == other)
    }

    @Test func interruptedSecondRenewalKeepsTheFirstRenewalProtected() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let original = fixture.profile("personal")
        let saved = try await fixture.save(original, token: "old")
        try fixture.bytes("personal", token: "old-live").write(to: fixture.live)
        let first = fixture.profile("personal")
        _ = try await fixture.stage(first, token: "first-renewal")
        try await fixture.store.addProfile(first)
        let firstBytes = try Data(contentsOf: saved.appending(path: "auth.json"))
        let second = fixture.profile("personal")
        _ = try await fixture.stage(second, token: "second-renewal")
        try await fixture.store.addProfile(second)
        // The second marker reached disk, but its credential replacement did not.
        try firstBytes.write(to: saved.appending(path: "auth.json"))
        let restarted = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        _ = try await restarted.syncActiveCredential()
        try await restarted.saveCurrentCredential()
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == firstBytes)
    }

    @Test func mismatchedOrCancelledRenewalDoesNotReplaceSavedCredential() async throws {
        let fixture = try RenewalFixture()
        defer { fixture.clean() }
        let original = fixture.profile("personal")
        let saved = try await fixture.save(original, token: "old")
        let old = try Data(contentsOf: saved.appending(path: "auth.json"))
        let renewal = fixture.profile("personal")
        let temporary = try await fixture.stage(renewal, token: "new")
        try fixture.bytes("wrong-workspace", token: "wrong").write(to: temporary.appending(path: "auth.json"))
        await #expect(throws: CodexClientError.identityUnavailable) { try await fixture.store.addProfile(renewal) }
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == old)
        try fixture.bytes("personal", token: "new").write(to: temporary.appending(path: "auth.json"))
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await fixture.store.addProfile(renewal)
        }
        do { try await task.value; Issue.record("Cancelled renewal must not commit.") }
        catch { #expect(error is CancellationError) }
        #expect(try Data(contentsOf: saved.appending(path: "auth.json")) == old)
        #expect(try await fixture.store.loadRegistry().accounts == [original])
    }
}

private struct RenewalFixture {
    let root: URL
    let base: URL
    let active: URL
    let store: AccountStore
    var live: URL { active.appending(path: "auth.json") }
    init() throws {
        root = FileManager.default.temporaryDirectory.appending(path: "reauthorization-check-\(UUID())")
        base = root.appending(path: "store")
        active = root.appending(path: "active")
        try FileManager.default.createDirectory(at: active, withIntermediateDirectories: true)
        store = AccountStore(baseURL: base, activeHomeURL: active)
    }
    func profile(_ account: String, name: String = "Saved account") -> AccountProfile {
        AccountProfile(id: UUID(), displayName: name, email: "renewal@example.test", accountID: account,
            createdAt: Date(timeIntervalSince1970: 1_700_000_000), lastUsedAt: Date(timeIntervalSince1970: 1_700_000_001))
    }
    func bytes(_ account: String, token: String) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["tokens": ["account_id": account, "access_token": token]])
    }
    func stage(_ profile: AccountProfile, token: String) async throws -> URL {
        let home = try await store.createProfileDirectory(id: profile.id)
        try bytes(profile.accountID!, token: token).write(to: home.appending(path: "auth.json"))
        return home
    }
    func save(_ profile: AccountProfile, token: String) async throws -> URL {
        let home = try await stage(profile, token: token)
        try await store.addProfile(profile)
        return home
    }
    func clean() { try? FileManager.default.removeItem(at: root) }
}
