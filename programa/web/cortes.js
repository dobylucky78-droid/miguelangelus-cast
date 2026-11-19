'use strict';
/*
 * (0.1.15) Aba ✂ Cortes: homilias em pé, transcrição (feita no computador pelo programa), sugestões de corte e o corte
 * com legenda. O vídeo toca de https://momentos.local/ (a pasta Momentos, só leitura). Quem transcreve e corta é o
 * programa (Motor\Cortes.cs). Usa do app.js: $, $$, esc, enviar.
 */
const CT = { videos: [], arquivo: '', falas: [], cand: [], ini: null, fim: null, pararEm: null };
const ctTempo = s => { s = Math.max(0, s || 0); const m = Math.floor(s / 60), r = Math.floor(s % 60); return `${m}:${String(r).padStart(2, '0')}`; };
const ctSeg = t => { t = (t || '').trim(); if (!t) return null; const p = t.split(':').map(Number); return p.some(isNaN) ? null : p.reduce((a, v) => a * 60 + v, 0); };

// (0.1.16) mensagem com link (ex.: a página para ativar a API do YouTube): o link abre no navegador e o texto dá para copiar
function ctMsg(t) {
  const el = $('#ctEstado');
  el.innerHTML = esc(t || '').replace(/https:\/\/[^\s<"]+/g, u => `<a href="#" data-link="${u}">abrir a página</a> <button class="peq" data-copiar="${u}" style="min-height:0;padding:2px 8px">copiar o link</button>`);
}
$('#ctEstado').addEventListener('click', e => {
  const a = e.target.closest('[data-link]'); if (a) { e.preventDefault(); enviar({ tipo: 'abrirLink', url: a.dataset.link }); return; }
  const c = e.target.closest('[data-copiar]');
  if (c) navigator.clipboard.writeText(c.dataset.copiar).then(() => c.textContent = '✔ copiado', () => prompt('Copie o link:', c.dataset.copiar));
});
function ctRenderLista(m) {
  CT.videos = m.videos || [];
  $('#ctVideos').innerHTML = CT.videos.map(v => `<button class="ct-item ${v.arquivo === CT.arquivo ? 'sel' : ''}" data-arq="${esc(v.arquivo)}">
      <b>${esc(v.nome)}</b><span class="dica">${esc(v.quando)} · ${v.transcrito ? `${v.candidatos} sugest${v.candidatos === 1 ? 'ão' : 'ões'}` : 'sem transcrição ainda'}</span></button>`).join('')
    || '<p class="dica">Nenhuma homilia em pé ainda. Elas aparecem aqui depois da Missa (homilia gravada pelo Cast) ou pelo botão "📱 Versão em pé de um vídeo…" em Ajustes › Transmissão.</p>';
  CT.cortes = m.cortes || []; CT.ytConectado = !!m.ytConectado; CT.ytEnviando = !!m.ytEnviando;
  $('#ctProntos').innerHTML = CT.cortes.map(c => `<div class="dica" style="display:flex;gap:6px;align-items:center;justify-content:space-between;margin:3px 0">
      <span>✂ ${esc(c.nome.replace(/\.mp4$/i, ''))}${c.yt ? ` · <a href="#" data-yt-abrir="${esc(c.nome)}" title="Abrir no YouTube Studio">▶ no YouTube (privado)</a>` : ''}</span>
      <button class="peq" data-pub="${esc(c.nome)}">Publicar…</button></div>`).join('') || '<p class="dica">Nenhum ainda.</p>';
  if (CT.pub) ctPubAtualizar();
  $('#ctModelo').hidden = !!m.temModelo;
  const sel = $('#ctMusica'), atual = sel.value || ctLs('ctMusica');
  const vin = (id, vazio) => { const s = $(id), at = s.value || ctLs(id); s.innerHTML = `<option value="">${vazio}</option>` + (m.vinhetas || []).map(x => `<option value="${esc(x)}" ${x === at ? 'selected' : ''}>${esc(x.replace(/\.[^.]+$/, ''))}</option>`).join(''); };
  vin('#ctAbertura', '— nenhuma —'); vin('#ctEncerramento', '— nenhum —');
  sel.innerHTML = '<option value="">— nenhuma —</option>' + (m.musicas || []).map(x => `<option value="${esc(x)}" ${x === atual ? 'selected' : ''}>${esc(x.replace(/\.[^.]+$/, ''))}</option>`).join('');
}
const ctLs = (k, v) => { try { if (v === undefined) return localStorage.getItem(k) || ''; localStorage.setItem(k, v); } catch { return ''; } };

function ctAbrir(arq) {
  CT.arquivo = arq; CT.falas = []; CT.cand = []; CT.ini = CT.fim = null;
  $$('#ctVideos .ct-item').forEach(b => b.classList.toggle('sel', b.dataset.arq === arq));
  $('#ctVideo').src = 'https://momentos.local/' + encodeURIComponent(arq);
  $('#ctFalas').innerHTML = '<p class="dica">Carregando…</p>'; $('#ctCandidatos').innerHTML = '';
  ctMarcar();
  enviar({ tipo: 'cortesAbrir', arquivo: arq });
}

function ctRenderTexto(m) {
  if (m.arquivo !== CT.arquivo) return;
  CT.falas = m.falas || []; CT.cand = m.candidatos || [];
  if (!m.falas) {
    $('#ctFalas').innerHTML = '<p class="dica">Ainda sem transcrição. O Cast transcreve sozinho depois da Missa (com a live e a gravação desligadas), se o modelo de fala estiver baixado. Dá para marcar o trecho pelo vídeo e gerar o corte sem legenda.</p>';
    $('#ctCandidatos').innerHTML = '';
    return;
  }
  $('#ctCandidatos').innerHTML = CT.cand.map((c, k) => `<div class="ct-cand" data-k="${k}">
      <div class="linha" style="justify-content:space-between;align-items:center"><b>${ctTempo(c.I)}–${ctTempo(c.F)} · ${Math.round(c.F - c.I)} s</b>
        <span><button class="peq" data-a="ver">▶ Ver</button> <button class="peq" data-a="usar">Usar este trecho</button></span></div>
      <div class="dica">${esc(c.Motivo || '')}</div>
      <div class="ct-cand-txt">${esc(c.Texto)}</div></div>`).join('') || '<p class="dica">Nenhuma sugestão (homilia curta?). Marque o trecho na transcrição.</p>';
  ctRenderFalas();
}
function ctRenderFalas() {
  $('#ctFalas').innerHTML = CT.falas.map((f, i) => {
    const dentro = CT.ini != null && CT.fim != null && f.I >= CT.ini - 0.05 && f.F <= CT.fim + 0.05;
    const sug = CT.cand.some(c => f.I >= c.I - 0.05 && f.F <= c.F + 0.05);
    return `<div class="ct-fala ${dentro ? 'marcada' : ''} ${sug ? 'sug' : ''}" data-i="${i}"><span class="t">${ctTempo(f.I)}</span><span class="x">${esc(f.T)}</span></div>`;
  }).join('');
}
function ctMarcar() {
  $('#ctIni').value = CT.ini != null ? ctTempo(CT.ini) : '';
  $('#ctFim').value = CT.fim != null ? ctTempo(CT.fim) : '';
  const d = CT.ini != null && CT.fim != null ? CT.fim - CT.ini : 0;
  $('#ctDur').textContent = d > 0 ? `${Math.round(d)} s` + (d > 90 ? ' · longo para Reels (até 90 s fica melhor)' : '') : '';
  $$('#ctFalas .ct-fala').forEach(el => {
    const f = CT.falas[+el.dataset.i];
    el.classList.toggle('marcada', CT.ini != null && CT.fim != null && f.I >= CT.ini - 0.05 && f.F <= CT.fim + 0.05);
  });
}
function ctTocar(ini, fim) {
  const v = $('#ctVideo'); if (!v.src) return;
  v.currentTime = ini; CT.pararEm = fim; v.play().catch(() => {});
}

window.chrome?.webview?.addEventListener('message', e => {
  const m = e.data; if (!m || typeof m !== 'object') return;
  if (m.tipo === 'cortesLista') ctRenderLista(m);
  else if (m.tipo === 'cortesTexto') ctRenderTexto(m);
  else if (m.tipo === 'cortesMsg') ctMsg(m.texto);
  // o andamento (versão em pé, transcrição, corte) chega a cada segundo junto com o estado da transmissão
  else if (m.tipo === 'transmissao') { $('#ctGerar').disabled = !!m.ocupado; $('#ctBaixar').disabled = !!m.ocupado; if (m.vertical && m.vertical !== CT.ultVertical) { CT.ultVertical = m.vertical; $('#ctEstado').textContent = m.vertical; } }
});
$('nav [data-tela="cortes"]').addEventListener('click', () => enviar({ tipo: 'cortesLista' }));
$('#ctVideos').addEventListener('click', e => { const b = e.target.closest('[data-arq]'); if (b) ctAbrir(b.dataset.arq); });
$('#ctCandidatos').addEventListener('click', e => {
  const b = e.target.closest('[data-a]'), c = CT.cand[+e.target.closest('[data-k]')?.dataset.k]; if (!b || !c) return;
  if (b.dataset.a === 'ver') ctTocar(c.I, c.F);
  CT.ini = c.I; CT.fim = c.F; ctMarcar();
});
$('#ctFalas').addEventListener('click', e => {
  const el = e.target.closest('.ct-fala'); if (!el || e.target.isContentEditable) return;
  const f = CT.falas[+el.dataset.i];
  if (e.shiftKey && CT.ini != null) CT.fim = Math.max(f.F, CT.ini + 1);
  else { CT.ini = f.I; if (CT.fim == null || CT.fim <= f.I) CT.fim = null; $('#ctVideo').currentTime = f.I; }
  ctMarcar();
});
// corrigir o texto de uma frase (duplo clique; Enter ou sair do campo grava)
$('#ctFalas').addEventListener('dblclick', e => {
  const x = e.target.closest('.ct-fala')?.querySelector('.x'); if (!x) return;
  x.contentEditable = 'true'; x.focus();
});
$('#ctFalas').addEventListener('keydown', e => { if (e.key === 'Enter' && e.target.isContentEditable) { e.preventDefault(); e.target.blur(); } });
$('#ctFalas').addEventListener('focusout', e => {
  const x = e.target; if (!x.isContentEditable) return;
  x.contentEditable = 'false';
  const i = +x.closest('.ct-fala').dataset.i, t = x.textContent.trim();
  if (t && t !== CT.falas[i].T) { CT.falas[i].T = t; enviar({ tipo: 'corteFala', arquivo: CT.arquivo, i, texto: t }); }
});
$('#ctIni').onchange = () => { CT.ini = ctSeg($('#ctIni').value); ctMarcar(); };
$('#ctFim').onchange = () => { CT.fim = ctSeg($('#ctFim').value); ctMarcar(); };
$('#ctVer').onclick = () => { if (CT.ini != null) ctTocar(CT.ini, CT.fim ?? CT.ini + 60); };
$('#ctVideo').addEventListener('timeupdate', e => { if (CT.pararEm != null && e.target.currentTime >= CT.pararEm) { e.target.pause(); CT.pararEm = null; } });
$('#ctGerar').onclick = () => {
  if (!CT.arquivo) return alert('Escolha uma homilia à esquerda.');
  if (CT.ini == null || CT.fim == null || CT.fim - CT.ini < 3) return alert('Marque o início e o fim do trecho (clique e Shift+clique nas frases, ou escreva os tempos).');
  $('#ctAudio').pause();
  enviar({ tipo: 'corteGerar', arquivo: CT.arquivo, ini: CT.ini, fim: CT.fim, musica: $('#ctMusica').value, volume: +$('#ctVolume').value, abertura: $('#ctAbertura').value, encerramento: $('#ctEncerramento').value });
};
$('#ctPasta').onclick = () => enviar({ tipo: 'cortesPasta' });
$('#ctBaixar').onclick = () => { if (confirm('Baixar o modelo de fala (ggml-medium-q5_0.bin, ~540 MB, do Hugging Face)?\nÉ baixado uma vez; depois a transcrição funciona sem internet e nada sai do computador.')) enviar({ tipo: 'cortesModelo' }); };
// (0.1.15) música de fundo: escolher, ouvir no volume do corte (toca junto com o vídeo, para sentir a mistura)
const ctVol = () => { const v = +$('#ctVolume').value; $('#ctVolumeTxt').textContent = v + '%'; $('#ctAudio').volume = Math.min(1, v / 100); ctLs('ctVolume', String(v)); };
$('#ctVolume').value = ctLs('ctVolume') || 15; ctVol();
$('#ctVolume').oninput = ctVol;
$('#ctMusica').onchange = () => { ctLs('ctMusica', $('#ctMusica').value); const a = $('#ctAudio'); a.pause(); a.removeAttribute('src'); $('#ctOuvir').textContent = '♪ Ouvir'; };
$('#ctOuvir').onclick = () => {
  const a = $('#ctAudio'), mus = $('#ctMusica').value; if (!mus) return alert('Escolha uma música (ou coloque músicas na pasta).');
  if (!a.paused) { a.pause(); $('#ctOuvir').textContent = '♪ Ouvir'; return; }
  a.src = 'https://momentos.local/' + encodeURIComponent('Músicas de fundo') + '/' + encodeURIComponent(mus); ctVol();
  a.play().catch(() => {}); $('#ctOuvir').textContent = '■ Parar';
};
$('#ctMusicasPasta').onclick = () => enviar({ tipo: 'cortesMusicasPasta' });
$('#ctVinhetasPasta').onclick = () => enviar({ tipo: 'cortesVinhetasPasta' });
['#ctAbertura', '#ctEncerramento'].forEach(id => $(id).onchange = () => ctLs(id, $(id).value));

// ---------- (0.1.16) publicar um corte ----------
function ctPubAtualizar() {
  const c = CT.cortes.find(x => x.nome === CT.pub);
  const b = $('#ctPubYt');
  b.disabled = !CT.ytConectado || CT.ytEnviando || !!c?.yt;
  b.textContent = c?.yt ? '✔ Já está no YouTube (privado)' : CT.ytEnviando ? '▶ enviando…' : '▶ YouTube (privado)';
  b.title = !CT.ytConectado ? 'Conecte a conta do YouTube em Ajustes › Transmissão' : 'O Cast envia o vídeo como Short PRIVADO; a equipe confere e publica no YouTube Studio';
}
$('#ctProntos').addEventListener('click', e => {
  const a = e.target.closest('[data-yt-abrir]');
  if (a) { e.preventDefault(); enviar({ tipo: 'corteYouTubeAbrir', nome: a.dataset.ytAbrir }); return; }
  const b = e.target.closest('[data-pub]'); if (!b) return;
  CT.pub = b.dataset.pub;
  const base = CT.pub.replace(/\.mp4$/i, '').replace(/\s*-\s*corte\s+[\dmh-]+$/i, '').trim();
  $('#ctPubNome').textContent = '✂ ' + CT.pub.replace(/\.mp4$/i, '');
  $('#ctPubTitulo').value = base;
  $('#ctPubLegenda').value = `${base}\n\n${ctLs('ctPubFim') || '#homilia #evangelho #fé'}`;
  $('#ctPub').hidden = false; ctPubAtualizar();
  // a capa sai sozinha (frase do trecho, data da homilia); se já existe, só mostra
  $('#ctCapaImg').removeAttribute('src'); $('#ctCapaFrase').value = ''; $('#ctCapaData').value = '';
  enviar({ tipo: 'corteCapa', nome: CT.pub, encerramento: $('#ctEncerramento').value });
  $('#ctPub').scrollIntoView({ block: 'nearest' });
});
// o fim da legenda (as hashtags) fica lembrado para o próximo corte
const ctPubGuardarFim = () => { const p = $('#ctPubLegenda').value.split(/\n\s*\n/); if (p.length > 1) ctLs('ctPubFim', p[p.length - 1].trim()); };
$('#ctPubYt').onclick = () => {
  const titulo = $('#ctPubTitulo').value.trim(); if (!titulo) return alert('Escreva um título.');
  ctPubGuardarFim();
  if (!confirm(`Enviar "${titulo}" para o YouTube como Short PRIVADO?\nNinguém vê até alguém publicar no YouTube Studio.`)) return;
  CT.ytEnviando = true; ctPubAtualizar();
  enviar({ tipo: 'corteYouTube', nome: CT.pub, titulo, descricao: $('#ctPubLegenda').value });
};
const ctPubManual = onde => { ctPubGuardarFim(); enviar({ tipo: 'cortePublicar', nome: CT.pub, onde, legenda: $('#ctPubLegenda').value }); };
$('#ctPubMeta').onclick = () => ctPubManual('meta');
$('#ctPubStudio').onclick = () => ctPubManual('youtube');
$('#ctCapaRefazer').onclick = () => {
  if (!CT.pub) return;
  $('#ctCapaRefazer').disabled = true;
  enviar({ tipo: 'corteCapa', nome: CT.pub, encerramento: $('#ctEncerramento').value, frase: $('#ctCapaFrase').value.trim(), data: $('#ctCapaData').value.trim() });
};
window.chrome?.webview?.addEventListener('message', e => {
  const m = e.data; if (m?.tipo !== 'corteCapa' || m.nome !== CT.pub) return;
  if (m.frase != null) $('#ctCapaFrase').value = m.frase;
  if (m.data != null) $('#ctCapaData').value = m.data;
  $('#ctCapaImg').src = m.url; $('#ctCapaRefazer').disabled = false;
});
$('#ctPubFechar').onclick =() => { $('#ctPub').hidden = true; CT.pub = null; };

// ---------- (0.1.16) ☁ homilias e cortes na nuvem ----------
const nhGb = b => (b / 1073741824).toLocaleString('pt-BR', { maximumFractionDigits: b < 1073741824 ? 2 : 1 });
const nhMb = b => b >= 1073741824 ? nhGb(b) + ' GB' : Math.round(b / 1048576) + ' MB';
let NH = null;
function nhRender(m) {
  NH = m;
  if (!m.conectado) {
    $('#nhEspaco').textContent = 'Conecte a conta Google em Ajustes › Nuvem para guardar as homilias e os cortes no Drive e cortar em casa.';
    $('#nhBarra').style.width = '0'; $('#nhLista').innerHTML = ''; $('#nhEstado').textContent = '';
    return;
  }
  const p = m.politica || {}, lim = (p.LimiteGb || 20) * 1073741824;
  if (m.erro) $('#nhEspaco').textContent = 'Não deu para ler a nuvem: ' + m.erro;
  else if (!m.lido) $('#nhEspaco').textContent = 'Lendo a nuvem…';
  else {
    const livre = m.limiteConta ? ` · a conta tem ${nhGb(m.limiteConta - m.usoConta)} GB livres de ${nhGb(m.limiteConta)} GB` : '';
    $('#nhEspaco').textContent = `O Cast usa ${nhMb(m.usoApp)} de ${p.LimiteGb} GB${livre} (lido ${m.lido})${m.email ? ' · conta ' + m.email : ''}`;
  }
  $('#nhAbrir').title = `Abre a pasta "Cast Momentos" no Google Drive com a conta ${m.email || 'conectada'}. Se aparecer "Acesso negado", o navegador está em outra conta: entre com ${m.email || 'a conta da paróquia'} (ou abra num perfil do Chrome que já use essa conta).`;
  const pc = Math.min(100, m.usoApp * 100 / lim);
  $('#nhBarra').style.width = pc + '%'; $('#nhBarra').style.background = pc >= 90 ? 'var(--vivo)' : pc >= 75 ? 'var(--av)' : 'var(--ac)';
  if (m.estado) $('#nhEstado').textContent = m.estado;
  const it = (a, corte) => `<div class="ct-item" style="cursor:default"><b>${corte ? '✂ ' : ''}${esc(a.nome.replace(/\.mp4$/i, '').replace(' (em pé)', ''))}</b>
      <span class="dica">${esc(a.quando)} · ${nhMb(a.tamanho)}${!corte ? (a.texto ? ' · com transcrição' : ' · sem transcrição') : ''}
      ${a.local ? ' · ✔ neste computador' : ` · <button class="peq" data-nh-baixar="${esc(a.nome)}" data-corte="${corte ? 1 : ''}" ${m.rodando ? 'disabled' : ''}>⬇ Baixar</button>`}</span></div>`;
  const h = (m.homilias || []).map(a => it(a, false)).join(''), c = (m.cortes || []).map(a => it(a, true)).join('');
  $('#nhLista').innerHTML = (h || c) ? h + c : (m.lido ? '<p class="dica">Nada na nuvem ainda. As homilias vão sozinhas depois da Missa (com a live e a gravação desligadas).</p>' : '');
  if (!$('#nhPol').open) {
    $('#nhEnvHom').checked = p.EnviarHomilias !== false; $('#nhEnvCor').checked = p.EnviarCortes !== false;
    $('#nhLimite').value = p.LimiteGb ?? 20; $('#nhDiasHom').value = p.DiasHomilias ?? 30; $('#nhDiasCor').value = p.DiasCortes ?? 90;
  }
}
window.chrome?.webview?.addEventListener('message', e => {
  const m = e.data; if (!m || typeof m !== 'object') return;
  if (m.tipo === 'nuvemHomilias') nhRender(m);
  else if (m.tipo === 'transmissao' && m.nuvemCortes) {
    $('#nhEstado').textContent = m.nuvemCortes;
    $$('#nhLista [data-nh-baixar]').forEach(b => b.disabled = !!m.nuvemRodando);
  }
});
$('#nhAtualizar').onclick = () => { $('#nhEspaco').textContent = 'Lendo a nuvem…'; enviar({ tipo: 'nuvemHomLer' }); };
$('#nhAbrir').onclick = () => enviar({ tipo: 'nuvemHomAbrir' });
$('#nhLista').addEventListener('click', e => {
  const b = e.target.closest('[data-nh-baixar]'); if (!b) return;
  b.disabled = true; enviar({ tipo: 'nuvemHomBaixar', nome: b.dataset.nhBaixar, corte: !!b.dataset.corte });
});
$('#nhSalvar').onclick = () => enviar({
  tipo: 'nuvemHomPolitica', enviarHomilias: $('#nhEnvHom').checked, enviarCortes: $('#nhEnvCor').checked,
  limiteGb: Math.max(1, +$('#nhLimite').value || 20), diasHomilias: Math.max(0, Math.round(+$('#nhDiasHom').value || 0)), diasCortes: Math.max(0, Math.round(+$('#nhDiasCor').value || 0))
});
$('#nhEnviar').onclick = () => { $('#nhEstado').textContent = '☁ olhando o que falta enviar…'; enviar({ tipo: 'nuvemHomEnviarAgora' }); };
$('#nhLiberar').onclick = () => {
  const p = NH?.politica || {};
  if (confirm(`Mandar para a lixeira do Drive as homilias mais antigas (e depois os cortes) até o Cast ficar em 80% do limite de ${p.LimiteGb || 20} GB?\nDá para recuperar pela lixeira do Drive por 30 dias. Os arquivos deste computador não são apagados.`)) enviar({ tipo: 'nuvemHomLiberar' });
};
