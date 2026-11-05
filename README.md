# mousharing

Satu mouse dan keyboard bawaan Mac untuk mengontrol Mac atau Windows melalui LAN.

**Status:** prototipe CLI dengan perpindahan otomatis melalui tepi layar. Dependency Mac sudah dipasang dan bisa diimpor; 29 tes protokol, clipboard, geometri, dan simulasi perpindahan lulus. Backend clipboard Mac diuji dengan pasteboard terpisah; clipboard native Windows masih perlu uji pada kedua laptop. Belum ada GUI atau installer.

- **Tepi layar:** otomatis berpindah antara Mac dan Windows; keyboard mengikuti perangkat yang sedang dikontrol.
- **F8 / Fn+F8:** alternatif untuk berpindah kontrol secara manual.
- **Escape:** kembali ke Mac. Escape saat ini dicadangkan, tidak diteruskan ke Windows.
- Mouse relatif, klik kiri/kanan/tengah, scroll, tombol ANSI US, modifier, F1–F12 kecuali F8.
- TLS dengan sertifikat Windows yang disalin secara eksplisit ke Mac dan password pairing minimal 16 karakter.
- Heartbeat dua arah; tombol yang ditahan dilepas saat reset, disconnect, atau timeout receiver 5 detik. Mac kembali ke mode lokal ketika koneksi gagal atau respons heartbeat habis waktu.
- Tidak merekam input atau menyimpan password pairing.
- Clipboard teks otomatis dua arah melalui koneksi TLS yang sama, maksimum 256 KiB UTF-8 per salinan. Isi clipboard tidak dicatat ke log atau file.
- Untuk hide cursor, proses Mac menjadi aplikasi aksesori foreground tanpa jendela atau ikon Dock selama remote. Fokus aplikasi Mac sebelumnya dipulihkan saat kembali lokal. Input selama transisi ditahan sampai capture siap.
- Saat remote, gerakan fisik mouse dilepas dari kursor Mac dan event lokal ditekan. Jika hide/capture kursor gagal, kontrol tetap pada Mac. Jika fokus foreground atau event tap hilang saat remote, sesi dihentikan dan kontrol dikembalikan ke Mac.

## Susunan dua layar

Masing-masing laptop menjalankan dan menampilkan desktopnya sendiri. Mouse terpasang di Mac, dan keyboard bawaan Mac mengikuti fokus kontrol.

Default susunan **Mac | Windows**: gerakkan kursor ke tepi kanan Mac untuk masuk ke Windows. Gerakkan kursor ke tepi kiri Windows untuk kembali ke Mac. Kursor muncul pada posisi vertikal yang sebanding di layar tujuan; kursor Mac disembunyikan selama Windows dikontrol.

Ukuran layar utama dibaca otomatis pada saat mulai. Posisi fisik Windows dipilih melalui argumen kedua launcher:

```sh
./start-mac.command IP_WINDOWS right  # Windows di kanan Mac (default)
./start-mac.command IP_WINDOWS left   # Windows di kiri Mac
./start-mac.command IP_WINDOWS top    # Windows di atas Mac
./start-mac.command IP_WINDOWS bottom # Windows di bawah Mac
```

Perpindahan balik membutuhkan gerakan keluar yang cukup di tepi bersama agar jitter kecil tidak memantulkan kontrol. Perpindahan otomatis ditunda selama tombol atau klik masih ditahan, sehingga drag tetap berada pada perangkat asal. Setelah kembali ke Mac ada jeda singkat dan posisi masuk diberi jarak dari tepi untuk mencegah perpindahan bolak-balik tanpa sengaja. Escape tetap menjadi jalan kembali ke Mac.

Saat ini satu layar utama per laptop. Setelah mengubah resolusi, scaling, atau layar utama, mulai ulang kedua program. Untuk mematikan perpindahan otomatis:

```sh
.venv/bin/python mousharing.py send --host IP_WINDOWS --cert data/windows-cert.pem --no-edge-switch
```

## Clipboard sharing

Setelah `Connected` dan `Clipboard text sharing: enabled`, teks baru yang disalin di salah satu laptop otomatis menggantikan clipboard laptop pasangan. Sinkronisasi tetap berjalan saat mouse berada di Mac maupun Windows, dengan interval pemeriksaan sekitar 250 ms. Clipboard yang sudah ada sebelum sesi dimulai tidak otomatis dikirim; salin ulang teks setelah terhubung.

- Copy/paste di Mac: **Command+C / Command+V**.
- Copy/paste saat mengontrol Windows: **Control+C / Control+V** pada keyboard Mac. Command tetap dipetakan ke tombol Windows.
- Teks Unicode, emoji, baris baru, dan teks kosong didukung. Batasnya 256 KiB UTF-8; konten lebih besar dilewati.
- Gambar, file, rich text, dan format HTML belum ditransfer. Jika aplikasi juga menyediakan versi teks, hanya teksnya yang dikirim.
- Konten hasil sinkronisasi tidak dikirim balik. Revisi logis mengurutkan salinan yang bersamaan agar kedua sisi konsisten. Clipboard yang sedang sibuk dicoba ulang tanpa memutus kontrol mouse.
- Sinkronisasi berhenti saat koneksi ditutup; isi terakhir tetap tersedia di clipboard sistem masing-masing.

Untuk mematikan clipboard sharing dari Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\start-windows.ps1 -NoClipboard
```

Atau jalankan sender Mac langsung dengan `--no-clipboard`. Jika salah satu perangkat mematikannya, clipboard sharing tidak diaktifkan untuk sesi tersebut.

**Update ke versi clipboard:** hentikan sender/receiver, jalankan `git pull --ff-only` pada kedua laptop (atau perbarui source dari ZIP), lalu mulai receiver Windows dan sender Mac kembali. Protokol kini versi 3. Dependency dan sertifikat lama tetap digunakan; setup tidak perlu diulang.

## 1. Setup Windows

Siapkan Python 3.11 atau lebih baru dari [python.org](https://www.python.org/downloads/windows/). Jika memakai installer yang menyediakan opsi PATH, aktifkan opsi tersebut.

Ambil source menggunakan Git:

```powershell
git clone https://github.com/widjis/mousharing.git
cd mousharing
```

Atau [unduh ZIP main](https://github.com/widjis/mousharing/archive/refs/heads/main.zip), ekstrak, lalu buka PowerShell di folder yang berisi `setup-windows.ps1`.

Jalankan sekali:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup-windows.ps1
```

Skrip membuat `.venv`, memasang dependency Windows, serta membuat identitas TLS di `data/`. Pengaturan execution policy hanya berlaku untuk proses ini. Sertifikat yang sudah ada tidak ditimpa. Setup menampilkan SHA256 sertifikat untuk dibandingkan di Mac.

**Salin hanya `data\windows-cert.pem` ke folder `data/` pada proyek Mac.** Buat folder `data` di Mac jika belum ada. `windows-key.pem` tetap di Windows, tidak perlu dikirim ke Mac atau GitHub. Sertifikat berlaku satu tahun; password pairing dipilih saat program dijalankan.

## 2. Jalankan receiver Windows

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\start-windows.ps1
```

Skrip menampilkan kandidat IP Wi-Fi/Ethernet. Pilih IP adapter LAN yang terhubung ke jaringan yang sama dengan Mac. Masukkan password pairing minimal 16 karakter; gunakan password yang sama saat menjalankan sender Mac. Password tidak ditampilkan atau disimpan ke file.

Jika koneksi terhalang firewall, gunakan jaringan Windows dengan profil **Private**. Bila diperlukan, jalankan perintah berikut di PowerShell **Run as administrator**, sekali saja:

```powershell
New-NetFirewallRule -DisplayName 'Mousharing (Private LAN)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47831 -Profile Private -RemoteAddress LocalSubnet
```

Rule dibatasi ke jaringan Private dan subnet lokal. Jangan port-forward port ini ke internet. Aplikasi melayani satu koneksi setiap waktu; belum ada pembatas percobaan password. Tutup receiver dengan Ctrl+C ketika tidak digunakan.

## 3. Setup dan jalankan Mac

Pada Mac pengembangan ini, `.venv` sudah disiapkan dengan Python 3.14 dan PyObjC 12.2.2. Untuk instalasi baru, gunakan Python 3.11+ yang telah tersedia:

```sh
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
```

Dari folder proyek, setelah sertifikat publik Windows disalin ke `data/windows-cert.pem`:

```sh
.venv/bin/python mousharing.py fingerprint --cert data/windows-cert.pem
```

Bandingkan hasil SHA256 dengan yang tampil saat setup Windows. Setelah cocok:

```sh
./start-mac.command IP_WINDOWS
```

Ganti `IP_WINDOWS` dengan IP LAN Windows. Launcher juga bisa diklik dua kali; alamat Windows akan ditanyakan. Masukkan password pairing yang sama dengan receiver.

Izinkan terminal/Python yang dipakai di **System Settings → Privacy & Security → Accessibility** dan **Input Monitoring**, lalu tutup dan buka ulang terminal/program bila diminta macOS. Izin diperiksa melalui sistem; skrip tidak mengubahnya secara otomatis.

Mulai dalam mode lokal. Lewati tepi yang menghadap Windows untuk berpindah otomatis. **F8/Fn+F8** tetap tersedia; **Escape** selalu kembali ke Mac. Ctrl+C menghentikan sender saat mode lokal.

## Verifikasi pengembangan

```sh
.venv/bin/python -m pip check
.venv/bin/python -m unittest discover -s tests -v
```

Tes memakai socket lokal TLS dan controller input tiruan, sehingga tidak mengetik atau menggerakkan mouse pengguna. Sertifikat tes dibuat sementara dengan generator yang sama dengan setup Windows. Tes mencakup framing pesan, pemetaan tombol shortcut, password salah, sertifikat tidak dipercaya, heartbeat, reset, pelepasan tombol saat disconnect, empat susunan layar, dan resolusi berbeda. Simulasi event loop sender melalui TLS menguji perpindahan Mac → Windows → Mac serta hide/show dan pemulihan posisi kursor tanpa memakai input fisik. Tes tambahan memeriksa nullifikasi klik remote di Mac, kegagalan hide/capture, penghentian ketika tap mati, urutan klik/tombol selama batching gerakan, dan jitter tepi. Gerakan berurutan digabung dalam jendela sekitar 4 ms dan koneksi memakai TCP_NODELAY untuk mengurangi antrean; performa fisik belum diukur. Skrip PowerShell telah diperiksa sintaksnya dari Mac; eksekusi Windows belum dilakukan.

## Uji clipboard pada kedua laptop

1. Setelah terhubung, copy teks baru di Mac, menyeberang, lalu paste ke Notepad Windows dengan Ctrl+V.
2. Copy teks baru di Windows dengan Ctrl+C, kembali ke Mac, lalu paste dengan Command+V.
3. Uji emoji dan teks beberapa baris, lalu pastikan keyboard/mouse tetap lancar selama copy/paste.
4. Uji opsi `-NoClipboard`: kontrol input tetap aktif, clipboard tidak berubah antarperangkat.

## Uji input pada kedua laptop

1. Jalankan receiver dan sender; mode lokal hanya memengaruhi Mac.
2. Lewati tepi kanan Mac (default), lalu tepi kiri Windows: pastikan kontrol dan keyboard berpindah dua arah. Uji F8 sebagai alternatif.
3. Uji posisi vertikal pada bagian atas/tengah/bawah layar, gerakan terus-menerus, klik, scroll, dan drag tanpa berpindah saat tombol ditahan.
4. Uji pengetikan, Shift, Ctrl+C/V di aplikasi Windows biasa. Command dipetakan ke tombol Windows; Control tetap Ctrl.
5. Tekan Escape; pastikan kembali lokal dan tidak ada tombol Windows tertahan.
6. Putus koneksi saat menahan tombol; pastikan input lokal kembali dan tombol Windows dilepas setelah timeout.
7. Uji sleep/wake dan penghentian proses. Koneksi ulang masih manual.

## Batas saat ini

Belum ada GUI, installer, auto-discovery, transfer clipboard gambar/file, dukungan monitor tambahan, atau autostart. Perangkat sumber input saat ini tetap Mac; Windows menerima input dan dapat mengembalikan kontrol saat kursor melewati tepi bersama. Pemetaan mengikuti posisi ANSI US; layout lain, IME, Fn, media keys, scaling lintas perangkat secara fisik, dan kombinasi modifier kiri/kanan bersamaan belum divalidasi. Sinkronisasi Caps Lock antarperangkat belum tersedia. Secure Input macOS dapat membatasi capture. Aplikasi Windows elevated/UAC dan lock screen bukan target prototipe ini.

Source dan dependency dipisahkan dari identitas lokal: `.venv/`, `data/`, sertifikat, dan private key diabaikan Git. Simpan backup identitas Windows secara privat jika diperlukan; setup tidak merotasi sertifikat secara otomatis.

Referensi implementasi: [Quartz event taps](https://developer.apple.com/documentation/coregraphics/quartz-event-services), [pynput](https://pynput.readthedocs.io/), [sertifikat X.509 PyCA](https://cryptography.io/en/latest/x509/tutorial/).

API layar: [Windows GetSystemMetrics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics), [DPI awareness](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext).

Hide cursor mengikuti batasan foreground pada [API Apple CGDisplayHideCursor](https://developer.apple.com/documentation/coregraphics/cgdisplayhidecursor(_:)). Aktivasi foreground berlangsung hanya selama remote; setelahnya fokus aplikasi sebelumnya dipulihkan.

API clipboard: [Apple NSPasteboard](https://developer.apple.com/documentation/appkit/nspasteboard), [Windows OpenClipboard](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openclipboard), [Windows SetClipboardData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setclipboarddata).
