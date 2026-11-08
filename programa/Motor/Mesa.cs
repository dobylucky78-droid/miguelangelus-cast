// A mesa de corte: cenas, preview e programa, transições. Roda numa linha própria a 30 quadros por segundo.
// A tela (HTML) só manda comandos (Comando) e recebe o estado; os monitores vão por um buffer compartilhado.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MiguelAngelusCast.Motor
{
    public sealed class CenaCfg
    {
        public string Id { get; set; }
        public string Nome { get; set; }
        public string Fonte { get; set; }          // ver Fonte.Criar
        public bool Preencher { get; set; }        // false = caber (mostra a imagem inteira)
    }

    public sealed class MesaCfg
    {
        public List<CenaCfg> Cenas { get; set; } = new List<CenaCfg>();
        public string Preview { get; set; }
        public string Programa { get; set; }
        public string Efeito { get; set; } = "esmaecer";   // corte | esmaecer | pagina
        public int DuracaoMs { get; set; } = 500;
        public int Largura { get; set; } = 1920;
        public int Altura { get; set; } = 1080;
        public int Qps { get; set; } = 30;
    }

    public sealed class Mesa : IDisposable
    {
        public const int ML = 640, MA = 360;                     // tamanho dos monitores na tela
        static readonly string arquivoCfg = Path.Combine(Program.PastaDados, "mesa.json");

        public MesaCfg Cfg { get; private set; }
        readonly ConcurrentQueue<Action> comandos = new ConcurrentQueue<Action>();
        readonly Dictionary<string, Fonte> fontes = new Dictionary<string, Fonte>();
        readonly Dictionary<string, (Imagem img, long seq, Fonte fonte, bool preencher)> desenhadas = new Dictionary<string, (Imagem, long, Fonte, bool)>();
        Imagem saida, preto;
        readonly Thread laco; volatile bool rodando = true;

        // transição em andamento
        string de, para; Stopwatch relTrans; string efeitoTrans; int durTrans;

        // monitores (preview em cima, programa embaixo, RGBA), escritos aqui e lidos pela tela
        public IntPtr Monitores; public int MonitoresTamanho => ML * MA * 4 * 2;
        public event Action<long> MonitoresProntos;          // nº do quadro
        public event Action<string> EstadoMudou;             // JSON do estado
        public event Action<Imagem> QuadroDeSaida;           // para gravação/transmissão (etapa 2)

        // números para a tela
        public double QpsReal, MsQuadro; public long Atrasos;

        public Mesa()
        {
            Cfg = Carregar();
            laco = new Thread(Rodar) { IsBackground = true, Name = "mesa", Priority = ThreadPriority.AboveNormal };
        }

        public void Iniciar(IntPtr monitores) { Monitores = monitores; laco.Start(); }

        static MesaCfg Carregar()
        {
            try { if (File.Exists(arquivoCfg)) return JsonSerializer.Deserialize<MesaCfg>(File.ReadAllText(arquivoCfg)) ?? Padrao(); }
            catch (Exception e) { Registro.Erro("ler mesa.json", e); }
            return Padrao();
        }
        static MesaCfg Padrao()
        {
            var c = new MesaCfg();
            c.Cenas.Add(new CenaCfg { Id = "barras", Nome = "Barras de teste", Fonte = "barras" });
            c.Cenas.Add(new CenaCfg { Id = "preto", Nome = "Tela preta", Fonte = "cor:#000000" });
            c.Cenas.Add(new CenaCfg { Id = "vinho", Nome = "Fundo vinho", Fonte = "cor:#5a1520" });
            c.Preview = "barras"; c.Programa = "preto";
            return c;
        }
        void Salvar()
        {
            try { File.WriteAllText(arquivoCfg, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar mesa.json", e); }
        }

        // ---------- comandos (chamados pela tela; executados na linha da mesa) ----------
        public void Fazer(Action a) => comandos.Enqueue(a);

        public void PorNoPreview(string id) => Fazer(() => { if (Cena(id) != null) { Cfg.Preview = id; Salvar(); Avisar(); } });
        public void Transicao() => Fazer(() => Comecar(Cfg.Preview, true));
        public void Direto(string id) => Fazer(() => Comecar(id, false));
        public void Efeito(string ef, int ms) => Fazer(() => { Cfg.Efeito = ef; Cfg.DuracaoMs = Math.Clamp(ms, 100, 5000); Salvar(); Avisar(); });
        public void NovaCena(string nome, string fonte, bool preencher) => NovaCena(nome, fonte, preencher, "c" + DateTime.Now.Ticks.ToString("x"));
        public void NovaCena(string nome, string fonte, bool preencher, string id) => Fazer(() =>
        {
            if (Cena(id) != null) return;
            Cfg.Cenas.Add(new CenaCfg { Id = id, Nome = string.IsNullOrWhiteSpace(nome) ? "Cena" : nome.Trim(), Fonte = fonte, Preencher = preencher });
            Salvar(); Avisar();
        });
        public void ApagarCena(string id) => Fazer(() =>
        {
            if (id == Cfg.Programa || id == para) return;                // a que está no ar não sai
            Cfg.Cenas.RemoveAll(c => c.Id == id);
            if (Cfg.Preview == id) Cfg.Preview = Cfg.Cenas.FirstOrDefault()?.Id;
            desenhadas.Remove(id);
            LimparFontes(); Salvar(); Avisar();
        });
        public void Mover(string id, int delta) => Fazer(() =>
        {
            int i = Cfg.Cenas.FindIndex(c => c.Id == id), j = i + delta;
            if (i < 0 || j < 0 || j >= Cfg.Cenas.Count) return;
            (Cfg.Cenas[i], Cfg.Cenas[j]) = (Cfg.Cenas[j], Cfg.Cenas[i]);
            Salvar(); Avisar();
        });
        public void PedirEstado() => Fazer(Avisar);

        CenaCfg Cena(string id) => Cfg.Cenas.FirstOrDefault(c => c.Id == id);

        void Comecar(string alvo, bool trocar)
        {
            if (alvo == null || Cena(alvo) == null || alvo == Cfg.Programa || relTrans != null) return;
            de = Cfg.Programa; para = alvo;
            efeitoTrans = Cfg.Efeito; durTrans = Cfg.DuracaoMs;
            if (efeitoTrans == "corte") Terminar(trocar);
            else { relTrans = Stopwatch.StartNew(); trocarNoFim = trocar; }
            Avisar();
        }
        bool trocarNoFim;
        void Terminar(bool trocar)
        {
            Cfg.Programa = para;
            if (trocar) Cfg.Preview = de;           // como nas mesas de corte: o que saiu do ar volta para o preview
            relTrans = null; de = para = null;
            Salvar();
            Registro.Info("no ar: " + Cena(Cfg.Programa)?.Nome);
        }

        // retrato das cenas para quem está fora da linha da mesa (o roteiro): id → (nome, fonte, estado), programa
        public volatile IReadOnlyDictionary<string, (string nome, string fonte, string estado)> Retrato = new Dictionary<string, (string, string, string)>();
        public volatile string ProgramaAtual, ParaAtual;

        void Avisar()
        {
            foreach (var c in Cfg.Cenas) if (c.Fonte != null && !c.Fonte.StartsWith("ndi:")) FontePara(c.Fonte);   // as leves já ficam prontas
            var e = new
            {
                tipo = "estado",
                cenas = Cfg.Cenas.Select(c => new { id = c.Id, nome = c.Nome, fonte = c.Fonte, preencher = c.Preencher, estado = fontes.TryGetValue(c.Fonte ?? "", out var f) ? f.Estado : "" }),
                preview = Cfg.Preview, programa = Cfg.Programa, efeito = Cfg.Efeito, duracao = Cfg.DuracaoMs,
                emTransicao = relTrans != null, para,
                saida = Cfg.Largura + "x" + Cfg.Altura + " " + Cfg.Qps + " q/s"
            };
            Retrato = Cfg.Cenas.ToDictionary(c => c.Id, c => (c.Nome, c.Fonte, fontes.TryGetValue(c.Fonte ?? "", out var f) ? f.Estado : ""));
            ProgramaAtual = Cfg.Programa; ParaAtual = para;
            EstadoMudou?.Invoke(JsonSerializer.Serialize(e));
        }

        // ---------- desenho ----------
        Fonte FontePara(string spec)
        {
            spec ??= "barras";
            if (!fontes.TryGetValue(spec, out var f))
            {
                f = spec == "barras" ? new FonteBarras(Cfg.Largura, Cfg.Altura)
                  : spec.StartsWith("video:") ? new FonteVideo(spec.Substring(6), Cfg.Largura, Cfg.Altura, Cfg.Qps)
                  : spec.StartsWith("capa:") ? new FonteCapa(spec.Substring(5), Cfg.Largura, Cfg.Altura)
                  : Fonte.Criar(spec);
                fontes[spec] = f;
            }
            return f;
        }
        void LimparFontes()
        {
            var usadas = new HashSet<string>(Cfg.Cenas.Select(c => c.Fonte ?? "barras"));
            foreach (var k in fontes.Keys.Where(k => !usadas.Contains(k)).ToList()) { lidas.Remove(fontes[k]); fontes[k].Dispose(); fontes.Remove(k); }
        }

        readonly Dictionary<Fonte, (long n, Imagem img)> lidas = new Dictionary<Fonte, (long, Imagem)>();
        long quadroAtual;

        // a imagem da cena já no tamanho de saída (só redesenha se a fonte mudou)
        Imagem Desenhar(string id)
        {
            var c = Cena(id);
            if (c == null) return preto;
            var f = FontePara(c.Fonte);
            if (!lidas.TryGetValue(f, out var lida) || lida.n != quadroAtual) { lida = (quadroAtual, f.Atual()); lidas[f] = lida; }   // cada fonte é lida uma vez por quadro
            var img = lida.img;
            desenhadas.TryGetValue(id, out var d);
            if (d.img == null) d.img = new Imagem(Cfg.Largura, Cfg.Altura);
            if (d.seq != f.Seq || d.fonte != f || d.preencher != c.Preencher || d.img.L != Cfg.Largura)
            {
                d.img.Garantir(Cfg.Largura, Cfg.Altura);
                Desenho.Ajustar(img, d.img, c.Preencher);
                d.seq = f.Seq; d.fonte = f; d.preencher = c.Preencher;
                desenhadas[id] = d;
            }
            return d.img;
        }

        unsafe void Rodar()
        {
            saida = new Imagem(Cfg.Largura, Cfg.Altura);
            preto = new Imagem(Cfg.Largura, Cfg.Altura); Desenho.Preto(preto);
            Avisar();
            var relogio = Stopwatch.StartNew();
            double intervalo = 1000.0 / Cfg.Qps, proximo = 0;
            long n = 0; int contQps = 0; var relQps = Stopwatch.StartNew(); double somaMs = 0;
            string ultimoEstadoFontes = "";
            while (rodando)
            {
                double agora = relogio.Elapsed.TotalMilliseconds;
                if (agora < proximo) { Thread.Sleep(Math.Max(0, (int)(proximo - agora) - 1)); continue; }
                if (agora - proximo > intervalo * 3) { Atrasos++; proximo = agora; }      // atrasou muito: não tenta recuperar
                proximo += intervalo;
                var ini = relogio.Elapsed.TotalMilliseconds;
                quadroAtual = n;
                try
                {
                    while (comandos.TryDequeue(out var a)) a();

                    // vídeos: toca quando a cena dele está no programa (ou entrando/saindo numa transição)
                    foreach (var f in fontes.Values)
                        if (f is FonteVideo v)
                            v.NoAr(Cfg.Cenas.Any(c => c.Fonte == v.Spec && (c.Id == Cfg.Programa || c.Id == para || c.Id == de)));

                    // programa (com a transição, se houver)
                    Imagem pg;
                    if (relTrans != null)
                    {
                        double t = Math.Min(1, relTrans.Elapsed.TotalMilliseconds / durTrans);
                        var a = Desenhar(de); var b = Desenhar(para);
                        if (efeitoTrans == "pagina") Desenho.VirarPagina(a, b, saida, t); else Desenho.Esmaecer(a, b, saida, t);
                        pg = saida;
                        if (t >= 1) { Terminar(trocarNoFim); Avisar(); }
                    }
                    else pg = Desenhar(Cfg.Programa);
                    QuadroDeSaida?.Invoke(pg);

                    // monitores a cada 2 quadros (15 q/s bastam para a tela)
                    if (n % 2 == 0 && Monitores != IntPtr.Zero)
                    {
                        var pv = Desenhar(Cfg.Preview);
                        Desenho.Monitor(pv, (byte*)Monitores, ML, MA);
                        Desenho.Monitor(pg, (byte*)Monitores + ML * MA * 4, ML, MA);
                        MonitoresProntos?.Invoke(n);
                    }

                    // cenas de câmera mudando de estado (procurando → ok): avisa a tela
                    var est = string.Join("|", fontes.Values.Select(f => f.Estado));
                    if (est != ultimoEstadoFontes) { ultimoEstadoFontes = est; Avisar(); }
                }
                catch (Exception e) { Registro.Erro("mesa", e); Thread.Sleep(50); }
                n++; contQps++; somaMs += relogio.Elapsed.TotalMilliseconds - ini;
                if (relQps.ElapsedMilliseconds >= 1000)
                {
                    QpsReal = contQps * 1000.0 / relQps.ElapsedMilliseconds; MsQuadro = somaMs / Math.Max(1, contQps);
                    contQps = 0; somaMs = 0; relQps.Restart();
                }
            }
        }

        public void Dispose()
        {
            rodando = false; laco.Join(1500);
            foreach (var f in fontes.Values) f.Dispose();
        }
    }
}
