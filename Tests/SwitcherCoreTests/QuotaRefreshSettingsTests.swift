import Foundation
import Testing
@testable import SwitcherCore

struct QuotaRefreshSettingsTests {
    @Test func missingIntervalUsesFiveMinutesWithoutChangingOldPreferences() throws {
        #expect(AppSettings.default.quotaRefreshIntervalMinutes == 5)
        let legacy = Data(#"{"language":"simplifiedChinese","showsMenuBarPercentage":false,"autoResetEnabled":true}"#.utf8)
        let settings = try JSONDecoder().decode(AppSettings.self, from: legacy)
        #expect(settings.quotaRefreshIntervalMinutes == 5)
        #expect(settings.language == .simplifiedChinese)
        #expect(!settings.showsMenuBarPercentage)
        #expect(settings.autoResetEnabled)
        #expect(try JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8)).quotaRefreshIntervalMinutes == 5)
    }

    @Test(arguments: [0, 1, 2, 5, 10, 15, 1440])
    func validIntervalsPersistAndRoundTrip(minutes: Int) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("quota-settings-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let store = AccountStore(baseURL: root, activeHomeURL: root.appendingPathComponent("active"))
        let settings = AppSettings(language: .english, showsMenuBarPercentage: false,
            autoResetEnabled: true, quotaRefreshIntervalMinutes: minutes)
        try await store.saveSettings(settings)
        let reopened = AccountStore(baseURL: root, activeHomeURL: root.appendingPathComponent("active"))
        #expect(try await reopened.loadSettings() == settings)
        #expect(try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(settings)) == settings)
    }

    @Test(arguments: ["-1", "1441", "1.5", "\"5\"", "true"])
    func invalidSerializedIntervalsAreRejected(raw: String) throws {
        let bytes = Data("{\"quotaRefreshIntervalMinutes\":\(raw)}".utf8)
        #expect(throws: (any Error).self) { try JSONDecoder().decode(AppSettings.self, from: bytes) }
    }

    @Test(arguments: [-1, 1441, Int.max])
    func invalidSaveLeavesPreviouslySavedPreferencesIntact(minutes: Int) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("quota-settings-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let store = AccountStore(baseURL: root, activeHomeURL: root.appendingPathComponent("active"))
        let saved = AppSettings(language: .simplifiedChinese, quotaRefreshIntervalMinutes: 15)
        try await store.saveSettings(saved)
        let before = try Data(contentsOf: root.appendingPathComponent("settings.json"))
        var invalid = saved
        invalid.quotaRefreshIntervalMinutes = minutes
        await #expect(throws: QuotaRefreshSettingsError.self) { try await store.saveSettings(invalid) }
        #expect(try Data(contentsOf: root.appendingPathComponent("settings.json")) == before)
        #expect(try await store.loadSettings() == saved)
    }
}
