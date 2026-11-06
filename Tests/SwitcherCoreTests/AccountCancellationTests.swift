import Foundation
import Testing
@testable import SwitcherCore

@MainActor
struct AccountCancellationTests {
    @Test func cancelWaitsForTemporaryProfileCleanupAndPreservesActiveLogin() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: true))
        defer { fixture.clean() }
        await fixture.model.start()
        let originalID = fixture.model.activeAccountID
        fixture.model.addAccount()
        let home = await fixture.client.waitForLogin(1)
        #expect(FileManager.default.fileExists(atPath: home.appendingPathComponent("auth.json").path))
        fixture.model.refreshWeeklyUsage()
        await fixture.model.waitForWeeklyUsageRefresh()
        #expect(fixture.model.usageRefreshRevision == 0)

        await fixture.model.cancelAddingAccountAndWait()

        #expect(!fixture.model.isAddingAccount)
        #expect(!FileManager.default.fileExists(atPath: home.path))
        #expect(fixture.model.visibleError == nil)
        #expect(fixture.model.accounts.count == 1)
        #expect(fixture.model.activeAccountID == originalID)
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func lateLoginCompletionCannotSaveAfterCancellationAndNextLoginStillWorks() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false))
        defer { fixture.clean() }
        await fixture.model.start()
        let originalID = fixture.model.activeAccountID
        fixture.model.addAccount()
        let cancelledHome = await fixture.client.waitForLogin(1)
        fixture.model.cancelAddingAccount()
        // The fake login deliberately ignores cancellation and returns a successful identity later.
        #expect(fixture.model.isAddingAccount)
        fixture.model.addAccount()
        #expect(await fixture.client.loginCount == 1)
        await fixture.client.completeLogin()
        await fixture.model.cancelAddingAccountAndWait()
        #expect(!FileManager.default.fileExists(atPath: cancelledHome.path))
        #expect(fixture.model.accounts.count == 1)

        fixture.model.addAccount()
        let savedHome = await fixture.client.waitForLogin(2)
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        #expect(!fixture.model.isAddingAccount)
        #expect(fixture.model.accounts.count == 2)
        #expect(FileManager.default.fileExists(atPath: savedHome.appendingPathComponent("auth.json").path))
        #expect(fixture.model.activeAccountID == originalID)
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
        #expect(fixture.model.visibleError == nil)
    }

    @Test func cancelledTaskCannotCommitAProfileAfterEnteringTheStoreActor() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: true))
        defer { fixture.clean() }
        await fixture.model.start()
        let profile = AccountProfile(id: UUID(), displayName: "Cancelled", email: "cancelled@example.test",
            accountID: "cancelled-fixture", createdAt: Date())
        let home = try await fixture.store.createProfileDirectory(id: profile.id)
        try Data(#"{"tokens":{"account_id":"cancelled-fixture"}}"#.utf8)
            .write(to: home.appendingPathComponent("auth.json"))
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await fixture.store.addProfile(profile)
        }
        do { try await task.value; Issue.record("A cancelled login must not commit its profile.") }
        catch { #expect(error is CancellationError) }
        try await fixture.store.discardUnregisteredProfile(id: profile.id)
        #expect(try await fixture.store.loadRegistry().accounts.count == 1)
        #expect(!FileManager.default.fileExists(atPath: home.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func cleanupDoesNotDeleteAnAlreadyCommittedProfile() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: true))
        defer { fixture.clean() }
        await fixture.model.start()
        let profile = AccountProfile(id: UUID(), displayName: "Saved", email: "saved@example.test",
            accountID: "saved-fixture", createdAt: Date())
        let home = try await fixture.store.createProfileDirectory(id: profile.id)
        let bytes = Data(#"{"tokens":{"account_id":"saved-fixture"}}"#.utf8)
        try bytes.write(to: home.appendingPathComponent("auth.json"))
        try await fixture.store.addProfile(profile)
        try await fixture.store.discardUnregisteredProfile(id: profile.id)
        #expect(try await fixture.store.loadRegistry().accounts.contains { $0.id == profile.id })
        #expect(try Data(contentsOf: home.appendingPathComponent("auth.json")) == bytes)
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    private func waitUntilIdle(_ model: AccountController) async {
        guard model.isAddingAccount else { return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            model.onChange = {
                guard !model.isAddingAccount else { return }
                model.onChange = nil
                continuation.resume()
            }
        }
    }
}

@MainActor
private struct CancellationFixture {
    let root: URL
    let activeAuth: URL
    let activeBytes = Data(#"{"tokens":{"account_id":"active-cancellation-fixture"}}"#.utf8)
    let store: AccountStore
    let client: ControlledLoginClient
    let model: AccountController

    init(client: ControlledLoginClient) throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("switcher-cancel-test-\(UUID())")
        let active = root.appendingPathComponent("active")
        try FileManager.default.createDirectory(at: active, withIntermediateDirectories: true)
        activeAuth = active.appendingPathComponent("auth.json")
        try activeBytes.write(to: activeAuth)
        store = AccountStore(baseURL: root.appendingPathComponent("store"), activeHomeURL: active)
        self.client = client
        model = AccountController(store: store, codex: client,
            switchService: SwitchService(desktop: CancellationDesktop(), store: store, codex: client))
    }

    func clean() { try? FileManager.default.removeItem(at: root) }
}

private actor ControlledLoginClient: AccountClient {
    private let cooperative: Bool
    private var homes: [URL] = []
    private var startedWaiters: [(Int, CheckedContinuation<URL, Never>)] = []
    private var completion: CheckedContinuation<Void, Never>?
    var loginCount: Int { homes.count }

    init(cooperative: Bool) { self.cooperative = cooperative }

    func waitForLogin(_ count: Int) async -> URL {
        if homes.count >= count { return homes[count - 1] }
        return await withCheckedContinuation { startedWaiters.append((count, $0)) }
    }

    func completeLogin() {
        let pending = completion
        completion = nil
        pending?.resume()
    }

    func login(profileHome: URL) async throws -> AccountIdentity {
        let number = homes.count + 1
        try Data("{\"tokens\":{\"account_id\":\"login-fixture-\(number)\"}}".utf8)
            .write(to: profileHome.appendingPathComponent("auth.json"))
        homes.append(profileHome)
        if cooperative {
            notifyStarted()
            try await Task.sleep(for: .seconds(600))
        } else {
            await withCheckedContinuation { continuation in
                completion = continuation
                notifyStarted()
            }
        }
        return AccountIdentity(accountID: "login-fixture-\(number)", email: "login\(number)@example.test")
    }

    private func notifyStarted() {
        let ready = startedWaiters.filter { $0.0 <= homes.count }
        startedWaiters.removeAll { $0.0 <= homes.count }
        for (count, continuation) in ready { continuation.resume(returning: homes[count - 1]) }
    }

    func readIdentity(profileHome: URL) async throws -> AccountIdentity { throw CodexClientError.identityUnavailable }
    func readWeeklyUsage(profileHome: URL) async throws -> WeeklyUsage { throw CodexClientError.weeklyUsageUnavailable }
    func consumeResetCredit(profileHome: URL, creditID: String, idempotencyKey: String) async throws -> ResetCreditOutcome { .noCredit }
}

private struct CancellationDesktop: DesktopControlling {
    func closeDesktop() async throws {}
    func reopenDesktop() async throws {}
}
