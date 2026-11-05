import Foundation
import CryptoKit
import Darwin
import Network

enum ProtocolSelfTest {
    static func run(path:String) throws {
        let fixtures=try jsonObject(Data(contentsOf:URL(fileURLWithPath:path)))
        let secret=try decoded(fixtures["keyBase64"],count:32), challenge=try decoded(fixtures["challengeBase64"],count:32), nonce=try decoded(fixtures["clientNonceBase64"],count:32)
        let c2s=SessionCipher.derive(secret:secret,challenge:challenge,clientNonce:nonce,direction:"c2s"),s2c=SessionCipher.derive(secret:secret,challenge:challenge,clientNonce:nonce,direction:"s2c")
        func hex(_ data:Data)->String { data.map { String(format:"%02x",$0) }.joined() }
        guard hex(c2s.withUnsafeBytes { Data($0) }) == fixtures["c2sKeyHex"] as? String,hex(s2c.withUnsafeBytes { Data($0) }) == fixtures["s2cKeyHex"] as? String else { throw NativeError.message("HKDF fixture mismatch") }
        guard let records=fixtures["records"] as? [String:[String:Any]] else { throw NativeError.message("Fixtures missing") }
        for (name,record) in records {
            guard let frame=record["envelope"] as? [String:Any],let aad=record["aad"] as? String,let seq=integer(frame["seq"]) else { throw NativeError.message("Fixture envelope invalid") }
            let key=name.hasPrefix("c2s") ? c2s:name.hasPrefix("s2c") ? s2c:SymmetricKey(data:secret)
            _ = try SessionCipher.open(frame,key:key,aad:aad,sequence:UInt64(seq))
            let raw=try decoded(record["plaintextBase64"])
            let box=try AES.GCM.seal(raw,using:key,nonce:AES.GCM.Nonce(data:decoded(frame["nonce"],count:12)),authenticating:Data(aad.utf8))
            guard encoded(box.ciphertext)==frame["ciphertext"] as? String,encoded(box.tag)==frame["tag"] as? String else { throw NativeError.message("AES-GCM fixture mismatch: \(name)") }
        }
        guard let invite=fixtures["invite"] as? String else { throw NativeError.message("Invite fixture missing") }; _ = try PairingInvite.parse(invite)
        guard integer(NSNumber(value:true)) == nil,integer(NSNumber(value:1.5)) == nil else { throw NativeError.message("Numeric validation failed") }
        try settingsLoadRegression()
    }
    private static func settingsLoadRegression() throws {
        let folder=FileManager.default.temporaryDirectory.appendingPathComponent("mousharing-settings-test-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true)
        let file=folder.appendingPathComponent("settings.json")
        defer { try? FileManager.default.removeItem(at:folder);_ = AppSettings.load(from:file) }
        let initial=AppSettings.load(from:file)
        guard canonicalUUID(initial.deviceID),AppSettings.staticLoadFailure == nil else { throw NativeError.message("Missing settings must initialize normally") }
        let invalid=Data("{corrupt-settings".utf8)
        try invalid.write(to:file)
        let blocked=AppSettings.load(from:file)
        guard AppSettings.staticLoadFailure != nil,blocked.deviceID.isEmpty else { throw NativeError.message("Corrupt settings did not fail closed") }
        var refusedSave=false
        do { try blocked.save(to:file) } catch { refusedSave=true }
        guard refusedSave,try Data(contentsOf:file)==invalid else { throw NativeError.message("Corrupt settings were overwritten") }
        _ = AppSettings.load(from:folder)
        guard AppSettings.staticLoadFailure != nil else { throw NativeError.message("Unreadable settings did not fail closed") }
        var known=AppSettings();known.deviceID="11111111-1111-4111-8111-111111111111";known.trustedPeerID="22222222-2222-4222-8222-222222222222";known.clientKeyAccount="client-public-test-account"
        try JSONEncoder().encode(known).write(to:file)
        let restored=AppSettings.load(from:file)
        guard AppSettings.staticLoadFailure == nil,restored.deviceID==known.deviceID,restored.trustedPeerID==known.trustedPeerID,restored.clientKeyAccount==known.clientKeyAccount else { throw NativeError.message("Restored settings lost identity or trust") }
    }

}
@main enum NativeEntryPoint {
    private static var testSession:NativeSession?
    static func protocolClient(host:String,port:UInt16,inviteCode:String) throws -> Never {
        let invite=try PairingInvite.parse(inviteCode)
        var settings=AppSettings();settings.deviceID="22222222-2222-4222-8222-222222222222";settings.deviceName="Swift fixture client";settings.role = .client;settings.clipboardEnabled=true
        let queue=DispatchQueue(label:"com.widjis.mousharing.protocol-test")
        let tcp=NWProtocolTCP.Options();tcp.noDelay=true
        let connection=NWConnection(host:NWEndpoint.Host(host),port:NWEndpoint.Port(rawValue:port)!,using:NWParameters(tls:nil,tcp:tcp))
        let session=NativeSession(connection:connection,queue:queue,server:false,identity:settings,secret:try decoded(invite.key,count:32),expectedMaster:invite.id)
        testSession=session
        session.onReady={ id,_,clipboard in guard id==invite.id,clipboard else { fputs("Protocol client capability mismatch\n",stderr);exit(1) };session.send(["type":"clipboard","text":"Swift native loopback ✅","clock":1,"origin":settings.deviceID]) }
        session.onPayload={ payload in guard payload["type"] as? String == "clipboard",payload["text"] as? String == "CROSS-RUNTIME SERVER ✅\nline 2",integer(payload["clock"]) == 2,payload["origin"] as? String == invite.id else { fputs("Protocol client payload mismatch\n",stderr);exit(1) };print("PASS native Swift TCP handshake + authenticated bidirectional clipboard payload (no hooks/OS clipboard)");exit(0) }
        session.onClose={ _ in fputs("Protocol client disconnected before completion\n",stderr);exit(1) }
        session.start()
        queue.asyncAfter(deadline:.now()+15) { fputs("Protocol client timeout\n",stderr);exit(1) }
        dispatchMain()
    }
    static func main() {
        if CommandLine.arguments.count>2 && CommandLine.arguments[1] == "--self-test" { do { try ProtocolSelfTest.run(path:CommandLine.arguments[2]);print("PASS native CryptoKit fixtures + settings fail-closed regressions");exit(0) } catch { fputs("Protocol self-test failed: \(error.localizedDescription)\n",stderr);exit(1) } }
        if CommandLine.arguments.count == 5 && CommandLine.arguments[1] == "--protocol-test-client" {
            guard let port=UInt16(CommandLine.arguments[3]),port>0 else { fputs("Invalid protocol test port\n",stderr);exit(1) }
            do { try protocolClient(host:CommandLine.arguments[2],port:port,inviteCode:CommandLine.arguments[4]) } catch { fputs("Protocol test failed: \(error.localizedDescription)\n",stderr);exit(1) }
        }
        MousharingMacApp.main()
    }
}
