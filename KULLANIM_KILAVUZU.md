# 🛡️ Warden — Hızlı Başlangıç & Kullanım Kılavuzu

## 📂 1. Uygulama ve Dosya Konumları

- **Proje Ana Klasörü:**  
  `D:\Warden\`

- **Çalıştırılabilir Uygulama Dosyası (`.exe`):**  
  `D:\Warden\bin\Release\net10.0-windows\Warden.exe`

- **Ayar ve Log Klasörü (AppData):**  
  `%AppData%\Warden\`  
  *(Çalıştır penceresine `Win + R` basıp `%AppData%\Warden` yazarak açabilirsiniz)*
  - `config.json` → Tüm kural, gecikme, telemetri favorileri ve ayarlar
  - `logs\service.log` → Arka plan çalışma kayıtları (otomatik 1 MB rotasyonlu)
  - `logs\telemetry.log` → Donanım sensörleri ve telemetri logları
  - `logs\crash.log` → Olası hata kayıtları

---

## 🚀 2. Uygulamayı Nasıl Açarım?

### Yöntem 1: Masaüstü Kısayolu ile Açma (Önerilen)
- Masaüstündeki **Warden** kısayoluna çift tıklayın.
- Uygulama sistem tepsisinde (**sağ alttaki bildirim alanı / Tray**) kalkan ikonuyla çalışır. Eğer zaten arka planda açıksa doğrudan kontrol panelini ekrana getirir.

### Yöntem 2: Doğrudan `.exe` ile Açma
1. `D:\Warden\bin\Release\net10.0-windows\` klasörüne gidin.
2. **`Warden.exe`** dosyasına çift tıklayın.

### Yöntem 3: Terminalden Açma
PowerShell veya Komut Satırı üzerinden:
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
