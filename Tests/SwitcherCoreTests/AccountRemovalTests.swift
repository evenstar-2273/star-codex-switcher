import Foundation
import Testing
@testable import SwitcherCore

struct AccountRemovalTests {
    @Test func removalPersistsWithoutChangingLiveCredentialsOrOtherAccounts() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let first = try await fixture.save("personal", active: true)
        let second = try await fixture.save("workspace", active: false)
        let live = try Data(contentsOf: fixture.auth)
        let secondHome = await fixture.store.profileHome(id: second.id)
        let secondBytes = try Data(contentsOf: secondHome.appending(path: "auth.json"))
        let history = fixture.active.appending(path: "session_index.jsonl")
        try Data("preserved-session-history".utf8).write(to: history)
        let usage = WeeklyUsage(remainingPercent: 60, resetsAt: Date(timeIntervalSince1970: 2_000_000_000))
        try await fixture.store.cacheWeeklyUsage(usage, profileID: first.id)
        try await fixture.store.cacheWeeklyUsage(usage, profileID: second.id)

        try await fixture.store.removeAccount(id: first.id)

        let removedHome = await fixture.store.profileHome(id: first.id)
        #expect(!FileManager.default.fileExists(atPath: removedHome.path))
        #expect(try Data(contentsOf: fixture.auth) == live)
        #expect(try Data(contentsOf: history) == Data("preserved-session-history".utf8))
        #expect(try Data(contentsOf: secondHome.appending(path: "auth.json")) == secondBytes)
        #expect(try await fixture.store.loadUsageCache().entries.map(\.profileID) == [second.id])
        for store in [fixture.store, AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)] {
            let synced = try await store.syncActiveCredential()
            #expect(synced.accounts == [second])
            #expect(synced.activeAccountID == nil)
            #expect(synced.removedAccountIdentities.count == 1)
            #expect(await store.activeCredentialWasRemoved)
        }
        #expect(try Data(contentsOf: fixture.auth) == live)
    }

    @Test func deletedIdentityDoesNotHideAnotherWorkspaceWithTheSameEmail() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        try await fixture.store.removeAccount(id: removed.id)
        try fixture.bytes("new-workspace").write(to: fixture.auth)
        let synced = try await fixture.store.syncActiveCredential()
        #expect(synced.accounts.count == 1)
        #expect(synced.accounts.first?.accountID == "new-workspace")
        #expect(synced.activeAccountID == synced.accounts.first?.id)
        #expect(await !fixture.store.activeCredentialWasRemoved)
    }

    @Test func explicitRegistrationRestoresARemovedCurrentLogin() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        try await fixture.store.removeAccount(id: removed.id)
        let before = try Data(contentsOf: fixture.auth)
        let restored = try await fixture.store.syncActiveCredential(includeRemoved: true)
        #expect(restored.accounts.count == 1)
        #expect(restored.accounts.first?.accountID == "personal")
        #expect(restored.activeAccountID != nil)
        #expect(restored.activeAccountID != removed.id)
        #expect(restored.removedAccountIdentities.isEmpty)
        #expect(try await fixture.store.syncActiveCredential().accounts.count == 1)
        #expect(try Data(contentsOf: fixture.auth) == before)
    }

    @Test func explicitSavedProfileRestoresARemovedIdentity() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        try await fixture.store.removeAccount(id: removed.id)
        let restored = try await fixture.save("personal", active: false)
        #expect(try await fixture.store.loadRegistry().removedAccountIdentities.isEmpty)
        #expect(try await fixture.store.syncActiveCredential().activeAccountID == restored.id)
    }

    @Test func existingImportedProfileTakesPrecedenceOverARemovalMarker() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let profile = try await fixture.save("personal", active: true)
        let path = fixture.base.appending(path: "accounts.json")
        var json = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: path)) as? [String: Any])
        json["removedAccountIdentities"] = [["accountID": "personal", "email": "same@example.test"]]
        try JSONSerialization.data(withJSONObject: json).write(to: path)
        let reopened = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        let synced = try await reopened.syncActiveCredential()
        #expect(synced.activeAccountID == profile.id)
        #expect(synced.accounts == [profile])
        #expect(synced.removedAccountIdentities.isEmpty)
    }

    @Test func registryWriteFailurePreservesSavedAndLiveCredentials() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let profile = try await fixture.save("personal", active: true)
        let home = await fixture.store.profileHome(id: profile.id)
        let before = try Data(contentsOf: fixture.auth)
        let path = fixture.base.appending(path: "accounts.json")
        try FileManager.default.moveItem(at: path, to: fixture.base.appending(path: "accounts-before.json"))
        try FileManager.default.createDirectory(at: path, withIntermediateDirectories: false)

        await #expect(throws: (any Error).self) { try await fixture.store.removeAccount(id: profile.id) }

        let unchanged = try await fixture.store.loadRegistry()
        #expect(unchanged.accounts == [profile])
        #expect(unchanged.activeAccountID == profile.id)
        #expect(unchanged.removedAccountIdentities.isEmpty)
        #expect(try Data(contentsOf: home.appending(path: "auth.json")) == before)
        #expect(try Data(contentsOf: fixture.auth) == before)
    }

    @Test func credentialRemovalFailureRestoresRegistryAndRemovalMarkers() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let profile = try await fixture.save("personal", active: true)
        let home = await fixture.store.profileHome(id: profile.id)
        let before = try Data(contentsOf: fixture.auth)
        let store = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active,
                                 fileManager: RefusingProfileRemoval(blocked: home))

        await #expect(throws: (any Error).self) { try await store.removeAccount(id: profile.id) }

        let restarted = AccountStore(baseURL: fixture.base, activeHomeURL: fixture.active)
        let unchanged = try await restarted.loadRegistry()
        #expect(unchanged.accounts == [profile])
        #expect(unchanged.activeAccountID == profile.id)
        #expect(unchanged.removedAccountIdentities.isEmpty)
        #expect(try Data(contentsOf: home.appending(path: "auth.json")) == before)
        #expect(try Data(contentsOf: fixture.auth) == before)
    }

    @Test func oldRegistryWithoutRemovalMarkersStillDecodes() throws {
        let registry = try JSONDecoder().decode(AccountRegistry.self, from: Data(#"{"accounts":[]}"#.utf8))
        #expect(registry == .empty)
    }

    @Test func switchingAfterRemovalDoesNotRecreateTheRemovedProfile() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        let target = try await fixture.save("workspace", active: false)
        try await fixture.store.removeAccount(id: removed.id)
        let desktop = RemovalDesktop()
        try await SwitchService(desktop: desktop, store: fixture.store, codex: CredentialIdentity())
            .switchAccount(to: target.id)
        let synced = try await fixture.store.syncActiveCredential()
        #expect(synced.accounts.count == 1)
        #expect(synced.accounts.first?.id == target.id)
        #expect(synced.activeAccountID == target.id)
        #expect(try CredentialIdentity.read(from: fixture.active)?.accountID == "workspace")
        #expect(await desktop.calls == 2)
    }

    @Test func failedSwitchAfterRemovalRestoresTheUnlistedLiveLogin() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        let target = try await fixture.save("workspace", active: false)
        try await fixture.store.removeAccount(id: removed.id)
        let before = try Data(contentsOf: fixture.auth)
        let targetHome = await fixture.store.profileHome(id: target.id)
        try fixture.bytes("unexpected-workspace").write(to: targetHome.appending(path: "auth.json"))
        let desktop = RemovalDesktop()
        do {
            try await SwitchService(desktop: desktop, store: fixture.store, codex: CredentialIdentity())
                .switchAccount(to: target.id)
            Issue.record("The mismatched target must fail verification.")
        } catch let error as OperationError {
            #expect(error.stage == .verifyTargetIdentity)
        }
        #expect(try Data(contentsOf: fixture.auth) == before)
        let synced = try await fixture.store.syncActiveCredential()
        #expect(synced.accounts == [target])
        #expect(synced.activeAccountID == nil)
        #expect(await fixture.store.activeCredentialWasRemoved)
        #expect(await desktop.calls == 1)
    }

    @Test func anUnknownUnlistedLoginStillBlocksSwitching() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let target = try await fixture.save("workspace", active: false)
        let before = try fixture.bytes("unrecognized")
        try before.write(to: fixture.auth)
        let desktop = RemovalDesktop()
        do {
            try await SwitchService(desktop: desktop, store: fixture.store, codex: CredentialIdentity())
                .switchAccount(to: target.id)
            Issue.record("Only deliberately removed unlisted logins may be snapshotted.")
        } catch let error as OperationError {
            #expect(error.stage == .saveCurrentCredential)
        }
        #expect(try Data(contentsOf: fixture.auth) == before)
        #expect(await desktop.calls == 0)
    }

    @Test func failedVerificationDoesNotOverwriteAnExternalLoginDuringRestoration() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        let target = try await fixture.save("workspace", active: false)
        try await fixture.store.removeAccount(id: removed.id)
        let external = try fixture.bytes("external-login")
        let desktop = RemovalDesktop()
        let reader = ExternallyReplacingIdentityReader(auth: fixture.auth, credential: external)
        do {
            try await SwitchService(desktop: desktop, store: fixture.store, codex: reader)
                .switchAccount(to: target.id)
            Issue.record("Verification should fail without replacing the externally changed login.")
        } catch let error as OperationError {
            #expect(error.stage == .verifyTargetIdentity)
            #expect(error.message.contains("Restoring the previous credential also failed"))
            #expect(!error.message.contains("synthetic-removal-token"))
        }
        #expect(try Data(contentsOf: fixture.auth) == external)
        #expect(try await fixture.store.loadRegistry().activeAccountID == nil)
        #expect(try await fixture.store.loadRegistry().accounts == [target])
        #expect(await desktop.calls == 1)
    }

    @Test func aRemovedLoginChangedDuringDesktopExitIsNotOverwritten() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let removed = try await fixture.save("personal", active: true)
        let target = try await fixture.save("workspace", active: false)
        try await fixture.store.removeAccount(id: removed.id)
        let changed = try fixture.bytes("external-login")
        let auth = fixture.auth
        let desktop = RemovalDesktop(onClose: { try changed.write(to: auth) })
        do {
            try await SwitchService(desktop: desktop, store: fixture.store, codex: CredentialIdentity())
                .switchAccount(to: target.id)
            Issue.record("A changed unlisted login must stop before target installation.")
        } catch let error as OperationError {
            #expect(error.stage == .saveCurrentCredential)
        }
        #expect(try Data(contentsOf: fixture.auth) == changed)
        #expect(try await fixture.store.loadRegistry().activeAccountID == nil)
        #expect(await desktop.calls == 1)
    }

    @Test @MainActor func deletingCurrentAccountDoesNotControlDesktopOrMisreportLogin() async throws {
        let fixture = try RemovalFixture()
        defer { fixture.clean() }
        let first = try await fixture.save("personal", active: true)
        let second = try await fixture.save("workspace", active: false)
        let desktop = RemovalDesktop()
        let client = RemovalClient()
        let model = AccountController(store: fixture.store, codex: client,
            switchService: SwitchService(desktop: desktop, store: fixture.store, codex: client))
        await model.start()
        await model.removeAccount(id: first.id)
        await model.waitForWeeklyUsageRefresh()
        #expect(model.accounts == [second])
        #expect(model.activeAccountID == nil)
        #expect(model.activeIdentityConfirmed)
        #expect(model.visibleError == nil)
        #expect(await desktop.calls == 0)
        #expect(try CredentialIdentity.read(from: fixture.active)?.accountID == "personal")
        try FileManager.default.removeItem(at: fixture.auth)
        model.refreshWeeklyUsage()
        await model.waitForWeeklyUsageRefresh()
        #expect(!model.activeIdentityConfirmed)
    }
}

private struct RemovalFixture {
    let root: URL
    let base: URL
    let active: URL
    let store: AccountStore
    var auth: URL { active.appending(path: "auth.json") }

    init() throws {
        root = FileManager.default.temporaryDirectory.appending(path: "account-removal-\(UUID())")
        base = root.appending(path: "store")
        active = root.appending(path: "active")
        try FileManager.default.createDirectory(at: active, withIntermediateDirectories: true)
        store = AccountStore(baseURL: base, activeHomeURL: active)
    }
    func bytes(_ id: String) throws -> Data {
        let claims = Data(#"{"email":"same@example.test"}"#.utf8).base64EncodedString()
        return try JSONSerialization.data(withJSONObject: ["tokens": [
            "account_id": id, "access_token": "synthetic-removal-token", "id_token": "header.\(claims).signature"
        ]])
    }
    func save(_ id: String, active isActive: Bool) async throws -> AccountProfile {
        let profile = AccountProfile(id: UUID(), displayName: id, email: "same@example.test", accountID: id,
                                     createdAt: Date(timeIntervalSince1970: 1_900_000_000))
        if isActive {
            try bytes(id).write(to: auth)
            try await store.importCurrentProfile(profile)
        } else {
            let home = try await store.createProfileDirectory(id: profile.id)
            try bytes(id).write(to: home.appending(path: "auth.json"))
            try await store.addProfile(profile)
        }
        return profile
    }
    func clean() { try? FileManager.default.removeItem(at: root) }
}

private final class RefusingProfileRemoval: FileManager, @unchecked Sendable {
    private let blocked: URL
    init(blocked: URL) { self.blocked = blocked; super.init() }
    override func removeItem(at URL: URL) throws {
        if URL == blocked { throw CocoaError(.fileWriteNoPermission) }
        try super.removeItem(at: URL)
    }
}

private actor RemovalDesktop: DesktopControlling {
    private(set) var calls = 0
    private let onClose: (@Sendable () throws -> Void)?
    init(onClose: (@Sendable () throws -> Void)? = nil) { self.onClose = onClose }
    func closeDesktop() throws { calls += 1; try onClose?() }
    func reopenDesktop() { calls += 1 }
}

private struct RemovalClient: AccountClient {
    func readIdentity(profileHome: URL) async throws -> AccountIdentity {
        guard let identity = try CredentialIdentity.read(from: profileHome) else { throw CodexClientError.identityUnavailable }
        return identity
    }
    func readWeeklyUsage(profileHome: URL) async throws -> WeeklyUsage {
        WeeklyUsage(remainingPercent: 60, resetsAt: Date(timeIntervalSince1970: 2_000_000_000))
    }
    func login(profileHome: URL) async throws -> AccountIdentity { try await readIdentity(profileHome: profileHome) }
    func consumeResetCredit(profileHome: URL, creditID: String, idempotencyKey: String) async throws -> ResetCreditOutcome {
        throw CodexClientError.connectionClosed
    }
}

private struct ExternallyReplacingIdentityReader: CodexIdentityReading {
    let auth: URL
    let credential: Data
    func readIdentity(profileHome: URL) async throws -> AccountIdentity {
        try credential.write(to: auth)
        throw CodexClientError.connectionClosed
    }
}
