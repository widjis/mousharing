import Foundation
import CryptoKit
import Security
import Darwin

let nativePort: UInt16 = 47832
let discoveryPort: UInt16 = 47833
let maxNativeFrame = 3 * 1024 * 1024
let maxClipboardBytes = 256 * 1024
func randomBytes(_ count: Int) -> Data { var bytes = [UInt8](repeating: 0, count: count); precondition(SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess); return Data(bytes) }
func jsonData(_ object: [String: Any]) throws -> Data { try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) }
func jsonObject(_ data: Data) throws -> [String: Any] { guard let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw NativeError.message("Pesan perangkat tidak valid.") }; return value }
func encoded(_ data: Data) -> String { data.base64EncodedString() }
func decoded(_ value: Any?, count: Int? = nil) throws -> Data { guard let string = value as? String, let data = Data(base64Encoded: string), count == nil || data.count == count, data.base64EncodedString() == string else { throw NativeError.message("Data pairing tidak valid.") }; return data }
struct PairingInvite: Codable {
    var v: Int = 1
    var id: String
    var name: String
    var port: UInt16 = nativePort
    var key: String
    var addresses: [String]
    var code: String { "MS1." + (try! JSONEncoder().encode(self)).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    static func parse(_ raw: String) throws -> PairingInvite {
        let code = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard code.hasPrefix("MS1."), code.utf8.count < 8192 else { throw NativeError.message("Tempel undangan Mousharing lengkap dari master.") }
        var base = String(code.dropFirst(4)).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/"); base += String(repeating: "=", count: (4 - base.count % 4) % 4)
        guard let data = Data(base64Encoded: base) else { throw NativeError.message("Undangan tidak terbaca. Salin ulang dari master.") }
        let invite = try JSONDecoder().decode(Self.self, from: data)
        guard invite.v == 1, canonicalUUID(invite.id), !invite.name.isEmpty, invite.name.count <= 128, invite.port > 0 else { throw NativeError.message("Versi undangan tidak didukung.") }
        _ = try decoded(invite.key, count: 32)
        return invite
    }
}
struct SessionCipher {
    let key: SymmetricKey
    let direction: String
    var sendSequence: UInt64 = 0
    var receiveSequence: UInt64 = 0
    static func seal(_ payload: [String: Any], key: SymmetricKey, aad: String, sequence: UInt64) throws -> [String: Any] {
        let box = try AES.GCM.seal(jsonData(payload), using: key, authenticating: Data(aad.utf8))
        return ["seq": sequence, "nonce": encoded(Data(box.nonce)), "ciphertext": encoded(box.ciphertext), "tag": encoded(box.tag)]
    }
    static func open(_ frame: [String: Any], key: SymmetricKey, aad: String, sequence: UInt64) throws -> [String: Any] {
        guard let seq = integer(frame["seq"]), seq >= 0, UInt64(seq) == sequence else { throw NativeError.message("Urutan pesan koneksi tidak valid.") }
        let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: decoded(frame["nonce"], count: 12)), ciphertext: decoded(frame["ciphertext"]), tag: decoded(frame["tag"], count: 16))
        return try jsonObject(AES.GCM.open(box, using: key, authenticating: Data(aad.utf8)))
    }
    mutating func seal(_ payload: [String: Any]) throws -> [String: Any] { guard sendSequence < UInt64(Int64.max) else { throw NativeError.message("Batas sesi tercapai. Hubungkan ulang.") }; sendSequence += 1; return try Self.seal(payload, key: key, aad: "mousharing-native-v1:\(direction):\(sendSequence)", sequence: sendSequence) }
    mutating func open(_ frame: [String: Any]) throws -> [String: Any] { guard receiveSequence < UInt64(Int64.max) else { throw NativeError.message("Batas sesi tercapai.") }; let next = receiveSequence + 1; let payload = try Self.open(frame, key: key, aad: "mousharing-native-v1:\(direction):\(next)", sequence: next); receiveSequence = next; return payload }
    static func derive(secret: Data, challenge: Data, clientNonce: Data, direction: String) -> SymmetricKey { HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret), salt: Data(SHA256.hash(data: challenge + clientNonce)), info: Data("mousharing-native-v1:\(direction)".utf8), outputByteCount: 32) }
}
struct ScreenRect {
    var x: Double = 0; var y: Double = 0; var width: Double; var height: Double
    var right: Double { x + width - 1 }; var bottom: Double { y + height - 1 }
    func clamp(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: max(self.x, min(right, x)), y: max(self.y, min(bottom, y))) }
    func ratio(_ side: ScreenSide, _ p: CGPoint) -> Double { max(0, min(1, (side == .left || side == .right) ? (p.y-y)/max(1,height-1) : (p.x-x)/max(1,width-1))) }
    func point(_ side: ScreenSide, _ ratio: Double, inset: Double = 12) -> CGPoint { let r = max(0, min(1,ratio)); var p = CGPoint(x: x+r*(width-1), y: y+r*(height-1)); switch side { case .left: p.x=x+inset; case .right: p.x=right-inset; case .top: p.y=y+inset; case .bottom: p.y=bottom-inset }; return clamp(p.x,p.y) }
    func crossed(_ side: ScreenSide, _ p: CGPoint, _ dx: Double, _ dy: Double) -> Bool { switch side { case .left: dx < 0 && p.x <= x+1; case .right: dx > 0 && p.x >= right-1; case .top: dy < 0 && p.y <= y+1; case .bottom: dy > 0 && p.y >= bottom-1 } }
}
func localAddresses() -> [String] {
    var list: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&list) == 0 else { return [] }; defer { freeifaddrs(list) }
    var addresses: [String] = []; var cursor=list
    while let item=cursor { defer { cursor=item.pointee.ifa_next }; guard let address=item.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET), item.pointee.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }; var host=[CChar](repeating:0,count:Int(NI_MAXHOST)); if getnameinfo(address,socklen_t(address.pointee.sa_len),&host,socklen_t(host.count),nil,0,NI_NUMERICHOST) == 0 { addresses.append(String(cString:host)) } }
    return addresses
}

func canonicalUUID(_ text:String) -> Bool { UUID(uuidString:text)?.uuidString.lowercased() == text }
func integer(_ raw:Any?) -> Int64? { guard let n=raw as? NSNumber,CFGetTypeID(n) != CFBooleanGetTypeID(),!String(cString:n.objCType).contains("f"),!String(cString:n.objCType).contains("d"),n.int64Value >= 0,n.decimalValue <= Decimal(Int64.max) else { return nil }; return n.int64Value }
func boolean(_ raw:Any?) -> Bool? { guard let n=raw as? NSNumber,CFGetTypeID(n)==CFBooleanGetTypeID() else { return nil };return n.boolValue }
