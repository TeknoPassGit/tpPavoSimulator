# TpPavoSimulator — çoklu proje kullanımı

Bu sürüm yalnızca PAVO simülatörüdür. Bilet API'si yoktur. C# ve Delphi istemcilerinde sadece PAVO hedefini ayrı test yapılandırmasıyla http://127.0.0.1:18080 adresine yönlendirin. Başka servislerin yerine kullanmayın.

## Kurulum ve başlatma

Windows x64 Release/TpPavoSimulator.exe self-contained tek EXE'dir; .NET kurulumu gerekmez. Panel ve temizlenmiş JSON şablonları EXE içindedir. Kaynak derleme komutları README.md içindedir.

```powershell
$env:SIMULATOR_DATA_ROOT = 'C:\PavoTestVerileri\ProjeA'
.\Release\TpPavoSimulator.exe
```

Kontrol paneli http://127.0.0.1:18080 adresindedir. Yalnızca sayısal IPv4 loopback kabul edilir. Kaynak veya üretim INI'sini değiştirmeyin; istemcinin ayrı test kopyasını kullanın.

## İstemci entegrasyonu

C# örneğinde HttpClient hedefi `http://127.0.0.1:18080/`, komut yolu `CompleteSale` olur. Proxy ve otomatik redirect kapalı olmalıdır. Delphi istemcisinde kullanılan HTTP bileşeninin temel URL'sini aynı adrese ayarlayın; komutları POST application/json gönderin. Test kopyasında eski cihaz IP'si için alternatif/fallback bağlantı bırakmayın.

İstek TransactionHandle içinde SerialNumber, Fingerprint/FingerPrint, TransactionDate, TransactionSequence taşır. CompleteSale içindeki PaymentInformations[0].Mediator=1 nakit, 2 karttır. Kalem ve vergi alanlarını gerçek istemci sözleşmesiyle gönderin; eksik veriler için alan veya oran tahmin etmeyin. Tam kapsam CONTRACTS.md içindedir.

Panelde tek Başarılı Ödeme seçeneği vardır. Nakit/kart türü seçilmez; yalnızca gelen istek belirler. Son istek türü bilgi olarak gösterilir. Kart GivenAmount=0.0 gerçek örneğe uygundur; pozitif değer bekleyen istemcilerle uyumluluk ayrıca ele alınmalıdır.

## Komutlar ve senaryolar

Pairing, CompleteSale, PerformEOD, AbortCurrentPayment, UpdateTerminalData, RebootDevice, ListPendingSaleWithDetail ve FinalizeSale desteklenir. PerformEOD, AbortCurrentPayment, UpdateTerminalData ve RebootDevice için yalnızca Busy20 ve Timeout senaryoları geçerlidir. Diğer yollar desteklenmiyor yanıtı alır; hiçbir gerçek cihaza yönlendirme yoktur.

Gerçek 20, 73, 119, 138, 199, 9001 hata örnekleri; normal 72/73 hataları; Correction72/Correction73 doğrulaması; gecikme, timeout, bozuk JSON, tekrar ve tahsilat sonrası yanıt kaybı vardır. 72'nin gerçek hata örneği yoktur. Abort gerçek tahsilatı geri alma/iadeyi, günsonu ise gerçek mali/banka sürecini doğrulamaz.

## Projeleri ayırma

Her proje için farklı SIMULATOR_DATA_ROOT ve ayrı süreç oturumu kullanın. Aynı portta iki süreç eşzamanlı çalışmaz. İstemci, proje veya API tokenına göre otomatik veri bölümlendirme yoktur. Aynı oturumda birden çok masaüstü uygulaması kullanılırsa senaryo ayarı, kayıtlar, pending listesi ve günsonu toplamı ortaktır; testleri sırayla yürütün.

Panel ayarı değiştirilene veya süreç yeniden başlayana kadar kalır. Kalıcı tahsilat ve bekleyen düzeltme kaydını yeni deneme açmak için silmeyin. Aynı işlemin tekrarı ikinci tahsilat üretmez; kayıtlı sonucun seçili hatayı geçersiz kılabildiği durumlar panelde açıklanır. Her bağımsız hata denemesinde yeni test işlem kimliği kullanın.

## Güvenlik ve yeni örnekler

Ağ erişimi kapalı test VM'si tercih edin. Simülatörün loopback olması istemcinin SQL, log/bulut, kapı/turnike veya yazıcı bağlantılarını engellemez; bunları test kopyasında ayrıca izole edin. Üretim kimlik bilgileri veya gerçek kart/müşteri verileri göndermeyin.

Yeni endpoint veya JSON örneği eklerken gerçek örneği References altında tutun, hassas değerleri temizleyerek Templates içine ayrı şablon ekleyin ve gömülü kaynak/test kapsamını doğrulayın. Mevcutmuş gibi protokol alanı üretmeyin. Alan adları, veri tipleri, null ve dizileri gerçek HTTP yanıtıyla otomatik karşılaştırın. Kaynak referanslar Release'e eklenmez.

## Yeni Delphi projesi için kısa sıra

1. Ayrı istemci test kopyasını ve veri klasörünü hazırlayın; dış bağlantıları kapatın.
2. Simülatörü başlatıp paneli açın; PAVO hedefini yalnızca 127.0.0.1:18080 yapın.
3. Panelde Başarılı Ödeme uygulayın. Nakit/kart türünü Delphi isteği belirlesin.
4. Tek yeni işlem gönderip gerçek HTTP gövdesini, tutarı, kalem/KDV değerlerini ve sanal tahsilat sayısını karşılaştırın.
5. Her hata için yeni işlemle deneyin; 72/73 düzeltmesinde yalnızca beklenen handle alanlarını değiştirip tutarı/kalemleri koruyun.
6. Timeout ve yanıt kaybında yeni tahsilat başlatmayın; aynı işlem kaydını inceleyin. Test sürecini sonunda kapatın.