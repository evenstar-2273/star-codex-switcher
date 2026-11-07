import Foundation
#if canImport(Darwin)
import Darwin
#endif
#if os(Windows)
import SwitcherPlatform
#endif
#if canImport(CryptoKit)
import CryptoKit
#endif

public protocol AccountStoring: Sendable {
    func loadRegistry() async throws -> AccountRegistry
    func profile(id: UUID) async throws -> AccountProfile
    func activeCredentialExists() async -> Bool
    func clearActiveCredential() async throws
    func activeCodexHome() async -> URL
    func saveCurrentCredential() async throws
    func activateTargetCredential(id: UUID) async throws
    func restoreActiveCredential(id: UUID) async throws
    func removedActiveCredentialSnapshot() async throws -> Data
    func restoreRemovedActiveCredential(_ credential: Data, replacingProfileID: UUID) async throws
    func commitActiveAccountID(_ id: UUID) async throws
}

extension AccountStoring {
    public func removedActiveCredentialSnapshot() async throws -> Data { throw AccountStoreError.activeProfileMissing }
    public func restoreRemovedActiveCredential(_ credential: Data, replacingProfileID: UUID) async throws { throw AccountStoreError.activeProfileMissing }
}

public actor AccountStore: AccountStoring {
    public let baseURL: URL
    public let activeHomeURL: URL

    private let fileManager: FileManager
    private let legacyBaseURL: URL?
    private var registry: AccountRegistry?
    private var usageCache: UsageCache?
    /// Set only when the last successful sync recognized a login deliberately removed from this tool.
    public private(set) var activeCredentialWasRemoved = false

    public init(
        baseURL: URL? = nil,
        legacyBaseURL: URL? = nil,
        activeHomeURL: URL? = nil,
        fileManager: FileManager = .default
    ) {
        self.fileManager = fileManager
        #if os(Windows)
        let applicationSupportURL = URL(fileURLWithPath: ProcessInfo.processInfo.environment["LOCALAPPDATA"]
            ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent("AppData/Local").path)
        #else
        let applicationSupportURL = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        )[0]
        #endif
        if let baseURL {
            self.baseURL = baseURL
            self.legacyBaseURL = legacyBaseURL
        } else {
            self.baseURL = applicationSupportURL.appending(
                path: "Codex Account Switcher",
                directoryHint: .isDirectory
            )
            self.legacyBaseURL = applicationSupportURL.appending(
                path: "Codex Account Switcher Lite",
                directoryHint: .isDirectory
            )
        }
        self.activeHomeURL = activeHomeURL
            ?? ProcessInfo.processInfo.environment["CODEX_HOME"].map { URL(fileURLWithPath: $0) }
            ?? fileManager.homeDirectoryForCurrentUser.appending(path: ".codex", directoryHint: .isDirectory)
    }

    private var accountsURL: URL { baseURL.appending(path: "accounts.json") }
    private var settingsURL: URL { baseURL.appending(path: "settings.json") }
    private var usageCacheURL: URL { baseURL.appending(path: "usage-cache.json") }
    private var profilesURL: URL { baseURL.appending(path: "accounts", directoryHint: .isDirectory) }
    public nonisolated var resetExpiryLogURL: URL { baseURL.appending(path: "reset-expiry-experiment.jsonl") }

    func loadResetExpiryLog() throws -> [ResetExpiryEvent] {
        try prepareDirectories()
        guard fileManager.fileExists(atPath: resetExpiryLogURL.path) else { return [] }
        return try readChecked(resetExpiryLogURL).split(separator: 0x0A).map {
            try Self.decoder.decode(ResetExpiryEvent.self, from: Data($0))
        }
    }

    func appendResetExpiryLog(_ events: [ResetExpiryEvent]) throws {
        try prepareDirectories()
        try checkPath(resetExpiryLogURL)
        if !fileManager.fileExists(atPath: resetExpiryLogURL.path) {
            try secureAtomicWrite(Data(), to: resetExpiryLogURL)
        }
        try restrictPermissions(resetExpiryLogURL, directory: false)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        var bytes = Data()
        for event in events {
            bytes.append(try encoder.encode(event))
            bytes.append(0x0A)
        }
        let handle = try FileHandle(forWritingTo: resetExpiryLogURL)
        do {
            try handle.seekToEnd()
            try handle.write(contentsOf: bytes)
            try handle.synchronize()
        } catch {
            try? handle.close()
            throw error
        }
        try handle.close()
    }

    public func loadRegistry() throws -> AccountRegistry {
        try prepareDirectories()
        if let registry { return registry }
        guard fileManager.fileExists(atPath: accountsURL.path) else {
            registry = .empty
            return .empty
        }
        var loaded = try Self.decoder.decode(AccountRegistry.self, from: readChecked(accountsURL))
        var changed = false
        for index in loaded.accounts.indices where loaded.accounts[index].accountID == nil {
            let profile = loaded.accounts[index]
            if let identity = try CredentialIdentity.read(from: profileHome(id: profile.id)) {
                if let expected = profile.email, let actual = identity.email,
                   expected.caseInsensitiveCompare(actual) != .orderedSame {
                    throw AccountStoreError.activeCredentialMismatch
                }
                loaded.accounts[index].accountID = identity.accountID
                changed = true
            }
        }
        if changed { try saveRegistry(loaded) }
        registry = loaded
        return loaded
    }

    public func loadSettings() throws -> AppSettings {
        try prepareDirectories()
        guard fileManager.fileExists(atPath: settingsURL.path) else { return .default }
        return try Self.decoder.decode(AppSettings.self, from: readChecked(settingsURL))
    }

    public func saveSettings(_ settings: AppSettings) throws {
        try AppSettings.validateQuotaRefreshInterval(settings.quotaRefreshIntervalMinutes)
        try prepareDirectories()
        try writeJSON(settings, to: settingsURL)
    }

    public func loadUsageCache() throws -> UsageCache {
        try prepareDirectories()
        if let usageCache { return usageCache }
        guard fileManager.fileExists(atPath: usageCacheURL.path) else {
            usageCache = .empty
            return .empty
        }
        let loaded = try Self.decoder.decode(UsageCache.self, from: readChecked(usageCacheURL))
        usageCache = loaded
        return loaded
    }

    public func cacheWeeklyUsage(_ usage: WeeklyUsage, profileID: UUID, fetchedAt: Date = Date()) throws {
        let registry = try loadRegistry()
        guard registry.accounts.contains(where: { $0.id == profileID }) else { return }
        var cache = try loadUsageCache()
        let entry = UsageCacheEntry(profileID: profileID, usage: usage, fetchedAt: fetchedAt)
        if let index = cache.entries.firstIndex(where: { $0.profileID == profileID }) {
            cache.entries[index] = entry
        } else {
            cache.entries.append(entry)
        }
        try saveUsageCache(cache)
    }

    public func profile(id: UUID) throws -> AccountProfile {
        let registry = try loadRegistry()
        guard let profile = registry.accounts.first(where: { $0.id == id }) else {
            throw AccountStoreError.profileNotFound
        }
        return profile
    }

    public func profileHome(id: UUID) -> URL {
        profilesURL.appending(path: id.uuidString, directoryHint: .isDirectory)
    }

    public func readSubscriptionSnapshot(profile: AccountProfile, isActive: Bool) -> SubscriptionSnapshot? {
        func read(_ path: URL) -> SubscriptionSnapshot? {
            guard let credential = try? readChecked(path) else { return nil }
            return SubscriptionSnapshotReader.read(
                credential: credential,
                expectedAccountID: profile.accountID,
                expectedEmail: profile.email
            )
        }

        let saved = read(profileHome(id: profile.id).appending(path: "auth.json"))
        guard isActive else { return saved }
        let current = read(activeHomeURL.appending(path: "auth.json"))
        guard let current else { return saved }
        guard let saved else { return current }
        return current.lastChecked >= saved.lastChecked ? current : saved
    }

    public func activeCodexHome() -> URL { activeHomeURL }

    public func activeCredentialExists() -> Bool {
        fileManager.fileExists(atPath: activeHomeURL.appending(path: "auth.json").path)
    }

    public func createProfileDirectory(id: UUID) throws -> URL {
        try prepareDirectories()
        let directory = profileHome(id: id)
        try checkPath(directory)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: false)
        try restrictPermissions(directory, directory: true)
        return directory
    }

    public func importCurrentProfile(_ profile: AccountProfile) throws {
        let source = activeHomeURL.appending(path: "auth.json")
        guard fileManager.fileExists(atPath: source.path) else {
            throw AccountStoreError.activeCredentialMissing
        }
        try importProfile(profile, credential: readChecked(source))
    }

    private func importProfile(_ profile: AccountProfile, credential: Data) throws {
        let home = try createProfileDirectory(id: profile.id)
        try secureAtomicWrite(credential, to: home.appending(path: "auth.json"))

        var current = try loadRegistry()
        let identity = AccountIdentity(accountID: profile.accountID, email: profile.email)
        current.removedAccountIdentities.removeAll { identity.matches($0) }
        current.accounts.append(profile)
        current.activeAccountID = profile.id
        try saveRegistry(current)
    }

    public func addProfile(_ profile: AccountProfile, replacingAccountID: UUID? = nil) throws {
        try Task.checkCancellation()
        let authURL = profileHome(id: profile.id).appending(path: "auth.json")
        guard fileManager.fileExists(atPath: authURL.path) else {
            throw AccountStoreError.targetCredentialMissing
        }
        try restrictPermissions(authURL, directory: false)
        var current = try loadRegistry()
        let identity = AccountIdentity(accountID: profile.accountID, email: profile.email)
        if let replacingAccountID {
            guard let target = current.accounts.first(where: { $0.id == replacingAccountID }) else {
                throw AccountStoreError.profileNotFound
            }
            guard identity.matches(target) else {
                throw CodexClientError.authorizedAccountMismatch
            }
        }
        if let existing = current.accounts.first(where: { candidate in
            replacingAccountID.map { candidate.id == $0 } ?? identity.matches(candidate)
        }) {
            // A successful browser authorization renews the same saved account in place.
            let bytes = try readChecked(authURL)
            guard let verified = try CredentialIdentity.decode(bytes), verified.matches(profile), verified.matches(existing) else {
                throw CodexClientError.identityUnavailable
            }
            let destination = profileHome(id: existing.id).appending(path: "auth.json")
            try checkPath(destination)
            let live = activeHomeURL.appending(path: "auth.json")
            if fileManager.fileExists(atPath: live.path) {
                let oldLive = try readChecked(live)
                if let oldIdentity = try CredentialIdentity.decode(oldLive), oldIdentity.matches(existing) {
                    // Persist only a fingerprint, never a second copy of the old token.
                    // A later refresh/restart must not overwrite this renewal with that exact old login.
                    try Task.checkCancellation()
                    let marker = ReauthorizationMarker(previousLive: try credentialFingerprint(oldLive),
                        savedCredential: try credentialFingerprint(bytes))
                    let markerURL = reauthorizationMarker(id: existing.id)
                    var protection: [ReauthorizationMarker] = []
                    if fileManager.fileExists(atPath: markerURL.path), fileManager.fileExists(atPath: destination.path) {
                        let savedHash = try credentialFingerprint(readChecked(destination))
                        let previous = try Self.decoder.decode([ReauthorizationMarker].self, from: readChecked(markerURL))
                        protection = Array(previous.filter {
                            $0.previousLive == marker.previousLive && $0.savedCredential == savedHash
                        }.prefix(1))
                    }
                    // Preserve the last committed renewal if a second renewal fails between writes.
                    protection.append(marker)
                    try writeJSON(protection, to: markerURL)
                }
            }
            try Task.checkCancellation()
            try secureAtomicWrite(bytes, to: destination)
            return
        }
        current.removedAccountIdentities.removeAll { identity.matches($0) }
        current.accounts.append(profile)
        // This actor owns the commit; cancellation while waiting to enter it must not save a login.
        try Task.checkCancellation()
        try saveRegistry(current)
    }

    public func discardUnregisteredProfile(id: UUID) throws {
        guard try !loadRegistry().accounts.contains(where: { $0.id == id }) else { return }
        let directory = profileHome(id: id)
        if fileManager.fileExists(atPath: directory.path) { try removeProfileDirectory(directory) }
    }

    private func registerActiveIdentity(_ identity: AccountIdentity, credential bytes: Data) throws {
        var current = try loadRegistry()
        if let profile = current.accounts.first(where: { identity.matches($0) }) {
            let destination = profileHome(id: profile.id).appending(path: "auth.json")
            if try !preserveReauthorizedCredential(profile: profile, liveBytes: bytes) {
                if try readChecked(destination) != bytes { try secureAtomicWrite(bytes, to: destination) }
            }
            if current.removedAccountIdentities.contains(where: { identity.matches($0) }) {
                current.removedAccountIdentities.removeAll { identity.matches($0) }
                try saveRegistry(current)
            }
            if current.activeAccountID != profile.id { try commitActiveAccountID(profile.id) }
        } else {
            let candidate = AccountProfile(id: UUID(), displayName: identity.suggestedDisplayName,
                email: identity.email, accountID: identity.accountID, createdAt: Date(), lastUsedAt: Date())
            try importProfile(candidate, credential: bytes)
        }
    }

    /// The live login determines the active profile. Identify and save the same credential bytes.
    public func syncActiveCredential(includeRemoved: Bool = false) throws -> AccountRegistry {
        activeCredentialWasRemoved = false
        var current = try loadRegistry()
        guard activeCredentialExists() else {
            if current.activeAccountID != nil {
                current.activeAccountID = nil
                try saveRegistry(current)
            }
            return current
        }
        let bytes = try readChecked(activeHomeURL.appending(path: "auth.json"))
        guard let identity = try CredentialIdentity.decode(bytes) else {
            throw CodexClientError.identityUnavailable
        }
        if !includeRemoved,
           !current.accounts.contains(where: { identity.matches($0) }),
           current.removedAccountIdentities.contains(where: { identity.matches($0) }) {
            if current.activeAccountID != nil {
                current.activeAccountID = nil
                try saveRegistry(current)
            }
            activeCredentialWasRemoved = true
            return current
        }
        try registerActiveIdentity(identity, credential: bytes)
        return try loadRegistry()
    }

    public func removeAccount(id: UUID) throws {
        var current = try loadRegistry()
        guard let profile = current.accounts.first(where: { $0.id == id }) else {
            throw AccountStoreError.profileNotFound
        }
        var cache = try loadUsageCache()
        if cache.entries.contains(where: { $0.profileID == id }) {
            cache.entries.removeAll(where: { $0.profileID == id })
            try saveUsageCache(cache)
        }
        let original = current
        current.accounts.removeAll(where: { $0.id == id })
        if current.activeAccountID == id { current.activeAccountID = nil }
        let identity = AccountIdentity(accountID: profile.accountID, email: profile.email)
        if !current.removedAccountIdentities.contains(where: { identity.matches($0) }) {
            current.removedAccountIdentities.append(identity)
        }
        try saveRegistry(current)
        do {
            try removeProfileDirectory(profileHome(id: id))
        } catch {
            let removalError = error
            do {
                try saveRegistry(original)
            } catch {
                throw NSError(domain: "CodexAccountSwitcher.AccountStore", code: 1, userInfo: [
                    NSLocalizedDescriptionKey: "\(removalError.localizedDescription) Restoring the account list also failed: \(error.localizedDescription)",
                ])
            }
            throw removalError
        }
    }

    public func saveCurrentCredential() throws {
        let current = try loadRegistry()
        guard let activeID = current.activeAccountID else {
            throw AccountStoreError.activeProfileMissing
        }
        guard current.accounts.contains(where: { $0.id == activeID }) else {
            throw AccountStoreError.activeProfileMissing
        }
        let source = activeHomeURL.appending(path: "auth.json")
        guard fileManager.fileExists(atPath: source.path) else {
            throw AccountStoreError.activeCredentialMissing
        }
        let bytes = try readChecked(source)
        let profile = try profile(id: activeID)
        if try !preserveReauthorizedCredential(profile: profile, liveBytes: bytes) {
            try secureAtomicWrite(bytes, to: profileHome(id: activeID).appending(path: "auth.json"))
        }
    }

    public func activateTargetCredential(id: UUID) throws {
        try installCredential(id: id)
    }

    public func clearActiveCredential() throws {
        try checkPath(activeHomeURL.appending(path: "auth.json"))
        try fileManager.removeItem(at: activeHomeURL.appending(path: "auth.json"))
    }

    public func restoreActiveCredential(id: UUID) throws {
        try installCredential(id: id)
    }

    /// Keep the unlisted login only in the switching transaction; do not recreate its saved profile.
    public func removedActiveCredentialSnapshot() throws -> Data {
        let bytes = try readChecked(activeHomeURL.appending(path: "auth.json"))
        try validateRemovedCredential(bytes)
        return bytes
    }

    public func restoreRemovedActiveCredential(_ credential: Data, replacingProfileID: UUID) throws {
        try validateRemovedCredential(credential)
        let activePath = activeHomeURL.appending(path: "auth.json")
        let currentBytes = try readChecked(activePath)
        if currentBytes == credential { return }
        _ = try profile(id: replacingProfileID)
        let installedBytes = try readChecked(profileHome(id: replacingProfileID).appending(path: "auth.json"))
        guard currentBytes == installedBytes else { throw AccountStoreError.activeCredentialMismatch }
        try secureAtomicWrite(credential, to: activePath)
    }

    private func validateRemovedCredential(_ credential: Data) throws {
        let current = try loadRegistry()
        guard current.activeAccountID == nil,
              let identity = try CredentialIdentity.decode(credential),
              !current.accounts.contains(where: { identity.matches($0) }),
              current.removedAccountIdentities.contains(where: { identity.matches($0) }) else {
            throw AccountStoreError.activeProfileMissing
        }
    }

    private func installCredential(id: UUID) throws {
        _ = try profile(id: id)
        let source = profileHome(id: id).appending(path: "auth.json")
        guard fileManager.fileExists(atPath: source.path) else {
            throw AccountStoreError.targetCredentialMissing
        }

        try checkPath(activeHomeURL)
        try fileManager.createDirectory(at: activeHomeURL, withIntermediateDirectories: true)
        let destination = activeHomeURL.appending(path: "auth.json")
        try copyCredential(from: source, to: destination)
    }

    public func commitActiveAccountID(_ id: UUID) throws {
        var current = try loadRegistry()
        guard let index = current.accounts.firstIndex(where: { $0.id == id }) else {
            throw AccountStoreError.profileNotFound
        }
        current.activeAccountID = id
        current.accounts[index].lastUsedAt = Date()
        try saveRegistry(current)
    }

    private func saveRegistry(_ value: AccountRegistry) throws {
        try writeJSON(value, to: accountsURL)
        registry = value
    }

    private func saveUsageCache(_ value: UsageCache) throws {
        try writeJSON(value, to: usageCacheURL)
        usageCache = value
    }

    private func prepareDirectories() throws {
        try checkPath(baseURL)
        try checkPath(profilesURL)
        if !fileManager.fileExists(atPath: baseURL.path),
           let legacyBaseURL,
           fileManager.fileExists(atPath: legacyBaseURL.path) {
            try checkPath(legacyBaseURL)
            try fileManager.moveItem(at: legacyBaseURL, to: baseURL)
        }
        try fileManager.createDirectory(at: baseURL, withIntermediateDirectories: true)
        try restrictPermissions(baseURL, directory: true)
        try fileManager.createDirectory(at: profilesURL, withIntermediateDirectories: true)
        try restrictPermissions(profilesURL, directory: true)
    }

    private func copyCredential(from source: URL, to destination: URL) throws {
        let bytes = try readChecked(source)
        try secureAtomicWrite(bytes, to: destination)
    }

    private func reauthorizationMarker(id: UUID) -> URL {
        profileHome(id: id).appending(path: "reauthorization-live.json")
    }

    private struct ReauthorizationMarker: Codable {
        let previousLive: String
        let savedCredential: String
    }

    private func preserveReauthorizedCredential(profile: AccountProfile, liveBytes: Data) throws -> Bool {
        let markerURL = reauthorizationMarker(id: profile.id)
        guard fileManager.fileExists(atPath: markerURL.path) else { return false }
        let markers = try Self.decoder.decode([ReauthorizationMarker].self, from: readChecked(markerURL))
        let liveHash = try credentialFingerprint(liveBytes)
        guard markers.contains(where: { $0.previousLive == liveHash }) else { return false }
        let saved = try readChecked(profileHome(id: profile.id).appending(path: "auth.json"))
        // The marker is written first. If credential replacement fails or the process exits
        // between the two writes, it must not suppress synchronization of the live login.
        let savedHash = try credentialFingerprint(saved)
        guard markers.contains(where: { $0.previousLive == liveHash && $0.savedCredential == savedHash }) else { return false }
        guard let identity = try CredentialIdentity.decode(saved), identity.matches(profile) else { return false }
        return true
    }

    private func credentialFingerprint(_ bytes: Data) throws -> String {
        #if os(Windows)
        var hash = [UInt8](repeating: 0, count: 32)
        let error = bytes.withUnsafeBytes { buffer in
            switcher_sha256(buffer.baseAddress, buffer.count, &hash)
        }
        guard error == 0 else { throw windowsError(error) }
        #elseif canImport(CryptoKit)
        let hash = Array(SHA256.hash(data: bytes))
        #else
        throw CodexClientError.identityUnavailable
        #endif
        #if os(Windows) || canImport(CryptoKit)
        return hash.map { String(format: "%02x", $0) }.joined()
        #endif
    }

    private func checkPath(_ path: URL) throws {
        #if os(Windows)
        let error = switcher_check_path(path.path)
        guard error == 0 else { throw windowsError(error) }
        #endif
    }

    private func readChecked(_ path: URL) throws -> Data {
        try checkPath(path)
        return try Data(contentsOf: path)
    }

    private func removeProfileDirectory(_ path: URL) throws {
        #if os(Windows)
        func checkTree(_ directory: URL) throws {
            try checkPath(directory)
            for child in try fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey]) {
                try checkPath(child)
                if try child.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true { try checkTree(child) }
            }
        }
        try checkTree(path)
        #endif
        try fileManager.removeItem(at: path)
    }

    private func secureAtomicWrite(_ bytes: Data, to destination: URL) throws {
        #if os(Windows)
        let error = bytes.withUnsafeBytes { buffer in
            switcher_atomic_write(destination.path, buffer.baseAddress, buffer.count)
        }
        guard error == 0 else { throw windowsError(error) }
        #else
        let temporary = destination
            .deletingLastPathComponent()
            .appending(path: "\(destination.lastPathComponent).switcher-\(UUID().uuidString).tmp")
        let descriptor = Darwin.open(
            temporary.path,
            O_WRONLY | O_CREAT | O_EXCL,
            S_IRUSR | S_IWUSR
        )
        guard descriptor >= 0 else { throw currentPOSIXError() }

        do {
            try bytes.withUnsafeBytes { rawBuffer in
                guard let baseAddress = rawBuffer.baseAddress else { return }
                var offset = 0
                while offset < rawBuffer.count {
                    let count = Darwin.write(
                        descriptor,
                        baseAddress.advanced(by: offset),
                        rawBuffer.count - offset
                    )
                    guard count > 0 else { throw currentPOSIXError() }
                    offset += count
                }
            }
            guard Darwin.fsync(descriptor) == 0 else { throw currentPOSIXError() }
            guard Darwin.close(descriptor) == 0 else { throw currentPOSIXError() }
        } catch {
            _ = Darwin.close(descriptor)
            try? fileManager.removeItem(at: temporary)
            throw error
        }

        if Darwin.rename(temporary.path, destination.path) != 0 {
            let error = currentPOSIXError()
            try? fileManager.removeItem(at: temporary)
            throw error
        }
        #endif
    }

    private func restrictPermissions(_ path: URL, directory: Bool) throws {
        #if os(Windows)
        let error = switcher_restrict_path(path.path, directory ? 1 : 0)
        guard error == 0 else { throw windowsError(error) }
        #else
        try fileManager.setAttributes([.posixPermissions: directory ? 0o700 : 0o600], ofItemAtPath: path.path)
        #endif
    }

    #if os(Windows)
    private func windowsError(_ code: UInt32) -> NSError {
        NSError(domain: "CodexAccountSwitcher.Windows", code: Int(code), userInfo: [
            NSLocalizedDescriptionKey: "Windows could not access private account storage (error \(code)).",
        ])
    }
    #else
    private func currentPOSIXError() -> POSIXError {
        POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
    }
    #endif

    private func writeJSON<T: Encodable>(_ value: T, to destination: URL) throws {
        let bytes = try Self.encoder.encode(value)
        try secureAtomicWrite(bytes, to: destination)
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()
}
