'use strict';
/*
 * 🐢 Segredo do Miguel (o mesmo do MiguelAngelus, 0.1.1): 5 cliques seguidos no logotipo (ou no logo do "Sobre"),
 * ou digitar "cowabunga", faz o Michelangelo (bandana laranja!) atravessar a tela do Cast gritando "Cowabunga!".
 * Só na tela do operador: a imagem da transmissão é montada pelo programa, à parte, e nunca mostra a tartaruga.
 */
const PALAVRA = 'cowabunga';
const Cowa = { cliques: [], buf: '', tempo: 0, rodando: false, espera: 0 };

const TARTARUGA = `<svg viewBox="0 0 120 120" width="150" height="150" aria-hidden="true">
  <ellipse cx="60" cy="112" rx="34" ry="5" fill="rgba(0,0,0,.25)"/>
  <g fill="#5fae3c" stroke="#2f6b1d" stroke-width="2">
    <rect x="36" y="86" width="14" height="22" rx="6"/><rect x="70" y="86" width="14" height="22" rx="6"/>
  </g>
  <ellipse cx="60" cy="72" rx="32" ry="26" fill="#8b5a2b" stroke="#4e3014" stroke-width="3"/>
  <ellipse cx="60" cy="74" rx="22" ry="17" fill="#e9c46a" stroke="#b08a2e" stroke-width="2"/>
  <path d="M60 58 v32 M44 66 h32 M46 80 h28" stroke="#b08a2e" stroke-width="2"/>
  <g class="cw-braco">
    <rect x="86" y="60" width="22" height="11" rx="5" fill="#5fae3c" stroke="#2f6b1d" stroke-width="2"/>
    <rect x="104" y="44" width="6" height="18" rx="2" fill="#7a4a1a"/><path d="M107 44 q8 -10 12 -2" stroke="#999" stroke-width="2" fill="none"/>
    <rect x="114" y="30" width="6" height="16" rx="2" fill="#7a4a1a" transform="rotate(25 117 38)"/>
  </g>
  <circle cx="60" cy="36" r="24" fill="#5fae3c" stroke="#2f6b1d" stroke-width="3"/>
  <rect x="35" y="27" width="50" height="13" rx="6" fill="#f28c28"/>
  <path d="M36 32 q-14 -6 -20 4 M36 35 q-12 6 -18 14" stroke="#f28c28" stroke-width="6" fill="none" stroke-linecap="round"/>
  <ellipse cx="51" cy="33" rx="6" ry="4.5" fill="#fff"/><ellipse cx="69" cy="33" rx="6" ry="4.5" fill="#fff"/>
  <circle cx="53" cy="33" r="2.2" fill="#111"/><circle cx="71" cy="33" r="2.2" fill="#111"/>
  <path d="M48 46 q12 10 24 0" stroke="#2f6b1d" stroke-width="3" fill="#fff" stroke-linejoin="round"/>
</svg>`;

function cowabunga() {
  if (Cowa.rodando) return;
  Cowa.rodando = true;
  const palco = document.createElement('div');
  palco.className = 'cw-palco';
  palco.innerHTML = `<div class="cw-heroi">${TARTARUGA}<div class="cw-balao">Cowabunga!</div></div>` +
    Array.from({ length: 14 }, (_, i) => `<span class="cw-pizza" style="left:${5 + i * 6.7}%;animation-delay:${(i % 5) * 0.18}s">🍕</span>`).join('') +
    '<div class="cw-recado">O Michelangelo aprova o <b>Miguel</b>Angelus <i>Cast</i>! 🐢</div>';
  document.body.appendChild(palco);
  setTimeout(() => { palco.remove(); Cowa.rodando = false; }, 5200);
}

// 5 cliques em até 3 segundos, somando o logotipo do alto e o logo do "Sobre"
function contarClique() {
  const agora = Date.now();
  Cowa.cliques = Cowa.cliques.filter(t => agora - t < 3000).concat(agora);
  if (Cowa.cliques.length >= 5) {
    Cowa.cliques = [];
    const sobre = document.getElementById('dlgSobre'); if (sobre?.open) sobre.close();
    cowabunga();
  }
}

(() => {
  // logotipo: 1 clique abre o "Sobre" (com um instantinho de espera); cliques seguidos contam para o segredo
  const marca = document.querySelector('.marca');
  if (marca) marca.onclick = () => {
    contarClique();
    clearTimeout(Cowa.espera);
    if (Cowa.cliques.length === 1) Cowa.espera = setTimeout(() => { if (Cowa.cliques.length === 1 && typeof abrirSobre === 'function') abrirSobre(); }, 450);
  };
  document.querySelector('#dlgSobre .sobre-topo img')?.addEventListener('click', contarClique);
  // digitando fora das caixas de texto
  window.addEventListener('keydown', e => {
    const t = e.target;
    if ((t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) || e.ctrlKey || e.altKey || e.metaKey || e.key.length !== 1) return;
    clearTimeout(Cowa.tempo);
    Cowa.tempo = setTimeout(() => { Cowa.buf = ''; }, 2500);
    const tentativa = Cowa.buf + e.key.toLowerCase();
    if (!PALAVRA.startsWith(tentativa)) { Cowa.buf = PALAVRA.startsWith(e.key.toLowerCase()) ? e.key.toLowerCase() : ''; return; }
    Cowa.buf = tentativa;
    if (tentativa === PALAVRA) { Cowa.buf = ''; cowabunga(); }
  }, true);
  // digitando numa caixa de texto: a palavra some da caixa e a tartaruga aparece
  document.addEventListener('input', e => {
    const c = e.target;
    if (!c || c.tagName !== 'INPUT' || typeof c.value !== 'string') return;
    if (c.value.toLowerCase().includes(PALAVRA)) {
      c.value = c.value.replace(new RegExp(PALAVRA, 'ig'), '').trim();
      c.dispatchEvent(new Event('input', { bubbles: true }));
      cowabunga();
    }
  });
})();
