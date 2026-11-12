// (0.1.5) Stream Deck / Stream Dock: Ajustes › "Stream Deck e botões" e o aviso de cada tecla apertada
(() => {
  const el = id => document.getElementById(id);
  const env = o => window.chrome?.webview?.postMessage(o);
  let toastT;

  function toast(texto, erro) {
    let t = el('btToast');
    if (!t) { t = document.createElement('div'); t.id = 'btToast'; document.body.appendChild(t); }
    t.textContent = 'Stream Deck · ' + texto; t.classList.toggle('erro', !!erro); t.classList.add('vis');
    clearTimeout(toastT); toastT = setTimeout(() => t.classList.remove('vis'), erro ? 3500 : 1800);
  }

  window.chrome?.webview?.addEventListener('message', e => {
    const m = e.data;
    if (m?.tipo === 'botao') {
      const erro = (m.texto || '').startsWith('!');
      toast(erro ? m.texto.slice(1) : m.texto, erro);
    } else if (m?.tipo === 'botoes') {
      el('btPorta').textContent = m.porta ? `127.0.0.1:${m.porta} · ligada` : `desligada (${m.erro || 'erro'})`;
      el('btExemplo').textContent = m.exemplo || '';
      if (m.msg != null) el('btMsg').textContent = m.msg;
      const ps = (m.plugin || []).filter(p => p.temPrograma);
      el('btPluginSit').textContent = !ps.length ? 'Programa do aparelho não encontrado neste computador.'
        : ps.map(p => p.nome + (p.instalado ? ': plugin instalado ✓' : ': plugin ainda não instalado')).join(' · ');
      if (m.msg != null && /plugin/i.test(m.msg)) el('btPluginSit').textContent = m.msg;
    }
  });

  el('btCriar').onclick = () => { el('btMsg').textContent = 'Criando…'; env({ tipo: 'botoesCriar' }); };
  el('btPlugin').onclick = () => { el('btPluginSit').textContent = 'Instalando… o programa do aparelho vai fechar e abrir de novo.'; env({ tipo: 'pluginInstalar' }); };
  el('btPasta').onclick = () => env({ tipo: 'botoesPasta' });
  el('btTestar').onclick = () => env({ tipo: 'botoesTestar' });
  el('btCopiar').onclick = () => { navigator.clipboard?.writeText(el('btExemplo').textContent); el('btMsg').textContent = 'Endereço copiado.'; };
  document.querySelector('#ajNav [data-sec="botoes"]')?.addEventListener('click', () => env({ tipo: 'botoesPedir' }));
  setTimeout(() => env({ tipo: 'botoesPedir' }), 500);
})();
