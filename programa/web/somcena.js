'use strict';
/*
 * (0.1.7) Som de cada cena (backlog 1.2), dentro do painel ✎ da cena.
 *   padrão  = som: null       → a mesa + o som dos vídeos (como sempre foi)
 *   mudo    = som: []         → a transmissão fica muda enquanto a cena estiver no ar
 *   escolher = lista de fontes { tipo: mesa | video | entrada, dispositivo, nome, volumeDb, ligada }
 * Quem mistura de verdade é o programa (Som.cs); aqui só se edita e se manda.
 */
const SOMC = { som: null, ultimo: 0, entradas: [] };
const scCena = () => (typeof CM !== 'undefined' && CM.alvo && CM.alvo !== '*') ? E.cenas.find(c => c.id === CM.alvo) : null;
const scClone = o => o == null ? null : JSON.parse(JSON.stringify(o));
const scModo = () => SOMC.som == null ? 'padrao' : SOMC.som.filter(f => f.ligada !== false).length === 0 && SOMC.som.length === 0 ? 'mudo' : 'escolher';

function scCarregar() {
  const c = scCena(); if (!c) return;
  SOMC.som = scClone(c.som ?? null);
  scRender();
}
function scEnviar() {
  if (!CM.alvo || CM.alvo === '*') return;
  SOMC.ultimo = Date.now();
  enviar({ tipo: 'somCena', id: CM.alvo, fontes: SOMC.som });
}
function scNome(f) {
  if (f.tipo === 'mesa') return '🎚 Mesa (Ajustes › Som)';
  if (f.tipo === 'video') return '🎬 Som dos vídeos';
  return (f.dispositivo || '').startsWith('loop:') ? '🖥 ' + (f.nome || 'Som do computador') : '🎤 ' + (f.nome || 'Outra entrada');
}
function scRender() {
  const modo = scModo();
  $('#cmSomModo').value = modo;
  $('#cmSomLista').hidden = modo !== 'escolher'; $('#cmSomAdd').hidden = modo !== 'escolher';
  const tem = t => (SOMC.som || []).some(f => f.tipo === t);
  $('#cmSomAdd [data-som="mesa"]').hidden = tem('mesa'); $('#cmSomAdd [data-som="video"]').hidden = tem('video');
  const usadas = new Set((SOMC.som || []).filter(f => f.tipo === 'entrada').map(f => f.dispositivo));
  $('#cmSomEntrada').innerHTML = '<option value="">Outra entrada ou som do computador…</option>' +
    SOMC.entradas.filter(d => !usadas.has(d.id)).map(d => `<option value="${esc(d.id)}">${esc(d.nome)}</option>`).join('');
  $('#cmSomLista').innerHTML = (SOMC.som || []).map((f, i) => `
    <div class="cm-som-linha ${f.ligada === false ? 'off' : ''}" data-i="${i}">
      <label class="ck2" style="margin:0"><input type="checkbox" data-c="ligada" ${f.ligada !== false ? 'checked' : ''}> ${esc(scNome(f))}</label>
      <input type="range" min="-30" max="12" step="1" value="${Math.round(f.volumeDb || 0)}" data-c="vol" title="Volume desta fonte nesta cena">
      <span class="dica cm-som-db">${(f.volumeDb || 0) > 0 ? '+' : ''}${Math.round(f.volumeDb || 0)} dB</span>
      <button class="peq" data-c="tirar" title="Tirar esta fonte da cena">✕</button>
    </div>`).join('') || '<div class="dica">Nenhuma fonte: a transmissão fica muda nesta cena.</div>';
}

$('#cmSomModo').onchange = () => {
  const v = $('#cmSomModo').value;
  if (v === 'padrao') SOMC.som = null;
  else if (v === 'mudo') SOMC.som = [];
  else if (!SOMC.som || !SOMC.som.length) SOMC.som = [{ tipo: 'mesa', volumeDb: 0, ligada: true }, { tipo: 'video', volumeDb: 0, ligada: true }];
  scRender(); scEnviar();
  if (v === 'escolher' && !SOMC.entradas.length) enviar({ tipo: 'somEntradas' });
};
$('#cmSomAdd').addEventListener('click', e => {
  const t = e.target.closest('[data-som]')?.dataset.som; if (!t) return;
  (SOMC.som ||= []).push({ tipo: t, volumeDb: 0, ligada: true }); scRender(); scEnviar();
});
$('#cmSomEntrada').onchange = () => {
  const id = $('#cmSomEntrada').value; if (!id) return;
  const d = SOMC.entradas.find(x => x.id === id);
  (SOMC.som ||= []).push({ tipo: 'entrada', dispositivo: id, nome: (d?.nome || '').replace(/^Som do computador: /, 'Som do computador · '), volumeDb: 0, ligada: true });
  scRender(); scEnviar();
};
$('#cmSomLista').addEventListener('input', e => {
  const l = e.target.closest('[data-i]'); if (!l || e.target.dataset.c !== 'vol') return;
  const f = SOMC.som[+l.dataset.i]; f.volumeDb = +e.target.value;
  l.querySelector('.cm-som-db').textContent = (f.volumeDb > 0 ? '+' : '') + f.volumeDb + ' dB';
  clearTimeout(SOMC.t); SOMC.t = setTimeout(scEnviar, 120);            // arrastando: manda aos poucos
});
$('#cmSomLista').addEventListener('change', e => {
  const l = e.target.closest('[data-i]'); if (!l) return;
  if (e.target.dataset.c === 'ligada') { SOMC.som[+l.dataset.i].ligada = e.target.checked; scRender(); scEnviar(); }
});
$('#cmSomLista').addEventListener('click', e => {
  const l = e.target.closest('[data-i]'); if (!l || e.target.dataset.c !== 'tirar') return;
  SOMC.som.splice(+l.dataset.i, 1); scRender(); scEnviar();
});

// liga no painel de camadas: abrir, e o estado novo que chega do programa
const scAbrirOrig = abrirCamadas;
abrirCamadas = function (id) {
  scAbrirOrig(id);
  $('#cmSomBox').hidden = id === '*';
  if (id !== '*') { scCarregar(); if (!SOMC.entradas.length) enviar({ tipo: 'somEntradas' }); }
};
const scEstadoOrig = camadasEstado;
camadasEstado = function () {
  scEstadoOrig();
  const mexendo = Date.now() - SOMC.ultimo < 700 || document.activeElement?.closest('#cmSomBox');
  if (CM.alvo && CM.alvo !== '*' && !mexendo) scCarregar();
};
window.chrome?.webview?.addEventListener('message', e => {
  if (e.data?.tipo === 'somEntradas') { SOMC.entradas = e.data.lista || []; if (CM.alvo && CM.alvo !== '*') scRender(); }
});
