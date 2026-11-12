'use strict';
/*
 * (0.1.2) Camadas das cenas (backlog 1.5) e imagens em todas as cenas (backlog 1.6).
 * A fonte principal da cena pode ocupar só um pedaço do quadro ("ret") e outras fontes vão por cima dela: outra câmera
 * no canto, duas lado a lado, as letras do MiguelAngelus (NDI com fundo transparente), logo, brasão, selo AO VIVO.
 * As caixas aparecem por cima do Preview: arrastar muda o lugar, o cantinho muda o tamanho. Quem desenha de verdade é o
 * programa (Mesa.cs): aqui só se edita e se manda.
 * Posições e tamanhos em fração do quadro (0 a 1), como no mesa.json.
 */
const CM = { alvo: null, ret: null, camadas: [], sel: null, arrasto: null, tEnvio: 0, ultimoEnvio: 0, ndi: null, pend: null };
const MX = .03, MY = .03 * 16 / 9;                       // margem dos cantos (igual na horizontal e na vertical, em pixels)
const cmId = () => 'k' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
const cmClone = o => JSON.parse(JSON.stringify(o));
const cmArq = p => (p || '').split(/[\\/]/).pop().replace(/\.[^.]+$/, '').replace(/^camada-/, '').replace(/-\d{14}$/, '');
function cmNomeCamada(k) {
  if (k.nome) return k.nome;
  const f = k.fonte || '';
  if (f.startsWith('fundo:')) {
    const [cor, ef] = f.slice(6).split('|');
    const nc = { auto: 'cor do dia', verde: 'verde', roxo: 'roxo', branco: 'branco', vermelho: 'vermelho', rosa: 'rosa', '#2c5aa0': 'azul' }[cor] || cor;
    const ne = { lisa: 'lisa', degrade: 'degradê', riscos: 'riscos', filete: 'filete', vinheta: 'vinheta', luz: 'luz suave', tema: 'tema do dia' }[ef]
      || (ef?.startsWith('tema-') ? 'tema ' + (TEMAS_FUNDO.find(t => t[0] === ef)?.[1].replace(/ \(.*/, '') || ef.slice(5)) : ef);
    return '🎨 Fundo · ' + nc + ' · ' + ne;
  }
  return f.startsWith('ndi:') ? '📷 ' + f.slice(4) : f.startsWith('selo:') ? '● Selo AO VIVO' : f.startsWith('imagem:') ? '🖼 ' + cmArq(f.slice(7)) : f;
}
const cmCena = () => CM.alvo && CM.alvo !== '*' ? E.cenas.find(c => c.id === CM.alvo) : null;

// ---------- abrir e fechar ----------
function abrirCamadas(id) {
  CM.alvo = id; CM.sel = null; CM.pend = null;
  cmCarregar(true);
  if (id !== '*') enviar({ tipo: 'preview', id });
  const g = id === '*';
  $('#camadas').hidden = false; $('#novaCena').hidden = true;
  $('#cmTopoCena').hidden = g; $('#cmSemSobreporBox').hidden = g; $('[data-add="ndi"]').hidden = g; $('#cmNdiBox').hidden = true;
  $('#cmTitulo').textContent = g ? 'Imagens em todas as cenas (menos as telas da capa)' : 'Camadas da cena';
  cmRender();
  $('#camadas').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}
function fecharCamadas() { CM.alvo = null; CM.arrasto = null; $('#camadas').hidden = true; $('#cmCaixas').hidden = true; }
$('#cmFechar').onclick = fecharCamadas;
$('#btnSobrepor').onclick = () => CM.alvo === '*' ? fecharCamadas() : abrirCamadas('*');

function cmCarregar(tudo) {
  const c = cmCena();
  if (CM.alvo === '*') { CM.camadas = cmClone(E.sobrepor || []); CM.ret = null; }
  else if (c) {
    CM.camadas = cmClone(c.camadas || []); CM.ret = c.ret ? [...c.ret] : null;
    if (tudo) { $('#cmNome').value = c.nome; $('#cmSemSobrepor').checked = !!c.semSobrepor; }
  }
}
// estado novo do programa: recarrega, a não ser que a pessoa esteja mexendo agora
function camadasEstado() {
  if (!CM.alvo) return;
  if (CM.alvo !== '*' && !cmCena()) return fecharCamadas();
  const mexendo = CM.arrasto || Date.now() - CM.ultimoEnvio < 500 || document.activeElement?.closest('#cmLista');
  if (!mexendo) { cmCarregar(false); cmRender(); } else { cmPontos(); cmCaixas(); }
}

// ---------- mandar para o programa ----------
function cmEnviar(gravar) {
  CM.ultimoEnvio = Date.now(); clearTimeout(CM.tEnvio);
  if (CM.alvo === '*') enviar({ tipo: 'sobrepor', camadas: CM.camadas, gravar });
  else if (CM.alvo) enviar({ tipo: 'camadas', id: CM.alvo, nome: $('#cmNome').value, ret: CM.ret, camadas: CM.camadas, semSobrepor: $('#cmSemSobrepor').checked, gravar });
}
function cmEnviarLogo() {      // durante o arrasto: no máximo 20 vezes por segundo, sem gravar
  if (Date.now() - CM.ultimoEnvio > 50) cmEnviar(false);
  else { clearTimeout(CM.tEnvio); CM.tEnvio = setTimeout(() => cmEnviar(false), 50); }
}
const cmMudou = () => { cmEnviar(true); cmRender(); };
$('#cmNome').onchange = () => cmEnviar(true);
$('#cmSemSobrepor').onchange = () => cmEnviar(true);

// ---------- lista ----------
// Ordem na tela = ordem de desenho, de cima para baixo: o que está acima da linha "Principal" fica ATRÁS dela
// (um fundo, por exemplo); o que está abaixo fica na frente. A de baixo de tudo fica na frente de tudo.
const cmPct = v => Math.round(v * 100) + '%';
const CORES_FUNDO = [['auto', 'Cor do dia (calendário)'], ['verde', 'Verde'], ['roxo', 'Roxo'], ['branco', 'Branco'], ['vermelho', 'Vermelho'], ['rosa', 'Rosa'], ['#2c5aa0', 'Azul (Nossa Senhora)'], ['outra', 'Outra cor…']];
const EFEITOS_FUNDO = [['lisa', 'Lisa'], ['degrade', 'Degradê'], ['riscos', 'Degradê com riscos'], ['filete', 'Degradê com filete dourado'], ['vinheta', 'Vinheta'], ['luz', 'Luz suave (anda devagar · pesa um pouco mais)']];
// (0.1.9) temas desenhados pelo Cast (backlog 1.8): o desenho fica no terço da direita; a cor sugerida entra ao escolher
const TEMAS_FUNDO = [['tema', 'Tema do dia (calendário)', ''], ['tema-advento', 'Advento (coroa e velas)', 'roxo'], ['tema-natal', 'Natal (estrela e gruta)', 'branco'],
  ['tema-quaresma', 'Quaresma (cruz e deserto)', 'roxo'], ['tema-pascoa', 'Páscoa (cruz vazia e sol)', 'branco'], ['tema-pentecostes', 'Pentecostes (pomba e chamas)', 'vermelho'],
  ['tema-maria', 'Nossa Senhora (estrelas e M)', '#2c5aa0'], ['tema-jose', 'São José (lírios e esquadro)', 'branco'], ['tema-eucaristia', 'Eucaristia (cálice e hóstia)', 'branco'],
  ['tema-comum', 'Tempo Comum (trigo e uva)', 'verde']];
const opcoesEfeito = sel => EFEITOS_FUNDO.map(([v, t]) => `<option value="${v}" ${sel === v ? 'selected' : ''}>${t}</option>`).join('') +
  '<optgroup label="Temas (desenho à direita)">' + TEMAS_FUNDO.map(([v, t]) => `<option value="${v}" ${sel === v ? 'selected' : ''}>${t}</option>`).join('') + '</optgroup>';
const cmFundo = k => { const [cor, ef] = (k.fonte || '').slice(6).split('|'); return { cor: cor || 'auto', ef: ef || 'degrade' }; };
const cmVisual = () => [...CM.camadas.filter(k => k.atras), 'base', ...CM.camadas.filter(k => !k.atras)];
function cmDeVisual(v) {           // a lista na ordem da tela → camadas (atrás/frente)
  const i = v.indexOf('base');
  v.forEach((k, j) => { if (k !== 'base') k.atras = j < i; });
  CM.camadas = v.filter(k => k !== 'base');
}
function cmLinhaBase(c) {
  if (!c) return `<div class="cm-linha base" data-k="base"><span class="cm-nome"><b>Principal de cada cena</b> <span class="dica">acima desta linha: atrás · abaixo: na frente</span></span></div>`;
  return `<div class="cm-linha base ${CM.sel === 'base' ? 'sel' : ''}" data-k="base">
      <span class="cm-nome"><b>Principal</b> <span class="dica">${esc(nomeTipo(c.fonte))}</span></span>
      <span class="dica">${CM.ret ? `${cmPct(CM.ret[2])} × ${cmPct(CM.ret[3])}` : 'tela toda'}</span>
      <span class="linha">${CM.ret ? '<button class="peq" data-a="cheia" title="Volta a ocupar a tela toda">Tela toda</button>' : ''}</span>
    </div>`;
}
function cmLinha(k, i, n) {
  const fundo = (k.fonte || '').startsWith('fundo:'), fd = fundo ? cmFundo(k) : null;
  const corLista = fundo && !CORES_FUNDO.some(([v]) => v === fd.cor);
  return `<div class="cm-linha ${CM.sel === k.id ? 'sel' : ''} ${k.ligada ? '' : 'off'} ${k.atras ? 'atras' : ''}" data-k="${esc(k.id)}">
      <span class="cm-alca" title="Arraste para mudar a ordem">⠿</span>
      <label class="cm-nome" title="Ligar ou desligar"><input type="checkbox" data-c="ligada" ${k.ligada ? 'checked' : ''}><span class="dot"></span>${esc(cmNomeCamada(k))}</label>
      ${fundo ? `<select data-c="fcor" title="Cor do fundo">${CORES_FUNDO.map(([v, t]) => `<option value="${v}" ${(corLista ? 'outra' : fd.cor) === v ? 'selected' : ''}>${t}</option>`).join('')}</select>
        <input type="color" data-c="fcorx" value="${corLista ? esc(fd.cor) : '#5a1520'}" ${corLista ? '' : 'hidden'} title="Escolha a cor">
        <select data-c="fef" title="Efeito ou tema">${opcoesEfeito(fd.ef)}</select>` : ''}
      <span class="cm-cantos" title="Levar para um canto">${['↖', '↗', '↙', '↘'].map((s, j) => `<button class="peq" data-canto="${j}">${s}</button>`).join('')}<button class="peq" data-canto="c" title="No meio">▪</button>${fundo ? '<button class="peq" data-canto="t" title="Tela toda">⛶</button>' : ''}</span>
      ${fundo ? '' : `<select data-c="preencher" title="Caber = imagem inteira · Preencher = cobre a caixa e corta as sobras"><option value="0">Caber</option><option value="1" ${k.preencher ? 'selected' : ''}>Preencher</option></select>`}
      <label class="ck2" title="Fio claro em volta (bom para a câmera no canto)"><input type="checkbox" data-c="moldura" ${k.moldura ? 'checked' : ''}>Moldura</label>
      <label class="cm-op" title="Transparência">Opac. <input type="range" min="10" max="100" step="5" value="${k.opacidade ?? 100}" data-c="opacidade"><b>${k.opacidade ?? 100}%</b></label>
      <span class="linha cm-ord"><button class="peq" data-a="tras" title="Mais para trás" ${i === 0 ? 'disabled' : ''}>▲</button><button class="peq" data-a="frente" title="Mais para a frente" ${i === n - 1 ? 'disabled' : ''}>▼</button><button class="peq" data-a="tirar" title="Tirar esta camada">✕</button></span>
    </div>`;
}
function cmRender() {
  const c = cmCena(), v = cmVisual();
  let h = v.map((k, i) => k === 'base' ? cmLinhaBase(c) : cmLinha(k, i, v.length)).join('');
  if (!CM.camadas.length) h += `<div class="dica cm-vazio">${c ? 'Nada ainda. Use os modelos prontos, “Pôr por cima” ou 🎨 Fundo.' : 'Nada ainda. O que entrar aqui aparece em todas as cenas, menos nas telas da capa.'}</div>`;
  else h += '<div class="dica">Arraste pelo ⠿ para mudar a ordem: acima da Principal fica atrás dela (fundo); a de baixo de todas fica na frente.</div>';
  $('#cmLista').innerHTML = h;
  cmPontos(); cmCaixas();
}
function cmPontos() {      // bolinha de estado de cada camada (câmera procurando, imagem que não abriu…)
  const c = cmCena(), est = CM.alvo === '*' ? (E.estSobrepor || []) : (c?.estCamadas || []);
  const porFonte = {}; (CM.alvo === '*' ? (E.sobrepor || []) : (c?.camadas || [])).forEach((k, i) => porFonte[k.fonte] = est[i]);
  $$('#cmLista .cm-linha[data-k]').forEach(l => {
    const k = CM.camadas.find(x => x.id === l.dataset.k), d = l.querySelector('.dot');
    if (k && d) { const e = porFonte[k.fonte]; d.style.background = corEstado(e); d.title = e || 'ainda não desenhada'; }
  });
}

function cmAchar(id) { return CM.camadas.find(k => k.id === id); }
function cmCanto(k, j) {
  if (j === 't') { Object.assign(k, { x: 0, y: 0, l: 1, a: 1 }); return; }
  if (j === 'c') { k.x = (1 - k.l) / 2; k.y = (1 - k.a) / 2; return; }
  k.x = j % 2 ? 1 - k.l - MX : MX; k.y = j > 1 ? 1 - k.a - MY : MY;
}
$('#cmLista').addEventListener('click', e => {
  if (CM.arrLista?.suprimir) return;
  const linha = e.target.closest('.cm-linha'); if (!linha) return;
  const id = linha.dataset.k, k = cmAchar(id);
  const a = e.target.closest('[data-a]')?.dataset.a, canto = e.target.closest('[data-canto]')?.dataset.canto;
  if (a === 'cheia') { CM.ret = null; return cmMudou(); }
  if (k && a === 'tirar') { CM.camadas = CM.camadas.filter(x => x !== k); if (CM.sel === id) CM.sel = null; return cmMudou(); }
  if (k && (a === 'tras' || a === 'frente')) {
    const v = cmVisual(), i = v.indexOf(k), j = i + (a === 'tras' ? -1 : 1);
    if (j >= 0 && j < v.length) { [v[i], v[j]] = [v[j], v[i]]; cmDeVisual(v); }
    return cmMudou();
  }
  if (k && canto != null) { cmCanto(k, /^[ct]$/.test(canto) ? canto : +canto); CM.sel = id; return cmMudou(); }
  if (!e.target.closest('input,select,label') && (id !== 'base' || cmCena())) { CM.sel = id; cmMarcar(); }
});
function cmCampo(e, gravar) {
  const el = e.target.closest('[data-c]'), linha = e.target.closest('.cm-linha'); if (!el || !linha) return;
  const k = cmAchar(linha.dataset.k); if (!k) return;
  const c = el.dataset.c;
  if (c === 'ligada' || c === 'moldura') k[c] = el.checked;
  else if (c === 'preencher') k.preencher = el.value === '1';
  else if (c === 'opacidade') { k.opacidade = +el.value; el.nextElementSibling.textContent = el.value + '%'; }
  else if (c === 'fcor' || c === 'fcorx' || c === 'fef') {
    const fd = cmFundo(k), sel = linha.querySelector('[data-c="fcor"]').value, x = linha.querySelector('[data-c="fcorx"]');
    x.hidden = sel !== 'outra';
    let cor = sel === 'outra' ? x.value : sel; const ef = linha.querySelector('[data-c="fef"]').value;
    if (!gravar) return;
    const sug = c === 'fef' && TEMAS_FUNDO.find(t => t[0] === ef)?.[2];      // escolheu um tema: entra a cor dele
    if (sug) cor = sug;
    k.fonte = 'fundo:' + cor + '|' + ef; k.nome = '';
    return cmMudou();
  }
  if (gravar) { cmEnviar(true); if (c === 'ligada') { linha.classList.toggle('off', !k.ligada); cmCaixas(); } }
  else cmEnviarLogo();
}
$('#cmLista').addEventListener('input', e => { if (e.target.type === 'range') cmCampo(e, false); });
$('#cmLista').addEventListener('change', e => cmCampo(e, true));

// arrastar as linhas da lista com o mouse (pelo ⠿ ou pelo nome); a linha dourada mostra onde entra
$('#cmLista').addEventListener('pointerdown', e => {
  const linha = e.target.closest('.cm-linha[data-k]');
  if (!linha || e.button !== 0 || linha.dataset.k === 'base') return;
  if (e.target.closest('button,select,input[type=range],input[type=color]')) return;
  if (e.target.closest('label') && !e.target.closest('.cm-nome')) return;
  CM.arrLista = { id: linha.dataset.k, linha, y0: e.clientY, x0: e.clientX, ativo: false, pos: -1 };
});
window.addEventListener('pointermove', e => {
  const A = CM.arrLista; if (!A || A.suprimir) return;
  if (!A.ativo) {
    if (Math.hypot(e.clientX - A.x0, e.clientY - A.y0) < 6) return;
    A.ativo = true; A.linha.classList.add('arrastando'); document.body.classList.add('arrastando-cena');
    A.marca = document.createElement('i'); A.marca.className = 'cena-marca cm-marca'; document.body.append(A.marca);
  }
  e.preventDefault();
  const ls = $$('#cmLista .cm-linha[data-k]');
  let pos = ls.length;
  for (let i = 0; i < ls.length; i++) { const r = ls[i].getBoundingClientRect(); if (e.clientY < r.top + r.height / 2) { pos = i; break; } }
  A.pos = pos;
  const ref = ls[Math.min(pos, ls.length - 1)].getBoundingClientRect(), y = pos < ls.length ? ref.top - 4 : ref.bottom + 2;
  Object.assign(A.marca.style, { left: ref.left + 'px', top: y + 'px', width: ref.width + 'px', height: '4px' });
});
function cmFimLista(e) {
  const A = CM.arrLista; if (!A || A.suprimir) return;
  if (!A.ativo) { CM.arrLista = null; return; }
  A.suprimir = true; setTimeout(() => { if (CM.arrLista === A) CM.arrLista = null; }, 60);
  A.marca?.remove(); A.linha.classList.remove('arrastando'); document.body.classList.remove('arrastando-cena');
  if (e?.type !== 'pointerup' || A.pos < 0) return cmRender();
  const v = cmVisual(), k = cmAchar(A.id), de = v.indexOf(k);
  let para = A.pos > de ? A.pos - 1 : A.pos;
  if (de < 0 || para === de) return cmRender();
  v.splice(de, 1); v.splice(para, 0, k);
  cmDeVisual(v); CM.sel = k.id; cmMudou();
}
window.addEventListener('pointerup', cmFimLista);
window.addEventListener('pointercancel', cmFimLista);

// ---------- pôr por cima ----------
function cmNova(fonte, extra) {
  const k = Object.assign({ id: cmId(), nome: '', fonte, x: 0, y: 0, l: .3, a: .3, preencher: false, moldura: false, opacidade: 100, ligada: true, atras: false }, extra);
  if (k.atras) CM.camadas.unshift(k); else CM.camadas.push(k);
  CM.sel = k.id;
  return k;
}
$$('#camadas [data-add]').forEach(b => b.onclick = () => {
  const t = b.dataset.add;
  if (t === 'fundo') { cmNova('fundo:auto|degrade', { l: 1, a: 1, preencher: true, atras: true }); return cmMudou(); }   // tela toda, atrás da principal
  if (t === 'ndi') { CM.pend = null; cmPedirNdi(); return; }
  if (t === 'web') { $('#cmWebBox').hidden = false; $('#cmWeb').focus(); return; }
  if (t === 'imagem') return enviar({ tipo: 'escolherImagemCamada' });
  if (t === 'selo') { const k = cmNova('selo:aovivo', { l: .15, a: .15 * 150 / 560 * 16 / 9 }); cmCanto(k, 0); return cmMudou(); }
  const p = E.marcas?.[t];
  if (!p) return alert(t === 'brasao' ? 'O brasão da paróquia ainda não foi escolhido (Ajustes › Capa da live).' : 'O logo da Pascom ainda não foi escolhido (Ajustes › Capa da live).');
  const k = cmNova('imagem:' + p, { nome: t === 'brasao' ? 'Brasão da paróquia' : 'Logo da Pascom', l: .08, a: .08 * 16 / 9 });
  cmCanto(k, t === 'brasao' ? 1 : 3); cmMudou();
});
function cmImagem(m) {
  if (!CM.alvo) return;
  const k = cmNova('imagem:' + m.caminho, { nome: '🖼 ' + (m.nome || ''), l: .12, a: .12 * 16 / 9 });
  cmCanto(k, 1); cmMudou();
}

// (0.1.9) página da internet por cima
$('#cmWebOk').onclick = () => {
  let u = $('#cmWeb').value.trim(); if (!u || !CM.alvo) return;
  if (!/^[a-z]+:/i.test(u)) u = 'https://' + u;
  const tam = $('#cmWebTam').value, [pl, pa] = tam.split('x').map(Number);
  const host = nomePagina('|' + u) || 'Página';
  const l = tam === '1080x1080' ? .25 : 1, a = l * pa / pl * 16 / 9;
  const k = cmNova('web:' + tam + '@100|' + u, { nome: '🌐 ' + host, l, a, preencher: true, y: tam === '1920x200' ? 1 - a : 0 });
  if (tam === '1080x1080') cmCanto(k, 1);
  $('#cmWebBox').hidden = true; $('#cmWeb').value = ''; cmMudou();
};
$('#cmWeb').onkeydown = e => { if (e.key === 'Enter') $('#cmWebOk').click(); if (e.key === 'Escape') $('#cmWebBox').hidden = true; };

// câmera ou telão por NDI
function cmPedirNdi() {
  $('#cmNdiBox').hidden = false;
  $('#cmNdi').innerHTML = '<option value="">procurando…</option>';
  enviar({ tipo: 'fontesNdi' });
}
function cmNdi(lista, erro) {
  CM.ndi = lista;
  if (CM.pend === 'letras') return cmLetras();
  if ($('#cmNdiBox').hidden) return;
  $('#cmNdi').innerHTML = lista.length ? lista.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join('')
    : `<option value="">${esc(erro || 'Nenhuma fonte NDI na rede ainda')}</option>`;
  const ja = cmCena()?.fonte?.slice(4);      // sugere uma câmera diferente da principal
  const outra = lista.find(n => n !== ja && !/letras/i.test(n)); if (outra) $('#cmNdi').value = outra;
}
$('#cmNdiOk').onclick = () => {
  const n = $('#cmNdi').value; if (!n) return;
  $('#cmNdiBox').hidden = true;
  const k = cmNova('ndi:' + n, { l: .3, a: .3, moldura: true });
  cmCanto(k, 3);
  if (CM.pend) cmModelo(CM.pend); else cmMudou();
  CM.pend = null;
};

// ---------- modelos prontos ----------
const cmCams = () => CM.camadas.filter(k => k.fonte.startsWith('ndi:') && !/letras/i.test(k.fonte));
function cmModelo(m) {
  const cams = cmCams();
  if (m === 'cheia') { CM.ret = null; return cmMudou(); }
  if (m === 'letras') { CM.pend = 'letras'; enviar({ tipo: 'fontesNdi' }); return; }
  if (!cams.length) { CM.pend = m; cmPedirNdi(); return; }      // precisa de mais uma câmera: escolhe e volta aqui
  const pos = (k, x, y, l, a, mold) => Object.assign(k, { x, y, l, a, moldura: mold, ligada: true });
  if (m === 'canto') { CM.ret = null; pos(cams[0], 0, 0, .3, .3, true); cmCanto(cams[0], 3); }
  else if (m === 'lado') { CM.ret = [0, .25, .5, .5]; pos(cams[0], .5, .25, .5, .5, false); }
  else if (m === 'tercos') {
    CM.ret = [0, 1 / 6, 2 / 3, 2 / 3];
    pos(cams[0], 2 / 3, 1 / 6, 1 / 3, 1 / 3, false);
    if (cams[1]) pos(cams[1], 2 / 3, 1 / 2, 1 / 3, 1 / 3, false);
  }
  CM.sel = cams[0].id;
  cmMudou();
}
function cmLetras() {
  CM.pend = null;
  const n = (CM.ndi || []).find(x => /miguelangelus/i.test(x) && /letras/i.test(x));
  if (!n) return alert('Não achei a fonte NDI “MiguelAngelus Letras” na rede.\nNo MiguelAngelus, ligue a saída das letras por NDI e tente de novo.');
  let k = CM.camadas.find(x => x.fonte === 'ndi:' + n);
  if (!k) k = cmNova('ndi:' + n, { nome: '𝐀 Letras do MiguelAngelus' });
  Object.assign(k, { x: 0, y: 0, l: 1, a: 1, preencher: false, moldura: false, ligada: true, atras: false });
  CM.camadas = CM.camadas.filter(x => x !== k).concat(k);       // as letras ficam na frente de tudo
  CM.sel = k.id; cmMudou();
}
$$('#camadas [data-modelo]').forEach(b => b.onclick = () => cmModelo(b.dataset.modelo));

// ---------- caixas por cima do Preview ----------
function cmCaixas() {
  const box = $('#cmCaixas');
  const mostrar = !!CM.alvo && (CM.alvo === '*' || E.preview === CM.alvo);
  box.hidden = !mostrar;
  $('#cmDica').innerHTML = !CM.alvo || mostrar ? 'Arraste as caixas no Preview para mudar o lugar; o cantinho de baixo, à direita, muda o tamanho (Shift solta a proporção). Setas do teclado ajustam a caixa escolhida.'
    : 'Esta cena não está no Preview. <button class="peq" id="cmPorPv">Pôr no Preview</button>';
  $('#cmPorPv')?.addEventListener('click', () => enviar({ tipo: 'preview', id: CM.alvo }));
  if (!mostrar) return;
  const itens = [];      // na ordem de desenho: as de trás, a principal, as da frente
  cmVisual().forEach(k => {
    if (k === 'base') { if (cmCena()) itens.push({ id: 'base', r: CM.ret || [0, 0, 1, 1], nome: 'Principal' }); }
    else if (k.ligada) itens.push({ id: k.id, r: [k.x, k.y, k.l, k.a], nome: cmNomeCamada(k) });
  });
  box.innerHTML = itens.map(it => `<div class="cm-cx ${it.id === 'base' ? 'base' : ''} ${CM.sel === it.id ? 'sel' : ''}" data-k="${esc(it.id)}"
      style="left:${it.r[0] * 100}%;top:${it.r[1] * 100}%;width:${it.r[2] * 100}%;height:${it.r[3] * 100}%"><span>${esc(it.nome)}</span><i data-alca title="Tamanho"></i></div>`).join('');
}
function cmMarcar() {
  $$('#cmCaixas .cm-cx').forEach(x => x.classList.toggle('sel', x.dataset.k === CM.sel));
  $$('#cmLista .cm-linha').forEach(x => x.classList.toggle('sel', x.dataset.k === CM.sel));
}
const cmRetDe = id => id === 'base' ? (CM.ret || [0, 0, 1, 1]).slice() : (k => [k.x, k.y, k.l, k.a])(cmAchar(id));
function cmPorRet(id, r) {
  // não deixa a caixa fugir do quadro (maior que o quadro: pode andar só até as bordas)
  const prende = (v, t) => t <= 1 ? Math.min(Math.max(v, 0), 1 - t) : Math.min(Math.max(v, 1 - t), 0);
  r = [prende(r[0], r[2]), prende(r[1], r[3]), r[2], r[3]];
  if (id === 'base') CM.ret = r;
  else { const k = cmAchar(id); [k.x, k.y, k.l, k.a] = r; }
  const cx = $(`#cmCaixas .cm-cx[data-k="${CSS.escape(id)}"]`);
  if (cx) Object.assign(cx.style, { left: r[0] * 100 + '%', top: r[1] * 100 + '%', width: r[2] * 100 + '%', height: r[3] * 100 + '%' });
}
// ímã: bordas, meio e margem dos cantos
const IMA = .012;
const ima = (v, alvos) => { for (const a of alvos) if (Math.abs(v - a) < IMA) return a; return v; };

$('#cmCaixas').addEventListener('pointerdown', e => {
  let cx = e.target.closest('.cm-cx'); if (!cx) return;
  e.preventDefault();
  // várias caixas sob o ponteiro (ex.: as letras ocupam a tela toda): pega a menor; o cantinho de tamanho sempre ganha
  const sob = document.elementsFromPoint(e.clientX, e.clientY);
  const alca = sob.find(x => x.matches?.('#cmCaixas [data-alca]'));
  if (alca) cx = alca.closest('.cm-cx');
  else {
    const area = x => x.offsetWidth * x.offsetHeight;
    cx = sob.filter(x => x.classList?.contains('cm-cx')).sort((a, b) => area(a) - area(b))[0] || cx;
  }
  const id = cx.dataset.k, rc = $('#cmCaixas').getBoundingClientRect();
  CM.sel = id; cmMarcar();
  CM.arrasto = { id, r0: cmRetDe(id), px: e.clientX, py: e.clientY, W: rc.width, H: rc.height, alca: !!alca, mexeu: false };
  $('#cmCaixas').setPointerCapture(e.pointerId);
});
$('#cmCaixas').addEventListener('pointermove', e => {
  const A = CM.arrasto; if (!A) return;
  const dx = (e.clientX - A.px) / A.W, dy = (e.clientY - A.py) / A.H;
  if (!A.mexeu && Math.abs(e.clientX - A.px) + Math.abs(e.clientY - A.py) < 3) return;
  A.mexeu = true;
  let [x, y, l, a] = A.r0;
  if (A.alca) {
    l = Math.max(.03, A.r0[2] + dx);
    if (Math.abs(x + l - 1) < IMA) l = 1 - x; else if (Math.abs(x + l - (1 - MX)) < IMA) l = 1 - MX - x;
    a = e.shiftKey ? Math.max(.03, A.r0[3] + dy) : l * A.r0[3] / A.r0[2];
  } else {
    x = ima(A.r0[0] + dx, [0, MX, 1 - l, 1 - l - MX, (1 - l) / 2]);
    y = ima(A.r0[1] + dy, [0, MY, 1 - a, 1 - a - MY, (1 - a) / 2]);
  }
  cmPorRet(A.id, [x, y, l, a]);
  cmEnviarLogo();
});
function cmSoltar() {
  const A = CM.arrasto; if (!A) return;
  CM.arrasto = null;
  if (A.mexeu) cmMudou();
}
$('#cmCaixas').addEventListener('pointerup', cmSoltar);
$('#cmCaixas').addEventListener('pointercancel', cmSoltar);

// setas ajustam a caixa escolhida (Shift = passo maior; Alt = tamanho)
document.addEventListener('keydown', e => {
  if (!CM.alvo || !CM.sel || $('#cmCaixas').hidden || e.target.closest('input,select,textarea')) return;
  const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]; if (!d) return;
  e.preventDefault();
  const p = e.shiftKey ? .02 : .005, r = cmRetDe(CM.sel);
  if (e.altKey) { const l = Math.max(.03, r[2] + (d[0] || -d[1]) * p); r[3] = r[3] * l / r[2]; r[2] = l; }
  else { r[0] += d[0] * p; r[1] += d[1] * p * 16 / 9; }
  cmPorRet(CM.sel, r);
  clearTimeout(CM.tTecla); cmEnviarLogo(); CM.tTecla = setTimeout(() => cmEnviar(true), 600);
});
