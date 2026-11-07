import Foundation

// The same ordered handoff and bounded restoration run on both platforms.

public protocol DesktopControlling: Sendable {
    func closeDesktop() async throws
    func reopenDesktop() async throws
}

public protocol CodexIdentityReading: Sendable {
    func readIdentity(profileHome: URL) async throws -> AccountIdentity
}

public protocol SwitchServicing: Sendable {
    func switchAccount(to targetID: UUID) async throws
}

public struct SwitchService: SwitchServicing {
    public let desktop: any DesktopControlling
    public let store: any AccountStoring
    public let codex: any CodexIdentityReading

    public init(desktop: any DesktopControlling, store: any AccountStoring, codex: any CodexIdentityReading) {
        self.desktop = desktop; self.store = store; self.codex = codex
    }

    public func switchAccount(to targetID: UUID) async throws {
        let target: AccountProfile
        let originalActiveID: UUID?
        let originalProfile: AccountProfile?
        let removedCredential: Data?
        do {
            target = try await store.profile(id: targetID)
        } catch {
            throw OperationError.stage(.activateTargetCredential, error)
        }

        do {
            let registry = try await store.loadRegistry()
            originalActiveID = registry.activeAccountID
            if let activeID = registry.activeAccountID {
                guard let profile = registry.accounts.first(where: { $0.id == activeID }) else {
                    throw AccountStoreError.activeProfileMissing
                }
                originalProfile = profile
                removedCredential = nil
            } else {
                if await store.activeCredentialExists() {
                    removedCredential = try await store.removedActiveCredentialSnapshot()
                } else {
                    removedCredential = nil
                }
                originalProfile = nil
            }
        } catch {
            throw OperationError.stage(.saveCurrentCredential, error)
        }

        do {
            try await desktop.closeDesktop()
        } catch {
            throw OperationError.stage(.closeDesktop, error)
        }

        do {
            if let originalProfile {
                let identity = try await codex.readIdentity(profileHome: await store.activeCodexHome())
                guard identity.matches(originalProfile) else {
                    throw AccountStoreError.activeCredentialMismatch
                }
                try await store.saveCurrentCredential()
            } else if let removedCredential {
                guard try await store.removedActiveCredentialSnapshot() == removedCredential else {
                    throw AccountStoreError.activeCredentialMismatch
                }
            } else if await store.activeCredentialExists() {
                throw AccountStoreError.activeCredentialMismatch
            }
        } catch {
            throw OperationError.stage(.saveCurrentCredential, error)
        }

        do {
            try await store.activateTargetCredential(id: targetID)
        } catch {
            throw OperationError.stage(.activateTargetCredential, error)
        }

        do {
            let identity = try await codex.readIdentity(profileHome: await store.activeCodexHome())
            guard identity.matches(target) else {
                throw CodexClientError.identityUnavailable
            }
        } catch {
            throw await restoringOriginalCredential(
                originalActiveID: originalActiveID,
                removedCredential: removedCredential,
                replacingProfileID: targetID,
                failedStage: .verifyTargetIdentity,
                originalError: error
            )
        }

        do {
            try await store.commitActiveAccountID(targetID)
        } catch {
            throw await restoringOriginalCredential(
                originalActiveID: originalActiveID,
                removedCredential: removedCredential,
                replacingProfileID: targetID,
                failedStage: .commitActiveAccountID,
                originalError: error
            )
        }

        do {
            try await desktop.reopenDesktop()
        } catch {
            throw OperationError.stage(.reopenDesktop, error)
        }
    }

    private func restoringOriginalCredential(
        originalActiveID: UUID?,
        removedCredential: Data?,
        replacingProfileID: UUID,
        failedStage: SwitchStage,
        originalError: any Error
    ) async -> OperationError {
        do {
            if let originalActiveID {
                try await store.restoreActiveCredential(id: originalActiveID)
            } else if let removedCredential {
                try await store.restoreRemovedActiveCredential(removedCredential, replacingProfileID: replacingProfileID)
            } else {
                try await store.clearActiveCredential()
            }
            return OperationError.stage(failedStage, originalError)
        } catch let restorationError {
            return OperationError(
                stage: failedStage,
                titleKey: "switch_failed",
                messageKey: nil,
                message: """
                \(originalError.localizedDescription) Restoring the previous credential also failed: \
                \(restorationError.localizedDescription)
                """,
                underlyingDescription: """
                \(String(describing: originalError)); restoration: \
                \(String(describing: restorationError))
                """
            )
        }
    }
}
