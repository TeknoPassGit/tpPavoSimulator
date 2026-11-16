'use strict';
const byId = id => document.getElementById(id);
const money = n => new Intl.NumberFormat('tr-TR',{style:'currency',currency:'TRY'}).format(n);
const labels = Object.fromEntries(Array.from(byId('scenario').options, o => [o.value,o.textContent]));
const label = s => labels[s] || s || '—';
async function local(path, options) {
  return fetch(path,Object.assign({redirect:'error',credentials:'omit',cache:'no-store'},options || {}));
}
async function apply() {
  const settings = {scenario:byId('scenario').value,delayMilliseconds:Number(byId('delay').value),repeatPolicy:byId('policy').value};
  byId('save').disabled = true;
  try {
    const response = await local('/sim/config',{method:'POST',headers:{'Content-Type':'application/json','X-Simulator-Control':'local'},body:JSON.stringify(settings)});
    if (!response.ok) throw new Error('Senaryo uygulanamadı.');
    const applied = await response.json();
    byId('status').textContent = 'Uygulandı: ' + label(applied.scenario);
    await poll();
  } finally { byId('save').disabled = false; }
}
async function poll() {
  try {
    const response = await local('/sim/state');
    if (!response.ok) throw new Error('Yerel servis yanıt vermedi.');
    const state = await response.json();
    byId('captures').textContent = state.captureCount;
    byId('total').textContent = money(state.totalAmount);
    byId('repeats').textContent = state.payments.reduce((n,p) => n + Math.max(0,p.requestCount-1),0);

    const last = state.lastHttp;
    if (last) {
      byId('request-scenario').textContent = 'İstek #' + last.requestId + ' · İstek anındaki seçim: ' + label(last.selectedScenario) +
        ' · Kullanılan: ' + label(last.usedScenario) + (last.repeated ? ' · Tekrar istek' : ' · Yeni istek');
      byId('httpstatus').textContent = last.statusCode == null ? (last.usedScenario === 'PENDING' ? 'Yanıt bekleniyor' : 'HTTP yanıtı yok') : 'HTTP ' + last.statusCode;
      byId('response').textContent = last.body == null ? 'İstemciye JSON yanıt gönderilmedi.' : last.body;
      byId('response-note').textContent = last.note || '';
      byId('response-time').textContent = (last.statusCode == null && last.usedScenario === 'PENDING' ? 'İstek: ' : '') +
        new Date(last.time).toLocaleString('tr-TR');
    } else {
      for (const [id,text] of [['request-scenario','Henüz istek yok.'],
        ['httpstatus','Henüz istek yok'],['response','Henüz yanıt gönderilmedi.'],['response-note',''],['response-time','—']]) byId(id).textContent = text;
    }
    const body = byId('records'); body.replaceChildren();
    state.payments.slice().reverse().forEach(p => {
      const row = body.insertRow();
      for (const value of [new Date(p.time).toLocaleTimeString('tr-TR'),p.saleNumber,money(p.amount),label(p.scenario),p.requestCount,
        (p.captured ? 'Sanal tahsilat var · ' : 'Tahsilat yok · ') + p.result]) row.insertCell().textContent = value;
    });
    byId('connection').textContent = state.writesBlocked ? 'Kayıt yazımı engelli' : 'Yalnızca yerel servisler hazır';
  } catch (_) { byId('connection').textContent = 'Yerel servis bağlantısı yok'; }
}
// Delay and serial apply immediately; the uncommitted scenario selection is not applied.
async function applySetting(change, done, failure) {
  try {
    const current = await (await local('/sim/config')).json();
    change(current);
    const response = await local('/sim/config',{method:'POST',headers:{'Content-Type':'application/json','X-Simulator-Control':'local'},body:JSON.stringify(current)});
    if (!response.ok) throw new Error();
    done(await response.json());
  } catch (_) { byId('status').textContent = failure; }
}
byId('delay').addEventListener('change',() => applySetting(c => { c.delayMilliseconds = Number(byId('delay').value); }, a => {
  byId('delay').value = a.delayMilliseconds;
  byId('status').textContent = 'Yanıt gecikmesi uygulandı: ' + a.delayMilliseconds + ' ms';
}, 'Yanıt gecikmesi uygulanamadı (0–180000 ms).'));
byId('serial').addEventListener('change',() => applySetting(c => { c.serialNumber = byId('serial').value.trim(); }, a => {
  byId('serial').value = a.serialNumber || '';
  byId('status').textContent = a.serialNumber ? 'Seri numarası kaydedildi: ' + a.serialNumber : 'Seri numarası boş: kontrol kapalı.';
}, 'Seri numarası kaydedilemedi.'));
byId('save').addEventListener('click',() => apply().catch(e => {byId('status').textContent=e.message;}));
byId('clear-last').addEventListener('click',async () => {
  try {
    const response = await local('/sim/clear-last',{method:'POST',headers:{'Content-Type':'application/json','X-Simulator-Control':'local'},body:'{}'});
    if (!response.ok) throw new Error();
    await poll();
  } catch (_) { byId('status').textContent = 'Son yanıt temizlenemedi.'; }
});
(async () => {
  try {
    const response = await local('/sim/config'); const config = await response.json();
    byId('scenario').value=config.scenario; byId('delay').value=config.delayMilliseconds; byId('serial').value=config.serialNumber || ''; byId('policy').value=config.repeatPolicy;
    byId('status').textContent='Yerel test için hazır.';
  } catch (_) { byId('status').textContent='Ayarlar okunamadı.'; }
  await poll(); setInterval(poll,1000);
})();