'use strict';
/*
 * (0.1.10) Câmera PTZ de cada cena, dentro do painel ✎ da cena.
 *  - a posição fica salva NA CENA (as coordenadas que a câmera informa), não num preset da câmera;
 *  - a partir dela: fica parada, achar (centraliza e para) ou seguir, com a pessoa dos cadastros guardados;
 *  - no Roteiro, o que o momento escolher vale mais; o que ficar em branco vem daqui.
 * Quem pergunta à câmera e manda a câmera é o programa (Ptz.cs / Roteiro.cs).
 */
const CC = { ultimo: 0, mov: '', tMsg: 0 };
const ccCena = () => (typeof CM !== 'undefined' && CM.alvo && CM.alvo !== '*') ? E.cenas.find(c => c.id === CM.alvo) : null;
const ccCams = () => CAM.lista.filter(c => c.Ip);
// a câmera da cena: a escolhida; se nenhuma, a câmera cuja imagem (NDI) é a fonte principal da cena
function ccSugerida(c) {
  if (c.camera) return c.camera;
  const f = (c.fonte || '').startsWith('ndi:') ? c.fonte.slice(4) : null;
  return (f && ccCams().find(x => x.Fonte === f)?.Id) || '';
}
const ccCamAtual = () => CAM.lista.find(x => x.Id === $('#cmCam').value);

function ccRender() {
  const c = ccCena(); if (!c) return;
  const sel = ccSugerida(c);
  $('#cmCam').innerHTML = '<option value="">— nenhuma (a cena não mexe em câmera) —</option>' +
    ccCams().map(x => `<option value="${esc(x.Id)}" ${x.Id === sel ? 'selected' : ''}>${esc(x.Nome)}</option>`).join('') +
    (sel && !ccCams().some(x => x.Id === sel) ? `<option value="${esc(sel)}" selected>(câmera apagada)</option>` : '');
  const tem = !!$('#cmCam').value;
  $('#cmCamCorpo').hidden = !tem;
  $('#cmCamSit').innerHTML = !ccCams().length ? 'Cadastre a câmera (com IP) em Ajustes › Câmeras.'
    : !tem ? '' : c.ptz ? `<span class="cm-cam-ok">● posição salva ${esc(c.ptz.quando)}</span>` : 'sem posição salva';
  $('#cmCamIr').disabled = $('#cmCamEsquecer').disabled = !c.ptz;
  $('#cmCamRastreio').value = c.rastreio || '';
  $('#cmCamPessoa').hidden = !c.rastreio;
  $('#cmCamPessoa').innerHTML = opcoesPessoa(c.pessoa || '', c.rastreio || 'seguir');
}
function ccEnviar() {
  if (!CM.alvo || CM.alvo === '*') return;
  CC.ultimo = Date.now();
  enviar({ tipo: 'camCena', id: CM.alvo, camera: $('#cmCam').value, rastreio: $('#cmCamRastreio').value, pessoa: $('#cmCamRastreio').value ? $('#cmCamPessoa').value : '' });
}
function ccMsg(t, erro) {
  const el = $('#cmCamMsg'); el.textContent = t; el.className = 'dica ' + (erro ? 'cm-cam-erro' : '');
  clearTimeout(CC.tMsg); CC.tMsg = setTimeout(() => el.textContent = '', 10000);
}

$('#cmCam').onchange = () => {
  const c = ccCena();
  if (c?.ptz && !confirm('A posição salva é da outra câmera e será esquecida. Trocar a câmera desta cena?')) { ccRender(); return; }
  ccEnviar(); $('#cmCamCorpo').hidden = !$('#cmCam').value;
};
$('#cmCamRastreio').onchange = () => {
  $('#cmCamPessoa').hidden = !$('#cmCamRastreio').value;
  $('#cmCamPessoa').innerHTML = opcoesPessoa($('#cmCamPessoa').value, $('#cmCamRastreio').value || 'seguir');
  ccEnviar();
};
$('#cmCamPessoa').onchange = ccEnviar;
$('#cmCamSalvar').onclick = () => {
  const cam = ccCamAtual(); if (!cam) return;
  ccMsg('Perguntando à câmera onde ela está…');
  enviar({ tipo: 'camCenaSalvar', id: CM.alvo, camera: cam.Id });
};
$('#cmCamIr').onclick = () => enviar({ tipo: 'camCenaIr', id: CM.alvo });
$('#cmCamEsquecer').onclick = () => { if (confirm('Esquecer a posição salva nesta cena?')) enviar({ tipo: 'camCenaEsquecer', id: CM.alvo }); };

// controle da câmera: segure para mover, solte para parar (os mesmos comandos de Ajustes › Câmeras)
$('#cmCamCorpo').addEventListener('pointerdown', e => {
  const cam = ccCamAtual(), m = e.target.closest('[data-ccmov]'), z = e.target.closest('[data-cczoom]');
  if (!cam || (!m && !z)) return;
  const vel = +$('#cmCamVel').value;
  if (m?.dataset.ccmov === 'home') { ptzHome(cam); return; }
  e.preventDefault(); e.target.setPointerCapture?.(e.pointerId);
  if (m) { const [dx, dy] = m.dataset.ccmov.split(',').map(Number); ptzMover(cam, dx, dy, vel); CC.mov = 'mov'; }
  if (z) { ptzZoom(cam, +z.dataset.cczoom, Math.round(vel * 7)); CC.mov = 'zoom'; }
});
const ccParar = () => { const cam = ccCamAtual(); if (!cam || !CC.mov) return; if (CC.mov === 'zoom') ptzZoom(cam, 0); else ptzParar(cam); CC.mov = ''; };
addEventListener('pointerup', ccParar); addEventListener('pointercancel', ccParar); addEventListener('blur', ccParar);

// liga no painel de camadas: abrir, e o estado novo que chega do programa
const ccAbrirOrig = abrirCamadas;
abrirCamadas = function (id) {
  ccAbrirOrig(id);
  $('#cmCamBox').hidden = id === '*';
  $('#cmCamMsg').textContent = '';
  if (id !== '*') { ccRender(); if (!CAM.lista.length) enviar({ tipo: 'camPedir' }); }
};
const ccEstadoOrig = camadasEstado;
camadasEstado = function () {
  ccEstadoOrig();
  const mexendo = Date.now() - CC.ultimo < 700 || document.activeElement?.closest('#cmCamBox select');
  if (CM.alvo && CM.alvo !== '*' && !mexendo) ccRender();
};
window.chrome?.webview?.addEventListener('message', e => {
  const t = e.data?.tipo;
  if (t === 'camCenaMsg' && e.data.id === CM.alvo) ccMsg(e.data.texto, e.data.erro);
  if ((t === 'cameras' || t === 'rtPessoas') && CM.alvo && CM.alvo !== '*' && !$('#camadas').hidden) setTimeout(ccRender, 0);
});
