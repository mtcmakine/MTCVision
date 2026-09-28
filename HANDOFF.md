# MTCVision — Devam Notu (Handoff)

Son güncelleme: 2026-09-04 (ikinci oturum). Bu dosya, çalışmaya başka bir makinede/oturumda kaldığı yerden devam edebilmek için yazıldı.

## Proje Özeti

Çamaşır makinesi kapağı üzerindeki lazer baskı yazıların ve görsel kusurların (çizik vb.) bir GigE Ethernet endüstriyel kamera ile kontrol edildiği, reçete tabanlı, açık kaynak AI kullanan bir makine görüsü uygulaması. Referans: Keyence IV2-Navigator arayüzü (Run/Program modları, ROI "Window" aracı, Limit Adjustment, Additional Learning).

- **Backend**: C# / .NET 8, console-hosted ASP.NET Core minimal API (`MTCVision/backend/`)
- **Frontend**: React + TypeScript + Vite (`MTCVision/frontend/`)
- **Kamera**: herhangi bir GigE Vision 1.2 / GenICam uyumlu kamera — özel bir SDK yok, protokol doğrudan uygulanmış (aşağıya bakın). Şimdiye kadar görülen iki cihaz: Teledyne DALSA Genie Nano-M2020 ve Zebra FS40/S40 (Fixed Industrial Scanner).
- **AI (henüz başlanmadı)**: OCR (Sdcb.PaddleOCR, ONNX) + PatchCore-tarzı anomali tespiti (ONNX ResNet18 backbone + k-NN memory bank), tamamen C#/.NET içinde, Python bağımlılığı yok

## DURUM: Kamera hattı (M2) TAMAMLANDI ve gerçek donanımda doğrulandı

Uçtan uca çalışıyor: keşif → bağlan → GenICam ayarları (pozlama/kazanç/kare hızı) → canlı GVSP akışı → JPEG → WebSocket → tarayıcı.
Doğrulanmış çıktı: **2064×1544 Mono8, 3.7 kare/sn, 0 eksik kare, 0 atılan blok.**

Sıradaki iş M3: reçete/ROI yönetimi ve AI (OCR + anomali tespiti).

## Kamera Donanımı

Backend'de kameraya özel hiçbir dal yok: `GvcpClient`/`GenApiNodeMap`/`GvspStreamReceiver` GigE Vision 1.2 (GVCP+GVSP) ve GenICam GenApi'yi protokol seviyesinde uyguluyor, kameranın kendi GenICam XML'ini indirip düğüm ağacını çözüyor. Aşağıdaki DALSA notları ("Genie Nano şunu yapıyor...") o kamerayla ilk bring-up sırasında keşfedilen **genel GenApi kuralları** (pAddress/pIndex/Converter/SwissKnife çözümlemesi) — DALSA'ya özel bir if/else değil, dolayısıyla aynı kod GenICam uyumlu başka bir kamerada da çalışır.

### Kamera 1 — Teledyne DALSA Genie Nano-M2020 (gerçek donanımda doğrulandı)

- **Model**: Teledyne DALSA **Genie Nano-M2020** (GigE Vision 1.2 / GenICam), firmware 1.04
- **Sensör**: Sony Pregius IMX265, Mono, global shutter, C-mount
- **GERÇEK ÇÖZÜNÜRLÜK: 2064×1544** — datasheet'teki "2048×1536" değil. Bu, ilk oturumdaki `ArgumentOutOfRangeException`'ın tek sebebiydi. Kameranın kendi bildirdiği `WidthMax`/`HeightMax` de 2064/1544.
- **PixelFormat**: Mono8 (`0x01080001`)
- **Seri No**: S1166816 · **MAC**: 00:01:0D:C3:80:66 · **IP**: `192.168.10.55`
- Kameranın GenICam XML'i: `TeledyneDALSA_Nano-IMX264-265_Mono_3.2M-5.1M_a01bdb1c_9CA18.0016.zip`, 1349 düğüm.

### Kamera 2 — Zebra FS40/S40 Fixed Industrial Scanner (etiketten, henüz bu hatta bağlanıp doğrulanmadı)

- **Model**: Zebra Technologies **S40** (ürün ailesi: FS40) — GigE Vision / GenICam uyumlu makine görüsü kamerası, PoE destekli
- **Host adı**: FS40C66B · **MAC**: 78:B8:D6:5C:C6:6B · **P/N**: FS40-SR20Z4-2C00W · **S/N**: 23046520180858 · **Üretim**: 15FEB23
- **Güç**: 5V/3A veya 10-30V/1.5A veya 48V/625mA (PoE, IEEE 802.3af/at)
- **Lazer**: Class 2 hedefleme lazeri (630-880nm, 1mW) — görüntü sensörünü etkilemez, yalnızca hedefleme/imleç
- Bu kamera ilk bağlandığında `GenICam ağacı yüklendi: N düğüm (<xml dosya adı>)` günlük satırı hangi XML paketini sunduğunu gösterir; DALSA'da kullanılmayan bir düğüm tipiyle karşılaşılırsa `GenApiNodeMap.ReadNodeAsync`/`WriteNodeAsync`'teki `NotSupportedException` bunu adıyla söyler.

## BU MAKİNEDE GEREKEN AĞ KURULUMU (ikisi de şart)

Ağ yapılandırması oturumlar arasında değişebiliyor; kamera erişilemiyorsa önce bu ikisini kontrol edin.

1. **Kamera alt ağında ikincil IP.** Realtek NIC bu makinede bir Hyper-V sanal switch'e bağlı, dolayısıyla host'un IP'si `vEthernet (...)` arayüzünde duruyor. IP'yi o arayüze eklemek gerekiyor:
   ```powershell
   Get-NetAdapter                       # doğru ifIndex'i bulun (vEthernet olan)
   New-NetIPAddress -InterfaceIndex 14 -IPAddress 192.168.10.252 -PrefixLength 24
   ping 192.168.10.55                   # doğrulayın
   ```
2. **Windows Firewall kuralı.** Varsayılan gelen politika "engelle" olduğu için GVSP (görüntü) UDP paketleri sessizce düşüyordu — kamera bağlanıyor, register'lar yazılıyor, ama **0 kare** geliyordu. İlk oturumun çözemediği asıl sorun buydu.
   ```powershell
   New-NetFirewallRule -DisplayName 'MTCVision GVSP Stream (UDP)' -Direction Inbound `
     -Action Allow -Protocol UDP -RemoteAddress 192.168.10.0/24 -Profile Any
   ```
   (Kural kuruldu; `Get-NetFirewallRule -DisplayName 'MTCVision GVSP Stream (UDP)'` ile doğrulanabilir.)

## Mimari (backend)

```
backend/
├── Program.cs                      Kestrel host, CORS (localhost:5173), WS, endpoint kayıtları
├── Api/
│   ├── HealthEndpoints.cs          GET /health
│   ├── CameraEndpoints.cs          /api/camera/{network-interfaces,discover,connect,disconnect,status,
│   │                               settings (GET/PUT), feature/{name} (GET/PUT)}
│   └── LiveStreamEndpoints.cs      GET /ws/live (WebSocket, JPEG binary frame broadcast)
├── Camera/
│   ├── GenICam/
│   │   ├── GvcpClient.cs           Ham GVCP: discovery, READREG/WRITEREG/READMEM/WRITEMEM,
│   │   │                           kontrol kanalı + heartbeat
│   │   ├── GenApiNodeMap.cs        Kameranın kendi GenICam XML'ini indirir ve düğüm ağacını çözer
│   │   └── GenApiFormula.cs        GenApi formül dili değerlendiricisi (SwissKnife/Converter)
│   ├── GvspStreamReceiver.cs       Ham GVSP alıcı: paket birleştirme, çözünürlük data leader'dan
│   ├── GigeCameraService.cs        Kamerayı sahiplenir: kontrol + ayarlar + akış
│   ├── ICameraService.cs           Arayüz
│   ├── CameraFrame.cs              record(byte[] Data, int Width, int Height, GvspPixelFormat, DateTime)
│   ├── GvspPixelFormat.cs          GVSP piksel formatı kodları
│   ├── FrameJpegEncoder.cs         OpenCvSharp ile Mono8/10/12/16 → JPEG
│   └── LiveFrameBroadcaster.cs     FrameCaptured'ı tüm WS client'lara JPEG olarak yayınlar
├── Vision/, Recipes/, Inspection/, Learning/   Boş (M3+ için ayrılmış)
└── Data/                           Recipes/, Images/, Embeddings/, History/, Statistics/ (boş)
```

NuGet: **yalnızca** `OpenCvSharp4` + `OpenCvSharp4.runtime.win`.

### `GigeVision.Core` bağımlılığı KALDIRILDI — neden

Kütüphanenin GenICam XML çözümleyicisi bu kamerada çalışmıyor: `Width`/`Height` hep 0 dönüyor, `Gain`/`SensorWidth` gibi değerler anlamsız (byte-order/offset hatası). Sebep, DALSA'nın XML'inin bu düğümleri düz "isim → sabit adres" eşlemesiyle değil, üç ayrı dolaylılık katmanıyla tanımlaması:

- **`pAddress` (dolaylı adres)**: `ExposureTime` → `pExposureTimeReg`, adresi `exposureTimeAddr` (0x20000830) + `persistenceOffset` (12) düğümlerinin **toplamı**.
- **`Converter` formülleri**: `AcquisitionFrameRate` → ham değer = Hz × 1000; `Gain` → `10 ** (raw/200)`.
- **`IntSwissKnife` ifadeleri**: `Width`'in adresi bile bir formül: `(MULTIPLE_ROI_MODE = 0 || REGISTER_STREAMING) ? 0x20000070 : 0x20001A20`.

Bunun yerine `Camera/GenICam/` altına küçük ama gerçek bir GenApi katmanı yazıldı. Desteklenen düğüm tipleri: `Integer`, `Float`, `IntReg`, `MaskedIntReg`, `FloatReg`, `StructReg`/`StructEntry`, `IntConverter`/`Converter`, `IntSwissKnife`/`SwissKnife`, `Enumeration`/`EnumEntry`, `Boolean`, `Command`; `pValue`, `pAddress`, `pVariable`, `pIndex`/`pValueIndexed`/`pValueDefault` referansları. Formül değerlendirici tam GenApi operatör önceliğini uygular (`?:`, `||`, `&&`, `|`, `^`, `&`, `=`/`<>`, karşılaştırma, `<<`/`>>`, aritmetik, `**`, tekli) ve `ROUND`/`LG`/`ABS` gibi fonksiyonları içerir.

Doğrulandı — hepsi gerçek kameradan doğru okunuyor/yazılıyor: `Width`=2064, `Height`=1544, `ExposureTime`, `Gain`, `AcquisitionFrameRate`, `OffsetX/Y`, `WidthMax/HeightMax`, `PixelFormat`, `AcquisitionStart/Stop`.

### GVSP alıcısı

Çözünürlük **hiçbir yerde parametre değil**: her GVSP bloğunun data leader paketi `size_x`/`size_y`/`pixel_format` taşıyor, alıcı bunu telden okuyor. Bu yüzden `connect` artık `knownWidth`/`knownHeight` istemiyor ve yanlış tahmin diye bir şey kalmadı.

Dikkat edilen noktalar:
- `SO_RCVBUF` = 64 MB. Tam kare 2264 paket ve hat hızında arka arkaya geliyor; varsayılan ~64 KB tampon taşıyordu.
- Alım kendi thread'inde, `AboveNormal` öncelikte, tahsissiz sıkı döngüde.
- Paket boyutu **1444 byte** (`SafeNonJumboPacketSize`). NIC jumbo frame desteklemiyor (MTU 1500); kütüphanenin "bir satır = bir paket" varsayılanı (2084 byte) `WSAEMSGSIZE` veriyordu.
- Sayaçlar ayrıştırılmış durumda: `framesIncomplete` (paket kaybı — bizim tarafımız) ile `blocksAbortedByCamera` (kamera bloğu açıp veri göndermeden kapatıyor — hat kapasitesi yetmiyor) farklı şeyler.

### Ağ arayüzü seçimi

Kamera keşif yayınına **aynı fiziksel kablodaki her arayüzden** cevap veriyor — farklı alt ağdakiler dahil. Ama görüntü akışı için kameraya "veriyi şu adrese gönder" deniyor, dolayısıyla yalnızca kameranın kendi alt ağındaki arayüz işe yarar. Yanlış arayüz seçilince kamera hiç cevap veremiyor ve bağlantı açıklamasız bir GVCP zaman aşımıyla ölüyordu.

Düzeltmeler:
- `GET /api/camera/discover` artık `localIp` olmadan çağrılabiliyor; **tüm IPv4 arayüzleri** taranıyor ve her kamera, onu bulan arayüzle birlikte dönüyor (`localIp` alanı). Aynı kamera birden fazla arayüzden görünürse alt ağı paylaşan tercih ediliyor.
- Frontend arayüz açılır listesini kaldırdı; bağlanırken kameranın kendi `localIp`'sini kullanıyor.
- `connect` alt ağ uyuşmazlığını kameraya hiç dokunmadan reddediyor ve hangi arayüzün kullanılması gerektiğini söylüyor (400, `CameraConfigurationException`).

### Kontrol kanalı (yeniden bağlanma)

Yeniden bağlanma bir süre "GVCP komutu yanıtsız kaldı" ile patlıyordu. İki sebep vardı, ikisi de düzeltildi:

- **GVCP istek numarası her yeni istemcide 1'den başlıyordu.** İşletim sistemi serbest kalan ephemeral kaynak portunu sıklıkla yeniden veriyor; kamera aynı uç noktadan gelen aynı numarayı zaten yanıtladığı bir komutun tekrarı sayıp **hiç cevap vermiyor**. Artık numara rastgele tohumlanıyor (`GvcpClient._requestId`).
- **Kontrol kanalı temiz bırakılamayınca** kamera onu `GevHeartbeatTimeout` (bu kamerada **10 sn**) boyunca tutuyor. `TakeControlAsync` artık bu süreden biraz uzun bir pencerede yeniden deniyor, `GevCCP`'yi geri okuyup gerçekten alındığını doğruluyor, ve başarısız olursa ne yapılacağını söyleyen bir hata veriyor. `ReleaseControlAsync` da bırakmayı birkaç kez deniyor.

Doğrulandı: art arda 5 bağlan, 3 kes/bağlan döngüsü, ve akış ortasında process'i öldürüp yeniden bağlanma — hepsi sorunsuz.

## AÇIK DONANIM SORUNU: hat 100 Mbit/s'te

Kamera hattı **~94 Mbit/s** tavana takılı; bu, tam çözünürlükte **3.7 kare/sn** demek. Ayarlardan bağımsız (kare hızını 40 Hz yapsanız da 3.7 kalıyor, kamera fazlasını iletmeden atıyor).

- Bu bir yazılım limiti **değil**: kameranın `DeviceLinkThroughputLimit` değeri 125.000.000 bayt/sn (= 1 Gbit/s, kameranın maksimumu) ve `framesIncomplete` **her zaman 0** — yani paket kaybı yok, kamera zaten göndermiyor.
- Host NIC'i 1 Gbps görünüyor, dolayısıyla darboğaz **kamera ↔ switch bağlantısı**: büyük olasılıkla yalnızca 2 çift kullanan/hasarlı bir kablo veya 100M'lik bir switch portu.
- **Yapılacak**: kabloyu Cat5e/Cat6 ile değiştirin ve gigabit porta takın. Düzelirse tam çözünürlükte ~30 kare/sn'ye çıkar.
- O zamana kadar kare hızını **3.5 Hz** civarında tutun; daha yükseği kameranın boş blok atmasına yol açar (`blocksAbortedByCamera` artar), ama görüntü kalitesini etkilemez.

Not: `AcquisitionFrameRate` pozlama süresiyle de sınırlı — 150 ms pozlamada kamera üst sınırı 6.66 Hz'e kırpar.

### Özellik aralıkları (Min/Max) ve `0x8008` hataları

- **Min/Max, Converter'ın altındaki ham düğümde** duruyor (`exposureTimeRaw`, `Gain_Reg`), üstteki `Float`'ta değil. `GenApiNodeMap.GetRangeAsync` limitleri orada okuyup `FormulaFrom` ile geri çeviriyor.
- Bu min/max register'ları **`<pIndex Offset="4">` ile taban adresten kaydırılıyor** (DALSA'nın "değer / min / max" üçlüsü). Adres çözümlemesi artık `pIndex` + `Offset`/`pOffset` destekliyor; yoksa min ve max mevcut değerin aynısı çıkıyordu.
- Aralık dışı bir değer (ör. kazanç 500) kameraya gönderilmeden **kırpılıyor**; `PUT /settings` gerçekte uygulanan değeri döndürüyor.
- **`AcquisitionStart` 0x8008 ile reddedilebiliyor**: `AcquisitionStop` ancak o anki karenin sonunda etkili oluyor ve kamera kareyi bitirene kadar yeni başlatmayı kabul etmiyor. Uzun pozlamada bu pencere saniyeler sürüyor. Artık `RunAcquisitionCommandAsync` kameranın kendi pozlama/kare periyodundan hesaplanan bir süre boyunca yeniden deniyor. Ayrıca `AcquisitionStop` `try` bloğunun içine alındı — daha önce başarısız bir yazma kamerayı kalıcı olarak durdurulmuş bırakıyordu.
- GVCP durum kodu tablosu bir kaydırılmıştı (0x8005'e "erişim reddedildi" deniyordu); GEV 1.2 §15.4.1'e göre düzeltildi.

## Sahnenin karanlık olması

İlk ölçümde kareler neredeyse tamamen siyahtı (ortalama piksel ~1.4/255) — ama sebep kameranın limitlerinin kullanılmamasıydı. Kameranın gerçek aralıkları:

- **Pozlama: 25 µs – 16.000.000 µs (16 sn)**
- **Kazanç: 1× – 251×**
- Kare hızı: 0.06 Hz – (pozlamanın izin verdiği üst sınır)

Ölçülen parlaklık (ortalama piksel):

| Ayar | Ortalama |
|---|---|
| 250 ms / 8× | 1.4 |
| 900 ms / 50× | 17.9 |
| 1.6 sn / 60× | 37.4 |
| 1.9 sn / 100× | 71.2 (%2.4 doygun) |

1.6 sn / 60× ayarında **net, ayrıntılı bir görüntü** alınıyor (kablolar, streç filme sarılı nesne) — görüntü hattı baştan sona doğru.

Ancak 0.6 Hz üretim için çok yavaş. Kalıcı çözüm **aydınlatma**: uygun bir makine görüsü aydınlatıcısıyla ~10 ms pozlama ve 1× kazançta tam kare hızında çalışılır. Ayrıca lens diyaframının açık olduğundan emin olun.

## Çalıştırma

```bash
# 1) Ağ kurulumunu doğrula (yukarıdaki iki madde)
ping 192.168.10.55

# 2) Backend
cd MTCVision/backend
dotnet run --urls http://localhost:5080

# 3) Frontend (ayrı terminal)
cd MTCVision/frontend
npm run dev            # http://localhost:5173

# Kabuktan uçtan uca test:
curl -X POST http://localhost:5080/api/camera/connect \
  -H "Content-Type: application/json" \
  -d '{"cameraIp":"192.168.10.55","localIp":"192.168.10.252"}'
curl http://localhost:5080/api/camera/settings
curl -X PUT http://localhost:5080/api/camera/settings \
  -H "Content-Type: application/json" \
  -d '{"frameRateHz":3.5,"exposureTimeUs":250000,"gain":8}'
curl http://localhost:5080/api/camera/status     # istatistikleri içerir
```

`GET/PUT /api/camera/feature/{name}` herhangi bir GenICam özelliğine erişim sağlar — ayar/teşhis için kaçış kapısı (ör. `DeviceLinkThroughputLimit`, `TriggerMode`).

Faydalı: `dotnet run`'ın yazdığı PID çoğu zaman gerçek Kestrel process'i değil (bir launcher). Gerçek process `MTCVision.Backend` adıyla görünür; `Get-Process MTCVision.Backend | Stop-Process -Force` ile durdurun (build "dosya kilitli" hatası verirse sebebi budur).

## M3 — Reçete, ROI ve denetim hattı (TAMAMLANDI, gerçek kamerada doğrulandı)

Uçtan uca çalışıyor: canlı görüntü üzerinde pencere çiz → iyi parçadan limit öğret → reçeteyi kaydet → denetle → pencere bazında OK/NG.

**Model** (`Recipes/Recipe.cs`) — Keyence IV2'nin reçete/"Window"/"Limit Adjustment" yapısını izliyor:
- `Recipe`: ad + öğretildiği kamera koşulları (pozlama/kazanç/kare hızı) + pencere listesi.
- `InspectionWindow`: bir `Roi` (sensör pikselinde) + bir araç + alt/üst limit.
- `InspectionToolKind`: `Brightness` (ROI ortalama gri) ve `Contrast` (ROI standart sapma). Kontrast lazer baskı için asıl araç: boş yüzey düz, baskı/çizik değil.

Her araç pencereyi **tek bir sayıya** indiriyor ve o sayı limitlerle karşılaştırılıyor. OCR ve anomali skoru sonradan aynı şekle oturacak; sonuç modeli değişmeyecek.

**Kalıcılık** (`Recipes/RecipeStore.cs`): `Data/Recipes/<id>.json`, reçete başına bir dosya. Yazma önce `.tmp`'ye yapılıp `File.Move` ile takas ediliyor — elektrik kesintisinde yarım yazma mevcut reçeteyi bozmasın diye. Id'ler HTTP'den gelip dosya adı olduğu için karakter kümesi kısıtlı.

**Denetim** (`Inspection/InspectionEngine.cs`): ROI kırpma + `Cv2.MeanStdDev`. `Measure` ise yargılamadan ölçüyor — "iyi parçadan öğret" bunu kullanıp limitleri ölçülen değerin ±%20'sine kuruyor.

**Uçlar**:
```
GET    /api/camera/snapshot          en son kare, JPEG (kalite 92)
GET    /api/recipes                  liste
POST   /api/recipes                  oluştur
GET    /api/recipes/{id}             oku
PUT    /api/recipes/{id}             güncelle
DELETE /api/recipes/{id}             sil
POST   /api/recipes/{id}/apply       reçetenin kamera ayarlarını kameraya uygula
POST   /api/inspection/run/{id}      en son kareyi reçeteye göre denetle
POST   /api/inspection/measure       bir bölgeyi yargılamadan ölç
```

**Frontend**:
- `hooks/useLiveStream.ts` — tüm uygulama için tek WebSocket; önceki karenin blob URL'i yenisi gelince serbest bırakılıyor.
- `components/RoiEditor.tsx` — görüntünün üzerine bindirilmiş SVG. `viewBox` = sensör boyutu ve `preserveAspectRatio="xMidYMid meet"`, `img`'nin `object-fit: contain`'iyle birebir örtüşüyor; ekran→sensör dönüşümü `getScreenCTM().inverse()` ile yapılıyor, yani letterbox hesabı elle yapılmıyor. Sürükle-çiz, tıkla-seç, taşı, sağ alt köşeden boyutlandır.
- `components/InspectionPanel.tsx` — reçete listesi/düzenleme, pencere aracı ve limitleri, "İyi Parçadan Öğret", "Denetle", sonuç tablosu. OK/NG pencere kenarlıklarına da yansıyor.
- `CameraPanel` artık yalnızca bağlantı + kamera ayarları; canlı görüntü ve istatistik yoklaması `App`'e taşındı (tek soket, tek yoklama).

## M4 — Tetiklemeli yakalama ve masaüstü arayüzü (TAMAMLANDI, gerçek kamerada doğrulandı)

### Tetiklemeli yakalama

Üretim akışının doğru şekli: kamera boşta bekler, tetik gelince **tek kare** üretir ve denetim tam o kareyi yargılar — serbest akışta en yeni hangi kare varsa onu değil.

- `CameraSettings`/`CameraSettingsUpdate` artık `TriggerMode` ("On"/"Off") ve `TriggerSource` ("Software"/"Line1"/…) taşıyor.
- `ICameraService.TriggerAsync()` — `TriggerSoftware` komutunu yollar ve gelen kareyi bekler. Kare olayına **tetiklemeden önce** abone olunuyor: kısa pozlamada kare, komut yazması dönmeden gelebiliyor. Zaman aşımı pozlamadan hesaplanıyor.
- Uçlar: `POST /api/camera/trigger` (tek kare), `POST /api/inspection/trigger-run/{recipeId}` (tetikle + denetle).
- Arayüzde alım modu Kamera diyaloğunda seçiliyor; tetiklemeli modda görüntü çubuğunda **Görüntü Al** düğmesi çıkıyor (kamera boşta olduğu için pencere çizmek de kare gerektiriyor).

Doğrulandı: tetiklemeli modda boşta **5 saniyede 0 kare**, 3 tetikte **tam 3 kare**; `trigger-run` art arda 44.44 / 44.47 / 44.42 gibi tekrarlanabilir ölçüm veriyor.

Bu sırada GenICam katmanında çıkan iki eksik giderildi:
- `Integer` düğümlerinde **literal** `ValueIndexed`/`ValueDefault` desteklenmiyordu (yalnızca `pValueIndexed` vardı); `TriggerMode` bu yüzden okunamıyordu.
- `TriggerSelector` bu kamerada sabit bir değer (yazılamaz). Artık `GenApiReadOnlyException` ile ayırt ediliyor ve tetikleme ayarının tamamını iptal etmiyor.

### Masaüstü arayüzü

Arayüz, Keyence IV-Navigator'ın ana ekranına göre yeniden kuruldu (referans: `AS_93913_IV_SG_96M14941_WW_GB_1028_1.pdf`, s. 18 "Main screen in [Run] / [Program]"). Tarayıcıda çalışan ama masaüstü aleti gibi duran, tam ekranı kaplayan, kendi kaydırılmayan bir kabuk:

- **Başlık çubuğu → menü → bağlantı şeridi → mod çubuğu → gövde → durum çubuğu** dizilimi.
- **Çalıştır / Program** büyük mod düğmeleri (aktif olan kehribar). Program'dan çıkarken reçete otomatik kaydediliyor — referans konsolun davranışı; alternatifi ekranda görünenden farklı bir reçeteyi sessizce denetlemek olurdu.
- Görüntünün üstünde siyah **karar çubuğu**: büyük OK/NG, alım modu, kare hızı, çevrim süresi.
- Sağ sütun, modda göre değişiyor: Çalıştır'da **araç göstergeleri** (ölçüm + limit bandı + OK/NG), araç bilgisi ve istatistik (toplam/OK/NG/süreler/NG oranı); Program'da kurulum, pencere listesi ve limit ayarı.
- Gösterge ölçeği limitlerden türetiliyor, 0–255'in tamamına yayılmıyor: 14–21 aralığındaki bir kontrast bandı 0–255'lik çubukta okunmaz hale geliyordu.
- Çalıştır modunda **Sürekli** döngü: `while` döngüsü, `setInterval` değil — yavaş bir denetim bir sonrakini geciktirsin, üst üste binmesin.
- Tema sabit açık gri; hatta duran bir konsolda OK/NG'nin her istasyonda aynı renkte olması gerekiyor.

Dosyalar: `App.tsx` (kabuk + durum), `components/{ImageStage,RoiEditor,StatusGauge,RunPanel,ProgramPanel,CameraDialog}.tsx`, `components/shell.css`, `components/runStatistics.ts`, `hooks/useLiveStream.ts`.

### Arayüz doğrulaması

`playwright` (Python) kurulu; `pip install playwright && python -m playwright install chromium`. Tarayıcıdan gerçek kamerayla tam akış sürüldü: bağlan → ayar → sürükleyerek iki pencere çiz → iyi parçadan öğret → Çalıştır'a geç (kaydeder) → sürekli denetim → OK; sonra bir limiti kasten bozup NG. Konsol hatası yok.

Not: Vite varsayılanda yalnızca IPv6 loopback'te (`[::1]`) dinliyor ve Chromium oraya bağlanamıyor. Playwright ile test ederken `npx vite --host 0.0.0.0 --port 5173` ile başlatın (CORS `http://localhost:5173` origin'ine bağlı olduğu için adres çubuğunda yine `localhost` kullanın).

## M5 — IV2 kurulum özellikleri ve uygulama kimliği (TAMAMLANDI, gerçek kamerada doğrulandı)

Referans konsolun kurulum adımları (`STEP1-1 Trigger Options`, `STEP1-2 Automatic Brightness Adjustment`, `STEP1-3 Focus Adjustment`, s. 23 "image tool bar") karşılık buldu.

### Kamera Kurulumu diyaloğu (sekmeli)

- **Bağlantı** — tara/bağlan/kes, sensör sıcaklığı (kamera `DeviceTemperature` veriyor).
- **Tetikleme** — Sürekli / Dahili tetik / Harici tetik (Line1). Harici seçilince tetik kenarı (`TriggerActivation`: yükselen/düşen/her kenar/seviye) ve tetik gecikmesi (`TriggerDelay`, 0–2 s) alanları çıkıyor. Dahili tetiğin aralığı (1–10.000 ms) mod çubuğunda.
- **Parlaklık** — otomatik ayar (aşağıda) + elle pozlama / kazanç / **siyah seviye** (`BlackLevel`, 0–4095) / kare hızı, her biri kameranın bildirdiği aralıkla.
- **Netlik** — canlı netlik ölçer.
- **Görüntü** — yatay/dikey çevirme (`ReverseX`/`ReverseY`) ve **Ayarları Kameraya Kaydet** (`UserSetSave`; ayarlar elektrik kesilse de korunur).

### Otomatik parlaklık ayarı

Kameranın kendi regülatörü (`autoBrightnessMode`) bu karanlık sahnede yakınsamadı — pozlamayı ters yöne götürdü. Yerine `AutoBrightnessTuner`: hedef gri seviyeye kapalı çevrimle yaklaşan, sınırlı (en fazla 12 adım), ne yaptığını adım adım raporlayan bir ayarlayıcı. Bitince pozlama ve kazanç **sabit** kalır — reçetenin öğretilmiş limitleri ancak sabit koşulda anlamlıdır.

Önemli bir ayrıntı: kamera pozlamanın üst sınırını kare hızından bağımsız (mutlak 16 s) bildiriyor ve yazılan pozlamayı karşılamak için kare hızını **sessizce düşürüyor**. Bu yüzden "en düşük kare hızı" bir istek değil, pozlamaya konan bir tavan olarak uygulanıyor; aksi halde karanlık bir sahnede 16 saniyelik pozlamada karar kılıyordu.

Doğrulandı: 2 Hz tabanında parlaklık 0 → **108.9** (hedef 110), pozlama 400 ms, kazanç 184, kare hızı 2 Hz korunuyor. Ulaşılamadığında sebebini söylüyor ("Sahne bu kare hızında hedefe ulaşacak kadar aydınlık değil — aydınlatma ekleyin").

### Netlik yardımcısı

Bu kamerada **motorlu lens yok**, dolayısıyla IV2'deki gibi otomatik odak fiziksel olarak mümkün değil. Karşılığı: `POST /api/camera/focus` bölgenin Laplacian varyansını döndürüyor, arayüz bunu 0,5 sn'de bir okuyup büyük bir sayı ve tepe-tutmalı bir çubuk olarak gösteriyor — operatör lens halkasını çevirirken bu değeri en yükseğe çıkarıyor. Ölçüm yalnızca kendisiyle karşılaştırılabilir (tek sahne, tek çalışma mesafesi).

### Görüntü araç çubuğu

Referanstaki gibi: uzaklaştır/yakınlaştır (%100/125/150/200/300/500), **Sığdır**, büyütme oranı, **Pencereler** göster/gizle anahtarı, **Manuel Tetik** (yalnızca tetiklemeli modda), **Görüntüyü Kaydet** (JPEG indirir). Yakınlaştırıldığında sahne kaydırılabiliyor; ROI bindirmesi görüntüyle aynı kutuyu paylaştığı için koordinatlar zoom'dan etkilenmiyor.

### Uygulama kimliği

Sol üstteki "MTCVision" başlığı kaldırıldı; menü çubuğu doğrudan kontrollerle başlıyor. Uygulama adı, sürümü, **API adresi ve bağlantı durumu** ile bağlı kameranın künyesi **Hakkında** diyaloğunda. Menü çubuğunun sağında tek satırlık sunucu durumu duruyor.

### GenICam katmanında bu iş sırasında eklenenler

- `Boolean` düğümlerinde `OnValue`/`OffValue` (ReverseX/Y bunları kullanıyor).
- SwissKnife içindeki `<Expression Name="…">` alt ifadeleri — `autoBrightnessTarget` bunlar olmadan çözülemiyordu.

## M6 — Açılış bağlantısı, ayar diyaloğu ve kamera kimliği (TAMAMLANDI, gerçek kamerada doğrulandı)

### Açılış ekranı

Uygulama, referans aracın "activation menu"sü gibi bir bağlantı ekranıyla açılıyor (`components/StartupScreen.tsx`): sunucu yanıt verir vermez ağ kendiliğinden taranıyor, bulunan kamera seçili geliyor, **Bağlan** deyince ana konsol açılıyor. **Kamerasız Aç** seçeneği de var — kayıtlı reçeteler kamera olmadan da incelenebilsin diye. Ana ekrandaki **Kamerayı Değiştir** bu ekrana geri dönüyor.

Tarama otomatik başlıyor çünkü hatta normalde tek kamera var; her vardiya "ara" sonra "bağlan" dedirtmek gereksiz tören.

### Kamera Ayarları diyaloğu — taslak + Kaydet/Çık

Diyalog artık bir **taslak** tutuyor: alanlar değiştirildiğinde kameraya hiçbir şey yazılmıyor, altta "kaydedilmemiş değişiklik var" uyarısı çıkıyor. Alt çubukta üç düğme var:

- **Çık** — yazmadan kapatır (geri alınacak bir şey yok, çünkü hiç yazılmadı).
- **Uygula** — yazar, açık kalır.
- **Kaydet ve Çık** — yazar ve kapatır.

Tüm bekleyen değişiklikler **tek çağrıda** yazılıyor; kamera alımı alan başına değil, bir kez döngüye giriyor.

Ayar değil de **eylem** olan düğmeler (Otomatik Ayarla, Ayarları Kameraya Kaydet, Bağlantıyı Kes, Tepe Değeri Sıfırla) anında etki ediyor — bunları ertelemenin anlamı yok.

### Kamera kimliği ve ağ ayarları (yeni "Kimlik ve Ağ" sekmesi)

- **Kamera adı** (`DeviceUserID`, StringReg @0x00E8, 15 karakter) — yazılıyor ve **hemen** geçerli oluyor. Doğrulandı: ad değiştirildikten sonra GVCP ağ taraması yeni adı gösteriyor.
- **Ağ ayarları** — `GevPersistentIPAddress` / `GevPersistentSubnetMask` / `GevPersistentDefaultGateway` ve adres alma yöntemi (`GevCurrentIPConfiguration`: Sabit IP / DHCP / LLA).

Ağ ayarları için önemli iki nokta, ikisi de arayüzde yazıyor:
- **Yeni adres ancak kamera yeniden başlatıldığında geçerli olur.** GigE Vision, çalışan bir kamerayı yeni adrese taşımaya izin vermiyor; kalıcı ayarlar yazılıyor ve kamera açılışta bunları alıyor. API yanıtı `rebootRequired` alanıyla bunu bildiriyor.
- Yazma sırası önemli: adres önce, yöntem sonra. Aksi halde kamera "Sabit IP"ye geçirilip eski kalıcı adrese yönlendirilmiş olurdu.

Geçersiz adres kameraya hiç gitmeden reddediliyor (`'999.1.2.3' geçerli bir IPv4 adresi değil.`).

Uçlar: `GET /api/camera/identity`, `PUT /api/camera/identity`.

GenICam katmanına bu iş için **StringReg** okuma/yazma eklendi (sabit genişlikli, NUL dolgulu ASCII alan; son bayt her zaman NUL bırakılıyor).

## M7 — Görüntü geçmişi ve marka (TAMAMLANDI, gerçek kamerada doğrulandı)

### Görüntü kaydı ve geçmişi

Referans konsolun `[Image Hist.]` ekranının karşılığı. `Inspection/ImageHistoryStore.cs`:

- **Kayıt klasörü ayarlanabilir** (varsayılan `Data/Images`). Yol operatörden geldiği için mutlak hale getirilip oluşturulabilirliği doğrulanıyor.
- **En fazla görüntü sayısı ayarlanabilir** (varsayılan 1000, üst sınır 10.000). Dolunca **en eskisi siliniyor** — halka tampon. Hatta gözetimsiz çalışan bir sistemde sürekli büyüyen klasör diski doldurur; bir hafta sonra gelen operatör en yenileri bulmalı.
- **Kayıt kuralı**: `Manual` (yalnızca elle), `All` (her denetim), `FailuresOnly` (yalnızca NG — çalışan hatta genelde bu istenir).
- Her görüntü bir `.jpg` + yanında `.json` künye (zaman, çözünürlük, boyut, OK/NG, reçete, not). Reçete deposundaki gibi "dosya başına bir kayıt" — bozulan bir dosya diğerlerini gizlemiyor.
- Denetim sırasında kayıt **hata yutuyor**: dolu ya da yazılamayan bir klasör, başarılı bir denetimi hataya çevirmemeli.
- Kimlikler dosya adı olduğu için klasör dışına çıkma denemeleri reddediliyor (`/api/images/../..` → 400).

**Önceki fotoğraftan OK/NG**: `POST /api/images/{id}/inspect/{recipeId}` kayıtlı JPEG'i çözüp güncel reçeteye göre yeniden yargılıyor. Bir saat önce reddedilen parçanın, değiştirilen limitle geçip geçmeyeceğini görmenin yolu bu. `InspectionEngine.Run` artık `CameraFrame` yanında doğrudan `Mat` de alıyor.

Uçlar: `GET /api/images` (ayarlar + liste), `GET|PUT /api/images/settings`, `GET /api/images/{id}` (JPEG), `POST /api/images` (elle kaydet), `DELETE /api/images/{id}`, `DELETE /api/images`, `POST /api/images/{id}/inspect/{recipeId}`.

Arayüz: menüdeki **Görüntü Geçmişi** — klasör/kapasite/kural ayarları, küçük resimli liste (zaman, reçete, boyut, OK/NG rozeti), önizleme, **Bu Görüntüyü Denetle**, seçiliyi sil, tümünü sil.

Doğrulandı: kapasite 5'e indirilip 8 denetim koşuldu → tam 5 görüntü kaldı (en yenileri). Kayıtlı görüntü üzerinden yeniden denetim pencere bazında OK/NG verdi. Özel klasör, elle kayıt ve toplu silme çalışıyor.

### Marka

`logo.svg` (MTC dişli) `frontend/public/logo.svg` altına alındı ve her yerde kullanılıyor: tarayıcı sekmesi ikonu, açılış ekranı başlığı, Hakkında diyaloğu. Renk değişkenlerine `--brand`/`--brand-dark` (logonun mavisi) eklendi; açılış başlığı bu maviyi kullanıyor, kehribar yalnızca Çalıştır/Program aktif mod göstergesine ayrıldı (referans konsoldaki işlevi).

## M8 — Arka planda çalışma, OK/NG öğretme, arayüz düzeltmeleri (TAMAMLANDI)

### Denetim döngüsü sunucuya taşındı

Döngü tarayıcıdaydı; sekme kapanınca hat duruyordu. Artık `Inspection/InspectionRunner.cs` sunucuda çalışıyor: `POST /api/inspection/run/start/{recipeId}`, `/run/stop`, `GET /api/inspection/run`. Tarayıcı yalnızca aynayı tutuyor — 500 ms'de bir durumu çekiyor.

Bir çevrimin hatası (düşen kare, kamera takılması) döngüyü bitirmiyor; hata bildirilip devam ediliyor. Hat hâlâ akıyor.

**Oturum geri yükleme**: uygulama açılışta sunucuya "ne yapıyorsun" diye soruyor. Kamera bağlıysa açılış ekranı atlanıyor, çalışan bir denetim varsa reçetesi yüklenip Çalıştır moduna geçiliyor.

Doğrulandı: tarayıcı tamamen kapatıldı, 10 sn beklendi, yeniden açıldı → uygulama doğrudan Çalıştır modunda, bu sürede biriken 50 denetimle geldi.

**Windows servisi**: `builder.Host.UseWindowsService()` eklendi ve `backend/install-service.ps1` yazıldı. Servis olarak kurulunca arka uç konsol penceresine bağlı olmaktan çıkıyor, makineyle birlikte başlıyor ve oturum kapansa da sürüyor. Arayüz artık sadece bir görüntüleyici.

### OK/NG örneğinden limit öğretme (Tool Auto Tuning karşılığı)

`Inspection/ToolAutoTuner.cs` ve `InspectionWindow.Samples`:

- **İyi Parça (OK)** / **Kötü Parça (NG)** düğmeleri, o anki kareden **bütün pencereleri tek seferde** ölçüp örnek olarak ekliyor. Tek çağrı, çünkü bütün pencereler aynı parçayı okumalı.
- **Limitleri Hesapla** her pencere için limitleri örneklerden türetiyor: yalnızca iyi örnek varsa küme çevresine pay bırakarak, iyi+kötü varsa iki küme arasının **ortasına** — böylece sınırdaki bir parça iki yöne de eşit uzaklıkta kalıyor.
- Kötü örnekler iyi örneklerin **arasına düşüyorsa** hiçbir eşik ayıramaz; bu durumda limit uydurmak yerine açıkça söylüyor: *"İyi ve kötü parçaların ölçümleri iç içe geçiyor; bu araçla ayrılamazlar."* Doğru cevap başka bir eşik değil, farklı bir araç veya daha iyi aydınlatma.
- Örnekler reçeteyle birlikte saklanıyor — bir limitin dayandığı kanıt reçeteyle beraber taşınıyor ve sonradan örnek eklenebiliyor. Seçili pencerenin örnekleri yeşil/kırmızı rozetlerle gösteriliyor.

Uçlar: `POST /api/inspection/sample`, `POST /api/inspection/auto-limits`.

### Arayüz düzeltmeleri

- Açılış ekranındaki iki kart **eşit yükseklikte**; düğmeleri alta sabitlendi.
- Tek kamera varken açılır liste, boş satır ve kaydırma oku gösteren bir liste kutusu yerine normal açılır menü.
- Diyalog gövdesi artık kalan alanı dolduruyor; sabit yükseklikli **Görüntü Geçmişi** penceresinde alt çubuk ortada asılı kalmıyordu.
- "Endüstriyel Görüntü İşleme Konsolu" sloganı her yerden kaldırıldı.
- Görüntü geçmişi ayarları artık `Data/image-history.json` içinde kalıcı — yalnızca NG kaydetmeye ayarlanmış bir hat yeniden başlatmadan sonra da öyle kalıyor.

## M9 — OCR aracı ve araç deneme (TAMAMLANDI, doğrulandı)

### OCR

`Vision/OcrEngine.cs` — Tesseract 5 (`Tesseract` NuGet, yerel DLL'ler pakette) + `Vision/tessdata/eng.traineddata` (4 MB). **Tamamen çevrimdışı**; dil dosyası csproj ile çıktı klasörüne kopyalanıyor.

Lazer baskı, OCR'ın eğitildiği basılı metnin tersi: sığ, düşük kontrastlı ve çoğu zaman zemininden **açık** renkli. Bu yüzden okuma öncesi bölge normalize edilip büyütülüyor (Tesseract'ın istediği ~64 px karakter yüksekliğine, en çok 6×), Otsu ile eşikleniyor ve **iki polarite de** denenip güveni yüksek olan seçiliyor — motor koyu-üstüne-açık bekliyor, lazer marka sıklıkla tersi.

Yeni araç: `InspectionToolKind.Ocr`. Pencereye **beklenen metin** giriliyor; ölçüm, okunan metnin beklenene **benzerlik yüzdesi** (Levenshtein tabanlı, büyük/küçük harf ve boşluk duyarsız). Tek ölçüm + limit şekli korunduğu için sonuç modeli, göstergeler ve öğretme akışı değişmedi.

Beklenen metin girilmezse araç yine bir sayı üretmek zorunda: okuma güvenini raporluyor ve bunu açıkça söylüyor.

Doğrulandı (bilinen metinli sentetik parça görüntüsü üzerinde):
- Doğru metin → okunan `MTC-2026-A17`, **%100**, OK
- Yanlış beklenen metin (`MTC-2026-B99`) → **%75**, NG
- Beklenen metin boş → değer 89 (okuma güveni) + açıklama

Arayüzde de uçtan uca çalıştı: araç listesinden OCR seçilip beklenen metin girildi, kayıtlı görüntü üzerinden denetim OK verdi. Okunan metin araç göstergesinde ve araç bilgisinde gösteriliyor.

### Araç deneme

Limit Ayarı panelinde **Aracı Dene**: seçili pencerenin aracını o anki karede çalıştırıp sonucu yargılamadan gösteriyor — değer, OCR ise okunan metin, ayrıca parlaklık/kontrast. Ölçüm yolu yargılama yoluyla **aynı kod**, dolayısıyla denemede görülen sayı denetimde çıkacak sayıdır.

### Yapısal değişiklik

`InspectionEngine` statik sınıftan enjekte edilen bir sınıfa dönüştü (OCR motoruna ihtiyacı olduğu için). Bir aracın kırpılmış bölgeyi sayıya çevirdiği **tek yer** `MeasureRoi`; hem yargılama hem deneme oradan geçiyor.

## M10 — Araç ekleme ve reçete yönetimi (TAMAMLANDI)

### Araç ekleme

Program panelindeki "Pencereler" bölümü **"Araçlar"** oldu ve başına bir **araç tipi seçici + "Araç Ekle"** düğmesi geldi. Eklenen araç görüntünün ortasına, karenin dörtte biri boyutunda yerleşiyor; oradan sürüklenip boyutlandırılıyor.

Sürükleyerek çizme de duruyor ve **aynı seçili araç tipini** kullanıyor — iki yol da aynı yere çıkıyor. Yeni aracın adı tipinden türetiliyor (`OCR 1`, `Parlaklık 2`) ve OCR için üst limit 255 yerine 100'e kuruluyor, çünkü ölçüm yüzde.

Önceki akışta araç tipi ancak çizdikten sonra seçilebiliyordu; artık ne eklendiği baştan belli.

### Reçete yönetimi

**Yeni / Kaydet / Sil** düğmeleri mod çubuğunda, reçete seçicinin yanına alındı — menüde de duruyorlar ama asıl yerleri burası. "Yeni" reçeteye baştan bir ad veriyor (`Reçete 3`), böylece kaydetmek için ayrıca ad yazmak gerekmiyor.

**Silme artık onay istiyor** (`components/ConfirmDialog.tsx`): kaç araç kaybedileceğini söylüyor ve geri alınamayacağını belirtiyor. Bir reçeteyi silmek, öğretilmiş örnekleri de götürüyor; bunlar kameradan geri gelmiyor.

### Sağ sütun taşması

Sağ sütundaki paneller esnek kutu içinde içeriklerinin altına inebiliyordu; araç listesi satırın ortasından kesilip alttaki panelin başlığının üzerine biniyordu. Paneller artık içeriklerinden küçülmüyor (`flex: none`, dolduran panelde `flex: 1 0 auto`) ve yer yetmediğinde **sütunun kendisi kayıyor**. Uzun araç listesi ayrıca kendi içinde kaydırılıyor.

## M11 — PP-OCRv5 ve kalıcı çalışma (TAMAMLANDI, ölçüldü ve doğrulandı)

### OCR motoru değiştirildi: Tesseract → PP-OCRv5

Tesseract'ın sonuçları hatalıydı. Karar tahmine değil ölçüme dayandırıldı: bu kameranın gerçekten gördüğü koşulları taklit eden yedi test görüntüsü üretildi (sığ marka, yüksek kazançta sensör gürültüsü, eşit olmayan aydınlatma, parlama, hafif odak kaybı) ve iki motor aynı görüntülerde, aynı ölçütle karşılaştırıldı.

| | Tesseract | PP-OCRv5 |
|---|---|---|
| Ortalama karakter benzerliği | %79 | **%100** |
| Tam eşleşme | 3/7 | **7/7** |

Tesseract gürültüde `MTC 2026017`, küçük+eğik durumda `MTC-2026.417` okudu; **odak dışı + düşük kontrastta hiçbir şey döndürmedi**. Fark mimari: Tesseract ikilileştirip şekil eşleştiriyor, bu da lazer markanın sahip olmadığı kontrastı gerektiriyor. PP-OCR ise metin algılayıcı + bozulmuş gerçek dünya metniyle eğitilmiş evrişimli tanıyıcı.

**Paket**: `RapidOcrNet` 4.1.0 — PP-OCRv5 modellerini (algılama 4.8 MB, Latin tanıma 7.9 MB, satır yönü 1 MB, sözlük) paketin içinde getiriyor ve ONNX Runtime ile çalıştırıyor. Paddle'ın ~500 MB'lık native çalışma zamanı gerekmiyor; **tamamen çevrimdışı**. Tesseract ve `Vision/tessdata` kaldırıldı.

Ön işleme neredeyse tamamen kaldırıldı: eski motorun ihtiyaç duyduğu Otsu eşikleme ve polarite çevirme artık bilgi yok ediyor — PP-OCR koyu zemin üzerine açık markayı zaten kendi hallediyor. Yalnızca küçük kırpmalar büyütülüyor (hedef 96 px satır yüksekliği), çünkü algılama küçültülmüş kopya üzerinde çalışıyor.

Güven değeri artık **karakter başına tanıma skorlarının** ortalaması, algılayıcının kutu skoru değil: kutu emin bulunmuşken içindeki karakterler tahmin olabilir.

**Gerçek parçada doğrulama**: kameranın önündeki çamaşır makinesi panelinde `Perde`, `Yünlüler Elde`, `Narin/İpek`, `Ek programlar`, `Durulama` doğru okundu.

**Operatör için önemli bulgu**: pencere ne kadar darsa okuma o kadar isabetli. Aynı yazı 1900 px genişlikte bir pencerede `Boaltma`, yazının etrafına sıkıca çizilmiş 460 px'lik pencerede `Boşaltma` okundu. Algılama küçültülmüş kopya üzerinde çalıştığı için geniş pencerede karakter başına piksel azalıyor. **Pencereyi yazının etrafına dar çizin.**

Türkçe karakterlerin tamamı (ş, ı, İ, ğ, ü, ö, ç) sözlükte mevcut. Kalan tek zorluk noktasız `ı` ile `i` ayrımı; bu çözünürlükte fiziksel sınıra yakın.

### Kalıcı çalışma

Kullanıcı "çalıştırdım, sayfayı yeniledim, çalışmıyordu" dedi. Üç ayrı eksik vardı, üçü de giderildi:

**1. Sunucu konsol penceresine bağlıydı.** Artık gerçekten Windows servisi olarak kurulu (`install-service.ps1` çalıştırıldı): `Automatic` başlatma, hata durumunda 5 sn / 15 sn / 60 sn ile yeniden başlatma. Makine yeniden başlasa da kalkıyor.

**2. Arayüz ayrı bir sunucu istiyordu.** Vite çıktısı artık `backend/wwwroot` altına derleniyor ve arka uç tarafından sunuluyor. Tek servis, tek adres: **http://localhost:5080**. Ayrıca çalıştırılacak bir dev sunucusu yok. Geliştirmede Vite hâlâ 5173'te çalışıyor ve `.env` üzerinden 5080'e bağlanıyor.

**3. Yeniden başlatma çalışmayı unutuyordu.** `SessionStore` kamera adresini ve çalışan reçeteyi `session.json` içine yazıyor; `SessionRestorer` açılışta kamerayı geri bağlıyor (2/10/30 sn artan denemelerle — kamera aynı elektrik hattındaysa makineyle birlikte açılıyor) ve denetimi kaldığı yerden başlatıyor.

Bu sırada ince bir hata çıktı: servis kapanırken `Dispose` → `StopAsync` çalışıp oturumu "operatör durdurdu" diye temizliyordu, dolayısıyla yeniden başlatma hiçbir zaman devam edemiyordu. Kapanma ile kasıtlı durdurma ayrıldı.

Doğrulandı: denetim çalışırken servis yeniden başlatıldı → kamera 2 sn'de geri bağlandı, denetim 3 sn sonra kendi kendine devam etti. Tarayıcı yenilendiğinde açılış ekranı değil, çalışan konsol geliyor.

### Veri konumu

Servis `publish/` altından çalıştığı için veri klasörü ikiye ayrılmıştı — mevcut reçeteler kaybolmuş görünüyordu. Veri kökü artık ikiliden bağımsız: **`C:\ProgramData\MTCVision`** (`MTCVISION_DATA` ile değiştirilebilir). Eski konumdaki veriler ilk açılışta bir kereye mahsus kopyalanıyor.

Servis olarak çalışırken okunacak konsol olmadığı için günlük `C:\ProgramData\MTCVision\mtcvision.log` dosyasına yazılıyor (5 MB'da devrediliyor).

### Kurulum

```powershell
cd MTCVisionackend
.\install-service.ps1        # arayüzü derler, yayınlar, servisi kurar ve başlatır
.\install-service.ps1 -Uninstall
```

## M12 — Canlı OCR teşhisi: neyin sınırladığı ölçüldü

Kullanıcı canlı denedi: panelde `Sıkma/Boşaltma` yazıyor, araç `Sikma/Boşlatma` okuyordu. Sırayla elendi:

**Ön işleme mi?** Hayır — daha kötü yapıyor. Ham girdi `Sikma/Boşaltma`, normalize/keskinleştirme/kontrast varyantlarının hepsi daha çok harf kaybetti. PP-OCR'ı neredeyse ham beslemek doğru tasarımmış.

**Çözünürlük mü?** Hayır. Aynı yazıyı 1×'ten 8×'e büyüterek denedim; sonuç düzelmedi ve *sentetik* metinde daha büyük punto daha çok hata verdi (14pt doğru, 56pt yanlış). Basit bir piksel eşiği yok; sonuç çerçevelemeye göre değişiyor.

**Model mi?** Kısmen. Temiz, yüksek kontrastlı `Sıkma/Boşaltma` render'ını **tam doğru** okuyor — yani model Türkçeyi biliyor, sözlükte bütün Türkçe karakterler var.

**Görüntü mü?** Asıl mesele burada. Yazı bölgesini 8× yakınlaştırdığımda görüntü ağır gürültülü ve yumuşaktı. Kamera **kazanç 135× (üst sınır 251×) ama pozlama yalnızca 25 ms** kullanıyordu — oysa 2.2 Hz'de ~400 ms'ye kadar pozlanabilir. Kazancı pozlamaya takas edince (200 ms / 17×, parlaklık aynı) gürültü kayboldu.

Gürültüsüz görüntüde harf **açıkça noktasız `ı`** ve model yine `i` diyor. Yani kalan hata modelin gerçek bir zayıflığı; görüntüde bilgi var, model kullanamıyor.

### Bundan çıkan üç düzeltme

**1. Netlik ölçeri gürültüyü ölçüyormuş.** Aynı odak dışı sahne kazanç 135×'te 6924, 17×'te 883 veriyordu — operatör bu sayıya bakarak odaklasa, kazancı artırmakla ödüllendirilirdi. Laplacian öncesine 3×3 medyan filtre kondu (Gauss yetmedi); yüksek kazancın ürettiği tek piksellik atım gürültüsünü siliyor, karakter kenarlarını bırakıyor.

**2. Türkçe karakter toleransı** (`InspectionWindow.IgnoreDiacritics`, varsayılan açık). `ı/i`, `ş/s`, `ğ/g`, `ü/u`, `ö/o`, `ç/c`, `İ/I` karşılaştırmada eşit sayılıyor. Denetimin işi doğru etiketin parçada olduğunu doğrulamak, OCR'ın Türkçesini not vermek değil. Sıkı kontrol isteyen kapatabilir.

Burada .NET'e özgü bir tuzak vardı: `char.ToUpperInvariant('ı')` noktasız ı'yı **değiştirmiyor** (`'i'` ise `'I'` oluyor). Katlama tablosuna açıkça eklenmesi gerekti.

**3. Ölçüm yanıtı hangi modda karşılaştırıldığını da döndürüyor** (`ignoreDiacritics`), böylece bir sonuç tartışılırken hangi kuralın uygulandığı görünür.

### Operatör için pratik kurallar

- **Pencereyi yazının etrafına dar çizin.** Aynı yazı 1900 px'lik pencerede `Boaltma`, 460 px'likte `Boşaltma` okundu.
- **Kazanç yerine pozlama kullanın.** Kare hızı izin verdiği sürece pozlamayı uzatmak gürültüyü kazanç artırmaktan çok daha iyi bir takas.
- **Lensi netleyin.** Netlik ölçeri artık dürüst; en yüksek değere getirin.

Gerçek panelde son durum: `Ek programlar` %100, `Sıkma/Boşaltma` %92.86 (yalnızca ş düşüyor). Limit 85 civarına kurulursa güvenli çalışır; örnekten öğretme akışı bunu zaten kendisi yapıyor.

## M13 — Daha iyi tanıma modeli ve çok dilli OCR (TAMAMLANDI, ölçüldü)

### Model seçimi ölçüyle yapıldı

Kullanıcı canlı denemede `Sıkma/Boşaltma` yerine hatalı okuma bildirdi. Gerçek panelden 8 kelimelik, yer gerçekliği bilinen bir test seti çıkarıldı (`Yorgan, Perde, Sıkma/Boşaltma, Durulama, Mix, Yünlüler Elde, Narin/İpek, Ek programlar`) ve dört model kombinasyonu **uygulamanın kendi karşılaştırma kuralıyla** ölçüldü:

| Tanıma modeli | Ortalama | Tam eşleşme | Süre |
|---|---|---|---|
| latin mobil (paketle gelen) | %99.1 | 7/8 | 317 ms |
| **ch mobil (seçilen)** | **%100** | **8/8** | 299 ms |
| ch server (84 MB) | %96.3 | 6/8 | 654 ms |
| ch server + server algılama | %83.3 | 4/8 | 823 ms |

**Büyük model daha iyi değil.** Server sürümleri hem daha kötü hem iki kat yavaş; server *algılama* modeli (88 MB) hiçbir fayda sağlamadı. Darboğaz algılama değil, tanıma.

Kazanan `ch_PP-OCRv5_rec_mobile` çünkü Türkçe aksanlı harfi **düşürmek yerine sade karşılığıyla değiştiriyor** (`Boşaltma` → `Bosaltma`, latin modelinde `Boaltma`). Aksan toleransıyla ikame tam eşleşme sayılır, silme sayılmaz. Sentetik kolay ve zor setlerde de gerileme yok (ikisinde de 7/7).

Canlı kamerada doğrulandı: 8 kelimenin **tamamı %100**.

### Çok dilli destek

Bir tanıma modeli tek bir yazı sistemini bilir. `Vision/models/languages.json` ile beş dil paketi kurulu; motor her paketi **ilk kullanımda** yüklüyor (algılama ve satır yönü paylaşımlı, çünkü yazıdan bağımsız):

| Paket | Kapsam | Boyut |
|---|---|---|
| `latin` (varsayılan) | Türkçe, İngilizce, İtalyanca, Almanca, Fransızca, İspanyolca, Yunanca, Çince | 16 MB |
| `arabic` | Arapça, Farsça, Urduca | 8 MB |
| `cyrillic` | Rusça, Ukraynaca, Bulgarca, Sırpça | 8 MB |
| `korean` | Korece | 13 MB |
| `devanagari` | Hintçe, Marathi, Nepalce | 8 MB |

Dil, OCR aracının kendi ayarı (`InspectionWindow.OcrLanguage`) — aynı reçetede farklı dillerde iki pencere olabilir. Arayüzde açılır liste olarak, yalnızca **dosyaları gerçekten kurulu** paketler gösteriliyor; eksik dosyalı bir paket listelenmez, çünkü seçilip hatta çalışırken patlaması daha kötü olurdu.

Yeni dil eklemek dağıtım adımı: modeli + sözlüğü `Vision/models/` içine koyup `languages.json`'a bir kayıt eklemek yeterli. Kod değişmez.

Altı dilde doğrulandı, hepsi %100: Arapça (`غسيل سريع`, sağdan sola), Kiril (`Отжим Полоскание`), Almanca (`Schleudern Grün`), İspanyolca (`Español Niño`), İtalyanca, Türkçe.

### Aksan toleransı genişletildi

Yalnızca Türkçe değil, Latin alfabesinin tamamı: `é/e`, `ñ/n`, `ä/a`, `ß/s`, `ø/o` vb. Arapça ve Kiril **katlanmıyor** — orada sade harf karşılığı yoktur, ikame gerçek bir hatadır.

## M14 — Aksan sorununun kökü: nerede duracağı ölçüldü

Kullanıcı `Aracı Dene` çıktısında `Sikma/Bosaltma` görüp haklı olarak rahatsız oldu: puan 100 ama ekrandaki metin yanlış. Nereye kadar düzeltilebileceği ölçüldü.

**İki modeli birleştirmek işe yaramıyor.** Güvene göre seçen bir topluluk denendi; `latin` modeli daha emin ama `ş`'yi büsbütün *düşürüyor* (`Boaltma`), `ch` ise *ikame ediyor* (`Bosaltma`). Katlamalı puanda ikame daha iyi olduğu için topluluk sonucu geriletiyor. Üstelik iki model iki kat süre demek.

**Bulanıklık değil.** Temiz sentetik metin 0'dan 2.6 σ Gauss bulanıklığa kadar denendi: `latin` modeli hepsinde `Sıkma/Boşaltma`'yı **harfi harfine doğru** okudu (netlik 1357'den 7'ye kadar). `ch` modeli hiçbirinde okuyamadı.

**Kontrast veya gürültü de değil.** Kontrast farkı 130'dan 35'e, gürültü 0'dan 10'a değiştirilerek sekiz kombinasyon denendi. Sonuç düzensiz: en düşük kontrastta doğru, en yüksekte yanlış. Yani `ı` tanıma ölçülebilir bir görüntü kalitesi eşiğine bağlı değil.

**Gerçek durum:** `latin` modeli Türkçe aksanlı harfleri üretebiliyor ama tutarsız; `ch` modeli hiç üretmiyor, her zaman sade karşılığını yazıyor. Kullanılabilir açık kaynak modeller arasında bu davranışı değiştiren bir seçenek yok. `ch` model **karar doğruluğunda** ölçülen en iyisi olduğu için (8/8) korundu.

### Yapılan: gizlemek yerine açıklamak

Metni olduğundan farklı göstermek yanlış olurdu. Bunun yerine katlamanın ne zaman devreye girdiği açıkça söyleniyor:

- `Aracı Dene` artık **okunan** ve **beklenen** metni yan yana gösteriyor.
- Yalnızca aksanda ayrılıyorlarsa not düşülüyor: *"Okunan metin beklenenden yalnızca aksan işaretlerinde ayrılıyor (Sıkma/Boşaltma)."*
- Aynı not Çalıştır modunda araç bilgisinde de görünüyor.

Böylece 100 puanın yanındaki `Sikma/Bosaltma` bir hata gibi değil, açıklanmış bir normalleştirme olarak okunuyor. Aksan toleransı kapatılırsa aynı okuma %92.31 verir ve NG olur — sıkı kontrol isteyen bunu seçebilir.

> **Sonradan geçersiz kaldı.** M15'te eğitilen model bu harfleri gerçekten okuyor, dolayısıyla tolerans artık **varsayılan olarak kapalı**. Seçenek duruyor; eğitilmemiş bir paket ya da aksanı çözemeyecek kadar sığ bir baskı için hâlâ anlamlı.

## M15 — Türkçe tanıyıcı eğitimi (TAMAMLANDI)

Kullanıcı aksan toleransını çözüm olarak kabul etmedi: metnin gerçekten doğru okunmasını istiyor. Hazır modellerle bunun mümkün olmadığı ölçülerek gösterildikten sonra (bkz. M14), kendi modelimizi eğitme yoluna gidildi.

### Donanım ve buradan doğan tasarım

i7-4790K, 4 çekirdek, 28 GB RAM, **GPU yok**. Bu, yaklaşımı belirledi:

- Genel amaçlı büyük bir tanıyıcı değil, **tek alfabeye odaklı küçük** bir model. Stok Latin modeli bir düzine dili kapsayan 502 karakter taşıyor; bizim sözlüğümüz 99 karakter (Türk alfabesi + rakam + etikette geçen noktalama). Küçük problem, eğitilebilir problem.
- Düz evrişim yerine **derinlik ayrılabilir** evrişim. Düz CRNN bu CPU'da 3.3 sn/parti ölçüldü — herhangi bir gerçekçi bütçede birkaç on bin örnek eder, bir alfabe öğrenmeye yetmez. Ayrılabilir hâli aynı alıcı alanı çok daha ucuza veriyor.
- **Sabit genişlikli parti** (256 px). Karışık genişlikler hesabın yarısını dolguya harcıyordu (ortalama 214'e karşı maksimum 509).

Sonuç: 0.6 M parametre, ~23 örnek/sn.

### Veri

Etiketlenmiş lazer baskı Türkçe parça derlemi yok ve toplamak hattın zamanından uzun sürer. Bunun yerine sentetik üretim: 16 fontta, kameranın gerçekten ürettiği bozulmalarla (0–2.4 σ bulanıklık, 0–14 σ gürültü, eşit olmayan aydınlatma rampası, ±2.5° dönme, koyu-üstüne-açık ve tersi). Bozulma aralıkları tahmin değil — canlı kameradan ölçüldü (panel kırpması ~110 parlaklık, ~21 standart sapma).

Metin dağılımı, uygulamanın gerçekten okuduğuna göre ağırlıklandırıldı: %34 panel kelimeleri, %17 genel Türkçe, %17 parça/tarih kodu, %12 **karıştırılan harf alıştırması** (ı/i, ş/s, ğ/g ikilileri sade karşılıklarıyla karışık), %12 **bitişik dik çizgi alıştırması** (lt, ll, tl, lı, it), %8 rastgele karakter. Son ikisi tahminle değil, ölçülen hatalara bakılarak eklendi.

### Entegrasyon riski önce kapatıldı

Eğitime saat harcamadan önce asıl risk sınandı: **C# çalışma zamanı kendi ürettiğimiz ONNX'i yükleyip çalıştırabiliyor mu?** Cevap evet. Model, stok PP-OCR sözleşmesini birebir taklit ediyor (giriş `x` [N,3,48,W], çıkış [N,T,sınıf], opset 14, satır başına bir karakter sözlüğü), dolayısıyla mevcut yükleme yolundan geçiyor — yeni çıkarım kodu yok.

PyTorch'un yeni dynamo dışa aktarıcısı parti boyutunu sabitleyip opset 18 üretiyordu; klasik izleme dışa aktarıcısı (`dynamo=False`) gerekti.

### Yol boyunca bulunan hata

İlk koşu 10 milyon adıma göre kurulmuş bir OneCycle planıyla başlamıştı; 5000 adımlık bir bütçede öğrenme oranı ısınma aşamasını hiç bitirmiyor, model hedeflenenin onda biriyle eğiliyordu. Üç saat harcanmadan fark edildi ve kısa ısınma + sabit oranla değiştirildi.

### İlk sonuçlar

2000 adımdan sonra gerçek panel kırpmalarında (Python değerlendirmesi):

| | değer |
|---|---|
| ortalama benzerlik | %81.4 |
| tam eşleşme | 5/8 |
| **aksanlı harf** | **5/5 doğru** |

Kritik olan satır: `Sıkma/Boşaltma` → **`Sıkma/Boşaltma`**, birebir. Hazır modellerin hiçbirinin yapamadığı şey, 2000 adımda oldu. `Narin/İpek`'teki `İ` de doğru çıktı.

### Yol boyunca bulunan iki şey daha

**Model çerçevelemeye duyarlı.** Sıkı kırpmada tam eşleşme 5/8'den 3/8'e düşüyordu. C# yolu algılamayla yazının etrafını sıkıca kırptığı için bu doğrudan canlı sonucu etkiliyor. Eğitim verisindeki kenar boşluğu aralığı sıfıra kadar genişletildi, ayrıca yatay esneme ve kenardan ısırma eklendi — algılama kutusu tam oturmuş değil, esneyip kırpıyor.

**Entegrasyonda sorun yok.** Aynı temiz görüntülerde C# tarafı Python değerlendirmemden *daha iyi* okudu, yani çalışma zamanının ön işlemesi doğru. Fark yalnızca modelin eğitim düzeyinden geliyordu.

### Asıl bulgu: algılama aşaması modeli bozuyordu

Eğitim bitince ilk canlı ölçüm felaketti — eğitilmiş model **%51.2**, hazır model %96.0. Kayıtlı
kırpmalarda %94.6 alan model canlıda çökmüş görünüyordu.

Sebep model değildi. Aynı canlı kareyi Python'dan doğrudan verdiğimde aynı model **%96** okuyordu.
Fark tek bir aşamadaydı: RapidOcrNet, tanımadan önce **metin algılama** çalıştırıp kendi kırpmasını
üretiyor. Operatörün çizdiği pencere zaten tek satır olduğu için bu aşama hiçbir şey kazandırmıyor,
ama görüntüyü yeniden kırpıp ölçekleyerek bizim çerçevelememize göre eğitilmiş modeli düşürüyor.

Çözüm `Vision/LineRecognizer.cs`: tanıma modelini ONNX Runtime üzerinden **doğrudan bölgeye**
uyguluyor — gri tonlama, sabit 48 px yüksekliğe oranı koruyarak ölçekleme, [-1, 1] eşleme, üç kanala
kopyalama; eğitimdeki ön işlemenin birebir aynısı. Hangi paketin bu yoldan geçeceği manifestte
`lineModel` ile belirtiliyor. Stok paketler eskisi gibi algılamayla çalışıyor: onlar o boru hattı
için eğitildi ve orada iyiler.

Bu, aylardır süren "hazır model mi, kendi modelimiz mi" sorusunun asıl cevabıydı — kendi modelimiz,
ama kendi çalıştırma yolumuzla.

### Ama pencereyi bütün okumak da yetmedi

Doğrudan okuma, pencere yazının etrafına *dar* çizildiğinde doğru. Operatör pencereyi bol çiziyor.
Demo reçetesinin gerçek pencereleriyle ölçüldü — 450x70'lik bir yazı için 521x129 çizilmiş pencere
bütün okunduğunda `Perde` yerine `I` veriyordu.

Sonuç: **algılama nerede olduğu için, bizim modelimiz ne yazdığı için.** Sırasıyla:

1. Stok algılayıcı pencere içindeki satır kutularını buluyor.
2. Stok tanıyıcının o kutu için ürettiği karakter skorları **metin mi değil mi** kapısı olarak
   kullanılıyor — okuduğu metin değil, yalnızca skoru. Zaten hesaplanmış bir bilgi.
3. Yanındaki gerçek yazıya göre **kendinden uzun ve dar** kutular eleniyor: pencereye giren panel
   kenarı tek bir dik çizgi olarak algılanıyor ve "bu harf değil" diyecek bir şey yok — ayırt eden
   şey bağlam. Pencerede başka yazı yoksa dokunulmuyor, çünkü o zaman gerçekten aranan işaret
   olabilir.
4. Aynı satırdaki kutular **birleştiriliyor**. Algılayıcı boşluk genişleyince satırı bölüyor;
   `Sıkma/Boşaltma` iki kutu geliyor ve ayrı ayrı okunduğunda `Sıkma/` + `GBoSat` çıkıyordu.
   Modelin eğitildiği şey de bütün satır.
5. Her satır bizim modelimizle okunuyor. Kutunun etrafına **dikeyde bol, yatayda cimri** boşluk
   bırakılıyor: üstten kesilen bir harf okumayı bozuyor, yandan giren komşu öge de öyle.
6. Hiç kutu bulunamazsa pencere bütün olarak okunuyor — algılayıcının kaçırdığı sönük bir baskı
   çoğu zaman hâlâ okunabilir.

Her adım ölçülerek eklendi, hepsinin bozduğu somut bir okuma var.

### İkinci tur: kalan tek harf

İlk tur canlıda hazır modeli geçti (%98.8'e %96.0) ama `Boşaltma` → `Boşatma`, bir `l` düşüyordu.
Kırpmayı birkaç piksel oynatınca bazen doğru okuyordu, güven skoru da hangisinin doğru olduğunu
ayırt etmiyordu (yanlış okumada %98.9, doğru okumada %94.6) — yani çok ölçekli oylama kurtarmazdı.
Yan yana gelen dar dik harfleri yeterince görmemişti.

İkinci tur bunun için: `_stroke_drill`, örneklerin %12'sini bu harf çiftlerine ayırıyor. 75 dakika,
kontrol noktasından devam. Önceki model `turkish_rec_round1.pt` olarak yedeklendi — geride kalsaydı
ona dönülecekti.

| | tur 1 | tur 2 |
|---|---|---|
| kayıtlı panel kırpmaları | %94.6 — 7/8 | **%100 — 8/8** |

Tur 1'in okuyamadığı tek kelime `Yorgan`'dı ve o kırpma karenin üst kenarında kesik. Tur 2 onu da
okudu.

### Üçüncü tur: kesik pencere

Gevşek çizilmiş pencerelerde kalan hata, yazının alt kenarını **kırpan** bir pencereydi — üstünde de
noktalı bir ayraç çizgisi vardı. Üçüncü tur bunun için: harfleri üstten/alttan 0–6 piksel kesen ve
çevreye düz ya da noktalı ayraç çizgisi ekleyen örnekler.

Turlar aynı boru hattından, aynı canlı karelerde, 8 tekrarla karşılaştırıldı — iki turu iki ayrı
ölçüm düzeneğiyle kıyaslamak yanıltıcı olurdu, bu yüzden tur 2 geçici olarak ikinci bir dil paketi
şeklinde kuruldu:

| | tur 2 | tur 3 |
|---|---|---|
| dar pencereler | 48/48 — %100 | 48/48 — %100 |
| gevşek pencereler | 21/32 — %90.7 | **22/32 — %95.2** |
| kesilmiş kırpma kopyaları (çevrimdışı) | %93.8 | **%97.9** |

Tur 3 hiçbir yerde geride değil; kuruldu. Tek tekrarla bakıldığında tur 3 daha kötü *görünüyordu* —
canlı sahne kareden kareye değiştiği için üç tekrar karar vermeye yetmiyor. Sekiz tekrar aksini
gösterdi.

### Sonuç — canlı panelde, 5 kare üzerinde, aksan toleransı kapalı

Yazının etrafına düzgün çizilmiş pencerelerde:

| beklenen | hazır (latin) | eğitilmiş |
|---|---|---|
| Sıkma/Boşaltma | 0/5 — `Sikma/Bosaltma` | **5/5** |
| Narin/İpek | 0/5 — `Narin/ipek` | **5/5** |
| Yünlüler Elde | 4/5 — `Yunlüler Elde` | **5/5** |
| Durulama | 5/5 | 5/5 |
| Perde | 5/5 | 5/5 |
| Ek programlar | 5/5 | 5/5 |
| **ortalama** | **%95.7** | **%100.0** |
| **tam eşleşme** | **19/30** | **30/30** |

Demo reçetesinin gevşek pencerelerinde ikisi başa baş (%96.2'ye %96.4) ama farklı sebeplerden:
hazır model her karede aksanları kaçırıyor, eğitilmiş model yalnızca yazıyı **alttan kesen** tek
pencerede tökezliyor. O pencere yeniden çizilirse fark kapanır — kesilen harf kimsenin okuyamayacağı
bir bilgi.

Eğitilmiş paket varsayılan yapıldı. Kullanıcının istediği buydu: aksan toleransıyla örtülmüş bir
başarı değil, harfin kendisinin doğru okunması.

Değişen varsayılanlar: `IgnoreDiacritics` artık `false` (yeni pencerelerde; kayıtlı reçeteler kendi
değerini korur), varsayılan dil paketi `turkish`. Diğer diller (Arapça, Kiril, Korece, Devanagari,
Latin) listede duruyor ve pencere başına seçilebiliyor — eğitilmiş paket yalnızca 99 karakter
biliyor, İtalyanca/Arapça bir etiket için doğru paket elle seçilmeli.

### Dosyalar

`MTCVision/training/`: `charset.py` (alfabe ve sözlük), `dataset.py` (sentetik üretim + bozulma), `model.py` (CRNN), `train.py` (eğitim), `evaluate.py` (gerçek panelde ölçüm), `export_onnx.py` (dışa aktarma ve konsola kurulum).

`MTCVision/backend/Vision/`: `LineRecognizer.cs` (ONNX tanıyıcıyı doğrudan bölgeye uygular), `OcrEngine.cs` (satır bulma, eleme, birleştirme ve paket yönetimi).

Eğitim burada, çıkarım .NET'te — daha önce kararlaştırılan ayrımın ilk gerçek uygulaması. Modeli yeniden eğitmek için: `python train.py --minutes 75 --resume out/turkish_rec.pt`, sonra `python evaluate.py --crops <kırpma klasörü>`, ve yalnızca daha iyiyse `python export_onnx.py --checkpoint out/turkish_rec.pt --install` + `install-service.ps1`.

## M16 — Faz 1: parçayı takip eden denetim (TAMAMLANDI)

Keyence IV3 kılavuzunun 12 bölümü okunup bizim konsolla karşılaştırıldı ve dört fazlı bir yol
haritası çıkarıldı. Faz 1 burada: pencerelerin parçanın yerinden bağımsız hâle gelmesi.

### Neden bu önce

Kılavuzun "kararlılık" bölümü her aracın sorununu anlatırken dönüp dolaşıp aynı iki şeye geliyor:
konum düzeltme ve maske. Sebebi, parçanın kameranın önüne her seferinde tam aynı yere gelmemesi.
Bizde pencereler kareye çakılıydı; parça birkaç piksel kayınca bütün ölçümler kayıyor ve tek çare
limitleri gevşetmek oluyordu — yani denetimden vazgeçmek.

### Master görüntü

`Recipes/MasterImageStore.cs`. Reçete başına bir PNG, `%ProgramData%\MTCVision\Masters` altında.

- Reçetenin **içinde** değil yanında: reçete her düzenlemede yeniden yazılıyor, bir kare ise megabaytlar.
- JPEG değil PNG: bu görüntüye karşı ölçüm yapılıyor ve JPEG'in lazer baskı çevresindeki halkalanması
  tam da denetimin aradığı türden bir fark.
- Önce geçici dosyaya yazılıp yerine taşınıyor, böylece çalışan hat hiçbir zaman yarım yazılmış bir
  master'a bakmıyor. Geçici ad `.tmp.png` — OpenCV kodlayıcıyı uzantıdan seçiyor, `.png.tmp` yazınca
  "could not find a writer for the specified extension" diyor (bu hataya girildi ve düzeltildi).
- Çözülmüş hâli yazma zamanına göre önbellekte; çalışan hat her kare için diskten okumuyor.

### Konum düzeltme

`Inspection/PositionAdjuster.cs`. Master üzerinde seçilen desen, canlı karede normalize edilmiş
çapraz korelasyonla (`TM_CCOEFF_NORMED`) aranıyor; bulunduğu yerin öğretilen yerden farkı bütün
pencerelere uygulanıyor.

- Arama, öğretilen konumun **arama payı** kadar açılmış hâliyle sınırlı: bütün kareyi taramak hem
  yavaş, hem tekrar eden bir dokuda başka yere oturma riski.
- Normalize korelasyon, çünkü parça master'dan biraz daha aydınlık geldiğinde de eşleşmeli —
  alternatifi parlaklığı şekil sanmak.
- Eşleşme eşiğin altındaysa parça **bulunamamış** sayılıyor ve sonuç NG. Yanlış yerde ölçmektense
  ölçmemek doğrusu.
- Yalnızca öteleme. Dönme, açı üzerinde arama demek — ayrı bir araç ve ayrı bir maliyet; dönen parça
  şimdilik "bulunamadı" diye raporlanıyor, sessizce yamuk ölçülmüyor.

Ölçüldü: master bilinen miktarda kaydırılıp koşturuldu, kayma **piksel piksel** doğru raporlandı ve
pencereler onunla birlikte gitti — (30,18), (-25,-12), (60,0), (0,-40) hepsinde eşleşme %99.9.

### Maske

Pencere içinde ölçüme girmeyecek dikdörtgenler, **pencere koordinatında** saklanıyor — böylece hem
operatör pencereyi taşıyınca hem de konum düzeltme onu kaydırınca maske üstünde durduğu şeyle
birlikte gidiyor.

OCR için maskelenen alan siyaha değil, **kalan alanın ortalama tonuna** boyanıyor: siyah bir blok
sert bir kenar demek ve metin algılayıcı onu bulup okumaya çalışır.

Ölçüldü: kullanıcının dik yazılı penceresinde baştaki ikon `π`/`Ü` olarak okunuyordu; üst 70 piksel
maskelenince hazır model `Ek programlar` metnini **%100** okudu.

### Toplam yargı

`OutputAssignment.Logic` — sayılan tüm araçlar geçmeli (All) ya da biri yetsin (Any). Pencere başına
`AffectsTotal` ile bir araç ölçülüp gösterilirken hattı durdurmayabiliyor, `FollowsPosition` ile
parçaya değil düzeneğe bağlı bir pencere kareye sabit kalabiliyor.

### Dijital çıkış yapılamadı — kamera izin vermiyor

Faz 1'in dördüncü kalemi OK/NG'nin dijital çıkışa sürülmesiydi. Bu kamerada mümkün değil:
`LineSelector` salt okunur (`'lineSelectorVal' bu kamerada sabit, değiştirilemez`), `LineSource`,
`UserOutputSelector` ve `UserOutputValue` düğümleri node map'te hiç yok. Yani GenICam üzerinden
yazılımla sürülebilir bir seviye yok. Sinyal isteniyorsa ayrı bir G/Ç modülü ya da sonucu ağdan
okuyan bir PLC gerekiyor. Etkisi olmayacak ayarları arayüze koymak, olmayan ayardan kötü olduğu için
`OutputAssignment` yalnızca mantığı taşıyor.

### Dosyalar

`backend/Recipes/MasterImageStore.cs`, `backend/Inspection/PositionAdjuster.cs`,
`backend/Inspection/InspectionEngine.cs` (maske, konum, toplam yargı),
`backend/Recipes/Recipe.cs` (`Master`, `Position`, `Output`, pencere başına `Mask`, `AffectsTotal`,
`FollowsPosition`), `backend/Api/RecipeEndpoints.cs` (master uç noktaları),
`frontend/src/components/RoiEditor.tsx` (çizim kipleri: pencere / maske / desen).

### Kurulum betiği düzeltildi

Bu iş sırasında ortaya çıktı: `install-service.ps1` servisi durdurup 2 saniye bekliyordu. Yetmediğinde
eski süreç 5080'i tutmaya devam ediyor, yeni kopya sessizce bağlanamıyor ve konsol saatlerce eski
ikiliden yanıt vermeye devam ediyordu — model dosyaları altından değişirken. Artık süreç gerçekten
ölene ve port boşalana kadar bekliyor, sonra da yeni sürecin HTTP'ye yanıt verdiğini doğrulamadan
"kuruldu" demiyor.

## M14 — Zebra FS40/S40 fotoğraf yakalama: GenICam yok, TCP tetik + HMI kanalı (TAMAMLANDI, gerçek kamerada doğrulandı)

### GÜNCEL DURUM: görüntü FTP'siz, kameranın kendi HMI kanalından geliyor (`Camera:Zebra:ImageSource = "Hmi"`, varsayılan)

Aşağıdaki FTP hattı gerçek kamerada **hiç görüntü getirmedi**: kamera `PASS` yanıtı verip job'u çalıştırıyordu ama sunucuya FTP bağlantısı açmıyordu. Kameranın kendi web HMI'ı sonradan bunu açıkladı: `STORAGE MANAGER — ftp/sftp transfer failed` bildirimi. Sebep kesinleşmedi (temel lisansta FTP kaydetme çalışmıyor olabilir, ayarlar deploy edilmemiş olabilir); ihtiyaç da kalmadı.

Aurora Focus HMI'ı (`http://<kamera>/`, React) iki kanal kullanıyor ve ikisi de lisanssız çalışıyor:
- **REST** (`/api/...`, JWT): `POST /api/auth/login` `{"id":"99"}` misafir (rol `OPERATOR`) oturumu verir — HMI'ın kendi otomatik girişi, şifre gerekmez. `GET /api/jobs/firetrigger` tetikler, `POST /api/jobs/getimage` `{path}` kayıtlı görüntüyü döndürür.
- **socket.io** (`ws://<kamera>/socket.io/?EIO=4&transport=websocket`, kimlik doğrulaması yok): her job koşusundan sonra tüm istemcilere `451-["message",{"type":"job_result_data",...}]` başlığı ve ardından **ikili bir FlatBuffers `JobResult`** çerçevesi yayınlanıyor. İçindeki `resultImage.image` sıkıştırılmış (JPEG) tam görüntü (1920×1199 gri).

`ZebraHmiImageSource` bunu doğrudan WebSocket üzerinden (ek NuGet yok) dinliyor: `0{` → `40` gönder, `2` → `3` cevapla, ikili olay başlığı + ardından gelen ikili çerçeve. `ZebraJobResultParser` FlatBuffers'ı elle çözüyor; vtable slot numaraları HMI'ın kendi üretilmiş erişimcilerinden alındı (JobResult: jobId 8, resultImage 14, name 16, timestamp 18; ResultImage: image 10). `ZebraFrameDecoder` görüntüyü gri `CameraFrame`'e çeviriyor (FTP alıcısıyla ortak). Tetik hâlâ TCP `TRIGGER_START` (kamera `PASS` yanıtı veriyor); görüntü kanalı tetik kaynağından bağımsız çünkü sonuç herkese yayınlanıyor.

**Doğrulandı (gerçek FS40, `POST /api/camera/connect` → `POST /api/camera/trigger`)**: her tetik ~0,1–0,4 sn'de 1920×1199 `Mono8` kare döndürdü; art arda 4 tetiğin özetleri farklı (sensör gürültüsü) — yani her biri taze çekim, tekrar yayın değil. `/api/camera/snapshot` gerçek JPEG verdi.

**Uyarı**: bu Zebra'nın **yayınlanmamış iç arayüzü**. Bir üretici yazılımı güncellemesi olay biçimini değiştirirse `ZebraJobResultParser` (ve olay adı `job_result_data`) güncellenmeli. Sonucun görüntü içermesi için job'da **Triggers → Show Image** açık ve "Update every 1 inspections" olmalı. FTP yoluna dönmek gerekirse `ImageSource: "Ftp"` — o zaman aşağıdaki FTP kurulumu geçerli.

Bu modda **FTP sunucusu, 21/60000-60009 güvenlik duvarı kuralı ve KEYENCE servisinin kapatılması gerekmiyor** (aşağıdaki bölümler yalnızca `ImageSource = "Ftp"` içindir).

### (Eski) FTP tasarımı — yalnızca `ImageSource = "Ftp"`

### Neden GigE Vision değil

İkinci kamera olarak eklenen Zebra **FS40/S40** (etiket: Host `FS40C66B`, MAC `78:B8:D6:5C:C6:6B`, P/N `FS40-SR20Z4-2C00W`) GVCP keşfine hiç yanıt vermiyor — `192.168.137.98`'e doğrudan/broadcast GVCP discovery paketleri elle gönderildi, sıfır yanıt. Kamera `http://192.168.137.98/` üzerinde kendi **Zebra Aurora Focus HMI**'ını sunuyor: bu kamera GigE Vision/GenICam'i yalnızca ayrıca satın alınan bir **"MV Toolset" lisansıyla** açıyor; lisanssız haliyle GVCP portu (3956/UDP) hiç dinlemiyor. Lisans, `register-aurora.zebra.com` hesabı + Aurora Focus Desktop'taki "Device Licensing" ekranından PKID ile aktive ediliyor — bu bir yazılım değişikliği değil, satın alma kararı.

### Bunun yerine: Aurora Focus'un kendi (lisanssız) TCP tetik + FTP çıkışı

Aurora Focus, temel (barkod okuyucu) lisansında bile bir job'a **TCP/IP tetik** ve **Connect > Save Image > FTP** çıkışı tanımlamaya izin veriyor — Zebra'nın kendi dokümanlarından doğrulandı (`docs.zebra.com/.../c-aurora-tcp-ip-settings.html`, `.../c-aurora-ftp-file-saving-in-aurora.html`, `.../c-aurora-configuring-jobs-to-accept-tcp-ip-triggers.html`). Akış: MTCVision job'un **Control Port**'una bağlanıp yapılandırılmış **Trigger String**'i yazıyor → Aurora Focus job'u koşturuyor → job'un FTP çıktısı `.jpg`+`.json`'ı MTCVision'ın kendi makinesindeki bir FTP sunucusuna gönderiyor.

Bu yüzden `ICameraService`'in **ikinci bir implementasyonu** eklendi (`Camera/Zebra/`), var olan hiçbir şey değiştirilmeden: `LatestFrameStore`/`LiveFrameBroadcaster`/`InspectionEngine`/ROI/reçete katmanı `ICameraService.FrameCaptured` + `CameraFrame` dışında hiçbir şey bilmiyor, dolayısıyla kamera arka ucu tamamen takas edilebilir.

- **`ZebraCameraOptions.cs`** — `appsettings.json`'daki `Camera:Zebra` bölümünden bağlanan ayarlar: `DeviceIp`, `TriggerPort`, `TriggerString`, `TriggerTerminator`, `FtpControlPort`, `FtpPasvMinPort/MaxPort`. Her biri Aurora Focus'un kendi ekranındaki aynı adlı alanla **birebir eşleşmeli**.
- **`ZebraFtpImageReceiver.cs`** — `FubarDev.FtpServer` (+ `.FileSystem.DotNet`) NuGet paketleriyle gömülü bir FTP sunucusu barındıran `IHostedService`. Anonim girişi **`EnableAnonymousAuthentication()` ile değil** elle `AnonymousMembershipProvider(new NoValidation())` ile açıyor — kütüphanenin varsayılan doğrulayıcısı şifrenin e-posta gibi görünmesini istiyor, ama Aurora Focus'un kendi kurulum kılavuzu şifreyi **boş bırakın** diyor. Yüklenen dosyayı FubarDev'in iç olaylarına bağlanmak yerine düz bir `FileSystemWatcher` ile yakalıyor (hangi FTP istemcisi olursa olsun aynı şekilde çalışsın diye), dosya hâlâ yazılıyorsa okunabilir olana kadar yeniden deniyor, OpenCvSharp ile **gri tonlamaya** çözüyor (var olan `FrameConverter`/inspection hattı yalnızca Mono8/10/12/14/16 biliyor, renkli/Bayer formatları değil) ve işledikten sonra `.jpg`+`.json`'ı siliyor (gözetimsiz hatta klasör şişmesin diye).
- **`ZebraCameraService.cs`** — `ICameraService`'in geri kalanı: `TriggerAsync` tetik dizesini yazıp `ZebraFtpImageReceiver.ImageReceived`'i bekliyor (zaman aşımı `TriggerTimeoutMs`); `DiscoverAsync` gerçek bir GVCP keşfi olmadığı için yapılandırılmış IP'ye kısa bir TCP erişilebilirlik yoklaması yapıp tek elemanlı bir liste dönüyor (frontend'in var olan "tara, sonra bağlan" akışı hiç değişmeden çalışsın diye). GenICam'e özgü üyeler (`ApplySettingsAsync`, `SaveToCameraAsync`, `GetFeatureAsync`/`SetFeatureAsync`, `UpdateIdentityAsync`) **sessizce no-op değil**, açık `CameraConfigurationException` fırlatıyor — bu kamerada pozlama/kazanç Aurora Focus'tan yönetiliyor ve sessiz no-op operatöre bir ayarı değiştirdiğini düşündürüp aslında hiçbir şey yapmamak anlamına gelirdi. Pratik etkisi: bu kamerayla Kamera Ayarları diyaloğundaki "Uygula"/"Otomatik Ayarla" düğmeleri açık bir hata gösterir — bilinen bir sınır, arayüz henüz bu kamera tipi için o kontrolleri gizlemiyor.
- **`Program.cs`** — `Camera:Backend` ayarı (`"Gige"` varsayılan, `"ZebraTcpFtp"`) hangi `ICameraService`'in kaydedileceğini seçiyor; `ZebraTcpFtp` seçiliyken gömülü FTP sunucusu da hosted service olarak başlıyor.

### Uçtan uca doğrulama (gerçek cihaz olmadan)

Gerçek Zebra henüz TCP/IP Control/FTP için yapılandırılmadığı için (aşağıya bakın), bütün hat sahte bileşenlerle test edildi: 6000 portunda bağlantıyı açık tutan sahte bir TCP dinleyici + `curl -T foto.jpg ftp://localhost/x.jpg --user anonymous:` ile gerçek bir FTP STOR. Gerçek `POST /api/camera/connect` → `POST /api/camera/trigger` çağrıları çalıştırıldı ve **gerçek HTTP API üzerinden** doğrulandı: tetik, karşıya yüklenen JPEG'in boyutlarıyla (`64x64`→`Mono8`) dönen bir `CameraFrame` ile sonuçlandı, `/api/camera/status` istatistikleri ve kimliği (`Zebra Technologies`/`FS40/S40`) doğru gösterdi, `/api/camera/snapshot` gerçek bir JPEG döndürdü — yani ROI/reçete/denetim hattının tamamı bu kamerayla da çalışacak.

### Bu makinede bulunan ve çözülen port 21 çakışması

Gömülü FTP sunucusu ilk denemede **`SocketException 10048`** ile patladı: bu PC'de zaten **`SRManagementToolFtpServer`** adlı bir Windows servisi (KEYENCE'in `AutoID_Ver7` "SR Management Tool"undan, muhtemelen bu hattın eski KEYENCE tarayıcı kurulumundan kalma — bu uygulamanın arayüzünün neden Keyence IV2-Navigator'a benzediğini de açıklıyor) port 21'i tutuyordu. Kullanıcı onayıyla durdurulup devre dışı bırakıldı (`Stop-Service` + `Set-Service -StartupType Disabled`, yönetici PowerShell gerektirdi).

### Gerçek kamerada kalan kurulum (cihaz tarafı, MTCVision kod değişikliği gerektirmiyor)

1. **Aurora Focus → cihaz Communication Settings → TCP/IP Settings**: "Enable TCP/IP Control" aç, Connection Type = Server (varsayılan), Control Port Number = `appsettings.json`'daki `Camera:Zebra:TriggerPort` (varsayılan 6000) ile aynı, Control Terminator = `TriggerTerminator` (varsayılan CR+LF) ile aynı, Trigger String Type = **Explicit**, Trigger String = `TriggerString` (varsayılan `TRIGGER`) ile aynı.
2. **Job → Connect → Triggers**: Trigger Source = **TCP/IP**, Deploy.
3. **Job → Connect → Save Image → FTP**: host IP = MTCVision'ın çalıştığı makinenin kameranın alt ağındaki IP'si (bu makinede `192.168.137.252`), kullanıcı adı `anonymous`, şifre **boş**.
4. **Windows Firewall**: gömülü FTP sunucusu için 21/TCP (kontrol) ve `FtpPasvMinPort`–`FtpPasvMaxPort` (varsayılan 60000–60009/TCP, pasif veri) gelen kurallarını aç — GVSP için M2'de açılan kuralla aynı gerekçe: yerel testler loopback muafiyeti sayesinde kuralsız çalıştı, ama hattaki gerçek kamera uzak bir istemci olduğu için bu kural olmadan sessizce reddedilecektir.
5. `appsettings.json`'da `Camera:Backend` → `"ZebraTcpFtp"` yapıp servisi yeniden başlat.

### Bilinen sınır

Bu kamera tipinde pozlama/kazanç/kare hızı ayarları yok — Aurora Focus'un kendi job'u onları kontrol ediyor. Kamera Ayarları diyaloğu ve "Otomatik Ayarla" bu kamerayla açık bir hata verir; bu kontrolleri arayüzden tamamen gizlemek yapılmadı (kapsam dışı bırakıldı), sadece sessiz başarısızlık yerine dürüst bir hataya çevrildi.

## M15 — Kamera tipi seçimi, dış TCP/IP haberleşme, mod hatırlama, yalnızca OCR (TAMAMLANDI; sahte cihaz ve gerçek tarayıcıyla doğrulandı)

### Kamera tipi arayüzden seçiliyor

`Camera:Backend` ayarı artık yalnızca ilk açılışın varsayılanı. `SwitchableCameraService` (`Camera/`) her çağrıyı seçili kamera tipine (`GigeCameraService` veya `ZebraCameraService`) iletiyor; `ICameraService`'i kullanan hiçbir yer değişmedi. Seçim `session.json`'da (`cameraBackend`) tutuluyor, yeniden başlatmada aynı tipe bağlanılıyor. Açılış ekranında **Kamera tipi** seçicisi var; değişince eski kamera bağlantısı kesilip yeni tipe göre tarama yapılıyor. Çalışan bir denetim varken değiştirmek 409 veriyor. Uçlar: `GET/PUT /api/camera/backend`.

Zebra'nın görüntü kanalı (`ZebraHmiImageSource`) artık her zaman açık bir arka plan servisi değil: kamera bağlanınca `Start(ip)`, kesilince `Stop()` ile çalışıyor. `IZebraImageSource` bunun için `Start/Stop` kazandı (FTP alıcısında boş).

**GigE bozulmadı.** Bu oturumda GigE modunda tarama DALSA'yı buldu (`Nano-M2020`, S1179307, `192.168.10.50`), `connect` GenICam ağacını (1349 düğüm) yükleyip ayarları okudu. Görülen "400" hataları `'192.168.137.252' adresli bir ağ arayüzü bulunamadı`: ağ kartının IP'si 192.168.10.252'ye çevrilince Zebra alt ağı (192.168.137.x) yok oluyor. DALSA'dan **kare gelmemesi** M2'deki güvenlik duvarı sorunu: bu makinede `MTCVision GVSP Stream (UDP)` kuralı yok (`netsh advfirewall firewall show rule name="MTCVision GVSP Stream (UDP)"` → "No rules match"). Yönetici PowerShell'de: `New-NetFirewallRule -DisplayName 'MTCVision GVSP Stream (UDP)' -Direction Inbound -Action Allow -Protocol UDP -RemoteAddress 192.168.10.0/24 -Profile Any`.

### Dış haberleşme (TCP/IP) — `Integration/`

Menüde **Haberleşme** penceresi. Bu makinedeki bir portu (varsayılan 5001) dinleyen TCP sunucusu (`ExternalIoServer`); PLC/robot bağlanır. Ayarlar `Data/external-io.json`'da: etkin/pasif, port, sonlandırıcı (CRLF/LF/CR/Yok), **tetik mesajı** (varsayılan `TriggerRequest`) ve yedi durum sinyalinin metni. Boş bırakılan sinyal gönderilmez.

- **Kontrolcüden gelen**: tetik mesajı → bir denetim (yalnızca Çalıştır modunda). Herhangi bir CR/LF satırı bitirir; bilinmeyen mesaj yok sayılır.
- **Kontrolcüye giden** (olay olarak, değişince): `Busy` (denetim başladı) → `ResultAvailable` → `AllOK` veya `AllNok` → `Ready`; başarısız denetimde `Error` → `Ready`; Çalıştır moduna geçilince `Run` (+`Ready`). Yeni bağlanan kontrolcüye güncel durum (Run/Busy/Ready) hemen gönderilir. Tetik yerine getirilemiyorsa (Program modu, kamera yok, reçete yok) `Error`, denetim zaten sürüyorsa `Busy` döner — tetik sessizce düşmez.
- Penceredeki **Gönder** düğmeleri bir sinyalin metnini bağlı kontrolcülere yollar (devreye alma için). Uçlar: `GET/PUT /api/external-io`, `POST /api/external-io/test/{sinyal}`.
- Bağlanabilmesi için portun Windows Güvenlik Duvarı'nda gelen TCP'ye açık olması gerekir.
- **İzleme sekmesi ve konsol**: pencere iki sekmeli — **İzleme** / **Ayarlar**. İzleme'de yedi sinyalin her biri için bir kutucuk var: `Run`/`Busy`/`Ready` o an doğruysa **sabit** yanar (`status.state`), gönderilen her sinyal ~2 sn **yanıp söner**; kutucukta son gönderilme zamanı ve sayaç görünür (kontrolcü bağlı olmasa da sayılır). **Konsolu Aç** koyu bir terminal açar: gelen (←), giden (→) mesajlar ile bağlantı/reddedilen tetik notları; Gelen/Giden/Bilgi filtresi, Temizle (yalnızca ekranı), otomatik kaydırma ve altta bağlı kontrolcülere **ham metin gönderme** satırı. Sunucu son 500 satırı bellekte tutuyor (`Integration/ExternalIoLog.cs`, halka tampon; diske yazılmaz), konsol açılınca geçmişi gösterir. Uçlar: `GET /api/external-io/log?after=<seq>`, `POST /api/external-io/send {text}`. Durum yanıtına `signals[]` (sayaç, `ageMs`) ve `state` (run/busy/ready) eklendi. Not: ham gönderilen metin bir sinyal değildir, kutucukları etkilemez.

Bunun için denetim mantığı `InspectionRunner`'da birleşti: `RunOnceAsync` (tetik + yargı + sayaç, aynı anda tek denetim, sürekli döngü varken `InspectionBusyException`) hem operatörün **Tek Denetim** düğmesini (`POST /api/inspection/run-once/{id}`) hem dış tetiği çalıştırıyor; sonuç ve istatistik sunucuda tutuluyor, arayüz Çalıştır modunda bunları her zaman yokluyor (dış tetiklerin sonucu ekranda görünsün diye). Döngü ve tekil denetimler `InspectionActivity` olaylarıyla haber veriyor.

Doğrulama (sahte Zebra + gerçek kareyle): bağlanınca `Run`,`Ready`; `TriggerRequest` → `Busy`, ~2,8 sn sonra (ilk OCR model yüklemesi) `ResultAvailable`,`AllOK`,`Ready`; art arda iki tetikte ikinci `Busy`; Program modunda `Error`; tekil denetimler ~200 ms.

### Çalıştır / Program modu hatırlanıyor

Mod ve açık reçete sunucuda (`OperatingMode`, `session.json` `mode`/`activeRecipeId`) tutuluyor; konsol, en son bırakıldığı modda ve reçetede açılıyor (yeniden başlatma ve sayfa yenileme dahil). Program'a geçmek sunucudaki sürekli döngüyü de durduruyor (önceden yalnızca ekran değişiyordu, döngü sürüyordu). Uçlar: `GET /api/session`, `PUT /api/session/mode`.

### Kontrast ve Parlaklık araçları kaldırıldı

`InspectionToolKind`'dan `Brightness`/`Contrast` çıktı, arayüzde yalnızca OCR kaldı ("OCR Aracı Ekle"). Bu araçlarla kaydedilmiş reçeteler yüklenirken o pencereleri **düşürüyor** (`RecipeStore.DropRetiredTools`; aksi halde reçete listeden kaybolurdu). Diskteki dosya reçete yeniden kaydedilene kadar değişmiyor. Arka uçtaki diğer araçlar (Area, BlobCount, Outline, Edge, Width, Pitch, Diameter) duruyor; arayüzü yok. "Aracı Dene"deki parlaklık/kontrast bilgisi ve **kameranın** Otomatik Parlaklık ayarı araç değil, kaldırılmadı.

### Kamera tetik kaynağı olarak TCP/IP

Kamera Ayarları → Tetikleme'ye dördüncü tip eklendi: **TCP/IP tetik**. Seçilince kamera yazılım tetiği modunda bekler (`TriggerMode On`, `TriggerSource Software`) ve tetiği, Haberleşme arayüzünün portuna gelen **tetik mesajı** verir; port ve tetik mesajı aynı sekmeden düzenlenir, **Uygula** arayüzü de etkinleştirir (önce arayüz kaydedilir: kullanılamayan bir port kamerayı beklemede bırakmasın). Kamerada bu, dahili (yazılım) tetikle aynı olduğundan seçim `session.json`'da (`tcpTrigger`) tutuluyor ve `SwitchableCameraService` GigE için ayarlarda kaynağı `TCP/IP` olarak gösteriyor / uygularken `Software`'e çeviriyor; tetik ile ilgisi olmayan güncellemeler (reçete pozlaması, otomatik parlaklık) seçimi bozmuyor.

Mesaj gelince: **Çalıştır** modunda denetim (önceki gibi); **Program** modunda, TCP/IP seçiliyse yalnızca bir kare alınır (`Busy` → `Ready`, canlı görüntü ve öğretme için), seçili değilse `Error` (önceki davranış). Bu seçiliyken tarayıcının dahili aralık tetiği çalışmıyor. Zebra'nın bildirdiği tetik kaynağı `TCP/IP`'den `Software`'e alındı (uygulama tetikleyen taraf; "TCP/IP" artık "mesaj gelen" anlamında).

Doğrulama: sahte Zebra ile Program modunda `TriggerRequest` → `Busy`,`Ready`, kare sayacı +1, snapshot; bayrak kapalıyken `Error`; arayüzde seçenek ve port/mesaj alanları. **Gerçek DALSA'da denenmedi**: DALSA şu an 192.168.1.40'ta ve kartın tek IP'si 192.168.137.252 olduğundan (Siemens PLCSIM adaptörü fiziksel değil) bağlanılamıyor; kartta ikinci bir 192.168.1.x IP'si gerekir.

### Demo terminal (PLC yerine) — `tools/DemoTerminal/`

Konsol uygulaması: Vision'ın TCP/IP portuna bağlanır, tetik mesajı gönderir, gelen sinyalleri (Ready/Busy/Run/Error/AllOK/AllNok/ResultAvailable) renkli ve zaman damgalı basar; tetikten karara geçen süreyi gösterir. Çalıştırma: `dotnet run --project tools/DemoTerminal -- --host 127.0.0.1 --port 5001 [--trigger TriggerRequest] [--terminator CRLF|LF|CR|None]`. Komutlar: `t` tetik, `a <ms>` otomatik tetik (`a` durdurur), `s` durum/sayaçlar, `q` çıkış; başka metin olduğu gibi gönderilir. Bağlantı kopunca 3 sn'de bir yeniden dener. Çalışan Vision'a karşı doğrulandı (Run, Ready → TriggerRequest → Busy, ResultAvailable, AllNok, Ready).

### Arayüz düzeltmeleri

- Görüntü Geçmişi penceresi 560 px'te kalıp alt butonlar taşıyordu: `shell.css`'teki `.modal` genişliği, sonradan yüklendiği için `.modal--history`'yi eziyordu. Artık `.modal.modal--history` (1000 px); alt çubuk da sarılabilir.
- Uygulama adı "Vision"; "MTCVision.Backend" yazısı kaldırıldı (durum çubuğu, açılış ekranı, Hakkında), sekme başlığı "Vision". (`/health` yanıtındaki `service` alanı ve Windows servis adı `MTCVision` teknik tanımlayıcı olarak duruyor.)
- Açılış ekranındaki "Kamera açık ve aynı alt ağa bağlı mı?" ipucu kaldırıldı.

## Sıradaki Adımlar (M11+)

1. **Anomali tespiti** (PatchCore tarzı: ONNX ResNet18 + k-NN memory bank) çizik vb. için; `Data/Embeddings/` altında memory bank. OCR ve eşik araçlarının yakalayamadığı "beklenmeyen görünüm" sınıfı için.
3. **Denetim geçmişi ve istatistik kalıcılığı** (`Data/History/`, `Data/Statistics/`) — görüntüler saklanıyor ama sayısal sonuçlar hâlâ yalnızca oturumda tutuluyor.
4. **Dijital çıkışla OK/NG sinyali** (`LineSelector`/`LineSource`); harici tetik girişi hazır, çıkış tarafı bağlanmadı.
5. Pencere başına maske/açı, ve reçete başına referans görüntü (`Data/Images/`).

## Bilinen Eksikler

- **GigE'den kare gelmemesi: `MTCVision.Backend.exe` için Windows'un kendi oluşturduğu engelleme kuralları.** İlk çalıştırmada çıkan güvenlik duvarı sorusu kapatılınca/yalnızca Özel ağa izin verilince Windows `MTCVision.Backend.exe` için **Public** profilde UDP ve TCP **Block** kuralı ekliyor (`netsh advfirewall firewall show rule name="MTCVision.Backend" verbose` ile görülür; `Get-NetFirewallRule` yönetici ister). Kamera kartı Public ise bağlantı, kimlik ve ayarlar çalışır ama GVSP paketleri hiç uygulamaya ulaşmaz (`framesCompleted` 0; kart 800 Mbit/s alırken UDP "Receive Errors" sayacı şişer). Engelleme, port bazlı izin kuralından **önce** gelir. Aynı kod `dotnet.exe` (".NET Host" izin kuralı var) üzerinden çalışınca 30 kare/sn geliyor: `dotnet bin/Debug/net8.0/MTCVision.Backend.dll --urls http://localhost:5080`. Kalıcı çözüm, yönetici PowerShell'de: `Get-NetFirewallRule -DisplayName 'MTCVision.Backend' | Where-Object Action -eq 'Block' | Remove-NetFirewallRule` (ya da kamera kartını Özel ağa al: `Set-NetConnectionProfile -InterfaceAlias Ethernet -NetworkCategory Private`). Dış TCP/IP arayüzü (PLC) aynı engele takılır; portu için ayrıca gelen TCP izni gerekir. `GvspStreamReceiver` artık ilk 3 paketi, 2000. paketteki sayıyı ve durma/işleme hatalarını `StatusChanged` ile bildiriyor (paket hiç gelmiyorsa "GVSP paketi #1" satırı görünmez).

- **Proje henüz bir git deposu değil** (`.git` yok). Kullanıcı açıkça "commit yapma" dedi; `git init` dahil sürüm kontrolü için açık onay gerekir.
- Kamera ayarları kamerada kalıcı değil (power-cycle'da sıfırlanır) — kalıcılık için `UserSetSave` gerekir, henüz bağlanmadı. Reçete modeli geldiğinde ayarlar zaten her bağlanışta uygulanacağı için bu muhtemelen gereksiz.
- `GevLinkSpeed` düğümü çözülemiyor (`pIndex` ile eşleşen `pValueIndexed` yok); ihtiyaç duyulmadı.
- `backend/backend_run.log` çalıştırma çıktısıdır, sürüm kontrolüne girmemeli.
