// MiguelAngelus Cast · plugin do Stream Dock / Stream Deck (MIT)
// Desenha cada tecla (144×144) a partir do estado do Cast. Devolve { sig, url }: sig muda só quando o desenho muda.
const COR = {
  fundo: '#141b2b', cena: '#1d3a6e', ar: '#c62828', pv: '#1f9d55', ouro: '#9a7420', roxo: '#4b2f80',
  cinza: '#2a3445', vinho: '#4a1d26', aviso: '#d9901a', apagado: '#1b2230', ptz: '#33405a'
};

function quebrar(ctx, texto, larg, tamMax, tamMin, maxLinhas, alturaMax) {
  for (let tam = tamMax; tam >= tamMin; tam -= 1) {
    ctx.font = `700 ${tam}px "Segoe UI", sans-serif`;
    const linhas = [];
    for (const parte of String(texto).split('\n')) {
      let atual = '';
      for (const p of parte.split(' ')) {
        const t = atual ? atual + ' ' + p : p;
        if (ctx.measureText(t).width <= larg || !atual) atual = t; else { linhas.push(atual); atual = p; }
      }
      linhas.push(atual);
    }
    if (linhas.length <= maxLinhas && linhas.length * tam * 1.12 <= alturaMax && linhas.every(l => ctx.measureText(l).width <= larg)) return { linhas, tam };
    if (tam === tamMin) {
      const l = linhas.slice(0, maxLinhas);
      while (ctx.measureText(l[l.length - 1] + '…').width > larg && l[l.length - 1].length > 1) l[l.length - 1] = l[l.length - 1].slice(0, -1);
      if (linhas.length > maxLinhas || ctx.measureText(linhas[maxLinhas - 1] || '').width > larg) l[l.length - 1] += '…';
      return { linhas: l, tam };
    }
  }
}

// t = { fundo, texto, topo, rodape, borda, ponto, apagado, grande }
function tecla(t) {
  const cv = document.createElement('canvas'); cv.width = 144; cv.height = 144;
  const c = cv.getContext('2d');
  c.fillStyle = t.apagado ? COR.apagado : (t.fundo || COR.fundo); c.fillRect(0, 0, 144, 144);
  if (!t.apagado) {   // leve brilho em cima, para parecer tecla
    const g = c.createLinearGradient(0, 0, 0, 144); g.addColorStop(0, 'rgba(255,255,255,.10)'); g.addColorStop(1, 'rgba(0,0,0,.18)');
    c.fillStyle = g; c.fillRect(0, 0, 144, 144);
  }
  if (t.borda) { c.strokeStyle = t.borda; c.lineWidth = 8; c.strokeRect(4, 4, 136, 136); }
  c.textAlign = 'center'; c.textBaseline = 'middle';
  const corTexto = t.apagado ? 'rgba(255,255,255,.45)' : '#fff';
  let topoY = 0;
  if (t.topo) {
    c.font = '700 17px "Segoe UI", sans-serif'; c.fillStyle = t.apagado ? 'rgba(255,255,255,.35)' : 'rgba(255,255,255,.8)';
    c.fillText(t.topo, 72, 20, 130); topoY = 32;
  }
  const temRod = !!t.rodape;
  const fim = temRod ? 112 : 138, ini = topoY || 6, area = fim - ini;
  const q = quebrar(c, t.texto || '', 128, t.grande || 30, 14, t.grande ? 2 : 3, area);
  const alt = q.tam * 1.12, y0 = ini + area / 2 - (q.linhas.length - 1) * alt / 2;
  c.fillStyle = corTexto;
  q.linhas.forEach((l, i) => c.fillText(l, 72, y0 + i * alt));
  // bolinha de "gravando/no ar": no canto de baixo, sem cobrir o texto
  if (t.ponto) { c.fillStyle = '#ff4d4d'; c.beginPath(); c.arc(15, temRod ? 129 : 128, 7, 0, Math.PI * 2); c.fill(); }
  if (temRod) {
    c.fillStyle = 'rgba(0,0,0,.35)'; c.fillRect(0, 114, 144, 30);
    c.font = '700 17px "Segoe UI", sans-serif'; c.fillStyle = t.apagado ? 'rgba(255,255,255,.45)' : '#fff';
    c.fillText(t.rodape, 72, 130, 136);
  }
  return cv.toDataURL('image/png');
}

const ARMADO = { gravar: 'gravar', aovivo: 'transmitir' };

// a = nome da ação (cena, transicao…), s = ajustes da tecla, e = estado do Cast (ou null), x = extra (pisca, aviso, apertada)
function desenhar(a, s, e, x) {
  let t;
  if (x.aviso) t = { fundo: COR.aviso, texto: x.aviso, topo: 'CAST' };
  else if (!e) t = semCast(a, s, x.motivo);
  else switch (a) {
    case 'cena': {
      const c = e.cenas.find(k => String(k.n) === String(s.cena || 1));
      const direto = s.modo === 'programa';
      if (!c) { t = { apagado: true, topo: 'CENA ' + (s.cena || 1), texto: 'não existe' }; break; }
      t = { fundo: c.programa ? COR.ar : c.preview ? COR.pv : COR.cena, topo: (direto ? 'DIRETO · ' : 'CENA ') + c.n, texto: c.nome,
        rodape: c.programa ? 'NO AR' : c.preview ? 'PREVIEW' : direto ? '' : '' };
      if (!t.rodape) delete t.rodape;
      break;
    }
    case 'transicao':
    case 'corte': {
      const pv = e.cenas.find(k => k.id === e.preview), pode = pv && e.preview !== e.programa;
      t = { fundo: a === 'corte' ? '#1b6b3f' : COR.pv, texto: a === 'corte' ? 'CORTE' : 'TRANSIÇÃO', grande: a === 'corte' ? 32 : 26,
        rodape: pode ? '→ ' + pv.nome : 'nada novo', apagado: !pode };
      break;
    }
    case 'capa': {
      const modo = s.modo || 'espera', fonte = 'capa:' + (modo === 'abertura' ? 'capa' : modo === 'encerramento' ? 'fim' : 'espera');
      const c = e.cenas.find(k => k.fonte === fonte);
      const nome = modo === 'abertura' ? 'Abertura' : modo === 'encerramento' ? 'Encerramento' : 'Tela de espera';
      t = { fundo: c?.programa ? COR.ar : c?.preview ? COR.pv : COR.ouro, topo: s.direto ? 'CAPA · NO AR' : 'CAPA', texto: nome,
        rodape: c?.programa ? 'NO AR' : c?.preview ? 'PREVIEW' : undefined };
      break;
    }
    case 'momento': {
      const ant = s.dir === 'anterior', m = e.momento || {};
      const alvo = ant ? m.anterior : m.proximo;
      t = { fundo: COR.roxo, topo: ant ? '◀ ANTERIOR' : 'PRÓXIMO ▶', texto: alvo || (m.total ? (ant ? 'é o primeiro' : 'fim do roteiro') : 'sem roteiro'),
        rodape: m.total ? `${m.n || 0} de ${m.total}` : undefined, apagado: !alvo };
      break;
    }
    case 'mudo':
      t = e.mudo ? { fundo: COR.ar, texto: 'SEM SOM', topo: 'MUDO', rodape: 'aperte p/ voltar', borda: x.pisca ? '#fff' : undefined }
        : { fundo: COR.cinza, texto: 'MUDO', grande: 32, rodape: 'som no ar' };
      break;
    case 'gravar':
      t = e.armado === 'gravar' ? { fundo: COR.aviso, texto: 'DE NOVO\npara parar', topo: 'GRAVAÇÃO' }
        : e.gravando ? { fundo: '#a31515', texto: e.tempoGrav || 'REC', topo: 'GRAVANDO', ponto: x.pisca, rodape: '2× p/ parar', grande: 28 }
        : { fundo: COR.vinho, texto: 'GRAVAR', grande: 30, ponto: true, rodape: 'parado' };
      break;
    case 'aovivo':
      t = e.armado === 'transmitir' ? { fundo: COR.aviso, texto: e.aoVivo ? 'DE NOVO\np/ ENCERRAR' : 'DE NOVO\np/ INICIAR', topo: 'TRANSMISSÃO' }
        : e.aoVivo ? { fundo: COR.ar, texto: e.tempoAr || 'NO AR', topo: 'NO AR', ponto: x.pisca, rodape: '2× encerra', grande: 28 }
        : { fundo: COR.vinho, texto: 'AO VIVO', grande: 30, rodape: '2 toques', topo: 'fora do ar' };
      break;
    case 'parar':
      t = { fundo: '#8e1b1b', texto: 'PARAR\ncâmera', topo: 'QUE SEGUE', apagado: !e.seguindo, rodape: e.seguindo ? undefined : 'desligada' };
      break;
    case 'ptz': {
      const cam = e.cameras.find(k => String(k.n) === String(s.camera || 1));
      const pos = cam?.posicoes.find(p => String(p.n) === String(s.posicao));
      t = { fundo: COR.ptz, topo: cam ? cam.nome : 'CÂMERA ' + (s.camera || 1), texto: pos ? pos.nome : s.posicao != null && s.posicao !== '' ? 'Posição ' + s.posicao : 'escolha a posição',
        apagado: !cam };
      break;
    }
    case 'info': t = painel(s, e, x); break;
    default: t = { texto: a };
  }
  if (x.apertada) t.borda = '#ffffff';
  const url = tecla(t);
  return url;
}

function painel(s, e, x) {
  const agora = new Date(), hh = agora.toTimeString().slice(0, 5);
  switch (s.mostrar || 'tempo') {
    case 'hora': return { fundo: COR.fundo, texto: hh, grande: 40, rodape: agora.toLocaleDateString('pt-BR', { weekday: 'short', day: '2-digit', month: '2-digit' }) };
    case 'programa': { const c = e.cenas.find(k => k.programa); return { fundo: COR.ar, topo: 'NO AR', texto: c ? c.nome : '—' }; }
    case 'momento': { const m = e.momento || {}; return { fundo: COR.roxo, topo: 'MOMENTO', texto: m.agora || '—', rodape: m.total ? `${m.n} de ${m.total}` : undefined }; }
    default:
      if (e.aoVivo) return { fundo: COR.ar, topo: 'NO AR', texto: e.tempoAr, grande: 28, ponto: x.pisca, rodape: e.gravando ? 'gravando' : hh };
      if (e.gravando) return { fundo: '#a31515', topo: 'GRAVANDO', texto: e.tempoGrav, grande: 28, ponto: x.pisca, rodape: hh };
      return { fundo: COR.cinza, topo: 'FORA DO AR', texto: hh, grande: 36 };
  }
}

function semCast(a, s, motivo) {
  const nomes = { cena: 'CENA ' + (s.cena || 1), transicao: 'TRANSIÇÃO', corte: 'CORTE', capa: 'CAPA', momento: s.dir === 'anterior' ? '◀ MOMENTO' : 'MOMENTO ▶',
    mudo: 'MUDO', gravar: 'GRAVAR', aovivo: 'AO VIVO', parar: 'PARAR câmera', ptz: 'PTZ', info: 'CAST' };
  return { apagado: true, texto: nomes[a] || a, rodape: motivo || 'Cast fechado' };
}
