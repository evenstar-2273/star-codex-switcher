import Foundation
import Testing
@testable import SwitcherCore

struct AccountReauthorizationTests {
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
