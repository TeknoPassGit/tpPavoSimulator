# TpPavoSimulator — yalnızca PAVO

.NET 8 ASP.NET Core yerel ödeme simülatörü. Tek dinleyici: http://127.0.0.1:18080.
Bilet API'si yoktur; 18081 açılmaz. Gerçek cihaza veya üretim servisine yönlendirme yapılmaz.

## Derleme

Proje klasöründe:

```powershell
dotnet build .\TpPavoSimulator.csproj
dotnet build .\TpPavoSimulator.csproj -c Release
dotnet publish .\TpPavoSimulator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\Release
```

Derleme için global.json ile seçilen .NET 8 SDK gerekir. Publish, Windows x64 çalışma zamanı paketlerini kullanır. Kaynaklar NuGet.Config içinde varsayılan olarak kapalıdır; gereken paketler yerel önbellekte bulunmalıdır.
Release/TpPavoSimulator.exe, .NET kurulumu gerektirmeyen Windows x64 tek EXE'dir. Kontrol paneli ve temizlenmiş JSON şablonları gömülüdür; kaynak klasörüne veya wwwroot/Templates dosyalarına ihtiyaç duymaz.

## Çalıştırma

```powershell
.\Release\TpPavoSimulator.exe
```

http://127.0.0.1:18080 adresini açın. Debug için start.ps1 kullanılabilir. Port kullanımda ise sahibi bilinmeyen süreçleri kapatmayın.
Varsayılan kayıt kökü EXE/DLL yanındaki runtime klasörüdür. SIMULATOR_DATA_ROOT ile ayrı bir test veri klasörü seçilebilir. Panel ayarı servis yeniden başlayınca Success'e döner; sanal tahsilat ve bekleyen düzeltme kayıtları korunur. Cihaz seri numarası ve yanıt gecikmesi ise data/SYNTHETIC-settings.json içinde saklanır ve yeniden başlatmada hatırlanır. Seri numarası doluysa, TransactionHandle.SerialNumber farklı gelen her PAVO isteği (Pairing dahil) 71 — Seri numarası uyuşmazlığı ile yanıtlanır; boşsa kontrol yapılmaz. Yanıt gecikmesi Pairing dışındaki tüm isteklere uygulanır.

## Davranış

- Tek Başarılı Ödeme seçeneği vardır. Gelen PaymentInformations[0].Mediator, 1=nakit veya 2=kredi kartı şablonunu belirler. Panelde ödeme türü yalnızca bilgi olarak gösterilir.
- Gerçek nakit/kart ve 20/73/119/138/199/9001 hata örneklerinin alan yapıları korunur. Kart GivenAmount=0.0 ve ResponseCode=00; nakitte GivenAmount istek tutarıdır. Şablonlar gerçek kart/banka kimliği içermez.
- Satış kalemleri aktarılır; KDV oranı istekten okunur, KDV dahil tutardan satır bazında iki ondalığa AwayFromZero ile hesaplanır. İndirim ve bölünmüş ödeme modellenmez.
- Error72/Error73 sürekli hata; Correction72/Correction73, düzeltilmiş tarih/sıra ve değişmeyen satış içeriğini doğrulayıp tek tahsilata geçer. 72'nin gerçek hata örneği yoktur.
- Aynı işlem için yeni tahsilat oluşturulmaz. ReplaySavedOutcome kayıtlı sonucu; ApplySelectedScenario seçili senaryoyu döndürür. Önceki tahsilat varsa hata yanıtı onu geri almaz. Panel bu farkı açıklar.
- Gecikme, timeout, bozuk JSON ve tahsilat sonrası yanıt kaybı desteklenir. Belirsiz sonuç yeni tahsilat olarak yorumlanmamalıdır.
- PerformEOD, AbortCurrentPayment (boşta 102), UpdateTerminalData ve RebootDevice gerçek yanıtlardan temizlenmiş şablonları kullanır. Bu komutlarda yalnızca Busy20 ve Timeout senaryoları uygulanır.
- Son gerçek HTTP gövdesi yalnızca bellekte gösterilir; durum/log dosyalarına ham istek/yanıt, kart, müşteri, token veya şifre yazılmaz.

## Testler

```powershell
dotnet run --project .\tests\HttpTests.csproj -- .\bin\Debug\net8.0\TpPavoSimulator.dll
dotnet run --project .\tests\HttpTests.csproj -- .\Release\TpPavoSimulator.exe
```

Test programı yalnızca loopback üzerinde kendi sunucusunu başlatır; tests/artifacts altında ayrı veri kullanır ve sonunda kendi sürecini kapatır. 18081'in kapalı, 18080'in kapanışta serbest olduğunu denetler. Proxy ve yönlendirmeler kapalıdır. Her iki portun başlangıçta boş olması gerekir.

Gerçek bankacılık, mali belge doğrulama, cihaz sürücüsü, gerçek günsonu/iptal/iade ve masaüstü uygulama entegrasyonu bu testlerle doğrulanmış sayılmaz. Başka uygulamaların dış ağ erişimi ayrıca izole edilmelidir.

## License

Developed by Akmer Bilişim Ltd. Şti.

TeknoPass is a trademark of Akmer Bilişim Ltd. Şti.

TpPavoSimulator is an independent open-source project, licensed under the [MIT License](LICENSE). It is a local simulator of the PAVO payment terminal HTTP interface, used to test POS integrations without a physical device. It listens only on 127.0.0.1 and never connects to a real device, bank or production service. Reference samples under References/ and PavoReferans/ have been anonymized with synthetic values.

PAVO is a trademark of its respective owner. This project is not affiliated with or endorsed by it.