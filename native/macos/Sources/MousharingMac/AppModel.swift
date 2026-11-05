import SwiftUI
import AppKit
import ApplicationServices

@MainActor final class AppModel:ObservableObject {
    static weak var active:AppModel?
    @Published var settings=AppSettings.load()
    @Published var running=false
    @Published var connected=false
    @Published var clipboardActive=false
    @Published var busy=false
    @Published var status="Siap menghubungkan perangkat"
    @Published var peerName=""
    @Published var error=""
    @Published var invitation=""
    @Published var invitationInput=""
    @Published var copied=""
    @Published var accessibility=false
    @Published var inputMonitoring=false
    @Published var discovered:[DiscoveredDevice]=[]
    private var masterKey=Data()
    private let service=NativeService()
    private let discovery=NativeDiscovery()
    private let input=InputEngine()
    private var clipboard:NativeClipboard?
    private var clipboardTimer:Timer?
    init() {
        Self.active=self
        if let failure=AppSettings.staticLoadFailure { self.error=failure;status="Konfigurasi perlu diperbaiki";return }
        do { if let key=try SecretStore.read() { guard key.count==32 else { throw NativeError.message("Pairing di Keychain tidak valid. Jangan ganti undangan sebelum data pairing diperiksa.") };masterKey=key } else { let candidate=randomBytes(32);try SecretStore.write(candidate);masterKey=candidate }; updateInvitation() } catch { self.error=error.localizedDescription }
        refreshPermissions()
        service.onState={ [weak self] token,connected,text,clipboard in DispatchQueue.main.async { guard let self,self.service.operationID==token else { return };self.connectionState(connected,text,clipboard:clipboard) } }
        service.onPayload={ [weak self] token,payload in DispatchQueue.main.async { guard let self,self.service.operationID==token else { return };self.receive(payload) } }
        service.onPeer={ [weak self] token,id in DispatchQueue.main.async { guard let self,self.service.operationID==token else { return }; if self.settings.role == .master { self.settings.trustedPeerID=id;self.saveSettings();if !self.error.isEmpty { self.stop() } } } }
        input.send={ [weak self] payload in self?.service.send(payload) }
        input.failure={ [weak self] message in self?.error=message;self?.stop() }
        input.localState={ [weak self] remote in guard let self else { return }; if self.connected { self.status=remote ? "Mengontrol \(self.peerName)":"Kontrol pada master" } }
        discovery.onFound={ [weak self] device in DispatchQueue.main.async { guard let self else { return }; self.discovered.removeAll { $0.id==device.id || Date().timeIntervalSince($0.lastSeen)>8 }; self.discovered.append(device) } }
        discovery.start(identity:settings,advertise:false)
    }
    private func configurationReady() -> Bool { if let failure=AppSettings.staticLoadFailure { error=failure;return false };return true }
    private func updateInvitation() { guard AppSettings.staticLoadFailure == nil,masterKey.count==32 else { invitation="";return };invitation=PairingInvite(id:settings.deviceID,name:settings.deviceName,key:encoded(masterKey),addresses:localAddresses()).code }
    func saveSettings() { guard configurationReady() else { return };do { settings.deviceName=String(settings.deviceName.trimmingCharacters(in:.whitespacesAndNewlines).prefix(128));guard !settings.deviceName.isEmpty else { throw NativeError.message("Nama perangkat tidak boleh kosong.") }; try settings.save();updateInvitation();error="" } catch { self.error=error.localizedDescription } }
    func refreshPermissions() { accessibility=AXIsProcessTrusted();inputMonitoring=CGPreflightListenEventAccess() }
    func openPermissions() { let options=[kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String:true] as CFDictionary;_ = AXIsProcessTrustedWithOptions(options);if settings.role == .master { _ = CGRequestListenEventAccess() };NSWorkspace.shared.open(URL(string:"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!) }
    func copyInvitation() { guard configurationReady() else { return };guard masterKey.count==32 else { error="Keychain pairing belum siap.";return };updateInvitation();NSPasteboard.general.clearContents();NSPasteboard.general.setString(invitation,forType:.string);copied="Undangan disalin. Tempel pada laptop pasangan." }
    func useInvitation() {
        guard configurationReady() else { return }
        guard !running else { return }
        do {
            let invite=try PairingInvite.parse(invitationInput)
            guard invite.id != settings.deviceID else { throw NativeError.message("Gunakan undangan dari laptop lain.") }
            let account="client-"+UUID().uuidString.lowercased()
            try SecretStore.write(try decoded(invite.key,count:32),account:account)
            // Persist a new metadata pointer before replacing the active configuration.
            // A failed save may leave an unused Keychain item, but never replaces the old key.
            var candidate=settings
            let changedMaster=candidate.pairedMasterID != invite.id
            candidate.clientKeyAccount=account
            candidate.pairedMasterID=invite.id
            candidate.pairedMasterName=invite.name
            if let found=discovered.first(where:{$0.id==invite.id}) { candidate.peerHost=found.host }
            else if changedMaster || candidate.peerHost.isEmpty { candidate.peerHost=invite.addresses.first ?? "" }
            candidate.role = .client
            try candidate.save()
            settings=candidate
            invitationInput=""
            error=""
            status="Pairing tersimpan. Pilih Hubungkan."
        } catch { self.error=error.localizedDescription }
    }
    func regenerateInvitation() { guard configurationReady() else { return };guard !running else { return };do { let candidate=randomBytes(32);try SecretStore.write(candidate);masterKey=candidate;settings.trustedPeerID=nil;try settings.save();updateInvitation();copied="Undangan baru dibuat. Undangan sebelumnya tidak berlaku.";error="" } catch { self.error=error.localizedDescription } }
    func start() {
        guard configurationReady() else { return }
        guard !running else { return };error="";refreshPermissions();saveSettings();guard error.isEmpty else { return }
        let key:Data
        if settings.role == .master { guard masterKey.count==32 else { error="Keychain pairing belum siap. Buka kunci Mac lalu mulai ulang aplikasi.";return };guard accessibility,inputMonitoring else { error="Aktifkan Accessibility dan Input Monitoring, lalu periksa ulang izin.";return };key=masterKey }
        else { let savedKey:Data?;do { savedKey=try SecretStore.read(account:settings.clientKeyAccount ?? "client") } catch { self.error=error.localizedDescription;return };guard let saved=savedKey,saved.count==32,settings.pairedMasterID != nil else { error="Tempel dan gunakan undangan pairing dari master terlebih dahulu.";return };guard !settings.peerHost.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty else { error="Pilih master yang ditemukan atau masukkan alamatnya.";return }; guard accessibility else { error="Aktifkan Accessibility agar Mac dapat menerima input, lalu periksa ulang izin.";return };key=saved }
        running=true;connected=false;status=settings.role == .master ? "Menunggu client terhubung":"Menghubungkan ke master…";discovery.start(identity:settings,advertise:settings.role == .master);service.start(settings:settings,secret:key,expectedMaster:settings.pairedMasterID)
    }
    private func connectionState(_ value:Bool,_ text:String,clipboard allowed:Bool) {
        guard running else { return }
        if value { connected=true;peerName=text;status="Terhubung ke \(text)";do { try input.start(role:settings.role,side:settings.side);if allowed && settings.clipboardEnabled { clipboardActive=true;clipboard=NativeClipboard(origin:settings.deviceID);let timer=Timer(timeInterval:0.25,repeats:true) { [weak self] _ in Task { @MainActor in guard let self,self.connected,let update=self.clipboard?.poll() else { return };self.service.send(update) } };RunLoop.main.add(timer,forMode:.common);clipboardTimer=timer } } catch { self.error=error.localizedDescription;stop() } }
        else { connected=false;clipboardActive=false;clipboardTimer?.invalidate();clipboardTimer=nil;clipboard=nil;input.stop();status=text;if text.hasPrefix("Tidak dapat membuka port") { running=false;service.stop();discovery.stop();error=text } }
    }
    private func receive(_ payload:[String:Any]) { guard connected else { return };do { if payload["type"] as? String == "clipboard" { if settings.clipboardEnabled { try clipboard?.apply(payload) } } else { try input.receive(payload) } } catch { self.error=error.localizedDescription;stop() } }
    func stop() { running=false;connected=false;clipboardActive=false;clipboardTimer?.invalidate();clipboardTimer=nil;clipboard=nil;input.stop();service.stop();discovery.stop();status="Sharing dijeda" }
}
