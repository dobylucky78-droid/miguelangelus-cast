// MiguelAngelus Cast · plugin do Stream Dock / Stream Deck (MIT)
// Fala com o programa do aparelho pelo WebSocket do SDK (o mesmo do Elgato) e com o Cast pela porta local (127.0.0.1).
let ws, estado = null, motivo = '', pisca = false;
const teclas = {};          // context → { a, s, ultimo, aviso, avisoAte, apertadaAte }
const PREFIXO = 'com.miguelangelus.cast.';
const ACAO_CMD = { transicao: 'transicao', corte: 'corte', mudo: 'mudo', gravar: 'gravar', aovivo: 'transmitir', parar: 'parar' };

const temCast = () => typeof CAST !== 'undefined' && CAST.porta && CAST.chave;
const base = () => `http://127.0.0.1:${CAST.porta}`;

async function pedir(caminho) {
  const ctl = new AbortController(); const t = setTimeout(() => ctl.abort(), 12000);
  try {
    const r = await fetch(base() + caminho + (caminho.includes('?') ? '&' : '?') + 'chave=' + encodeURIComponent(CAST.chave), { signal: ctl.signal, cache: 'no-store' });
    return await r.json();
  } finally { clearTimeout(t); }
}

async function lerEstado() {
  if (!temCast()) { estado = null; motivo = 'abra o Cast'; return; }
  try {
    const r = await pedir('/estado');
    if (r && Array.isArray(r.cenas)) { estado = r; motivo = ''; }
    else { estado = null; motivo = 'reinstale o plugin'; }   // chave de outro Cast: Ajustes › Stream Deck › Instalar o plugin
  }
  catch { estado = null; motivo = 'Cast fechado'; }
}

function comando(a, s) {
  switch (a) {
    case 'cena': return `/cmd/${s.modo === 'programa' ? 'programa' : 'preview'}?cena=${encodeURIComponent(s.cena || 1)}`;
    case 'capa': return `/cmd/capa?modo=${encodeURIComponent(s.modo || 'espera')}${s.direto ? '&direto=1' : ''}`;
    case 'momento': return s.dir === 'anterior' ? '/cmd/anterior' : '/cmd/proximo';
    case 'ptz': return `/cmd/ptz?camera=${encodeURIComponent(s.camera || 1)}&posicao=${encodeURIComponent(s.posicao ?? '')}`;
    case 'info': return null;
    case 'abrir': return `/cmd/abrir?programa=${s.programa === 'miguel' ? 'miguel' : 'cast'}`;
    default: return ACAO_CMD[a] ? '/cmd/' + ACAO_CMD[a] : null;
  }
}

function enviar(o) { if (ws?.readyState === 1) ws.send(JSON.stringify(o)); }

// ícones fixos da tecla "Abrir programa" (icones.js)
const icone = qual => (typeof ICONES !== 'undefined' && ICONES[qual]) || null;

function redesenhar(ctx, forcar) {
  const k = teclas[ctx]; if (!k) return;
  if (k.a === 'abrir') {   // não depende do Cast: só o ícone do programa
    const url = icone(k.s.programa === 'miguel' ? 'miguel' : 'cast');
    if (url && (forcar || url !== k.ultimo)) { k.ultimo = url; enviar({ event: 'setImage', context: ctx, payload: { image: url, target: 0 } }); }
    return;
  }
  const agora = Date.now();
  const url = desenhar(k.a, k.s, estado, {
    pisca, motivo,
    aviso: k.avisoAte > agora ? k.aviso : '',
    apertada: k.apertadaAte > agora
  });
  if (!forcar && url === k.ultimo) return;
  k.ultimo = url;
  enviar({ event: 'setImage', context: ctx, payload: { image: url, target: 0 } });
}
const redesenharTodas = () => Object.keys(teclas).forEach(c => redesenhar(c));

async function apertou(ctx) {
  const k = teclas[ctx]; if (!k) return;
  const cmd = comando(k.a, k.s); if (!cmd) return;
  k.apertadaAte = Date.now() + 220; redesenhar(ctx);
  if (!temCast()) { enviar({ event: 'showAlert', context: ctx }); return; }
  try {
    const r = await pedir(cmd);
    // "Aperte de novo…" já aparece na própria tecla (estado.armado); os outros recusos piscam o aviso
    if (!r.ok && r.msg && !/^Aperte de novo/.test(r.msg)) { k.aviso = r.msg; k.avisoAte = Date.now() + 1800; }
  } catch { enviar({ event: 'showAlert', context: ctx }); }
  await lerEstado(); redesenharTodas();
}

// relógio: lê o estado ~3 vezes por segundo e redesenha só o que mudou
let lendo = false;
const relogio = new Worker('relogio.js');
let n = 0;
relogio.onmessage = async () => {
  n++; if (n % 2 === 0) pisca = !pisca;
  if (!Object.keys(teclas).length || lendo) return;
  lendo = true; try { await lerEstado(); } finally { lendo = false; }
  redesenharTodas();
};
relogio.postMessage(300);

// entrada chamada pelo programa do aparelho (mesmo nome no Stream Dock e no Stream Deck)
function connectElgatoStreamDeckSocket(porta, uuid, registrar, info) {
  ws = new WebSocket('ws://127.0.0.1:' + porta);
  ws.onopen = () => ws.send(JSON.stringify({ event: registrar, uuid }));
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    const a = (m.action || '').startsWith(PREFIXO) ? m.action.slice(PREFIXO.length) : null;
    switch (m.event) {
      case 'willAppear':
        teclas[m.context] = { a, s: m.payload?.settings || {}, ultimo: '' };
        redesenhar(m.context, true); break;
      case 'willDisappear': delete teclas[m.context]; break;
      case 'didReceiveSettings':
        if (teclas[m.context]) { teclas[m.context].s = m.payload?.settings || {}; redesenhar(m.context, true); } break;
      case 'sendToPlugin':
        if (teclas[m.context] && m.payload?.settings) { teclas[m.context].s = m.payload.settings; redesenhar(m.context, true); } break;
      case 'keyDown': apertou(m.context); break;
    }
  };
}

// o Stream Dock também procura este nome
const connectSocket = connectElgatoStreamDeckSocket;
