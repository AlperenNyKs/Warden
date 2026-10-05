# 🛡️ Warden — Hızlı Başlangıç & Kullanım Kılavuzu

## 📦 1. Kurulum

1. GitHub'da **Releases** sayfasından en son **`Warden-Setup-x.y.z.exe`** dosyasını indir.
2. Çalıştır. Windows "Bilgisayarınız korundu" (SmartScreen) derse: **Ek bilgi → Yine de çalıştır**
   (setup imzasız olduğu için bu uyarı normal).
3. Kurulumda **"PawnIO sürücüsünü kur"** seçili kalsın (CPU sıcaklık/saat/güç sensörleri için gerekli).
4. Bitince Warden tepside açılır. Açılışta başlasın istiyorsan **Ayarlar → Windows ile başlat**.

- **Güncelleme:** Yeni sürümün setup'ını çalıştırman yeterli; ayarların korunur.
- **Kaldırma:** Ayarlar → Uygulamalar → Warden → Kaldır (açılış görevi de silinir).

## 📂 2. Dosya Konumları

- **Uygulama:** `C:\Program Files\Warden\Warden.exe`
- **Ayar ve Log Klasörü (AppData):** `%AppData%\Warden\`
  *(`Win + R` → `%AppData%\Warden`)*
  - `config.json` → Tüm kural, gecikme, telemetri favorileri ve ayarlar
  - `logs\service.log` → Arka plan çalışma kayıtları (otomatik 1 MB rotasyonlu)
  - `logs\telemetry.log` → Donanım sensörleri ve telemetri logları
  - `logs\crash.log` → Olası hata kayıtları

> ⚠️ **"Windows ile başlat" yalnızca korumalı klasörde çalışır.** Warden oturum açılışında yönetici yetkisiyle
> başlatılır. `D:\Warden\bin\...` gibi standart kullanıcının yazabildiği bir klasörden çalışırsa, herhangi bir
> program `Warden.exe`'yi veya bir DLL'ini değiştirip yönetici yetkisi kazanabilir; bu yüzden Warden orada
> başlangıç görevi oluşturmaz. Setup ile Program Files'a kurmak bu sorunu çözer.

### Geliştirici: kaynaktan çalıştırma
```powershell
cd D:\Warden
dotnet run -c Release
```

---

## 🎮 3. Özellikler ve Kullanım

1. **Arayüzü Açma:**
   - Ekranın sağ altındaki bildirim alanında (saatin yanında) **Warden kalkan ikonuna** tıklayın.

2. **Profilleri Ayarlama (🎧):**
   - **"Yüklü Oyunları Tara"** butonuna basarak oyunları otomatik tespit edin.
   - Her oyunun karşısındaki menüden SteelSeries Sonar profilini eşleştirin.

3. **Sistem Telemetrisi (📊):**
   - CPU ve GPU sıcaklıkları, saat hızları, watt tüketimleri ve kullanım oranlarını canlı izleyin.
   - İstediğiniz metriğin yanındaki `★` butonuna basarak üstteki Hızlı Bakış alanına sabitleyin.
   - `📈` butonuna basarak 60 saniyelik canlı grafiğe dahil edin.

4. **GPU Monitör & Afterburner (⚡):**
   - GPU saat hızı belirlenen sınırı aştığında hedef MSI Afterburner profilini otomatik uygular.

5. **Aygıt Yöneticisi (🔇):**
   - İstemediğiniz sanal veya hayalet ses aygıtlarını arka planda otomatik devre dışı bırakır.

6. **Kapatma:**
   - Sistem tepsisindeki kalkan ikonuna sağ tıklayıp **"Çıkış"** (İngilizcede "Exit") diyerek tamamen kapatabilirsiniz. Pencereyi çarpıdan (X) kapatmak uygulamayı kapatmaz, tepside sessizce korumaya devam eder.
