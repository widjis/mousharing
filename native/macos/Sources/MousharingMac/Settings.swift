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
    var clientKeyAccount: String?
    static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Mousharing/settings.json")
    private(set) static var staticLoadFailure:String?
    static func load(from location:URL = Self.url) -> AppSettings {
        staticLoadFailure=nil
        let data:Data
        do { data=try Data(contentsOf:location) }
        catch {
            if let failure=error as? CocoaError,failure.code == .fileReadNoSuchFile { return Self() }
            staticLoadFailure="Konfigurasi tidak dapat dibaca. File tetap dipertahankan: \(location.path). Perbaiki akses file lalu buka ulang Mousharing."
            return Self(deviceID:"",deviceName:"Konfigurasi belum siap")
        }
        do { return try JSONDecoder().decode(Self.self,from:data) }
        catch {
            staticLoadFailure="Konfigurasi tidak valid. File tetap dipertahankan: \(location.path). Perbaiki atau pulihkan file tersebut lalu buka ulang Mousharing."
            return Self(deviceID:"",deviceName:"Konfigurasi belum siap")
        }
    }
    func save(to location:URL = Self.url) throws {
        if let failure=Self.staticLoadFailure { throw NativeError.message(failure) }
        try FileManager.default.createDirectory(at:location.deletingLastPathComponent(),withIntermediateDirectories:true)
        try JSONEncoder().encode(self).write(to:location,options:[.atomic])
    }

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
