import SwiftUI
import AppKit

struct MousharingMacApp: App {
    @NSApplicationDelegateAdaptor(MousharingAppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel()
    var body: some Scene {
        WindowGroup("Mousharing", id: "main") { MainView(model: model).frame(minWidth: 780, minHeight: 590) }
            .defaultSize(width: 900, height: 680)
        MenuBarExtra("Mousharing", systemImage: model.connected ? "computermouse.fill" : "computermouse") {
            Text(model.status)
            if model.running { Button("Jeda sharing") { model.stop() } }
            OpenMainButton()
            Divider()
            Button("Keluar") { model.stop(); NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
        }
    }
}
struct OpenMainButton: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View { Button("Buka Mousharing") { openWindow(id: "main"); NSApplication.shared.activate(ignoringOtherApps: true) } }
}
struct MainView: View {
    @ObservedObject var model: AppModel
    @State private var section = "Mulai"
    @State private var confirmRotate=false
    var body: some View {
        NavigationSplitView {
            List(selection: $section) {
                Label("Mulai", systemImage: "computermouse").tag("Mulai")
                Label("Susunan layar", systemImage: "display.2").tag("Susunan layar")
                Label("Pengaturan", systemImage: "slider.horizontal.3").tag("Pengaturan")
            }.navigationSplitViewColumnWidth(190)
            .safeAreaInset(edge: .bottom) { VStack(alignment: .leading, spacing: 5) { Text("Mousharing").font(.headline); Text("Native preview · Mac + Windows").font(.caption).foregroundStyle(.secondary) }.frame(maxWidth: .infinity, alignment: .leading).padding() }
        } detail: {
            VStack(spacing: 0) {
                HStack(spacing: 10) {
                    Image(systemName: model.connected ? "checkmark.circle.fill" : "circle.dashed").foregroundStyle(model.connected ? .green : .secondary)
                    VStack(alignment: .leading, spacing: 3) { Text(model.status).font(.headline); Text(model.peerName.isEmpty ? "Hubungkan dua laptop dalam jaringan yang sama." : model.peerName).font(.subheadline).foregroundStyle(.secondary) }
                    if model.connected { Text(model.clipboardActive ? "Clipboard aktif":"Clipboard nonaktif").font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    if model.running { Button("Jeda sharing", action: model.stop) }
                }.padding(24)
                Divider()
                ScrollView { VStack(alignment: .leading, spacing: 22) {
                    if section == "Mulai" { startContent }
                    else if section == "Susunan layar" { layoutContent }
                    else { settingsContent }
                    if !model.error.isEmpty { Label(model.error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red).font(.callout).textSelection(.enabled) }
                }.padding(28).frame(maxWidth: 720, alignment: .leading).frame(maxWidth: .infinity) }
            }
        }.disabled(model.busy)
        .confirmationDialog("Ganti undangan pairing?",isPresented:$confirmRotate,titleVisibility:.visible) { Button("Buat undangan baru",role:.destructive) { model.regenerateInvitation() };Button("Batal",role:.cancel) {} } message: { Text("Undangan sebelumnya tidak berlaku. Perangkat lain perlu dipasangkan kembali.") }
    }
    private var startContent: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Satu mouse. Dua perangkat.").font(.largeTitle.bold())
            Text("Pilih perangkat yang menyediakan mouse dan keyboard, lalu hubungkan pasangannya.").foregroundStyle(.secondary)
            GroupBox("Peran Mac ini") {
                VStack(alignment: .leading, spacing: 12) {
                    Picker("Peran", selection: $model.settings.role) { ForEach(DeviceRole.allCases) { Text($0.title).tag($0) } }.labelsHidden().pickerStyle(.radioGroup)
                    Text(model.settings.role.detail).font(.callout).foregroundStyle(.secondary)
                }.padding(10).frame(maxWidth: .infinity, alignment: .leading)
            }.disabled(model.running)
            if model.settings.role == .master {
                GroupBox("Hubungkan perangkat lain") {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Salin undangan, lalu tempel di Mousharing pada laptop pasangan. Undangan ini memberikan akses input; bagikan hanya ke perangkat Anda.").font(.callout).foregroundStyle(.secondary)
                        HStack { SecureField("Undangan pairing", text: .constant(model.invitation)).textFieldStyle(.roundedBorder); Button("Salin undangan") { model.copyInvitation() }.disabled(model.invitation.isEmpty) }
                        if !model.copied.isEmpty { Text(model.copied).font(.caption).foregroundStyle(.secondary) }
                    }.padding(10)
                }
            } else {
                GroupBox("Hubungkan ke master") {
                    VStack(alignment: .leading, spacing: 12) {
                        SecureField("Tempel undangan pairing dari master", text: $model.invitationInput).textFieldStyle(.roundedBorder)
                        HStack { Button("Gunakan undangan") { model.useInvitation() }; Text("Tersimpan aman di Keychain.").font(.caption).foregroundStyle(.secondary) }
                        if !model.discovered.isEmpty {
                            Text("Master ditemukan").font(.headline)
                            ForEach(model.discovered) { device in Button { model.settings.peerHost = device.host } label: { HStack { Label(device.name, systemImage: "desktopcomputer"); Spacer(); Text(model.settings.pairedMasterID != nil && model.settings.pairedMasterID != device.id ? "Undangan berbeda":device.host).foregroundStyle(.secondary) } }.buttonStyle(.bordered).disabled(model.settings.pairedMasterID != nil && model.settings.pairedMasterID != device.id) }
                        }
                        TextField("Alamat master (IP atau nama host)", text: $model.settings.peerHost).textFieldStyle(.roundedBorder)
                        Text("Pilih master yang ditemukan atau masukkan alamat jika jaringan memblokir penemuan otomatis.").font(.caption).foregroundStyle(.secondary)
                    }.padding(10)
                }.disabled(model.running)
            }
            GroupBox("Izin input Mac") { VStack(alignment:.leading,spacing:10) { HStack { Label(model.accessibility ? "Accessibility aktif" : "Accessibility diperlukan",systemImage:model.accessibility ? "checkmark.circle":"lock");Spacer();Button("Buka pengaturan izin") { model.openPermissions() } };if model.settings.role == .master { Label(model.inputMonitoring ? "Input Monitoring aktif":"Input Monitoring diperlukan",systemImage:model.inputMonitoring ? "checkmark.circle":"lock") } }.padding(10) }
            HStack { Button(model.settings.role == .master ? "Mulai berbagi" : "Hubungkan") { model.start() }.buttonStyle(.borderedProminent).controlSize(.large).disabled(model.running); if model.running && !model.connected { ProgressView().controlSize(.small) }; Spacer(); Button("Periksa ulang izin") { model.refreshPermissions() }.buttonStyle(.link) }
            Text("Escape kembali ke master. F8 berpindah manual. Tutup sharing untuk berhenti meneruskan input.").font(.caption).foregroundStyle(.secondary)
        }
    }
    private var layoutContent: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Susunan layar").font(.largeTitle.bold())
            Text("Pilih posisi layar client terhadap master. Pengaturan berlaku pada master.").foregroundStyle(.secondary)
            layoutDiagram.frame(maxWidth:.infinity).padding(.vertical,25)
            Picker("Client berada di", selection: $model.settings.side) { ForEach(ScreenSide.allCases) { Text($0.title).tag($0) } }.pickerStyle(.segmented)
            Text("Kursor menyeberang melalui sisi ini dan kembali melalui sisi yang berhadapan. Keyboard mengikuti perangkat yang dikontrol.").font(.callout).foregroundStyle(.secondary)
            Button("Simpan susunan") { model.saveSettings() }.disabled(model.running)
            if model.running { Text("Jeda sharing sebelum mengubah susunan layar.").font(.caption).foregroundStyle(.secondary) }
        }.disabled(model.running)
    }
    @ViewBuilder private var layoutDiagram:some View {
        switch model.settings.side {
        case .left: HStack(spacing:22) { displayCard("Client","display",selected:true);Image(systemName:"arrow.left.arrow.right");displayCard("Master","keyboard",selected:false) }
        case .right: HStack(spacing:22) { displayCard("Master","keyboard",selected:false);Image(systemName:"arrow.left.arrow.right");displayCard("Client","display",selected:true) }
        case .top: VStack(spacing:12) { displayCard("Client","display",selected:true);Image(systemName:"arrow.up.arrow.down");displayCard("Master","keyboard",selected:false) }
        case .bottom: VStack(spacing:12) { displayCard("Master","keyboard",selected:false);Image(systemName:"arrow.up.arrow.down");displayCard("Client","display",selected:true) }
        }
    }
    private func displayCard(_ title: String, _ icon: String, selected: Bool) -> some View { VStack(spacing: 10) { Image(systemName: icon).font(.system(size: 36)); Text(title).font(.headline) }.frame(width: 165, height: 120).background(selected ? Color.accentColor.opacity(0.1) : Color.secondary.opacity(0.07), in: RoundedRectangle(cornerRadius: 12)).overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.quaternary)) }
    private var settingsContent: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Pengaturan").font(.largeTitle.bold())
            Form {
                TextField("Nama perangkat", text: $model.settings.deviceName)
                Toggle("Bagikan clipboard teks dua arah", isOn: $model.settings.clipboardEnabled)
                Text("Teks baru yang disalin selama terhubung dikirim ke pasangan. Maksimum 256 KiB; gambar dan file belum didukung.").font(.caption).foregroundStyle(.secondary)
            }.formStyle(.grouped).disabled(model.running)
            Button("Simpan pengaturan") { model.saveSettings() }.disabled(model.running)
            Divider()
            Text("Pairing").font(.headline)
            Text("Buat undangan baru untuk memutus kepercayaan pada undangan sebelumnya. Perangkat lain perlu dipasangkan kembali.").font(.callout).foregroundStyle(.secondary)
            Button("Buat undangan baru", role: .destructive) { confirmRotate=true }.disabled(model.running)
        }
    }
}

@MainActor final class MousharingAppDelegate:NSObject,NSApplicationDelegate { func applicationWillTerminate(_ notification:Notification) { AppModel.active?.stop() } }
