# Fix_L1 - DirectInput to XInput Controller Converter (SDL2 & ViGEmBus Edition)

Aplikasi konverter / emulator gamepad DirectInput (Generic USB Joystick / Gamepad Murah) menjadi **Virtual Xbox 360 Controller (XInput)** berbasis **.NET 10.0**, **SDL2**, dan **ViGEmBus**.

Dilengkapi dengan fitur khusus **Debounce Filter Anti-Obrak L1 ("Fix_L1")**, pemetaan manual seluruh input gamepad, kalibrasi analog & deadzone, serta dukungan getaran (force feedback / vibration).

---

## Fitur Utama

1. **Pemetaan Seluruh Input Gamepad ke Xbox 360 Secara Manual (Full Wizard & Individual Remap)**:
   - **Tombol Aksi**: `A`, `B`, `X`, `Y`
   - **Shoulder / Bumper**: `LB` (L1) & `RB` (R1)
   - **Triggers**: `LT` (L2) & `RT` (R2) — mendukung tombol digital fisik maupun tuas analog
   - **Menu & Khusus**: `Back / Select`, `Start`, `Guide / Home / Xbox Logo`
   - **Stick Clicks**: `L3` (Left Thumb Click) & `R3` (Right Thumb Click)
   - **D-Pad (Panah Arah)**: Mendukung D-Pad fisik berbasis POV Hat, tombol terpisah, maupun axis
   - **Analog Sticks (Kiri & Kanan)**: Auto-deteksi sumbu axis dan pembalikan arah (Invert Y/X)

2. **Dukungan Getaran (Force Feedback / Vibration)**:
   - Menerima sinyal rumble dari game via ViGEmBus dan meneruskannya ke motor getar stik fisik via `SDL_JoystickRumble` & `SDL_Haptic`.
   - Mengatur intensitas getaran (0 - 100%).
   - Menu pengujian getaran interaktif:
     - Motor Kiri (Heavy / Low Frequency)
     - Motor Kanan (Light / High Frequency)
     - Dual Motor (Maksimal)
     - Denyut Getar (Pulse 3x)

3. **Fitur Khusus Fix_L1 (Anti-Obrak Debounce Filter)**:
   - Dirancang khusus untuk tombol L1 / switch stik yang oblak, longgar, atau chattering (mengirim sinyal putus-nyambung saat ditahan).
   - Menjaga status tombol tetap aktif selama interval ambang batas (default: 20ms) saat terjadi micro-release / bounce.
   - Dapat diaktifkan/dinonaktifkan dan diatur threshold milidetiknya di menu konfigurasi.

4. **Deadzone & Stick Drift Reducer**:
   - Deadzone filter (default: 11000) untuk meredam stick drift pada stik murah yang mudah goyang / melayang di tengah.

5. **Live Input Viewer**:
   - Menampilkan visualisasi real-time pembacaan tombol, stick, trigger, D-pad, dan status raw vs filtered L1 di layar sebelum masuk ke dalam game.

6. **Penyimpanan Profil Otomatis (`controllers_config.json`)**:
   - Profil gamepad disimpan otomatis berdasarkan GUID dan nama perangkat, sehingga gamepad langsung otomatis aktif saat dicolokkan.

7. **Ubah Nama Tampilan Stik di Windows (`joy.cpl`)**:
   - Mengubah nama gamepad yang muncul di dialog Game Controllers Windows (`joy.cpl`) dan di dalam game.
   - Setiap gamepad memiliki Virtual PID unik sehingga masing-masing stik dapat memiliki nama berbeda (misal: *Stik 1 (Player 1)*, *Stik 2 (Player 2)*).
   - Otomatis tersinkronisasi ke Windows Registry tanpa memerlukan akses Administrator khusus (menggunakan HKCU OEM Properties).
   - Dapat diatur melalui Menu Utama (Menu 6), menu Edit Controller (Menu 3 -> 1), atau klik kanan icon System Tray.
   - Opsi reset cepat kembali ke nama default (*Controller (XBOX 360 For Windows)*).

---

## Prasyarat

- **Windows 10 / 11 (64-bit)**
- **.NET 10.0 SDK / Runtime**
- **ViGEmBus Driver**: Pastikan driver [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) sudah terpasang di Windows.

---

## Cara Menjalankan

Buka terminal di folder project, lalu jalankan:

```powershell
dotnet run --project Fix_L1/Fix_L1.csproj
```

Atau buka file `Fix_L1.slnx` menggunakan Visual Studio 2022 / Visual Studio 2025.
