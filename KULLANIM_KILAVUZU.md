# 🛡️ Warden — Hızlı Başlangıç & Kullanım Kılavuzu

## 📦 1. Kurulum

1. GitHub'da **Releases** sayfasından en son **`Warden-Setup-x.y.z.exe`** dosyasını indir.
2. Çalıştır. Windows "Bilgisayarınız korundu" (SmartScreen) derse: **Ek bilgi → Yine de çalıştır**
   (setup imzasız olduğu için bu uyarı normal).
3. Kurulumda **"PawnIO sürücüsünü kur"** seçili kalsın (CPU sıcaklık/saat/güç sensörleri için gerekli).
4. Bitince Warden tepside açılır. Açılışta başlasın istiyorsan **Ayarlar → Windows ile başlat**.

- **Güncelleme:** Warden açılışta ve 6 saatte bir yeni sürüm var mı bakar; varsa bildirim gelir, tıklayınca indirip kurar ve kendini yeniden açar (Ayarlar → Güncellemeler'den elle de kontrol edebilirsin). İndirilen setup GitHub'daki SHA256 özetiyle doğrulanmadan çalıştırılmaz. Ayarların korunur.
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

2. **Panel:**
   - Uygulama bu sayfayla açılır. Aktif oyun ve oturum süresi, CPU/GPU sıcaklık göstergeleri, son 60 saniyelik sıcaklık grafiği, bellek / GPU hot spot / fan / CPU gücü ve bağlantı durumları tek ekranda. Buradan kayıt başlatabilir veya oyunları tarayabilirsin.

3. **Profilleri Ayarlama (Sonar):**
   - Warden açıldıktan yaklaşık 30 saniye sonra yüklü oyunları kendisi tarar. Yeni veya kaldırılmış oyun varsa bildirim gösterir; bildirime tıklayınca bu sayfa açılır. Ayarlar → Oyunlar bölümünden kapatılabilir.
   - İstediğiniz an **"Yüklü Oyunları Tara"** butonuyla elle de tarayabilirsiniz.
   - SteelSeries GG'de oyuna özel bir profili olan oyunlara (ör. "Valorant Pro Preset", "War Thunder") o profil otomatik atanır. Var olan kurallarınız değiştirilmez; otomatik atanan bir kuralı silerseniz tekrar atanmaz.
   - Bilgisayardan kaldırılan oyunlar bir sonraki taramada listeden ve kurallardan silinir. Elle eklediğiniz oyunlara dokunulmaz.
   - Diğer oyunlar için karşısındaki menüden SteelSeries Sonar profilini eşleştirin.

4. **Sistem Telemetrisi (Sensörler):**
   - CPU ve GPU sıcaklıkları, saat hızları, watt tüketimleri ve kullanım oranlarını canlı izleyin.
   - İstediğiniz metriğin yanındaki `★` butonuna basarak üstteki Hızlı Bakış alanına sabitleyin.
   - `📈` butonuna basarak 60 saniyelik canlı grafiğe dahil edin.
   - **Kayıt:** "⏺ Kaydı başlat" ile o anki değerleri CSV dosyasına yazar (Excel ile açılır). "Kuralı olan bir oyun açıkken otomatik kaydet" seçiliyse oyun açılınca başlar, kapanınca durur. Dosyalar: `%AppData%\Warden\sessions`.
   - **Sıcaklık alarmı (Ayarlar):** CPU veya GPU sıcaklığı 10 saniye boyunca sınırın (varsayılan CPU 90°C, GPU 85°C) üstünde kalırsa bildirim gelir.

5. **GPU Monitör & Afterburner (GPU):**
   - NVIDIA, AMD ve Intel ekran kartlarını destekler. GPU saat hızı belirlenen sınırı aştığında hedef MSI Afterburner profilini otomatik uygular (Afterburner farklı bir klasöre kuruluysa da bulunur).

6. **Aygıt Yöneticisi (Ses):**
   - İstemediğiniz sanal veya hayalet ses aygıtlarını arka planda otomatik devre dışı bırakır.

7. **Sistem Durumu (Durum):**
   - İlk açılışta otomatik gelir. SteelSeries GG / Sonar, MSI Afterburner, PawnIO, ekran kartı ve kurulum konumunu kontrol eder; eksik olan için "İndir" butonu gösterir. Kapalı modüllerin bağımlılıkları "Modül kapalı" olarak gösterilir.

8. **Modüller (Ayarlar → Modüller):**
   - Kullanmadığınız özellikleri kapatabilirsiniz: **Sonar profil geçişi**, **Ses cihazı denetleyicisi**, **Donanım izleme** (sensörler, sıcaklık alarmı, CSV kaydı) ve **GPU / Afterburner profili**.
   - Kapalı modül arka planda hiç çalışmaz: Sonar kapalıyken GG'ye bağlanılmaz, donanım izleme kapalıyken sensör sürücüsü yüklenmez. Sayfası menüde soluk görünür; açmak için sayfadaki **"Modülü aç"** butonunu kullanabilirsiniz.
   - Sonar kapalıyken de oyun takibi sürer: Panel'deki aktif oyun ve oyun açılınca otomatik CSV kaydı çalışmaya devam eder.
   - GPU profili saat hızını sensörlerden okuduğu için donanım izleme açık olmalıdır.

9. **Kapatma:**
   - Sistem tepsisindeki kalkan ikonuna sağ tıklayıp **"Çıkış"** (İngilizcede "Exit") diyerek tamamen kapatabilirsiniz. Pencereyi çarpıdan (X) kapatmak uygulamayı kapatmaz, tepside sessizce korumaya devam eder.
