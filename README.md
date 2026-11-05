# mousharing

Satu mouse dan keyboard bawaan Mac untuk mengontrol Mac atau Windows melalui LAN.

**Status:** prototipe CLI. Dependency Mac sudah dipasang dan bisa diimpor; tes protokol/TLS lokal tersedia. Input fisik lintas Mac–Windows belum diuji. Belum ada GUI atau installer.

- **F8 / Fn+F8:** berpindah antara Mac dan Windows.
- **Escape:** kembali ke Mac. Escape saat ini dicadangkan, tidak diteruskan ke Windows.
- Mouse relatif, klik kiri/kanan/tengah, scroll, tombol ANSI US, modifier, F1–F12 kecuali F8.
- TLS dengan sertifikat Windows yang disalin secara eksplisit ke Mac dan password pairing minimal 16 karakter.
- Heartbeat dua arah; tombol yang ditahan dilepas saat reset, disconnect, atau timeout receiver 5 detik. Mac kembali ke mode lokal ketika koneksi gagal atau respons heartbeat habis waktu.
- Tidak merekam input atau menyimpan password pairing.

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

Mulai dalam mode lokal. Tekan **F8/Fn+F8** untuk mengontrol Windows, **Escape** untuk kembali ke Mac. Ctrl+C menghentikan sender saat mode lokal.

## Verifikasi pengembangan

```sh
.venv/bin/python -m pip check
.venv/bin/python -m unittest discover -s tests -v
```

Tes memakai socket lokal TLS dan controller input tiruan, sehingga tidak mengetik atau menggerakkan mouse pengguna. Sertifikat tes dibuat sementara dengan generator yang sama dengan setup Windows. Tes mencakup framing pesan, pemetaan tombol shortcut, password salah, sertifikat tidak dipercaya, heartbeat, reset, dan pelepasan tombol saat disconnect. Skrip PowerShell telah diperiksa sintaksnya dari Mac; eksekusi Windows belum dilakukan.

## Uji dua laptop yang masih diperlukan

1. Jalankan receiver dan sender; mode lokal hanya memengaruhi Mac.
2. Tekan F8; uji gerakan mouse terus-menerus, drag, klik, dan scroll.
3. Uji pengetikan, Shift, Ctrl+C/V di aplikasi Windows biasa. Command dipetakan ke tombol Windows; Control tetap Ctrl.
4. Tekan Escape; pastikan kembali lokal dan tidak ada tombol Windows tertahan.
5. Putus koneksi saat menahan tombol; pastikan input lokal kembali dan tombol Windows dilepas setelah timeout.
6. Uji sleep/wake dan penghentian proses. Koneksi ulang masih manual.

## Batas saat ini

Belum ada GUI, installer, auto-discovery, clipboard, perpindahan tepi layar, Windows → Mac, atau autostart. Pemetaan mengikuti posisi ANSI US; layout lain, IME, Fn, media keys, dan kombinasi modifier kiri/kanan bersamaan belum divalidasi. Sinkronisasi Caps Lock antarperangkat belum tersedia. Secure Input macOS dapat membatasi capture. Aplikasi Windows elevated/UAC dan lock screen bukan target prototipe ini.

Source dan dependency dipisahkan dari identitas lokal: `.venv/`, `data/`, sertifikat, dan private key diabaikan Git. Simpan backup identitas Windows secara privat jika diperlukan; setup tidak merotasi sertifikat secara otomatis.

Referensi implementasi: [Quartz event taps](https://developer.apple.com/documentation/coregraphics/quartz-event-services), [pynput](https://pynput.readthedocs.io/), [sertifikat X.509 PyCA](https://cryptography.io/en/latest/x509/tutorial/).
