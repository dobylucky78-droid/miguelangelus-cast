'use strict';
// Tela do MiguelAngelus Cast. Conversa com o programa (C#) por mensagens JSON (chrome.webview);
// os monitores chegam por um buffer compartilhado (RGBA, preview em cima e programa embaixo).
const $ = s => document.querySelector(s), $$ = s => [...document.querySelectorAll(s)];
const NO_APP = !!window.chrome?.webview;
const enviar = m => NO_APP && window.chrome.webview.postMessage(m);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

const E = { cenas: [], preview: null, programa: null, efeito: 'esmaecer', duracao: 500, emTransicao: false };
let mon = null;                       // { buf, l, a }
const ctxPv = $('#cvPv').getContext('2d'), ctxPg = $('#cvPg').getContext('2d');

// ---------- tema (guardado neste computador) ----------
function aplicarTema(t) {
  $('#app').classList.toggle('claro', t === 'claro');
  $('#btnTema').textContent = t === 'claro' ? 'Tema escuro' : 'Tema claro';
  try { localStorage.setItem('cast.tema', t); } catch {}
}
let tema = 'escuro'; try { tema = localStorage.getItem('cast.tema') || 'escuro'; } catch {}
aplicarTema(tema);
$('#btnTema').onclick = () => { tema = tema === 'claro' ? 'escuro' : 'claro'; aplicarTema(tema); };

// ---------- navegação ----------
$$('nav [data-tela]').forEach(b => b.onclick = () => {
  $$('nav [data-tela]').forEach(x => x.classList.toggle('sel', x === b));
  $$('main > section').forEach(s => s.hidden = s.dataset.tela !== b.dataset.tela);
  const rt = b.dataset.tela === 'rastreio';
  enviar({ tipo: 'rtVisivel', sim: rt });
  if (rt) { enviar({ tipo: 'rtAbrir' }); enviar({ tipo: 'camPedir' }); enviar({ tipo: 'rtPessoas' }); }
  if (b.dataset.tela === 'inicio' && typeof pedirInicio === 'function') pedirInicio();
  if (b.dataset.tela === 'roteiro') enviar({ tipo: 'rotPedir' });
});

// ---------- mensagens do programa ----------
if (NO_APP) {
  window.chrome.webview.addEventListener('sharedbufferreceived', e => {
    const d = e.additionalData || {};
    if (d.tipo === 'monitores') mon = { buf: e.getBuffer(), l: d.l, a: d.a };
  });
  window.chrome.webview.addEventListener('message', e => {
    const m = e.data;
    if (m.tipo === 'quadro') desenharMonitores();
    else if (m.tipo === 'estado') { Object.assign(E, m); renderCenas(); renderEfeitos(); if (typeof renderRoVivo === 'function') { renderRoVivo(); if (!document.activeElement?.closest('#roLista')) renderRoLista(); } }
    else if (m.tipo === 'numeros') $('#numeros').textContent = `Saída ${E.saida || ''} · ${String(m.qps).replace('.', ',')} q/s · ${String(m.ms).replace('.', ',')} ms por quadro${m.atrasos ? ' · ' + m.atrasos + ' atrasos' : ''}`;
    else if (m.tipo === 'versao') $('#versao').textContent = 'versão ' + m.versao;
    else if (m.tipo === 'fontesNdi') preencherNdi(m.lista, m.erro);
    else if (m.tipo === 'imagemEscolhida') $('#ncImagem').value = m.caminho;
    else if (m.tipo === 'videoEscolhido') $('#ncVideo').value = m.caminho;
    else if (m.tipo === 'transmissao') renderSaida(m);
    else if (m.tipo === 'nivel') renderNivel(m);
    else if (m.tipo === 'rastreio') renderRastreio(m);
    else if (m.tipo === 'cameras') { renderCameras(m); renderRtCameras(); if (!document.activeElement?.closest('#roLista')) renderRoLista(); }
    else if (m.tipo === 'camMsg') camMsg(m.texto);
    else if (m.tipo === 'camSel') CAM.sel = m.id;
    else if (m.tipo === 'roteiro') renderRoteiro(m);
    else if (m.tipo === 'capa') renderCapa(m);
    else if (m.tipo === 'inicio') renderInicio(m);
    else if (m.tipo === 'rede') renderRede(m);
    else if (m.tipo === 'rtPessoas') renderPessoas(m);
    else if (m.tipo === 'sobre') { $('#soVersao').textContent = 'versão ' + m.versao; $('#soDados').textContent = m.dados; }
    else if (m.tipo === 'backupMsg') $('#bkMsg').textContent = m.msg;
    else if (m.tipo === 'inicioCk') renderInicioCk(m);
    else if (m.tipo === 'capaImg') { if (m.modo === 'mini') $('#inCapa').src = 'data:image/jpeg;base64,' + m.img; else if (m.modo === CP.modo) $('#cpImg').src = 'data:image/jpeg;base64,' + m.img; }
    else if (m.tipo === 'rotVideoEscolhido') videoEscolhido(m);
    else if (m.tipo === 'somCfg') renderSomCfg(m);
    else if (m.tipo === 'saidaCfg') renderAjSaida(m);
    else if (m.tipo === 'codificadores') { codTestados = m.lista; $('#ajCodInfo').textContent = m.lista.length ? 'Funcionam aqui: ' + m.lista.map(nomeCod).join(', ') : 'Nenhum codificador funcionou.'; renderCod(); }
    else if (m.tipo === 'aviso') { $('#avisoSaida').textContent = m.texto; setTimeout(() => $('#avisoSaida').textContent = '', 12000); }
  });
  enviar({ tipo: 'pronto' }); enviar({ tipo: 'rotPedir' }); enviar({ tipo: 'camPedir' });
}

function desenharMonitores() {
  if (!mon) return;
  const n = mon.l * mon.a * 4;
  ctxPv.putImageData(new ImageData(new Uint8ClampedArray(mon.buf, 0, n), mon.l, mon.a), 0, 0);
  ctxPg.putImageData(new ImageData(new Uint8ClampedArray(mon.buf, n, n), mon.l, mon.a), 0, 0);
}

// ---------- cenas ----------
const nomeTipo = f => !f ? '' : f.startsWith('ndi:') ? '📷 ' + f.slice(4) : f.startsWith('imagem:') ? '🖼 ' + f.slice(7).split(/[\\/]/).pop() : f.startsWith('video:') ? '🎬 ' + f.slice(6).split(/[\\/]/).pop() : f.startsWith('cor:') ? '■ cor ' + f.slice(4) : f.startsWith('capa:') ? '✠ capa da live' + (f === 'capa:espera' ? ' · contagem' : '') : '▦ barras de teste';
const corEstado = e => e === 'ok' || /^(pronto|tocando|terminou|preparando)/.test(e || '') ? 'var(--ok)' : e ? 'var(--av)' : 'var(--su)';

function renderCenas() {
  const cena = id => E.cenas.find(c => c.id === id);
  $('#nomePv').textContent = cena(E.preview)?.nome || '';
  $('#nomePg').textContent = (E.emTransicao ? '→ ' + (cena(E.para)?.nome || '') : cena(E.programa)?.nome) || '';
  $('#cenas').innerHTML = E.cenas.map((c, i) => `
    <button class="cena ${c.id === E.preview ? 'pv' : ''} ${c.id === E.programa ? 'pg' : ''}" data-id="${esc(c.id)}" title="${esc(c.estado && c.estado !== 'ok' ? c.estado : '')}">
      ${i < 9 ? `<span class="n">${i + 1}</span>` : ''}
      <b>${esc(c.nome)}</b>
      <span class="tipo"><span class="dot" style="background:${corEstado(c.estado)}"></span>${esc(nomeTipo(c.fonte))}${c.fonte?.startsWith('video:') && c.estado ? ' · ' + esc(c.estado.replace(/^pronto · /, '')) : ''}</span>
      <span class="acoes"><span data-acao="esq" title="Para a esquerda">◀</span><span data-acao="dir" title="Para a direita">▶</span><span data-acao="apagar" title="Apagar a cena">✕</span></span>
    </button>`).join('') + `<button class="cena nova-btn" id="btnNova">＋ Cena</button>`;
}

$('#cenas').addEventListener('click', e => {
  if (e.target.closest('#btnNova')) return abrirNova();
  const b = e.target.closest('.cena[data-id]'); if (!b) return;
  const acao = e.target.closest('[data-acao]')?.dataset.acao, id = b.dataset.id;
  if (acao === 'apagar') {
    if (id === E.programa) return alert('A cena que está no programa não pode ser apagada.');
    if (confirm(`Apagar a cena "${E.cenas.find(c => c.id === id)?.nome}"?`)) enviar({ tipo: 'apagarCena', id });
  } else if (acao === 'esq' || acao === 'dir') enviar({ tipo: 'mover', id, delta: acao === 'esq' ? -1 : 1 });
  else enviar({ tipo: 'preview', id });
});
$('#cenas').addEventListener('dblclick', e => {
  const b = e.target.closest('.cena[data-id]');
  if (b && !e.target.closest('[data-acao]')) enviar({ tipo: 'direto', id: b.dataset.id });
});

// ---------- transição ----------
function renderEfeitos() {
  $$('#efeitos [data-ef]').forEach(b => b.classList.toggle('sel', b.dataset.ef === E.efeito));
  $('#dur').value = E.duracao; $('#durTxt').textContent = (E.duracao / 1000).toFixed(1).replace('.', ',') + ' s';
  $('#dur').disabled = E.efeito === 'corte';
  $('#btnTransicao').disabled = E.emTransicao;
}
$('#btnTransicao').onclick = () => enviar({ tipo: 'transicao' });
$$('#efeitos [data-ef]').forEach(b => b.onclick = () => enviar({ tipo: 'efeito', efeito: b.dataset.ef, ms: +$('#dur').value }));
$('#dur').oninput = () => { $('#durTxt').textContent = ($('#dur').value / 1000).toFixed(1).replace('.', ',') + ' s'; };
$('#dur').onchange = () => enviar({ tipo: 'efeito', efeito: E.efeito, ms: +$('#dur').value });

document.addEventListener('keydown', e => {
  if (e.target.closest('input,select,textarea') || $('[data-tela="mesa"]').hidden) return;
  if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); enviar({ tipo: 'transicao' }); }
  else if (/^[1-9]$/.test(e.key) && E.cenas[+e.key - 1]) enviar({ tipo: e.shiftKey ? 'direto' : 'preview', id: E.cenas[+e.key - 1].id });
});

// ---------- nova cena ----------
function abrirNova() {
  $('#novaCena').hidden = false; $('#ncNome').focus(); $('#ncErro').textContent = '';
  if ($('#ncTipo').value === 'ndi') enviar({ tipo: 'fontesNdi' });
}
$('#btnFecharNova').onclick = () => $('#novaCena').hidden = true;
$('#ncTipo').onchange = () => {
  $$('[data-nc]').forEach(el => el.hidden = el.dataset.nc !== $('#ncTipo').value);
  if ($('#ncTipo').value === 'ndi') enviar({ tipo: 'fontesNdi' });
};
$('#btnNdi').onclick = e => { e.preventDefault(); $('#ncNdi').innerHTML = '<option>procurando…</option>'; enviar({ tipo: 'fontesNdi' }); };
$('#btnImagem').onclick = e => { e.preventDefault(); enviar({ tipo: 'escolherImagem' }); };
$('#btnVideo').onclick = e => { e.preventDefault(); enviar({ tipo: 'escolherVideo' }); };
function preencherNdi(lista, erro) {
  $('#ncNdi').innerHTML = lista.length ? lista.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join('')
    : `<option value="">${esc(erro || 'Nenhuma fonte NDI na rede ainda (↻ para procurar de novo)')}</option>`;
}
$('#btnCriar').onclick = () => {
  const tipo = $('#ncTipo').value; let fonte = 'barras';
  if (tipo === 'ndi') { if (!$('#ncNdi').value) return $('#ncErro').textContent = 'Escolha a fonte NDI.'; fonte = 'ndi:' + $('#ncNdi').value; }
  else if (tipo === 'imagem') { if (!$('#ncImagem').value) return $('#ncErro').textContent = 'Escolha a imagem.'; fonte = 'imagem:' + $('#ncImagem').value; }
  else if (tipo === 'video') { if (!$('#ncVideo').value) return $('#ncErro').textContent = 'Escolha o vídeo.'; fonte = 'video:' + $('#ncVideo').value; }
  else if (tipo === 'cor') fonte = 'cor:' + $('#ncCor').value;
  const nome = $('#ncNome').value.trim() || (tipo === 'ndi' ? $('#ncNdi').value : tipo === 'cor' ? 'Cor' : tipo === 'imagem' ? $('#ncImagem').value.split(/[\\/]/).pop() : tipo === 'video' ? $('#ncVideo').value.split(/[\\/]/).pop().replace(/\.[^.]+$/, '') : 'Barras');
  enviar({ tipo: 'novaCena', nome, fonte, preencher: $('#ncAjuste').value === 'preencher' });
  $('#novaCena').hidden = true; $('#ncNome').value = '';
};

// ---------- gravar e transmitir ----------
const S = { aoVivo: false, gravando: false };
let SC = null, codTestados = null;
const nomeCod = c => ({ h264_qsv: 'Intel (placa)', h264_nvenc: 'NVIDIA (placa)', h264_amf: 'AMD (placa)', h264_mf: 'Windows', libopenh264: 'OpenH264 (processador)' })[c] || c;
const corDest = e => /no ar|gravando/.test(e) ? 'var(--ok)' : /caiu|atrasado|parou/.test(e) ? 'var(--vivo)' : /conectando/.test(e) ? 'var(--av)' : 'var(--su)';

function renderSaida(m) {
  Object.assign(S, m);
  $('#btnGravar').classList.toggle('on', m.gravando);
  $('#btnGravar').textContent = m.gravando ? `■ Parar gravação ${m.tempoGrav}` : '● Gravar';
  $('#btnAr').classList.toggle('on', m.aoVivo);
  $('#btnAr').textContent = m.aoVivo ? `Encerrar transmissão · ${m.tempoAr}` : 'Iniciar transmissão';
  $('#destinos').innerHTML = m.destinos.filter(d => d.ligado).map(d => `<span class="dest ${d.ligado ? '' : 'off'}" title="${esc(d.estado)}${d.quedas ? ' · caiu ' + d.quedas + 'x' : ''}">
    <span class="dot" style="background:${corDest(d.estado)}"></span>${esc(d.nome)}${m.aoVivo ? ' · ' + esc(d.estado) + (d.kbps ? ' · ' + d.kbps + ' kbps' : '') : ''}</span>`).join('')
    || '<span class="dica">Nenhum destino ligado (Ajustes › Transmissão)</span>';
  $('#saidaInfo').textContent = (m.aoVivo || m.gravando) ? [nomeCod(m.codificador), m.kbps + ' kbps', m.perdidos ? m.perdidos + ' quadros perdidos' : '', m.gravando ? m.arquivo : '', m.erro].filter(Boolean).join(' · ') : '';
  const p = $('#pillSaida'); p.classList.toggle('vivo', m.aoVivo); p.lastElementChild.textContent = m.aoVivo ? 'AO VIVO ' + m.tempoAr : 'Fora do ar';
  $('#pillGrav').hidden = !m.gravando; $('#pillGrav').lastElementChild.textContent = 'Gravando ' + m.tempoGrav;
}
$('#btnGravar').onclick = () => {
  if (S.gravando && !confirm('Parar a gravação?')) return;
  enviar({ tipo: 'gravar', ligar: !S.gravando });
};
$('#btnAr').onclick = () => {
  if (S.aoVivo) { if (confirm('ENCERRAR a transmissão em todas as plataformas?')) enviar({ tipo: 'transmitir', ligar: false }); return; }
  const lig = (SC?.destinos || []).filter(d => d.ligado).map(d => d.nome);
  if (!lig.length) return $('#avisoSaida').textContent = 'Nenhum destino ligado. Ligue em Ajustes › Transmissão.';
  if (confirm('Iniciar a transmissão para: ' + lig.join(', ') + '?')) enviar({ tipo: 'transmitir', ligar: true });
};

function renderAjSaida(m) {
  SC = m;
  $('#ajDestinos').innerHTML = m.destinos.map(d => `
    <div class="ajdest" data-id="${esc(d.id)}">
      <label class="nome"><input type="checkbox" data-c="ligado" ${d.ligado ? 'checked' : ''}>${esc(d.nome)}</label>
      <label class="c2">Servidor${d.tipo === 'instagram' ? ' (copie do Live Producer)' : ''}<input data-c="servidor" value="${esc(d.servidor)}" placeholder="rtmps://…" spellcheck="false"></label>
      <label class="c2">Chave da transmissão<input data-c="chave" type="password" autocomplete="off" placeholder="${d.chave ? 'guardada: ' + esc(d.chave) + ' (cole outra para trocar)' : 'cole a chave aqui'}"></label>
      <span class="linha"><button class="peq prim" data-a="salvar">Salvar</button>${d.chave ? '<button class="peq" data-a="apagar">Apagar chave</button>' : ''}</span>
    </div>`).join('');
  $('#ajKbps').value = String(m.kbps);
  if (!$('#ajKbps').value) { $('#ajKbps').insertAdjacentHTML('beforeend', `<option value="${m.kbps}">${m.kbps} kbps</option>`); $('#ajKbps').value = String(m.kbps); }
  renderCod();
  $('#ajPasta').value = m.pasta;
  $('#ajFfmpeg').textContent = m.temFfmpeg ? '' : 'Atenção: o FFmpeg não está na pasta do programa; sem ele não dá para gravar nem transmitir.';
}
function renderCod() {
  if (!SC) return;
  const lista = codTestados || ['h264_qsv', 'h264_nvenc', 'h264_amf', 'h264_mf', 'libopenh264'];
  $('#ajCod').innerHTML = `<option value="auto">Automático${SC.detectado ? ' (' + nomeCod(SC.detectado) + ')' : ''}</option>` + lista.map(c => `<option value="${c}">${nomeCod(c)}</option>`).join('');
  $('#ajCod').value = SC.codificador; if (!$('#ajCod').value) $('#ajCod').value = 'auto';
}
$('#ajDestinos').addEventListener('click', e => {
  const b = e.target.closest('[data-a]'); if (!b) return;
  const row = b.closest('.ajdest'), v = c => row.querySelector(`[data-c="${c}"]`);
  if (b.dataset.a === 'apagar') { if (confirm('Apagar a chave guardada deste destino?')) enviar({ tipo: 'salvarDestino', id: row.dataset.id, apagarChave: true }); return; }
  enviar({ tipo: 'salvarDestino', id: row.dataset.id, servidor: v('servidor').value, ligado: v('ligado').checked, chave: v('chave').value });
  v('chave').value = '';
});
$('#ajDestinos').addEventListener('change', e => {
  if (e.target.dataset.c !== 'ligado') return;
  enviar({ tipo: 'salvarDestino', id: e.target.closest('.ajdest').dataset.id, ligado: e.target.checked });
});
$('#ajKbps').onchange = $('#ajCod').onchange = () => enviar({ tipo: 'salvarSaida', kbps: +$('#ajKbps').value, codificador: $('#ajCod').value });
$('#btnTestarCod').onclick = () => { $('#ajCodInfo').textContent = 'testando…'; enviar({ tipo: 'testarCodificadores' }); };
$('#btnPasta').onclick = () => enviar({ tipo: 'escolherPasta' });
$('#btnAbrirPasta').onclick = () => enviar({ tipo: 'abrirPasta' });
// ---------- som ----------
let SOM = null; const pico = { E: -120, D: -120, tE: 0, tD: 0 };
const pct = db => Math.max(0, Math.min(100, (db + 60) / 60 * 100));     // -60 dB .. 0 dB
function barra(i, s, db, lado) {
  const agora = performance.now();
  if (db > pico[lado] || agora - pico['t' + lado] > 1500) { pico[lado] = db; pico['t' + lado] = agora; }
  for (const [ii, ss] of [[i, s]]) {
    const el = $('#' + ii), pk = $('#' + ss);
    if (el) el.style.clipPath = `inset(0 ${100 - pct(db)}% 0 0)`;
    if (pk) pk.style.left = `calc(${pct(pico[lado])}% - 2px)`;
  }
}
const fmtDb = v => v <= -100 ? '−∞' : (v > 0 ? '+' : '') + v.toFixed(0).replace('-', '−');
function renderNivel(m) {
  for (const suf of ['', '2']) { barra('vuE' + suf, 'pkE' + suf, m.pe, 'E'); barra('vuD' + suf, 'pkD' + suf, m.pd, 'D'); }
  const txt = m.estado !== 'ok' && m.estado !== 'sem som' ? m.estado : `pico ${fmtDb(Math.max(m.pe, m.pd))} dB`;
  $('#somDb').textContent = txt; $('#smDb2').textContent = txt;
  $('#somNome').textContent = m.nome; $('#somNome').title = m.nome;
  $('#somAviso').textContent = m.avisoMesa ? '⚠ ' + m.avisoMesa : '';
  const sg = v => (v >= 0 ? '+' : '−') + Math.abs(v).toFixed(0);
  const niv = [m.lufs > -70 ? 'volume ' + fmtDb(m.lufs) + ' LUFS' : '', m.niv ? 'nivelador ' + sg(m.nivGanho) + ' dB' : 'nivelador desligado', m.lim < -0.5 ? 'limitador ' + sg(m.lim) + ' dB' : '', m.atraso > 0 ? 'atraso ' + m.atraso + ' ms' : '', m.abaixado ? '🎬 vídeo tocando: mesa abaixada' : ''].filter(Boolean).join(' · ');
  const nivTxt = m.estado === 'ok' ? niv : ''; $('#somNiv').textContent = nivTxt; $('#smNiv').textContent = nivTxt;
  $('#btnMudo').classList.toggle('on', m.mudo); $('#btnMudo').textContent = m.mudo ? 'MUDO (clique para voltar)' : 'Mudo';
  S.mudo = m.mudo;
}
$('#btnMudo').onclick = () => enviar({ tipo: 'mudo', ligado: !S.mudo });

function renderSomCfg(m) {
  SOM = m;
  $('#smPerfil').innerHTML = m.perfis.map(p => `<option value="${esc(p.id)}">${esc(p.nome)}</option>`).join('');
  $('#smPerfil').value = m.perfil;
  const grupos = [...new Set(m.dispositivos.map(d => d.grupo))];
  $('#smDisp').innerHTML = `<option value="">Sem som</option>` + grupos.map(g => `<optgroup label="${esc(g)}">` +
    m.dispositivos.filter(d => d.grupo === g).map(d => `<option value="${esc(d.id)}">${esc(d.nome)}${d.canais > 2 ? ' · ' + d.canais + ' canais' : ''}</option>`).join('') + '</optgroup>').join('');
  $('#smDisp').value = m.dispositivo; if ($('#smDisp').value !== m.dispositivo) $('#smDisp').insertAdjacentHTML('beforeend', `<option value="${esc(m.dispositivo)}">(não encontrada) ${esc(m.dispositivo)}</option>`), $('#smDisp').value = m.dispositivo;
  const disp = m.dispositivos.find(d => d.id === m.dispositivo);
  const n = Math.max(m.canaisEntrada || 0, disp?.canais || 0, 2), r = i => m.rotulos[i - 1] || ('Entrada ' + i);
  let ops = '';
  for (let i = 1; i < n; i += 2) ops += `<option value="${i},${i + 1}">${esc(r(i))} + ${esc(r(i + 1))} (estéreo)</option>`;
  for (let i = 1; i <= n; i++) ops += `<option value="${i}">${esc(r(i))} (mono)</option>`;
  $('#smCanais').innerHTML = ops; $('#smCanais').value = m.canais.join(',');
  $('#smGanho').value = m.ganho; $('#smGanhoTxt').textContent = (m.ganho > 0 ? '+' : '') + m.ganho + ' dB';
  const perfil = m.perfis.find(p => p.id === m.perfil);
  const sugerir = m.perfil === 'generico' && disp && m.perfis.find(p => p.reconhecer && disp.nome.toLowerCase().includes(p.reconhecer.toLowerCase()));
  $('#smAviso').textContent = sugerir ? `Esta entrada parece ser da ${sugerir.nome}: escolha o perfil dela em "Mesa de som".` : (perfil?.aviso || '');
  $('#smEstado').textContent = m.estado !== 'ok' && m.estado !== 'sem som' ? m.estado : '';
  $('#smRede').hidden = m.perfil !== 'ui24r';
  mostrarAtraso(m.atraso);
  $('#smAbaixar').checked = m.abaixar; $('#smAbaixarDb').value = String(m.abaixarDb); $('#smAbaixarDb').disabled = !m.abaixar; $('#smVolVideo').value = String(m.volumeVideo);
  $('#smNivelar').checked = m.nivelar; $('#smAlvo').value = String(m.alvo); $('#smAlvo').disabled = !m.nivelar;
  if (document.activeElement !== $('#smMesaIp')) $('#smMesaIp').value = m.mesaIp || '';
  $('#smMesaEstado').textContent = !m.mesaIp ? 'opcional: traz os nomes dados na mesa (só leitura, o Cast não mexe na mesa)' : m.mesaEstado === 'conectada' ? '● conectada · ' + m.mesaModelo + ' · só leitura' : m.mesaEstado;
}
const salvarSom = extra => enviar({ tipo: 'salvarSom', dispositivo: $('#smDisp').value, canais: $('#smCanais').value.split(',').map(Number), ganho: +$('#smGanho').value, perfil: $('#smPerfil').value, ...extra });
$('#smDisp').onchange = () => { $('#smCanais').value = '1,2'; salvarSom(); };
$('#smCanais').onchange = $('#smPerfil').onchange = () => salvarSom();
$('#smGanho').oninput = () => { const g = +$('#smGanho').value; $('#smGanhoTxt').textContent = (g > 0 ? '+' : '') + g + ' dB'; salvarSom({ soGanho: true }); };
$('#smNivelar').onchange = () => salvarSom({ soGanho: true, nivelar: $('#smNivelar').checked });
$('#smAlvo').onchange = () => salvarSom({ soGanho: true, alvo: +$('#smAlvo').value });
function mostrarAtraso(v) { $('#smAtrasoTxt').textContent = v + ' ms'; $('#smAtraso').value = Math.min(v, 1000); if (document.activeElement !== $('#smAtrasoN')) $('#smAtrasoN').value = v; }
function mudarAtraso(v) { v = Math.max(0, Math.min(2000, Math.round(+v / 10) * 10 || 0)); mostrarAtraso(v); salvarSom({ soGanho: true, atraso: v }); }
$('#smAtraso').oninput = () => mudarAtraso($('#smAtraso').value);
$('#smAtrasoN').onchange = () => mudarAtraso($('#smAtrasoN').value);
$('#btnAtrasoMenos').onclick = () => mudarAtraso(+$('#smAtrasoN').value - 10);
$('#btnAtrasoMais').onclick = () => mudarAtraso(+$('#smAtrasoN').value + 10);
$('#smAbaixar').onchange = () => salvarSom({ soGanho: true, abaixar: $('#smAbaixar').checked });
$('#smAbaixarDb').onchange = () => salvarSom({ soGanho: true, abaixarDb: +$('#smAbaixarDb').value });
$('#smVolVideo').onchange = () => salvarSom({ soGanho: true, volumeVideo: +$('#smVolVideo').value });
$('#btnSmMesa').onclick = () => salvarSom({ mesaIp: $('#smMesaIp').value });
$('#btnSmLista').onclick = () => enviar({ tipo: 'pedirSom' });
// ---------- câmera que segue (rastreio) ----------
const RT = { real: false };
function renderRastreio(m) {
  if (m.erro) $('#rtErro').textContent = m.erro;
  if (!m.aberto) return;
  Object.assign(RT, m);
  if (m.img) $('#rtImg').src = m.img;
  $('#rtInfo').textContent = [m.l1, m.l2, m.l3].filter(Boolean).join('\n') || '—';
  $('#rtEvento').textContent = m.evento ? 'Câmera: ' + m.evento : '';
  const b = $('#rtReal'); b.classList.toggle('on', m.real); b.textContent = 'Mover a câmera de verdade: ' + (m.real ? 'LIGADO' : 'DESLIGADO');
  $('#rtSeguir').checked = m.seguir; $('#rtAnalisar').checked = m.analisar;
  $('#rtCadastrar').textContent = m.coletando > 0 ? `Cadastrando… (${m.coletando})` : m.cadastrado ? 'Cadastrar de novo' : 'Cadastrar rosto';
  const sel = $('#rtFonte');
  if (document.activeElement !== sel) {
    const atual = sel.value || m.fonte || m.cfg?.fonte || '';
    const lista = [...new Set([...(m.fontes || []), ...(atual ? [atual] : [])])];
    const html = lista.length ? lista.map(n => `<option value="${esc(n)}">${esc(n)}${n === m.fonte ? ' (conectada)' : ''}</option>`).join('') : '<option value="">procurando câmeras NDI…</option>';
    if (sel.dataset.h !== html) { sel.innerHTML = html; sel.dataset.h = html; }
    if (atual) sel.value = atual;
  }
  if (m.cfg) {
    $('#rtIp').value = m.cfg.Ip; $('#rtPorta').value = m.cfg.Porta; $('#rtPan').value = m.cfg.Pan; $('#rtTilt').value = m.cfg.Tilt;
    $('#rtZona').value = m.cfg.Zona; $('#rtInverter').checked = m.cfg.Inverter; $('#rtLeve').checked = m.cfg.Leve;
  }
  // Ao vivo: linha do rastreio com o PARAR sempre à mão
  $('#rtVivo').hidden = false;
  $('#rtVivoTxt').textContent = (m.real ? 'MOVENDO A CÂMERA · ' : 'simulação · ') + (m.coletando > 0 ? 'cadastrando…' : (m.l2 || '').replace(/\s+\[rosto.*$/, '')) + (m.fonte ? ' · ' + m.fonte : '');
}
$('#rtReal').onclick = () => {
  if (!RT.real && !confirm('Ligar o movimento da câmera de verdade (' + $('#rtIp').value + ')?')) return;
  enviar({ tipo: 'rtReal', sim: !RT.real });
};
$('#rtParar').onclick = $('#rtVivoParar').onclick = () => enviar({ tipo: 'rtParar' });
$('#rtAtualizar').onclick = () => enviar({ tipo: 'rtAtualizar' });
$('#rtConectar').onclick = () => { if ($('#rtFonte').value) enviar({ tipo: 'rtConectar', fonte: $('#rtFonte').value, leve: $('#rtLeve').checked }); };
$('#rtCadastrar').onclick = () => enviar({ tipo: 'rtCadastrar' });
$('#rtApagar').onclick = () => enviar({ tipo: 'rtApagar' });
$('#rtSeguir').onchange = () => enviar({ tipo: 'rtSeguir', sim: $('#rtSeguir').checked });
$('#rtAnalisar').onchange = () => enviar({ tipo: 'rtAnalisar', sim: $('#rtAnalisar').checked });
const rtAjustes = () => enviar({ tipo: 'rtAjustes', pan: +$('#rtPan').value, tilt: +$('#rtTilt').value, zona: +$('#rtZona').value, inverter: $('#rtInverter').checked, ip: $('#rtIp').value, porta: +$('#rtPorta').value });
['#rtPan', '#rtTilt', '#rtZona', '#rtInverter', '#rtIp', '#rtPorta'].forEach(s => $(s).onchange = rtAjustes);
$('#rtTeste').onclick = () => enviar({ tipo: 'rtTeste' });
$('#rtPasta').onclick = () => enviar({ tipo: 'rtPasta' });
$('#rtPacote').onclick = () => enviar({ tipo: 'rtPacote' });
$('#rtImg').addEventListener('click', e => {
  const im = e.currentTarget, r = im.getBoundingClientRect();
  if (!im.naturalWidth) return;
  const esc2 = Math.min(r.width / im.naturalWidth, r.height / im.naturalHeight), w = im.naturalWidth * esc2, h = im.naturalHeight * esc2;
  const x = (e.clientX - r.left - (r.width - w) / 2) / w, y = (e.clientY - r.top - (r.height - h) / 2) / h;
  if (x >= 0 && x <= 1 && y >= 0 && y <= 1) enviar({ tipo: 'rtToque', x, y });
});

// ---------- ajustes: seções ----------
const NOMES_SEC = { aparencia: 'Aparência', capa: 'Capa da live', joystick: 'Joysticks', rede: 'Rede e internet', miguel: 'MiguelAngelus e celulares', backup: 'Backup e sobre' };
function abrirSecao(sec) {
  $$('#ajNav [data-sec]').forEach(b => b.classList.toggle('sel', b.dataset.sec === sec));
  const tem = !!document.querySelector(`.ajcorpo > [data-sec="${sec}"]`);
  $$('.ajcorpo > [data-sec]').forEach(d => d.hidden = d.dataset.sec !== (tem ? sec : 'em-breve'));
  if (!tem) { $('#ajBreveNome').textContent = NOMES_SEC[sec] || sec; $('#ajBreveTxt').textContent = 'Esta seção do protótipo ainda está sendo construída.'; }
  if (sec === 'cameras') { enviar({ tipo: 'camPedir' }); }
  if (sec === 'capa') enviar({ tipo: 'capaPedir', modo: CP.modo });
  if (sec === 'rede' || sec === 'miguel') enviar({ tipo: 'redePedir' });
  if (sec === 'backup') enviar({ tipo: 'sobrePedir' });
  if (sec === 'aparencia') renderAparencia();
  try { localStorage.setItem('cast.secAj', sec); } catch {}
}
$('#ajNav').addEventListener('click', e => { const b = e.target.closest('[data-sec]'); if (b) abrirSecao(b.dataset.sec); });
// a seção lembrada só abre depois que o arquivo inteiro carregou (as seções do fim do arquivo ainda não existem aqui)
setTimeout(() => { let s = 'som'; try { s = localStorage.getItem('cast.secAj') || 'som'; } catch {} abrirSecao(s); }, 0);

// ---------- câmeras e PTZ (os comandos VISCA são os mesmos do MiguelAngelus, ptz.js) ----------
const CAM = { lista: [], fontes: [], sel: null, vel: 0.5, mov: '' };
const PORTAS_PTZ = { 'visca-ip': 52381, udp: 1259, tcp: 5678 };
const camAtual = () => CAM.lista.find(c => c.Id === CAM.sel);
const ptzEnviar = (c, bytes) => c && c.Ip && enviar({ tipo: 'ptz', id: c.Id, bytes });
function ptzMover(c, dx, dy, forca = 0.5) {
  const vp = Math.max(1, Math.min(0x18, Math.round(1 + forca * 0x17))), vt = Math.max(1, Math.min(0x14, Math.round(1 + forca * 0x13)));
  ptzEnviar(c, [0x81, 0x01, 0x06, 0x01, vp, vt, dx < 0 ? 1 : dx > 0 ? 2 : 3, dy < 0 ? 1 : dy > 0 ? 2 : 3, 0xFF]);
}
const ptzParar = c => ptzEnviar(c, [0x81, 0x01, 0x06, 0x01, 0x08, 0x08, 0x03, 0x03, 0xFF]);
const ptzZoom = (c, z, vel = 4) => { const v = Math.max(0, Math.min(7, vel)); ptzEnviar(c, [0x81, 0x01, 0x04, 0x07, z > 0 ? 0x20 + v : z < 0 ? 0x30 + v : 0, 0xFF]); };
const ptzHome = c => ptzEnviar(c, [0x81, 0x01, 0x06, 0x04, 0xFF]);
const ptzIrPara = (c, n) => ptzEnviar(c, [0x81, 0x01, 0x04, 0x3F, 0x02, n & 0x7F, 0xFF]);

function renderCameras(m) {
  if (m.lista) CAM.lista = m.lista;
  if (m.fontes) CAM.fontes = m.fontes;
  if (!CAM.sel || !camAtual()) CAM.sel = CAM.lista[0]?.Id || null;
  $('#camLista').innerHTML = CAM.lista.map(c => `<button class="mo ${c.Id === CAM.sel ? 'sel' : ''}" data-cam="${esc(c.Id)}"><span>📷 ${esc(c.Nome)}<small>${esc(c.Ip || 'sem PTZ')} · ${c.Posicoes.length} posições</small></span></button>`).join('')
    || '<p class="dica">Nenhuma câmera ainda.</p>';
  const c = camAtual(); $('#camEdit').hidden = !c;
  if (!c) return;
  if (!m.soPosicoes) {
    $('#camNome').value = c.Nome; $('#camIp').value = c.Ip; $('#camPorta').value = c.Porta; $('#camProto').value = c.Protocolo;
    const fontes = [...new Set([...(CAM.fontes || []), ...(c.Fonte ? [c.Fonte] : [])])];
    $('#camFonte').innerHTML = '<option value="">— sem imagem —</option>' + fontes.map(f => `<option value="${esc(f)}">${esc(f)}</option>`).join('');
    $('#camFonte').value = c.Fonte || '';
  }
  $('#camPtz').hidden = !c.Ip;
  const pos = c.Posicoes.slice().sort((a, b) => a.N - b.N);
  $('#camNPos').textContent = `(${pos.length})`;
  $('#camPos').innerHTML = pos.map(p => `<span class="ptz-p"><button data-ptzir="${p.N}" title="Posição nº ${p.N} da câmera">📍 ${esc(p.Nome)}</button><button class="ptz-x ptz-ren" data-ptzren="${p.N}" title="Trocar o nome">✎</button><button class="ptz-x ptz-del" data-ptzdel="${p.N}" title="Tirar da lista (a câmera continua com ela)">✕</button></span>`).join('') || '<span class="dica">Nenhuma posição na lista.</span>';
}
const camMsg = t => { $('#camMsg').textContent = t; clearTimeout(CAM.tMsg); CAM.tMsg = setTimeout(() => $('#camMsg').textContent = '', 8000); };
$('#camLista').addEventListener('click', e => { const b = e.target.closest('[data-cam]'); if (b) { CAM.sel = b.dataset.cam; renderCameras({}); } });
$('#btnNovaCam').onclick = () => enviar({ tipo: 'camNova' });
$('#btnCamNdi').onclick = () => enviar({ tipo: 'camPedir', ndi: true });
$('#camProto').onchange = () => { $('#camPorta').value = PORTAS_PTZ[$('#camProto').value]; };
$('#btnCamSalvar').onclick = () => {
  const c = camAtual(); if (!c) return;
  enviar({ tipo: 'camSalvar', id: c.Id, nome: $('#camNome').value.trim() || 'Câmera', fonte: $('#camFonte').value, ip: $('#camIp').value.trim(), porta: +$('#camPorta').value || PORTAS_PTZ[$('#camProto').value], protocolo: $('#camProto').value });
  camMsg('Câmera salva.');
};
$('#btnCamApagar').onclick = () => { const c = camAtual(); if (c && confirm(`Apagar a câmera "${c.Nome}" do Cast? (As posições continuam gravadas na própria câmera.)`)) enviar({ tipo: 'camApagar', id: c.Id }); };
$('#camVel').oninput = () => CAM.vel = +$('#camVel').value;
$('#camPtz').addEventListener('pointerdown', e => {
  const c = camAtual(), m = e.target.closest('[data-ptzmov]'), z = e.target.closest('[data-ptzzoom]');
  if (!c || (!m && !z)) return;
  e.preventDefault(); e.target.setPointerCapture?.(e.pointerId);
  if (m) { const [dx, dy] = m.dataset.ptzmov.split(',').map(Number); ptzMover(c, dx, dy, CAM.vel); CAM.mov = 'mov'; }
  if (z) { ptzZoom(c, +z.dataset.ptzzoom, Math.round(CAM.vel * 7)); CAM.mov = 'zoom'; }
});
const pararPad = () => { const c = camAtual(); if (!c || !CAM.mov) return; if (CAM.mov === 'zoom') ptzZoom(c, 0); else ptzParar(c); CAM.mov = ''; };
addEventListener('pointerup', pararPad); addEventListener('pointercancel', pararPad); addEventListener('blur', pararPad);
$('#camPtz').addEventListener('click', e => {
  const c = camAtual(); if (!c) return;
  const b = e.target.closest('[data-ptz],[data-ptzir],[data-ptzren],[data-ptzdel]'); if (!b) return;
  if (b.dataset.ptz === 'home') ptzHome(c);
  else if (b.dataset.ptzir) { ptzIrPara(c, +b.dataset.ptzir); camMsg(`${c.Nome} indo para "${c.Posicoes.find(p => p.N === +b.dataset.ptzir)?.Nome}".`); }
  else if (b.dataset.ptzren) {
    const p = c.Posicoes.find(x => x.N === +b.dataset.ptzren); const nome = (prompt(`Nome da posição nº ${p.N} (ex.: Altar, Ambão, Coral, Geral):`, p.Nome) || '').trim();
    if (nome && nome !== p.Nome) enviar({ tipo: 'camPosRenomear', id: c.Id, n: p.N, nome });
  } else if (b.dataset.ptzdel) {
    const p = c.Posicoes.find(x => x.N === +b.dataset.ptzdel);
    if (confirm(`Tirar a posição "${p.Nome}" da lista? (A câmera continua com ela gravada.)`)) enviar({ tipo: 'camPosTirar', id: c.Id, n: p.N });
  }
});
$('#btnCamGuardar').onclick = () => {
  const c = camAtual(); if (!c) return;
  const usados = new Set(c.Posicoes.map(p => p.N)); let n = 20; while (usados.has(n) || (n >= 95 && n <= 99)) n++;
  if (n > 127) return camMsg('A câmera não tem mais números livres para posições.');
  const nome = (prompt(`Gravar NA CÂMERA a posição em que ela está agora, no nº ${n}.\n(As posições que já estavam na câmera, como as do OBS, não são tocadas.)\n\nNome desta posição (ex.: Altar, Ambão, Coral, Geral):`) || '').trim();
  if (nome) enviar({ tipo: 'camPosGuardar', id: c.Id, nome });
};
$('#btnCamTrazer').onclick = () => {
  const c = camAtual(); if (!c) return;
  const r = prompt('Quais posições já estão gravadas na câmera (por exemplo, pelo OBS)?\nElas entram na lista só para chamar — nada é gravado na câmera. Depois, ✎ troca o nome.\n\nNúmeros (ex.: 0-15 ou 1, 2, 5):', '0-15');
  if (!r) return;
  const nums = new Set();
  for (const parte of r.split(/[,;\s]+/).filter(Boolean)) {
    const m = /^(\d+)(?:-(\d+))?$/.exec(parte);
    if (!m) return camMsg(`Não entendi "${parte}". Use, por exemplo, 0-15 ou 1, 2, 5.`);
    for (let k = +m[1]; k <= +(m[2] ?? m[1]) && k <= 127; k++) nums.add(k);
  }
  enviar({ tipo: 'camPosTrazer', id: c.Id, nums: [...nums] });
};

// câmera que segue: escolher uma câmera da lista preenche a imagem (NDI), o IP e a porta
function renderRtCameras() {
  const sel = $('#rtCamera'); if (!sel) return;
  const atual = sel.value;
  sel.innerHTML = CAM.lista.length ? CAM.lista.map(c => `<option value="${esc(c.Id)}">${esc(c.Nome)}${c.Ip ? ' · ' + esc(c.Ip) : ''}</option>`).join('') : '<option value="">cadastre em Ajustes › Câmeras</option>';
  if (atual && CAM.lista.some(c => c.Id === atual)) sel.value = atual;
}
$('#rtUsarCamera').onclick = () => { if ($('#rtCamera').value) enviar({ tipo: 'rtUsarCamera', id: $('#rtCamera').value, leve: $('#rtLeve').checked }); };

// ---------- roteiro ----------
const RO = { perfis: [], perfil: null, lista: [], agora: -1, aviso: '', alterado: false };
const TIPOS = ['sem', 'fixo', 'achar', 'seguir'];
const NOME_TIPO = { sem: 'sem câmera', fixo: 'fixo', achar: 'achar', seguir: 'seguir', video: 'vídeo' };
const arquivoDe = p => (p || '').split(/[\\/]/).pop();
function renderRoteiro(m) {
  if (m) { Object.assign(RO, m); if (m.msg) { $('#roMsg').textContent = m.msg; clearTimeout(RO.tMsg); RO.tMsg = setTimeout(() => $('#roMsg').textContent = '', 8000); } }
  const perfil = RO.perfis.find(p => p.id === RO.perfil);
  $('#roPerfilNome').textContent = $('#rvPerfil').textContent = perfil?.nome || '';
  $('#roAlterado').textContent = RO.alterado ? '· alterações não salvas' : '';
  $('#roPerfis').innerHTML = RO.perfis.map(p => `<button class="mo ${p.id === RO.perfil ? 'sel' : ''}" data-perfil="${esc(p.id)}"><span>${esc(p.nome)}<small>${p.modelo ? 'modelo do Cast' : 'meu perfil'} · ${p.n} momentos</small></span></button>`).join('');
  if (!document.activeElement?.closest('#roLista')) renderRoLista();
  renderRoVivo();
}
function opcoesCenas(sel) {
  return '<option value="">— escolha a cena —</option>' + E.cenas.filter(c => !c.fonte?.startsWith('video:')).map(c => `<option value="${esc(c.id)}" ${c.id === sel ? 'selected' : ''}>${esc(c.nome)}</option>`).join('');
}
function opcoesCam(sel) { return '<option value="">— câmera —</option>' + CAM.lista.map(c => `<option value="${esc(c.Id)}" ${c.Id === sel ? 'selected' : ''}>${esc(c.Nome)}</option>`).join(''); }
function opcoesPos(camId, sel) {
  const c = CAM.lista.find(x => x.Id === camId); if (!c) return '<option value="-1">—</option>';
  return '<option value="-1">— posição —</option>' + c.Posicoes.slice().sort((a, b) => a.N - b.N).map(p => `<option value="${p.N}" ${p.N === sel ? 'selected' : ''}>${esc(p.Nome)}</option>`).join('');
}
function renderRoLista() {
  $('#roLista').innerHTML = RO.lista.map((m, i) => {
    const acoes = `<span class="acoes"><button data-a="sobe" title="Subir">▲</button><button data-a="desce" title="Descer">▼</button><button data-a="tira" title="Tirar do roteiro">✕</button></span>`;
    if (m.Tipo === 'video') return `<div class="rolinha video ${i === RO.agora ? 'agora' : ''}" data-i="${i}">
      <span class="n">${i + 1}</span><input data-c="Nome" value="${esc(m.Nome)}"><span class="tp t-video" style="justify-self:start">vídeo</span>
      <span class="campos"><button class="peq" data-a="arquivo" title="${esc(m.Video)}" style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">🎬 ${esc(arquivoDe(m.Video) || 'escolher arquivo…')}</button></span>
      <span class="campos"><button class="peq" data-a="segue" style="flex:1">${m.Segue ? 'Ao acabar: segue sozinho' : 'Ao acabar: espera'}</button></span>${acoes}</div>`;
    const comCam = m.Tipo !== 'sem';
    return `<div class="rolinha ${i === RO.agora ? 'agora' : ''}" data-i="${i}">
      <span class="n">${i + 1}</span><input data-c="Nome" value="${esc(m.Nome)}">
      <button class="tpb tp t-${m.Tipo}" data-a="tipo" title="Clique para trocar o tipo">${NOME_TIPO[m.Tipo]}</button>
      <select data-c="Cena">${opcoesCenas(m.Cena)}</select>
      <span class="campos">${comCam ? `<select data-c="Camera">${opcoesCam(m.Camera)}</select><select data-c="Posicao">${opcoesPos(m.Camera, m.Posicao)}</select>` : '<span class="dica">—</span>'}</span>${acoes}</div>`;
  }).join('') || '<p class="dica">Nenhum momento. Use "+ Momento".</p>';
}
const roMexe = f => { const l = JSON.parse(JSON.stringify(RO.lista)); f(l); RO.lista = l; RO.alterado = true; renderRoLista(); $('#roAlterado').textContent = '· alterações não salvas'; enviar({ tipo: 'rotEditar', lista: l }); };
$('#roLista').addEventListener('click', e => {
  const b = e.target.closest('[data-a]'), row = e.target.closest('[data-i]'); if (!b || !row) return;
  const i = +row.dataset.i, a = b.dataset.a;
  if (a === 'tipo') roMexe(l => { l[i].Tipo = TIPOS[(TIPOS.indexOf(l[i].Tipo) + 1) % TIPOS.length]; });
  else if (a === 'sobe' && i > 0) roMexe(l => { [l[i - 1], l[i]] = [l[i], l[i - 1]]; });
  else if (a === 'desce' && i < RO.lista.length - 1) roMexe(l => { [l[i + 1], l[i]] = [l[i], l[i + 1]]; });
  else if (a === 'tira' && confirm(`Tirar "${RO.lista[i].Nome}" do roteiro?`)) roMexe(l => l.splice(i, 1));
  else if (a === 'segue') roMexe(l => { l[i].Segue = !l[i].Segue; });
  else if (a === 'arquivo') enviar({ tipo: 'rotVideo', i });
});
$('#roLista').addEventListener('change', e => {
  const c = e.target.dataset.c, row = e.target.closest('[data-i]'); if (!c || !row) return;
  const i = +row.dataset.i, v = e.target.value;
  roMexe(l => { if (c === 'Posicao') l[i].Posicao = +v; else l[i][c] = v; if (c === 'Camera') l[i].Posicao = -1; });
});
$('#roMais').onclick = () => roMexe(l => l.push({ Nome: 'Momento', Tipo: 'fixo', Cena: '', Camera: CAM.lista[0]?.Id || '', Posicao: -1, Video: '', Segue: true }));
$('#roVideoIni').onclick = () => enviar({ tipo: 'rotVideo', i: -1, noFim: false });
$('#roVideoFim').onclick = () => enviar({ tipo: 'rotVideo', i: -1, noFim: true });
function videoEscolhido(m) {
  const nome = arquivoDe(m.caminho).replace(/\.[^.]+$/, '');
  if (m.i >= 0) roMexe(l => { l[m.i].Video = m.caminho; if (/^(Vídeo|Momento)$/.test(l[m.i].Nome)) l[m.i].Nome = nome; });
  else roMexe(l => { const it = { Nome: nome, Tipo: 'video', Cena: '', Camera: '', Posicao: -1, Video: m.caminho, Segue: true };
    if (m.noFim) l.splice(Math.max(0, l.length - 1), 0, it); else l.splice(Math.min(1, l.length), 0, it); });
}
$('#roPerfis').addEventListener('click', e => {
  const b = e.target.closest('[data-perfil]'); if (!b || b.dataset.perfil === RO.perfil) return;
  if (RO.alterado && !confirm('Há alterações não salvas neste perfil. Trocar mesmo assim (e perder as alterações)?')) return;
  enviar({ tipo: 'rotEscolher', id: b.dataset.perfil });
});
$('#roSalvar').onclick = () => enviar({ tipo: 'rotSalvar' });
$('#roNovo').onclick = () => { const n = prompt('Nome do novo perfil (ex.: Missa das 19h com coral):'); if (n) enviar({ tipo: 'rotNovo', nome: n }); };
$('#roRenomear').onclick = () => { const p = RO.perfis.find(x => x.id === RO.perfil); const n = prompt('Novo nome do perfil:', p?.nome || ''); if (n) enviar({ tipo: 'rotRenomear', nome: n }); };
$('#roApagar').onclick = () => { const p = RO.perfis.find(x => x.id === RO.perfil); if (p && confirm(`Apagar o perfil "${p.nome}"?`)) enviar({ tipo: 'rotApagar' }); };
$('#roExportar').onclick = () => enviar({ tipo: 'rotExportar' });
$('#roImportar').onclick = () => enviar({ tipo: 'rotImportar' });
$('#roIniciar').onclick = () => { enviar({ tipo: 'rotIniciar' }); document.querySelector('nav [data-tela="mesa"]').click(); };

// ao vivo
function renderRoVivo() {
  const prox = RO.lista[RO.agora + 1];
  $('#rvLista').innerHTML = RO.lista.map((m, i) => {
    const cena = m.Tipo === 'video' ? arquivoDe(m.Video) : (E.cenas.find(c => c.id === m.Cena)?.nome || '— sem cena —');
    return `<button class="mo ${i < RO.agora ? 'feito' : ''} ${i === RO.agora ? 'agora' : ''}" data-ir="${i}">${esc(m.Nome)} <span class="tp t-${m.Tipo}">${NOME_TIPO[m.Tipo]}</span><span class="dir">${esc(cena)}</span></button>`;
  }).join('');
  $('#rvProximo').textContent = prox ? 'Próximo momento: ' + prox.Nome : (RO.agora >= 0 ? 'Fim do roteiro' : 'Próximo momento');
  $('#rvProximo').disabled = !prox;
  $('#rvAviso').textContent = RO.aviso || '';
  const ag = $('#rvLista .agora'); if (ag && !RO.rolou?.[RO.agora]) { ag.scrollIntoView({ block: 'nearest' }); }
  renderVideoBar();
}
function renderVideoBar() {
  const m = RO.lista[RO.agora];
  const vid = m?.Tipo === 'video' ? E.cenas.find(c => c.fonte === 'video:' + m.Video) : null;
  $('#rvVideo').hidden = !vid;
  if (!vid) return;
  const est = vid.estado || '', t = /(\d+:\d+(?::\d+)?)\s*\/\s*(\d+:\d+(?::\d+)?)/.exec(est);
  const seg = s => s.split(':').reduce((a, x) => a * 60 + +x, 0);
  $('#rvVidNome').textContent = '▶ ' + arquivoDe(m.Video);
  $('#rvVidTempo').textContent = t ? `${t[1]} de ${t[2]}` : est;
  $('#rvVidPct').style.width = (est === 'terminou' ? 100 : t ? Math.round(100 * seg(t[1]) / Math.max(1, seg(t[2]))) : 0) + '%';
  const prox = RO.lista[RO.agora + 1];
  $('#rvVidDepois').textContent = est === 'terminou' ? 'Acabou · parado no último quadro' : prox ? 'Depois: ' + prox.Nome + (m.Segue ? ' (entra sozinho)' : ' (espera você apertar)') : 'Último momento';
}
$('#rvLista').addEventListener('click', e => { const b = e.target.closest('[data-ir]'); if (b) enviar({ tipo: 'rotIr', i: +b.dataset.ir }); });
$('#rvProximo').onclick = () => enviar({ tipo: 'rotProximo' });
$('#rvAnterior').onclick = () => enviar({ tipo: 'rotAnterior' });
$('#rvPular').onclick = () => enviar({ tipo: 'rotProximo' });
$('#rvAbrir').onclick = () => document.querySelector('nav [data-tela="roteiro"]').click();

// ---------- capa da live ----------
const CP = { cfg: null, modo: 'capa' };
const CP_CAMPOS = { cpTitulo: 'Titulo', cpChamada: 'Chamada', cpExtra: 'Extra', cpCelebrante: 'Celebrante', cpCelebracao: 'Celebracao', cpProxima: 'Proxima' };
const CP_IMG_INFO = {
  folheto: 'A ilustração do folheto da semana costuma ter direitos autorais: serve para a capa da paróquia, fica só neste computador e nunca vai para exemplos públicos.',
  foto: 'Uma foto do altar ou da fachada, da própria paróquia. Escolhida uma vez.',
  simbolo: 'Desenho próprio do Cast (hóstia e cálice), para quando não houver ilustração.'
};
function renderCapa(m) {
  CP.cfg = m.cfg; CP.modo = m.modo || CP.modo; CP.pal = m.paletas;
  const c = m.cfg, foco = document.activeElement;
  for (const [id, k] of Object.entries(CP_CAMPOS)) if (foco !== $('#' + id)) $('#' + id).value = c[k] || '';
  if (foco !== $('#cpQuando')) $('#cpQuando').value = (c.Quando || '').slice(0, 16);
  $('#cpCelAuto').textContent = '· do calendário: ' + m.celebracaoAuto;
  $('#cpCelebracao').placeholder = m.celebracaoAuto;
  $$('#cpModelos [data-mod]').forEach(b => b.classList.toggle('ligado', b.dataset.mod === c.Modelo));
  $$('#cpImagens [data-img]').forEach(b => b.classList.toggle('ligado', b.dataset.img === c.Imagem));
  $$('#cpModos [data-modo]').forEach(b => b.classList.toggle('ligado', b.dataset.modo === CP.modo));
  const nomeAuto = m.paletas[m.corAuto]?.nome || m.corAuto;
  $('#cpCorInfo').textContent = c.Cor ? `Escolhida à mão: ${m.paletas[c.Cor]?.nome}. O calendário diria ${nomeAuto}.` : `Automática: ${nomeAuto}, pelo calendário litúrgico.`;
  $('#cpCores').innerHTML = `<button data-cor="" class="${c.Cor ? '' : 'ligado'}">Automática</button>` +
    Object.entries(m.paletas).map(([k, p]) => `<button class="sw ${c.Cor === k ? 'ligado' : ''}" data-cor="${k}" title="${esc(p.nome)}" aria-label="${esc(p.nome)}" style="background:${p.amostra}"></button>`).join('');
  const arq = c.Imagem === 'folheto' ? c.Folheto : c.Imagem === 'foto' ? c.Foto : '';
  $('#cpEscolherImg').hidden = c.Imagem === 'simbolo';
  $('#cpImgNome').textContent = c.Imagem === 'simbolo' ? '' : (arquivoDe(arq) || 'nenhum arquivo escolhido');
  $('#cpImgInfo').textContent = CP_IMG_INFO[c.Imagem] || '';
  [['marca1', 'Brasão da paróquia', c.Marca1, c.UsarMarca1], ['marca2', 'Logo da Pascom', c.Marca2, c.UsarMarca2]].forEach(([q, nome, arq, usar]) => {
    const b = $(`[data-marca="${q}"]`); b.textContent = nome; b.classList.toggle('ligado', !!arq && usar); b.disabled = !arq;
    b.nextElementSibling.textContent = arq ? arquivoDe(arq) : 'nenhum arquivo';
  });
  $('#cpCenas').textContent = m.cenas ? 'Cenas já estão no Ao vivo' : 'Criar as cenas no Ao vivo';
  $('#cpCenas').disabled = !!m.cenas;
  if (m.msg) { $('#cpMsg').textContent = m.msg; clearTimeout(CP.tMsg); CP.tMsg = setTimeout(() => $('#cpMsg').textContent = '', 10000); }
}
function capaMudar(f) { if (!CP.cfg) return; f(CP.cfg); enviar({ tipo: 'capaMudar', cfg: CP.cfg, modo: CP.modo }); }
for (const [id, k] of Object.entries(CP_CAMPOS)) $('#' + id).addEventListener('input', e => { clearTimeout(CP.tDig); CP.tDig = setTimeout(() => capaMudar(c => c[k] = e.target.value), 350); });
$('#cpQuando').addEventListener('change', e => { if (e.target.value) capaMudar(c => c.Quando = e.target.value + ':00'); });
$('#cpModelos').addEventListener('click', e => { const b = e.target.closest('[data-mod]'); if (b) capaMudar(c => c.Modelo = b.dataset.mod); });
$('#cpCores').addEventListener('click', e => { const b = e.target.closest('[data-cor]'); if (b) capaMudar(c => c.Cor = b.dataset.cor); });
$('#cpImagens').addEventListener('click', e => {
  const b = e.target.closest('[data-img]'); if (!b) return;
  const q = b.dataset.img, arq = q === 'folheto' ? CP.cfg?.Folheto : q === 'foto' ? CP.cfg?.Foto : 'x';
  if (!arq) enviar({ tipo: 'capaImagem', qual: q, modo: CP.modo }); else capaMudar(c => c.Imagem = q);
});
$('#cpEscolherImg').onclick = () => enviar({ tipo: 'capaImagem', qual: CP.cfg?.Imagem, modo: CP.modo });
$$('[data-escolher]').forEach(b => b.onclick = () => enviar({ tipo: 'capaImagem', qual: b.dataset.escolher, modo: CP.modo }));
$$('[data-marca]').forEach(b => b.onclick = () => capaMudar(c => { if (b.dataset.marca === 'marca1') c.UsarMarca1 = !c.UsarMarca1; else c.UsarMarca2 = !c.UsarMarca2; }));
$('#cpModos').addEventListener('click', e => { const b = e.target.closest('[data-modo]'); if (b) { CP.modo = b.dataset.modo; enviar({ tipo: 'capaPedir', modo: CP.modo }); } });
$('#cpExportar').onclick = () => enviar({ tipo: 'capaExportar', modo: CP.modo });
$('#cpCenas').onclick = () => enviar({ tipo: 'capaCenas', modo: CP.modo });

// ---------- início ----------
const IN = {};
function pedirInicio() { enviar({ tipo: 'inicioPedir' }); enviar({ tipo: 'capaMini' }); }
function renderInicio(m) {
  Object.assign(IN, m);
  $('#inTitulo').textContent = m.titulo || 'Santa Missa';
  $('#inCorDot').style.background = m.amostra;
  $('#inCelebracao').textContent = m.celebracao;
  $('#inQuando').textContent = '· ' + m.dataLonga + ', ' + m.hora;
  $('#inCelebrante').textContent = m.celebrante ? 'Celebrante: ' + m.celebrante : 'Sem celebrante definido';
  $('#inEsperaInfo').textContent = m.esperaAuto ? 'tela de espera às ' + m.horaEspera : 'tela de espera: você põe no ar';
  $('#inDestinos').innerHTML = m.destinos.map(d => `<button class="peq ${d.ligado ? 'ligado' : ''}" data-dest="${esc(d.id)}" title="${d.chave ? 'chave guardada' : 'falta a chave'}">${esc(d.nome)}</button>`).join('') || '<span class="dica">nenhuma em Ajustes › Transmissão</span>';
  const d = new Date(m.quando);
  $('#inTituloLive').textContent = `${m.titulo} · ${m.celebracao} · ${String(d.getDate()).padStart(2, '0')}/${String(d.getMonth() + 1).padStart(2, '0')} ${m.hora}`;
  if (document.activeElement !== $('#inEsperaMin')) $('#inEsperaMin').value = m.esperaMin;
  $('#inEspera').textContent = m.esperaAuto ? 'Ligada' : 'Desligada';
  $('#inEspera').classList.toggle('ligado', m.esperaAuto);
  $('#inGrav').textContent = m.gravando ? 'gravando agora' : 'use GRAVAR no Ao vivo';
  $('#inPerfil').innerHTML = RO.perfis.map(p => `<option value="${esc(p.id)}" ${p.id === RO.perfil ? 'selected' : ''}>${esc(p.nome)}</option>`).join('');
  $('#inPreparar').textContent = m.iniciado ? 'Celebração em andamento' : 'Preparar a celebração';
  $('#inIrMesa').hidden = !m.iniciado;
  if (m.msg) $('#inMsg').textContent = m.msg;
  contarInicio();
}
function renderInicioCk(m) {
  const cor = { ok: 'var(--ok)', aviso: 'var(--av)', erro: 'var(--vivo)' };
  $('#inCk').innerHTML = m.itens.map(i => `<div class="ck"><span class="dot" style="background:${cor[i.estado]}"></span>${esc(i.nome)}<span class="r">${esc(i.det)}</span></div>`).join('');
  const n = e => m.itens.filter(i => i.estado === e).length;
  $('#inResumo').innerHTML = `<span style="color:var(--ok)">${n('ok')} ok</span>` + (n('aviso') ? ` · <span style="color:var(--av)">${n('aviso')} aviso${n('aviso') > 1 ? 's' : ''}</span>` : '') + (n('erro') ? ` · <span style="color:var(--vivo)">${n('erro')} com problema</span>` : '');
}
function contarInicio() {
  if (!IN.quando) return;
  const s = Math.round((new Date(IN.quando) - Date.now()) / 1000);
  let t;
  if (s <= 0) t = s > -3 * 3600 ? 'agora' : '—';
  else if (s >= 86400) { const dd = Math.floor(s / 86400); t = dd + (dd > 1 ? ' dias' : ' dia'); }
  else { const h = Math.floor(s / 3600), mi = Math.floor(s % 3600 / 60), se = s % 60; t = (h ? h + ':' + String(mi).padStart(2, '0') : mi) + ':' + String(se).padStart(2, '0'); }
  $('#inContagem').textContent = t;
}
setInterval(() => { if (!$('section[data-tela="inicio"]').hidden) contarInicio(); }, 1000);
setInterval(() => { if (!$('section[data-tela="inicio"]').hidden) enviar({ tipo: 'inicioPedir' }); }, 20000);
$('#inDestinos').addEventListener('click', e => { const b = e.target.closest('[data-dest]'); if (!b) return; const d = IN.destinos.find(x => x.id === b.dataset.dest); enviar({ tipo: 'salvarDestino', id: d.id, ligado: !d.ligado }); setTimeout(() => enviar({ tipo: 'inicioPedir' }), 300); });
$('#inPerfil').onchange = e => { enviar({ tipo: 'rotEscolher', id: e.target.value }); setTimeout(() => enviar({ tipo: 'inicioPedir' }), 300); };
$('#inEspera').onclick = () => enviar({ tipo: 'inicioEspera', ligado: !IN.esperaAuto, min: +$('#inEsperaMin').value || 15 });
$('#inEsperaMin').onchange = () => enviar({ tipo: 'inicioEspera', ligado: !!IN.esperaAuto, min: +$('#inEsperaMin').value || 15 });
$('#inConferir').onclick = () => { $('#inCk').innerHTML = '<p class="dica">Conferindo…</p>'; enviar({ tipo: 'inicioPedir' }); };
const irCapa = () => { document.querySelector('nav [data-tela="ajustes"]').click(); abrirSecao('capa'); };
$('#inIrCapa').onclick = irCapa; $('#inEditarCapa').onclick = irCapa;
$('#inPreparar').onclick = () => { if (IN.iniciado) { document.querySelector('nav [data-tela="mesa"]').click(); return; } enviar({ tipo: 'inicioPreparar' }); };
$('#inIrMesa').onclick = () => document.querySelector('nav [data-tela="mesa"]').click();
// ---------- aparência ----------
const AP = { grandes: false, zoom: 1 };
try { AP.grandes = localStorage.getItem('cast.grandes') === '1'; AP.zoom = +(localStorage.getItem('cast.zoom') || 1); } catch {}
function aplicarAparencia() {
  $('#app').classList.toggle('grandes', AP.grandes);
  document.body.style.zoom = AP.zoom;
  try { localStorage.setItem('cast.grandes', AP.grandes ? '1' : '0'); localStorage.setItem('cast.zoom', AP.zoom); } catch {}
}
function renderAparencia() {
  $$('[data-tema]').forEach(b => b.classList.toggle('ligado', b.dataset.tema === tema));
  $('#apGrandes').textContent = AP.grandes ? 'Ligado' : 'Desligado'; $('#apGrandes').classList.toggle('ligado', AP.grandes);
  $$('[data-zoom]').forEach(b => b.classList.toggle('ligado', +b.dataset.zoom === AP.zoom));
}
aplicarAparencia();
$$('[data-tema]').forEach(b => b.onclick = () => { tema = b.dataset.tema; aplicarTema(tema); renderAparencia(); });
$('#apGrandes').onclick = () => { AP.grandes = !AP.grandes; aplicarAparencia(); renderAparencia(); };
$$('[data-zoom]').forEach(b => b.onclick = () => { AP.zoom = +b.dataset.zoom; aplicarAparencia(); renderAparencia(); });

// ---------- rede e MiguelAngelus ----------
function renderRede(m) {
  $('#redePlacas').innerHTML = m.placas.map(p => `<div class="ck">${esc(p.tipo)} · ${esc(p.nome)}<span class="r"><span class="dica">${esc(p.desc)}</span> ${esc(p.ips)}${p.mbps ? ' · ' + p.mbps + ' Mb/s' : ''}</span></div>`).join('') || '<p class="dica">Nenhuma placa de rede ligada.</p>';
  $('#redeEquip').innerHTML = m.equip.map(e => {
    const ok = e.ms >= 0 || e.ms === -2, cor = !e.mesma ? 'var(--vivo)' : ok ? 'var(--ok)' : 'var(--av)';
    const det = !e.mesma ? 'fora da rede deste computador (confira o IP)' : e.ms === -2 ? 'mesma rede · controle por UDP' : e.ms >= 0 ? `responde em ${e.ms} ms` : 'mesma rede, mas não respondeu';
    return `<div class="ck"><span class="dot" style="background:${cor}"></span>${esc(e.nome)}<span class="r">${esc(e.ip)} · ${det}</span></div>`;
  }).join('') || '<p class="dica">Nenhuma câmera com IP nem mesa de som configuradas.</p>';
  $('#redeNdi').textContent = m.ndi.length ? m.ndi.join(' · ') : 'Nenhuma imagem NDI encontrada agora.';
  if (m.net) $('#redeNet').innerHTML = m.net.map(s => `<div class="ck"><span class="dot" style="background:${s.ms >= 0 ? 'var(--ok)' : 'var(--vivo)'}"></span>${esc(s.nome)}<span class="r">${s.ms >= 0 ? 'alcança · ' + s.ms + ' ms' : 'não alcançou'}</span></div>`).join('');
  const mi = m.ndi.filter(n => /miguelangelus/i.test(n) && !/cast/i.test(n));
  $('#miStatus').innerHTML = mi.length
    ? mi.map(n => `<div class="ck"><span class="dot" style="background:var(--ok)"></span>${esc(n.replace(/\s*\(.*\)$/, ''))}<span class="r">${esc((/\((.*)\)/.exec(n) || [])[1] || '')} · imagem NDI disponível</span></div>`).join('')
    : '<p class="dica">Nenhum MiguelAngelus mandando imagem NDI na rede agora. Abra o MiguelAngelus com a faixa de letras (NDI) ligada.</p>';
}
$('#redeAtualizar').onclick = () => enviar({ tipo: 'redePedir' });
$('#miAtualizar').onclick = () => enviar({ tipo: 'redePedir' });
$('#redeInternet').onclick = () => { $('#redeNet').innerHTML = '<p class="dica">Testando…</p>'; enviar({ tipo: 'redePedir', internet: true }); };

// ---------- backup e sobre ----------
$('#bkExportar').onclick = () => enviar({ tipo: 'backupExportar' });
$('#bkImportar').onclick = () => enviar({ tipo: 'backupImportar' });
$$('[data-pasta]').forEach(b => b.onclick = () => enviar({ tipo: 'abrirPastaDados', qual: b.dataset.pasta }));
// ---------- cadastros de rosto guardados ----------
function renderPessoas(m) {
  $('#rtPessoasLista').innerHTML = m.lista.length ? m.lista.map(p => `<div class="ck">${esc(p.nome)}${p.funcao ? ` <span class="dica">· ${esc(p.funcao)}</span>` : ''}<span class="r"><span class="dica">autorizou em ${esc(p.desde)}</span>
    ${m.emUso === p.nome ? '<span style="color:var(--ok)">em uso</span>' : `<button class="peq" data-usarp="${esc(p.id)}">Usar</button>`}<button class="peq" data-apagarp="${esc(p.id)}">Apagar</button></span></div>`).join('')
    : '<p class="dica" style="margin:0">Nenhum cadastro guardado.</p>';
  if (m.msg) $('#rtPessoasMsg').textContent = m.msg;
}
$('#rtPessoasLista').addEventListener('click', e => {
  const u = e.target.closest('[data-usarp]'), a = e.target.closest('[data-apagarp]');
  if (u) enviar({ tipo: 'rtUsarPessoa', id: u.dataset.usarp });
  if (a) enviar({ tipo: 'rtApagarPessoa', id: a.dataset.apagarp });
});
$('#rtGuardarPessoa').onclick = () => enviar({ tipo: 'rtGuardarPessoa', nome: $('#rtPessoaNome').value, funcao: $('#rtPessoaFuncao').value });
$('#rtGuardados').addEventListener('toggle', () => { if ($('#rtGuardados').open) enviar({ tipo: 'rtPessoas' }); });