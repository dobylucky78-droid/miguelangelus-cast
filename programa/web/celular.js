// (0.1.20) Ajustes › MiguelAngelus e celulares › 📱 Controle pelo celular: ligar, código, QR code e celulares pareados
(() => {
  const el = id => document.getElementById(id);
  const env = o => window.chrome?.webview?.postMessage(o);
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let C = null;

  function qrSvg(texto) {
    if (typeof qrcode !== 'function') return '';
    const q = qrcode(0, 'M'); q.addData(texto); q.make();
    return q.createSvgTag({ cellSize: 5, margin: 3, scalable: true });
  }
  function quando(ms) {
    if (!ms) return '';
    const s = (Date.now() - ms) / 1000;
    if (s < 120) return 'agora';
    if (s < 3600) return `há ${Math.round(s / 60)} min`;
    const d = new Date(ms);
    return `${String(d.getDate()).padStart(2, '0')}/${String(d.getMonth() + 1).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }

  function render() {
    if (!C) return;
    const bt = el('celLigar');
    bt.textContent = C.ligado ? 'Ligado' : 'Desligado'; bt.classList.toggle('ligado', !!C.ligado);
    const p = el('celPainel');
    if (!C.ligado) { p.innerHTML = '<p class="dica" style="margin:0">Desligado: nenhum celular consegue mandar no Cast.</p>' + lista(); return; }
    if (!C.porta) { p.innerHTML = `<p class="dica" style="margin:0">Não consegui ligar (${esc(C.erro || 'erro')}).</p>` + lista(); return; }
    const end = (C.enderecos || [])[0];
    const url = end ? `http://${end}:${C.porta}/?c=${C.codigo}` : '';
    p.innerHTML = `<div style="display:flex;gap:16px;align-items:center;flex-wrap:wrap">
        ${url ? `<div class="cel-qr" style="width:150px;height:150px;background:#fff;border-radius:8px;padding:4px">${qrSvg(url)}</div>` : ''}
        <div style="flex:1;min-width:220px">
          <div class="dica">Aponte a câmera do celular para o QR code, ou abra no navegador do celular:</div>
          ${(C.enderecos || []).map(a => `<div style="font:600 15px Consolas,monospace;margin:4px 0">http://${esc(a)}:${C.porta}</div>`).join('') || '<div class="dica">Este computador não está em nenhuma rede.</div>'}
          <div class="dica" style="margin-top:6px">Código: <b style="font:700 22px Consolas,monospace;letter-spacing:4px;color:var(--ac)">${esc(C.codigo)}</b> <span>(serve para um celular só; depois troca sozinho)</span></div>
        </div></div>` + lista();
  }
  function lista() {
    const ap = C.aparelhos || [];
    if (!ap.length) return '<p class="dica" style="margin:8px 0 0">Nenhum celular pareado.</p>';
    return '<div class="dica" style="margin:10px 0 4px">Celulares pareados</div>' + ap.map(a =>
      `<div class="ck">📱 ${esc(a.nome)} <span class="dica">· visto ${esc(quando(a.visto))}</span><span class="r"><button class="peq" data-cel-tirar="${esc(a.id)}">Tirar</button></span></div>`).join('');
  }

  window.chrome?.webview?.addEventListener('message', e => {
    if (e.data?.tipo === 'celular') { C = e.data; render(); }
  });
  el('celLigar').onclick = () => env({ tipo: 'celLigar', on: !C?.ligado });
  el('celPainel').addEventListener('click', e => {
    const b = e.target.closest('[data-cel-tirar]');
    if (b && confirm('Tirar este celular? Ele vai precisar parear de novo.')) env({ tipo: 'celTirar', id: b.dataset.celTirar });
  });
  document.querySelector('#ajNav [data-sec="miguel"]')?.addEventListener('click', () => env({ tipo: 'celPedir' }));
  setTimeout(() => env({ tipo: 'celPedir' }), 600);
  // "visto há…" e endereços (o Wi-Fi pode mudar): de tempos em tempos, com a seção aberta
  setInterval(() => { if (!el('celPn')?.closest('[hidden]') && el('celPn')?.offsetParent) env({ tipo: 'celPedir' }); }, 30000);
})();
