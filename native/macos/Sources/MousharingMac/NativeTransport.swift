import Foundation
import Network
import CryptoKit

final class NativeSession {
    let connection: NWConnection
    let queue: DispatchQueue
    let server: Bool
    let identity: AppSettings
    let secret: Data
    let expectedMaster: String?
    var allowedPeer: String?
    var onReady: ((String,String,Bool)->Void)?
    private var clipboardAllowed=false
    var onPayload: (([String:Any])->Void)?
    var onClose: ((String)->Void)?
    private var buffer=Data()
    private var challenge=Data()
    private var clientNonce=Data()
    private var outbound: SessionCipher?
    private var inbound: SessionCipher?
    private var stage=0
    private var timer: DispatchSourceTimer?
    private var lastReceive=Date()
    private var started=Date()
    private var closed=false
    private var pendingBytes=0
    var ready: Bool { stage == 3 }
    init(connection: NWConnection, queue: DispatchQueue, server: Bool, identity: AppSettings, secret: Data, expectedMaster: String?=nil, allowedPeer: String?=nil) { self.connection=connection; self.queue=queue; self.server=server; self.identity=identity; self.secret=secret; self.expectedMaster=expectedMaster; self.allowedPeer=allowedPeer }
    func start() {
        connection.stateUpdateHandler = { [weak self] state in guard let self else { return }; switch state { case .ready: if self.server { self.challenge=randomBytes(32); self.write(["type":"challenge","v":1,"challenge":encoded(self.challenge),"id":self.identity.deviceID,"name":self.identity.deviceName]); self.stage=1 }; self.receive(); case .failed(let error): self.close("Koneksi gagal: \(error.localizedDescription)"); case .cancelled: self.close("Koneksi ditutup."); default: break } }
        startTimer();connection.start(queue: queue)
    }
    private func startTimer() { let timer=DispatchSource.makeTimerSource(queue:queue); timer.schedule(deadline:.now()+1,repeating:1); timer.setEventHandler { [weak self] in guard let self else { return }; if self.ready { if Date().timeIntervalSince(self.lastReceive)>6 { self.close("Koneksi tidak merespons. Hubungkan ulang.") } else { self.send(["type":"ping"]) } } else if Date().timeIntervalSince(self.started)>15 { self.close("Pairing tidak selesai. Periksa undangan dan alamat master.") } }; self.timer=timer; timer.resume() }
    private func receive() { connection.receive(minimumIncompleteLength:1,maximumLength:65536) { [weak self] data,_,done,error in guard let self,!self.closed else { return }; if let data { self.buffer.append(data); if self.buffer.count>maxNativeFrame && !self.buffer.contains(10) { self.close("Pesan terlalu besar."); return }; while let newline=self.buffer.firstIndex(of:10) { let line=Data(self.buffer[..<newline]); self.buffer.removeSubrange(...newline); guard line.count+1<=maxNativeFrame else { self.close("Pesan terlalu besar."); return }; do { try self.handle(jsonObject(line)); self.lastReceive=Date() } catch { self.close("Pairing atau autentikasi pesan gagal. Periksa undangan pairing."); return } } }; if done || error != nil { self.close("Perangkat terputus.") } else { self.receive() } } }
    private func handle(_ frame:[String:Any]) throws {
        let key=SymmetricKey(data:secret)
        if stage == 0 && !server {
            guard frame["type"] as? String == "challenge", integer(frame["v"]) == 1, frame["id"] as? String == expectedMaster else { throw NativeError.message("Master berbeda dari undangan.") }
            challenge=try decoded(frame["challenge"],count:32); clientNonce=randomBytes(32)
            write(try SessionCipher.seal(["type":"hello","v":1,"challenge":encoded(challenge),"clientNonce":encoded(clientNonce),"id":identity.deviceID,"name":identity.deviceName,"clipboard":identity.clipboardEnabled],key:key,aad:"mousharing-native-v1:hello",sequence:0)); stage=2; return
        }
        if stage == 1 && server {
            let hello=try SessionCipher.open(frame,key:key,aad:"mousharing-native-v1:hello",sequence:0)
            guard hello["type"] as? String == "hello", integer(hello["v"]) == 1, try decoded(hello["challenge"],count:32) == challenge, let peer=hello["id"] as? String, canonicalUUID(peer),peer != identity.deviceID,let peerName=hello["name"] as? String,!peerName.isEmpty,peerName.count<=128, allowedPeer == nil || allowedPeer == peer else { throw NativeError.message("Perangkat belum dipercaya.") }
            clientNonce=try decoded(hello["clientNonce"],count:32)
            clipboardAllowed=identity.clipboardEnabled && boolean(hello["clipboard"]) == true
            write(try SessionCipher.seal(["type":"welcome","v":1,"challenge":encoded(challenge),"clientNonce":encoded(clientNonce),"id":identity.deviceID,"name":identity.deviceName,"clipboard":clipboardAllowed],key:key,aad:"mousharing-native-v1:welcome",sequence:0)); establish(); onReady?(peer,String((hello["name"] as? String ?? "Client").prefix(128)),clipboardAllowed); return
        }
        if stage == 2 && !server {
            let welcome=try SessionCipher.open(frame,key:key,aad:"mousharing-native-v1:welcome",sequence:0)
            guard welcome["type"] as? String == "welcome", integer(welcome["v"]) == 1, welcome["id"] as? String == expectedMaster,let peerName=welcome["name"] as? String,!peerName.isEmpty,peerName.count<=128, try decoded(welcome["challenge"],count:32) == challenge, try decoded(welcome["clientNonce"],count:32) == clientNonce else { throw NativeError.message("Pairing gagal.") }
            clipboardAllowed=identity.clipboardEnabled && boolean(welcome["clipboard"]) == true
            establish(); onReady?(expectedMaster!,String((welcome["name"] as? String ?? "Master").prefix(128)),clipboardAllowed); return
        }
        guard stage == 3, var cipher=inbound else { throw NativeError.message("Urutan pairing tidak valid.") }; let payload=try cipher.open(frame); inbound=cipher
        guard let type=payload["type"] as? String else { throw NativeError.message("Jenis pesan tidak valid.") }
        let allowed = server ? ["leave","clipboard","ping","pong"] : ["enter","move","button","key","scroll","reset","clipboard","ping","pong"]; guard allowed.contains(type) else { throw NativeError.message("Arah pesan input tidak valid.") }; if type == "ping" { send(["type":"pong"]) } else if type != "pong" { if type != "clipboard" || clipboardAllowed { onPayload?(payload) } }
    }
    private func establish() { outbound=SessionCipher(key:SessionCipher.derive(secret:secret,challenge:challenge,clientNonce:clientNonce,direction:server ? "s2c":"c2s"),direction:server ? "s2c":"c2s"); inbound=SessionCipher(key:SessionCipher.derive(secret:secret,challenge:challenge,clientNonce:clientNonce,direction:server ? "c2s":"s2c"),direction:server ? "c2s":"s2c"); stage=3 }
    func send(_ payload:[String:Any]) { guard payload["type"] as? String != "clipboard" || clipboardAllowed else { return };guard ready,!closed,var cipher=outbound else { return }; do { let frame=try cipher.seal(payload); outbound=cipher; write(frame) } catch { close("Pesan tidak dapat dikirim.") } }
    private func write(_ frame:[String:Any]) { do { var data=try jsonData(frame); guard data.count+1<=maxNativeFrame else { close("Pesan terlalu besar."); return }; data.append(10); guard pendingBytes+data.count<=4*1024*1024 else { close("Koneksi terlalu lambat. Sharing dijeda.");return };pendingBytes+=data.count;let count=data.count; connection.send(content:data,completion:.contentProcessed { [weak self] error in self?.pendingBytes-=count;if error != nil { self?.close("Pengiriman terputus.") } }) } catch { close("Pesan tidak valid.") } }
    func close(_ reason:String) { guard !closed else { return }; closed=true; timer?.cancel(); connection.cancel(); onClose?(reason); onClose=nil }
}
final class NativeService {
    let queue=DispatchQueue(label:"com.widjis.mousharing.native.network")
    private let tokenLock=NSLock()
    private var tokenValue=UUID()
    var operationID:UUID { tokenLock.lock();defer { tokenLock.unlock() };return tokenValue }
    private func newOperation() -> UUID { tokenLock.lock();defer { tokenLock.unlock() };tokenValue=UUID();return tokenValue }
    var listener:NWListener?
    var session:NativeSession?
    private var acceptedPeerID:String?
    private var retryAttempt=0
    var onState:((UUID,Bool,String,Bool)->Void)?
    var onPayload:((UUID,[String:Any])->Void)?
    var onPeer:((UUID,String)->Void)?
    private func stopOnQueue() { listener?.cancel();listener=nil;if session?.server == true { session?.send(["type":"reset"]) };session?.close("Sharing dijeda");session=nil }
    func start(settings:AppSettings,secret:Data,expectedMaster:String?) {
        let token=newOperation()
        queue.async { [weak self] in guard let self,self.operationID==token else { return };self.stopOnQueue();self.acceptedPeerID=settings.trustedPeerID;self.retryAttempt=0;do {
            let tcp=NWProtocolTCP.Options();tcp.noDelay=true;let parameters=NWParameters(tls:nil,tcp:tcp);parameters.allowLocalEndpointReuse=true
            if settings.role == .master {
                let listener=try NWListener(using:parameters,on:NWEndpoint.Port(rawValue:nativePort)!); self.listener=listener
                listener.newConnectionHandler = { [weak self] connection in guard let self,self.operationID==token else { connection.cancel();return };if self.session != nil { connection.cancel();return };self.install(connection,settings:settings,secret:secret,expectedMaster:nil,token:token) }
                listener.stateUpdateHandler = { [weak self] state in guard let self,self.operationID==token else { return };if case .failed(let error)=state { self.onState?(token,false,"Tidak dapat membuka port: \(error.localizedDescription)",false) } }
                listener.start(queue:self.queue);self.onState?(token,false,"Menunggu client terhubung",false)
            } else { let connection=NWConnection(host:NWEndpoint.Host(settings.peerHost),port:NWEndpoint.Port(rawValue:nativePort)!,using:parameters);self.install(connection,settings:settings,secret:secret,expectedMaster:expectedMaster,token:token) }
        } catch { self.onState?(token,false,error.localizedDescription,false) } }
    }
    private func install(_ connection:NWConnection,settings:AppSettings,secret:Data,expectedMaster:String?,token:UUID) {
        let session=NativeSession(connection:connection,queue:queue,server:settings.role == .master,identity:settings,secret:secret,expectedMaster:expectedMaster,allowedPeer:acceptedPeerID); self.session=session
        session.onReady={ [weak self] id,name,clipboard in guard let self,self.operationID==token else { return };self.retryAttempt=0;if settings.role == .master { self.acceptedPeerID=id };self.onPeer?(token,id);self.onState?(token,true,name,clipboard) }
        session.onPayload={ [weak self] payload in guard let self,self.operationID==token else { return };self.onPayload?(token,payload) }
        session.onClose={ [weak self,weak session] reason in guard let self,self.operationID==token,self.session === session else { return };self.session=nil
            if settings.role == .master { self.onState?(token,false,"Client terputus. Menunggu terhubung kembali.",false) }
            else { let delays:[Double]=[1,2,4,8,15];let delay=delays[min(self.retryAttempt,delays.count-1)];self.retryAttempt+=1;self.onState?(token,false,"\(reason) Mencoba lagi dalam \(Int(delay)) detik…",false);self.queue.asyncAfter(deadline:.now()+delay) { [weak self] in guard let self,self.operationID==token,self.session==nil else { return };let tcp=NWProtocolTCP.Options();tcp.noDelay=true;let parameters=NWParameters(tls:nil,tcp:tcp);let connection=NWConnection(host:NWEndpoint.Host(settings.peerHost),port:NWEndpoint.Port(rawValue:nativePort)!,using:parameters);self.install(connection,settings:settings,secret:secret,expectedMaster:expectedMaster,token:token) } }
        }
        session.start()
    }
    func send(_ payload:[String:Any]) { let token=operationID;queue.async { [weak self] in guard let self,self.operationID==token else { return };self.session?.send(payload) } }
    func stop() { let token=newOperation();queue.async { [weak self] in guard let self,self.operationID==token else { return };self.stopOnQueue() } }
}
