# Mousharing Windows native

WinUI 3 + C# / .NET 10, Win32 Raw Input / low-level hooks / SendInput, system tray dan clipboard Unicode native. Tidak menggunakan Python atau WebView.

## Build

Jalankan pada Windows 10 1809+ atau Windows 11 dengan .NET 10 SDK dan toolchain Windows desktop. Paket WinUI Windows App SDK 1.8 dipatok di proyek. Build pertama mengunduh paket NuGet yang diperlukan.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\native\windows\build.ps1
```

Output x64: `native\windows\dist\win-x64\Mousharing.Windows.exe`. Folder publish adalah paket mandiri; pertahankan seluruh file pendamping. ARM64: tambahkan `-Architecture ARM64`. Belum merupakan installer/MSIX bertanda tangan. Build otomatis ada di workflow native; hasil compiler dan pengujian fisik dilaporkan terpisah.

.NET 10 dipilih karena LTS; tidak memerlukan Python. [Kebijakan dukungan Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Penggunaan

1. Jalankan aplikasi pada kedua laptop. Pilih satu **Master** (tempat mouse/keyboard utama terpasang) dan satu **Client**.
2. Pada master, pilih **Mulai berbagi**, lalu **Salin undangan pairing**. Undangan berisi akses pasangan; bagikan langsung ke laptop client yang dipercaya.
3. Pada client, tempel undangan di field tersamar. Pilih master yang ditemukan atau gunakan alamat IPv4 di pengaturan lanjutan. Klik **Hubungkan**.
4. Master menentukan posisi layar client. Kursor menyeberang lewat tepi bersama. F8 berpindah manual; Escape kembali lokal.
5. Clipboard teks dua arah otomatis bila diaktifkan pada kedua aplikasi. Maksimum 256 KiB UTF-8; hanya salinan baru selama sesi, bukan isi clipboard lama. Gambar/file tidak ditransfer.
6. **Jeda** menghentikan koneksi dan semua sharing. **Lanjutkan** menggunakan pasangan tersimpan; client mencoba kembali dengan jeda bertahap. **Putuskan** berhenti tanpa mencoba ulang.
7. Menutup jendela menyembunyikannya ke system tray. Buka, Jeda/Lanjutkan, atau Keluar dari ikon tray. Keluar melepaskan capture dan tombol remote.

Native menggunakan TCP 47832 dan discovery UDP 47833, terpisah dari prototipe Python. Pada Windows Firewall, izinkan aplikasi hanya pada jaringan Private yang digunakan. Jika broadcast dibatasi, alamat IPv4 manual tetap tersedia. Aplikasi tidak menambahkan rule firewall atau service sistem secara otomatis.

## Penyimpanan dan pemulihan

Nama, peran, layout, identitas pasangan, dan alamat tersimpan di `%LOCALAPPDATA%\Mousharing\settings.json`. Kunci pairing dilindungi DPAPI untuk akun Windows ini di file `.protected`; import baru menulis kandidat unik lalu mengikatnya lewat konfigurasi atomik, sehingga kegagalan penyimpanan tidak menimpa kredensial aktif. Kandidat yang belum terpakai tetap terenkripsi. File `client.protected` lama tetap didukung; jangan salin ke akun lain. Konfigurasi rusak atau kunci yang tidak bisa didekripsi dipertahankan dan ditampilkan sebagai error, tidak diam-diam diganti.

**Hapus pairing** menghentikan sesi dan menghapus kepercayaan pasangan. Master membuat kunci baru untuk undangan berikutnya. Satu pasangan aktif per master; role tidak dapat diganti selama sesi aktif.

## Verifikasi wajib pada perangkat

Build CI dan tes crypto lintas bahasa tidak membuktikan penangkapan input fisik. Uji Mac-master → Windows-client dan Windows-master → Mac-client: gerakan kontinu, klik, drag, scroll, modifier, kembali lewat tepi, F8, Escape, clipboard Unicode, pause/resume, jaringan putus, sleep/wake, quit, dan perubahan role. UI juga perlu diperiksa pada high contrast, display scaling 200%, keyboard navigation, dan Narrator.

Windows master memakai Raw Input untuk delta relatif dan low-level hooks untuk penekanan input. Capture memakai overlay native transparan yang ditutup saat lokal/berhenti. Pengujian fisik hide cursor dan raw input tetap diperlukan. Target awal adalah satu layar utama per laptop dan aplikasi desktop biasa. UAC, secure desktop, aplikasi elevated, layout non-ANSI-US, dan multi-monitor belum merupakan target yang terverifikasi. Tidak ada eskalasi administrator otomatis atau bypass proteksi Windows.

## Smoke test UI di CI

`Mousharing.Windows.exe --ui-smoke-test` memuat XAML dan jendela native, memeriksa event Loaded, lalu menutup melalui jalur Quit. Exit code 0 berarti startup berhasil, 1 berarti kegagalan, 2 berarti timeout. Mode ini tidak membaca konfigurasi pribadi, tidak membuka discovery, dan tidak memulai hooks, sharing, atau clipboard. Ini tidak menggantikan uji perangkat fisik.
