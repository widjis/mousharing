import Foundation
import Security

enum DeviceRole: String, Codable, CaseIterable, Identifiable {
    case master, client
    var id: String { rawValue }
    var title: String { self == .master ? "Bagikan mouse & keyboard saya" : "Gunakan perangkat lain" }
    var detail: String { self == .master ? "Mac ini menjadi master. Mouse dan keyboard Mac mengontrol kedua layar." : "Mac ini menerima mouse dan keyboard dari master." }
}
enum ScreenSide: String, Codable, CaseIterable, Identifiable {
    case left, right, top, bottom
    var id: String { rawValue }
    var title: String { switch self { case .left: "Kiri"; case .right: "Kanan"; case .top: "Atas"; case .bottom: "Bawah" } }
    var opposite: ScreenSide { switch self { case .left: .right; case .right: .left; case .top: .bottom; case .bottom: .top } }
}
struct AppSettings: Codable {
    var deviceID = UUID().uuidString.lowercased()
    var deviceName = Host.current().localizedName ?? "Mac saya"
    var role = DeviceRole.master
    var peerHost = ""
    var side = ScreenSide.right
    var clipboardEnabled = true
    var trustedPeerID: String?
    var pairedMasterID: String?
    var pairedMasterName: String?
    static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Mousharing/settings.json")
    static func load() -> AppSettings { (try? JSONDecoder().decode(Self.self, from: Data(contentsOf: url))) ?? Self() }
    func save() throws { try FileManager.default.createDirectory(at: Self.url.deletingLastPathComponent(), withIntermediateDirectories: true); try JSONEncoder().encode(self).write(to: Self.url, options: [.atomic]) }
}
struct SecretStore {
    static func read(account: String = "master") throws -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.widjis.mousharing.native", kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        let status=SecItemCopyMatching(query as CFDictionary,&result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess,let data=result as? Data else { throw NativeError.message("Keychain tidak dapat dibaca. Buka kunci Mac dan izinkan akses Mousharing; pairing lama tetap dipertahankan.") }
        return data
    }
    static func write(_ data: Data, account: String = "master") throws {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.widjis.mousharing.native", kSecAttrAccount as String: account]
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecItemNotFound {
            var item = query; item[kSecValueData as String] = data; item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw NativeError.message("Tidak dapat menyimpan pairing di Keychain.") }
        } else if update != errSecSuccess { throw NativeError.message("Keychain tidak tersedia. Buka kunci Mac lalu coba lagi.") }
    }
}
enum NativeError: LocalizedError { case message(String); var errorDescription: String? { if case .message(let text) = self { return text }; return nil } }
