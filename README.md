# HP Victus 15 Fan Control

Aplikasi Windows untuk mengatur mode kipas dan memantau suhu serta RPM CPU/GPU melalui antarmuka WMI/BIOS HP. Ditujukan untuk HP Victus 15, termasuk FA1094TX dan seri 15-fa/15-fb. Ketersediaan fitur bergantung pada model, BIOS, dan firmware.

## Preview

![Preview aplikasi HP Victus 15 Fan Control](./preview.png)

## Fitur

- **Auto** — mengembalikan pengaturan kipas ke kontrol otomatis BIOS.
- **Quiet** — preset senyap, target sekitar 2.500 RPM.
- **Balanced** dan **Performance** — preset kecepatan kipas untuk beban sedang dan tinggi.
- **MAX FAN** — meminta kecepatan kipas maksimum.
- Slider untuk memilih level kipas secara manual.
- Tampilan tachometer kipas CPU/GPU, suhu, status, dan log firmware.

Angka RPM preset merupakan perkiraan; kecepatan aktual ditentukan firmware dan kondisi laptop. Sensor suhu atau RPM tertentu mungkin tidak tersedia pada semua model.

## Menjalankan aplikasi

1. Buka folder  `Fan Control Victus 15`.
2. Jalankan `launcher.exe`.
3. Setujui permintaan hak Administrator (UAC).

## Catatan

- Kontrol kipas berinteraksi langsung dengan firmware BIOS. Pilih **Auto** untuk mengembalikan kendali kipas ke profil BIOS.
- Pantau suhu saat menggunakan pengaturan manual dan jangan menghalangi ventilasi laptop.
