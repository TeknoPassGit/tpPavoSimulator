# PAVO sözleşme kapsamı

Tek HTTP dinleyici 127.0.0.1:18080; tüm PAVO komutları POST application/json kullanır. Başka servis veya dış cihaza yönlendirme yoktur. Bilet API'si ve durum modeli kaldırılmıştır; 18081 dinlenmez.

## Komutlar

- /Pairing: gerçek yanıt şablonu (References/PAIR_gelen_*.txt): HasError=false, Message="", ErrorCode/Data yok. SerialNumber gelen değerdir; Fingerprint sentetik cihaz fingerprint'idir (istemcininki geri dönmez); TransactionSequence gelen+1; TransactionDate simülatör saatidir (mikrosaniye). Gecikme uygulanmaz.
- /CompleteSale: gerçek nakit/kart ve altı hata örneğinden şablonlar; değişken değerler yerel test verisidir.
- /PerformEOD: gerçek günsonu yanıtından temizlenmiş şablon; işlem listesi ve toplamlar sanal tahsilatlardan üretilir. Gerçek banka günsonu değildir.
- /AbortCurrentPayment: gerçek boşta iptal yanıtı (ErrorCode 102, Data=null); tahsilatı geri almaz. Süren ödemenin iptali modellenmez.
- /UpdateTerminalData: gerçek yanıt şablonu, Data.Response="Veriler güncellendi."
- /RebootDevice: gerçek yanıt şablonu, Data.Response="Cihaz yeniden başlatılıyor."; yalnızca yanıt döner, yeniden başlatma simüle edilmez.
- /ListPendingSaleWithDetail: finalize edilmemiş sanal tahsilatlar; gerçek cihaz durum makinesi iddiası yoktur.
- /FinalizeSale: sanal kaydı sonlandırır; gerçek başarı yanıt örneği yoktur.
- Diğer yollar 501 UNSUPPORTED döner.

## Gerçek şablonlar

References içindeki iki başarı ve altı hata örneği şema referansıdır. Çalışma zamanında yalnızca Templates içindeki temizlenmiş gömülü kaynaklar kullanılır; ham referanslar yayımlanmaz. Kontrol paneli de gömülüdür.

Başarıda Message, OrderNo, SaleUid ve Errors null kalır; IsOffline=true. TransactionHandle alanındaki FingerPrint yazımı referansa uygundur. Nakit PaymentTypeId=1, CashPayment dolu, OnlinePayment=null; kart PaymentTypeId=2, OnlinePayment dolu, CashPayment=null, ResponseCode=00, GivenAmount=0.0. Kart/banka verileri tamamen sanaldır.

Tüm AddedSaleItems kalemleri istek sırasıyla aktarılır. Name, ItemQuantity, UnitPriceAmount, TotalPriceAmount, VATRate, VATAmount ve ReservedText alanları referanstaki yapıdadır. VATRate doğrudan sayısal alan veya açık sayısal KDV eki bulunan TaxGroupCode içinden okunur; varsayılan oran yoktur. VATAmount=Round(TotalPriceAmount*VATRate/(100+VATRate),2,AwayFromZero). Miktar ve tutarların tutarlılığı doğrulanır. Bölünmüş ödeme ve indirim/PriceEffect hesaplanmış sayılmaz.

Hata 20/73'te Data=null; 119/138/199'da Data nesnesi ve null alt alanları korunur. 9001'de Data yoktur, üst seviyede Errors bulunur. Fingerprint/FingerPrint farkları dahil alanlar ve mesajlar kendi referanslarına uygundur. Gerçek hata JSON'una simülatör açıklaması eklenmez.

## Senaryo, tekrar ve düzeltme

Tek Success seçeneğinde şablonu yalnızca istek ödeme türü belirler. Eski SuccessCash/SuccessCard adları uyumluluk için Success'e normalleştirilir; ödeme türünü zorlamaz. Son istek türü yalnızca panel bilgisidir.

Senaryo yalnızca Senaryoyu uygula düğmesiyle değişir. Panel seçilen, uygulanan ve son istekte kullanılan senaryoyu ayırır. Restart ayarı varsayılana döndürür; ödeme/düzeltme kayıtlarını silmez. ReplaySavedOutcome kayıtlı sonucu döndürebilir; ApplySelectedScenario yeni yanıt seçer, ikinci tahsilat oluşturmaz. Hata denemeleri için yeni mantıksal işlem kimliği kullanılmalıdır.

Correction72 ilk yanıtta tarihi, Correction73 sıra numarasını değiştirir. Sonraki isteğin düzeltilmiş handle, tutar ve kalemleri doğrulanmadan başarı verilmez. Kalıcı alias ve kanonik özetler restart sonrasında da eşleştirme sağlar. Correction73 gerçek 73 hata şablonunu kullanır; 72 hata şablonunun gerçek örneği yoktur. Normal Error72/Error73 sürekli hata senaryoları olarak korunur.

PerformEOD, AbortCurrentPayment, UpdateTerminalData ve RebootDevice için yalnızca Busy20 (gerçek 20 şablonu) ve Timeout (yanıt yok) uygulanır; diğer senaryolarda normal yanıt döner. Bu komutların TransactionHandle değerleri istekten aynen yansıtılır.

Son HTTP yanıtı durum kodu ve senaryo bilgisiyle RAM'de gösterilir; diske/loga ham gövde yazılmaz. Timeout veya yanıt kaybı alınmış gibi JSON üretmez. Üretici durum kodlarının resmi mali/banka anlamları veya gerçek iptal/iade semantiği bu simülatörle kanıtlanmaz.