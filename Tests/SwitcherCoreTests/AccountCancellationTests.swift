import Foundation
import Testing
@testable import SwitcherCore

@MainActor
struct AccountCancellationTests {
    @Test func cancelledShutdownWaitStillObservesProcessExit() async throws {
        let barrier = ProcessExitBarrier()
        let waiter = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await barrier.wait(timeout: 1)
        }
        try await Task.sleep(for: .milliseconds(30))
        barrier.markExited()
        #expect(await waiter.value)
        // Repeated stop callers share the already-observed exit, even after cancellation.
        barrier.markExited()
        #expect(await barrier.wait(timeout: 0))
    }

    @Test func shutdownWaitHasABoundAndLateExitRemainsObservable() async {
        let barrier = ProcessExitBarrier()
        #expect(await barrier.wait(timeout: 0.02) == false)
        barrier.markExited()
        #expect(await barrier.wait(timeout: 0))
    }

    @Test func concurrentShutdownWaitersObserveTheSameExit() async throws {
        let barrier = ProcessExitBarrier()
        async let first = barrier.wait(timeout: 1)
        async let second = barrier.wait(timeout: 1)
        try await Task.sleep(for: .milliseconds(20))
        barrier.markExited()
        let results = await (first, second)
        #expect(results.0)
        #expect(results.1)
    }

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

    @Test func cancelledLoginReportsRealCleanupFailureWithoutCancellationStackText() async throws {
        let manager = RefusingTemporaryDeletion()
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: true), fileManager: manager)
        defer { fixture.clean() }
        await fixture.model.start()
        let originalID = fixture.model.activeAccountID
        fixture.model.addAccount()
        let home = await fixture.client.waitForLogin(1)
        manager.rejectRemoval(of: home)
        await fixture.model.cancelAddingAccountAndWait()
        let message = fixture.model.visibleError?.message ?? ""
        #expect(message.hasPrefix("Sign-in was cancelled, but the incomplete account profile could not be removed: "))
        #expect(message.contains("Synthetic temporary-profile access denied."))
        #expect(!message.contains("CancellationError"))
        #expect(!fixture.model.isAddingAccount)
        #expect(FileManager.default.fileExists(atPath: home.path))
        #expect(fixture.model.accounts.count == 1 && fixture.model.activeAccountID == originalID)
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func shutdownTimeoutDoesNotDeleteFilesTheLoginProcessMayStillOwn() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: true,
            cancellationFailure: .processTerminationTimedOut))
        defer { fixture.clean() }
        await fixture.model.start()
        fixture.model.addAccount()
        let home = await fixture.client.waitForLogin(1)
        await fixture.model.cancelAddingAccountAndWait()
        #expect(fixture.model.visibleError?.message == CodexClientError.processTerminationTimedOut.localizedDescription)
        #expect(FileManager.default.fileExists(atPath: home.appendingPathComponent("auth.json").path))
        #expect(fixture.model.accounts.count == 1)
        #expect(!fixture.model.isAddingAccount)
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func reauthorizationCleanupFailurePreservesSavedAccountAndReportsSavedOutcome() async throws {
        let manager = RefusingTemporaryDeletion()
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false,
            accountID: "active-cancellation-fixture"), fileManager: manager)
        defer { fixture.clean() }
        await fixture.model.start()
        let originalID = fixture.model.activeAccountID
        fixture.model.addAccount()
        let staging = await fixture.client.waitForLogin(1)
        manager.rejectRemoval(of: staging)
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        #expect(fixture.model.accounts.count == 1 && fixture.model.accounts.first?.id == originalID)
        #expect(fixture.model.visibleError?.message.hasPrefix("The account was saved, but the incomplete account profile could not be removed: ") == true)
        #expect(FileManager.default.fileExists(atPath: staging.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
        let savedID = try #require(originalID)
        let savedHome = await fixture.store.profileHome(id: savedID)
        #expect(try Data(contentsOf: savedHome.appendingPathComponent("auth.json")) == Data(contentsOf: staging.appendingPathComponent("auth.json")))
    }

    @Test func restoringSelectedAccountRejectsADifferentAuthorizedIdentity() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false))
        defer { fixture.clean() }
        await fixture.model.start()
        let target = try #require(fixture.model.activeAccountID)
        let saved = await fixture.store.profileHome(id: target)
        let originalSaved = try Data(contentsOf: saved.appendingPathComponent("auth.json"))
        fixture.model.addAccount(replacingAccountID: target)
        let staging = await fixture.client.waitForLogin(1)
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        #expect(fixture.model.visibleError?.message == CodexClientError.authorizedAccountMismatch.localizedDescription)
        #expect(try await fixture.store.loadRegistry().accounts.count == 1)
        #expect(try Data(contentsOf: saved.appendingPathComponent("auth.json")) == originalSaved)
        #expect(!FileManager.default.fileExists(atPath: staging.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func deletedRestoreTargetIsNotRecreatedAfterBrowserAuthorization() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false,
            accountID: "active-cancellation-fixture"))
        defer { fixture.clean() }
        await fixture.model.start()
        let target = try #require(fixture.model.activeAccountID)
        fixture.model.addAccount(replacingAccountID: target)
        let staging = await fixture.client.waitForLogin(1)
        try await fixture.store.removeAccount(id: target)
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        #expect(fixture.model.visibleError?.message == AccountStoreError.profileNotFound.localizedDescription)
        #expect(try await fixture.store.loadRegistry().accounts.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: staging.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func restoreTargetIsRecheckedAtomicallyAtTheStoreCommitBoundary() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false))
        defer { fixture.clean() }
        await fixture.model.start()
        let target = try #require(fixture.model.activeAccountID)
        let original = try await fixture.store.profile(id: target)
        let renewal = AccountProfile(id: UUID(), displayName: "Renewal", email: original.email,
            accountID: original.accountID, createdAt: Date())
        let staging = try await fixture.store.createProfileDirectory(id: renewal.id)
        try fixture.activeBytes.write(to: staging.appendingPathComponent("auth.json"))
        // Simulate deletion after the controller has checked the target but before commit.
        try await fixture.store.removeAccount(id: target)
        await #expect(throws: AccountStoreError.profileNotFound) {
            try await fixture.store.addProfile(renewal, replacingAccountID: target)
        }
        try await fixture.store.discardUnregisteredProfile(id: renewal.id)
        #expect(try await fixture.store.loadRegistry().accounts.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: staging.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func restoringSelectedIdentityUpdatesTheExistingAccountInPlace() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false,
            accountID: "active-cancellation-fixture"))
        defer { fixture.clean() }
        await fixture.model.start()
        let target = try #require(fixture.model.activeAccountID)
        fixture.model.addAccount(replacingAccountID: target)
        let staging = await fixture.client.waitForLogin(1)
        let renewedBytes = try Data(contentsOf: staging.appendingPathComponent("auth.json"))
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        let saved = await fixture.store.profileHome(id: target)
        #expect(fixture.model.visibleError == nil)
        #expect(fixture.model.accounts.count == 1 && fixture.model.accounts.first?.id == target)
        #expect(try Data(contentsOf: saved.appendingPathComponent("auth.json")) == renewedBytes)
        #expect(!FileManager.default.fileExists(atPath: staging.path))
        #expect(try Data(contentsOf: fixture.activeAuth) == fixture.activeBytes)
    }

    @Test func ordinaryAdditionWithoutARestoreTargetCanSaveADifferentAccount() async throws {
        let fixture = try CancellationFixture(client: ControlledLoginClient(cooperative: false))
        defer { fixture.clean() }
        await fixture.model.start()
        fixture.model.addAccount(replacingAccountID: nil)
        let home = await fixture.client.waitForLogin(1)
        await fixture.client.completeLogin()
        await waitUntilIdle(fixture.model)
        #expect(fixture.model.visibleError == nil)
        #expect(fixture.model.accounts.count == 2)
        #expect(FileManager.default.fileExists(atPath: home.appendingPathComponent("auth.json").path))
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

    init(client: ControlledLoginClient, fileManager: FileManager = .default) throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("switcher-cancel-test-\(UUID())")
        let active = root.appendingPathComponent("active")
        try FileManager.default.createDirectory(at: active, withIntermediateDirectories: true)
        activeAuth = active.appendingPathComponent("auth.json")
        try activeBytes.write(to: activeAuth)
        store = AccountStore(baseURL: root.appendingPathComponent("store"), activeHomeURL: active, fileManager: fileManager)
        self.client = client
        model = AccountController(store: store, codex: client,
            switchService: SwitchService(desktop: CancellationDesktop(), store: store, codex: client))
    }

    func clean() { try? FileManager.default.removeItem(at: root) }
}

private actor ControlledLoginClient: AccountClient {
    private let cooperative: Bool
    private let cancellationFailure: CodexClientError?
    private let accountID: String?
    private var homes: [URL] = []
    private var startedWaiters: [(Int, CheckedContinuation<URL, Never>)] = []
    private var completion: CheckedContinuation<Void, Never>?
    var loginCount: Int { homes.count }

    init(cooperative: Bool, cancellationFailure: CodexClientError? = nil, accountID: String? = nil) {
        self.cooperative = cooperative
        self.cancellationFailure = cancellationFailure
        self.accountID = accountID
    }

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
        let loginID = accountID ?? "login-fixture-\(number)"
        try Data("{\"tokens\":{\"account_id\":\"\(loginID)\",\"access_token\":\"synthetic-login-\(number)\"}}".utf8)
            .write(to: profileHome.appendingPathComponent("auth.json"))
        homes.append(profileHome)
        if cooperative {
            notifyStarted()
            do { try await Task.sleep(for: .seconds(600)) }
            catch {
                if let cancellationFailure { throw cancellationFailure }
                throw error
            }
        } else {
            await withCheckedContinuation { continuation in
                completion = continuation
                notifyStarted()
            }
        }
        return AccountIdentity(accountID: loginID, email: "login\(number)@example.test")
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

private final class RefusingTemporaryDeletion: FileManager, @unchecked Sendable {
    private let lock = NSLock()
    private var denied: URL?
    func rejectRemoval(of path: URL) { lock.lock(); denied = path; lock.unlock() }
    override func removeItem(at url: URL) throws {
        lock.lock(); let rejected = url == denied; lock.unlock()
        if rejected {
            throw NSError(domain: NSCocoaErrorDomain, code: CocoaError.fileWriteNoPermission.rawValue,
                userInfo: [NSLocalizedDescriptionKey: "Synthetic temporary-profile access denied."])
        }
        try super.removeItem(at: url)
    }
}

private struct CancellationDesktop: DesktopControlling {
    func closeDesktop() async throws {}
    func reopenDesktop() async throws {}
}
