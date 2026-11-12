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
        // (0.1.2) camadas: a fonte principal pode ocupar só um retângulo (Ret) e outras fontes vão por cima dela
        public float[] Ret { get; set; }           // x, y, largura, altura em fração do quadro; null = tela toda
        public List<Camada> Camadas { get; set; } = new List<Camada>();
        public bool SemSobrepor { get; set; }      // não leva as imagens de "em todas as cenas"
        // (0.1.7) som desta cena; null = padrão (a mesa + o som dos vídeos); lista vazia ou tudo desligado = transmissão muda
        public List<SomFonte> Som { get; set; }
        // (0.1.10) câmera PTZ da cena: a posição salva aqui (coordenadas, não um preset da câmera) e o rastreio a partir dela
        public string Camera { get; set; } = "";     // id da câmera PTZ desta cena (vazio = nenhuma)
        public PtzCena Ptz { get; set; }
        public string Rastreio { get; set; } = "";   // "" = fica parada | "achar" = centraliza e para | "seguir" = segue
        public string Pessoa { get; set; } = "";     // id do cadastro guardado (vazio = o maior rosto / o rosto em uso)
    }

    // Uma fonte de som da cena: "mesa" (a entrada dos Ajustes › Som), "video" (o som dos vídeos) ou "entrada"
    // (outra entrada do Windows: microfone USB, outra placa ou o som do computador "loop:…")
    public sealed class SomFonte
    {
        public string Tipo { get; set; } = "mesa";
        public string Dispositivo { get; set; } = "";
        public string Nome { get; set; } = "";
        public double VolumeDb { get; set; } = 0;
        public bool Ligada { get; set; } = true;
        public SomFonte Copia() => (SomFonte)MemberwiseClone();
    }

    // Uma camada por cima da fonte principal (câmera no canto, logo, selo AO VIVO, letras do MiguelAngelus…)
    public sealed class Camada
    {
        public string Id { get; set; }
        public string Nome { get; set; }
        public string Fonte { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float L { get; set; } = 1;
        public float A { get; set; } = 1;
        public bool Preencher { get; set; }
        public bool Moldura { get; set; }
        public int Opacidade { get; set; } = 100;
        public bool Ligada { get; set; } = true;
        public bool Atras { get; set; }            // (0.1.4) atrás da fonte principal (fundo)
    }

    public sealed class MesaCfg
    {
        public List<CenaCfg> Cenas { get; set; } = new List<CenaCfg>();
        public List<Camada> Sobrepor { get; set; } = new List<Camada>();   // (0.1.2) imagens em todas as cenas (menos as da capa)
        public string Preview { get; set; }
        public string Programa { get; set; }
        public string Efeito { get; set; } = "esmaecer";   // corte | esmaecer | pagina
        public int DuracaoMs { get; set; } = 500;
        public int Largura { get; set; } = 1920;
        public int Altura { get; set; } = 1080;
        public int Qps { get; set; } = 30;
        public bool Miniaturas { get; set; } = true;       // (0.1.9) miniatura ao vivo em cada botão de cena (Ajustes › Aparência)
    }

    public sealed class Mesa : IDisposable
    {
        public const int ML = 640, MA = 360;                     // tamanho dos monitores na tela
        static readonly string arquivoCfg = Path.Combine(Program.PastaDados, "mesa.json");

        public MesaCfg Cfg { get; private set; }
        readonly ConcurrentQueue<Action> comandos = new ConcurrentQueue<Action>();
        readonly Dictionary<string, Fonte> fontes = new Dictionary<string, Fonte>();
        sealed class Desenhada { public Imagem img; public long seq = -1; public Fonte fonte; public bool preencher, composta; public int chave; }
        readonly Dictionary<string, Desenhada> desenhadas = new Dictionary<string, Desenhada>();
        int verCamadas;                                           // muda a cada edição de camadas (redesenha)
        Imagem saida, preto;
        readonly Thread laco; volatile bool rodando = true;

        // transição em andamento
        string de, para; Stopwatch relTrans; string efeitoTrans; int durTrans;

        // monitores (preview em cima, programa embaixo, RGBA), escritos aqui e lidos pela tela
        public IntPtr Monitores; public int MonitoresTamanho => ML * MA * 4 * 2 + MiniL * MiniA * 4 * MiniMax;
        // (0.1.9) miniaturas das cenas, depois dos dois monitores: uma por cena, na ordem dos botões
        public const int MiniL = 192, MiniA = 108, MiniMax = 40;
        int proxMini;
        public void DefinirMiniaturas(bool on) => Fazer(() => { Cfg.Miniaturas = on; Salvar(); Avisar(); });
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
            try
            {
                if (File.Exists(arquivoCfg))
                {
                    var c = JsonSerializer.Deserialize<MesaCfg>(File.ReadAllText(arquivoCfg)) ?? Padrao();
                    c.Sobrepor ??= new List<Camada>();
                    foreach (var k in c.Cenas.Where(x => x.Camadas != null).SelectMany(x => x.Camadas).Concat(c.Sobrepor)) k.Fonte = AcharImagem(k.Fonte);
                    foreach (var x in c.Cenas) { x.Camadas = Limpar(x.Camadas); x.Ret = Limpar(x.Ret); }
                    c.Sobrepor = Limpar(c.Sobrepor);
                    return c;
                }
            }
            catch (Exception e) { Registro.Erro("ler mesa.json", e); }
            return Padrao();
        }
        // backup vindo de outro computador: as imagens das camadas ficam na pasta "capa" daqui
        static string AcharImagem(string spec)
        {
            if (spec == null || !spec.StartsWith("imagem:")) return spec;
            var p = spec.Substring(7);
            if (File.Exists(p)) return spec;
            var aqui = Path.Combine(Capa.Pasta, Path.GetFileName(p));
            return File.Exists(aqui) ? "imagem:" + aqui : spec;
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
        public void Transicao() => Fazer(() => { PedidosTroca++; Comecar(Cfg.Preview, true); });
        // (0.1.5) Stream Deck: corte seco, sem mudar o efeito escolhido; e acesso às cenas por número ou nome
        public void Corte() => Fazer(() => { PedidosTroca++; Comecar(Cfg.Preview, true, "corte"); });
        // (0.1.9) quantas vezes Transição/Corte foram apertados (o roteiro sabe que a troca foi pedida mesmo se o
        // Preview já é a cena do ar — ex.: uma câmera só, mudando de posição)
        public volatile int PedidosTroca;
        public CenaCfg AcharCena(string q)
        {
            var cs = Cfg.Cenas.ToList(); if (string.IsNullOrWhiteSpace(q)) return null;
            if (int.TryParse(q, out var n)) return n >= 1 && n <= cs.Count ? cs[n - 1] : null;
            return cs.FirstOrDefault(c => c.Id == q) ?? cs.FirstOrDefault(c => string.Equals(c.Nome?.Trim(), q.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? cs.FirstOrDefault(c => c.Fonte == q);
        }
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
        // (0.1.3) arrastar a cena com o mouse: tira de onde está e põe na posição "pos" (0 = primeira)
        public void MoverPara(string id, int pos) => Fazer(() =>
        {
            int i = Cfg.Cenas.FindIndex(c => c.Id == id);
            if (i < 0) return;
            var c = Cfg.Cenas[i]; Cfg.Cenas.RemoveAt(i);
            Cfg.Cenas.Insert(Math.Clamp(pos, 0, Cfg.Cenas.Count), c);
            Salvar(); Avisar();
        });
        // (0.1.7) botão direito na cena: duplicar (leva tudo: fonte, camadas, fundo, posições) e renomear
        public void Duplicar(string id) => Fazer(() =>
        {
            int i = Cfg.Cenas.FindIndex(c => c.Id == id); if (i < 0) return;
            var o = Cfg.Cenas[i];
            var nomes = Cfg.Cenas.Select(c => c.Nome).ToHashSet();
            string nome = o.Nome + " (cópia)"; for (int n = 2; nomes.Contains(nome); n++) nome = $"{o.Nome} (cópia {n})";
            var copia = new CenaCfg
            {
                Id = "c" + DateTime.Now.Ticks.ToString("x"), Nome = nome, Fonte = o.Fonte, Preencher = o.Preencher,
                Ret = o.Ret?.ToArray(), SemSobrepor = o.SemSobrepor, Som = o.Som?.Select(s => s.Copia()).ToList(),
                Camera = o.Camera, Ptz = o.Ptz?.Copia(), Rastreio = o.Rastreio, Pessoa = o.Pessoa,
                Camadas = o.Camadas.Select(k => new Camada
                {
                    Id = "k" + Guid.NewGuid().ToString("N").Substring(0, 8), Nome = k.Nome, Fonte = k.Fonte, X = k.X, Y = k.Y, L = k.L, A = k.A,
                    Preencher = k.Preencher, Moldura = k.Moldura, Opacidade = k.Opacidade, Ligada = k.Ligada, Atras = k.Atras
                }).ToList()
            };
            Cfg.Cenas.Insert(i + 1, copia);
            Registro.Info($"cena duplicada: {o.Nome} → {nome}");
            Salvar(); Avisar();
        });
        // (0.1.7) som da cena. SomMudou(fontes, ms) avisa o Som quando a cena do ar muda (ms = a duração da transição)
        public event Action<List<SomFonte>, int> SomMudou;
        public void DefinirSom(string id, List<SomFonte> fontes) => Fazer(() =>
        {
            var c = Cena(id); if (c == null) return;
            c.Som = fontes?.Where(s => s != null && (s.Tipo == "mesa" || s.Tipo == "video" || (s.Tipo == "entrada" && !string.IsNullOrWhiteSpace(s.Dispositivo))))
                .Take(8).Select(s => { s.VolumeDb = Math.Clamp(s.VolumeDb, -60, 12); return s; }).ToList();
            Salvar(); Avisar();
            if (id == Cfg.Programa && relTrans == null) SomMudou?.Invoke(c.Som, 150);
            EntradasDeSomMudaram?.Invoke();
        });
        public event Action EntradasDeSomMudaram;
        public List<string> EntradasDeSom() => Cfg.Cenas.ToList().Where(c => c.Som != null).SelectMany(c => c.Som)
            .Where(s => s.Tipo == "entrada" && !string.IsNullOrEmpty(s.Dispositivo)).Select(s => s.Dispositivo).Distinct().ToList();
        public void AvisarSom() => Fazer(() => SomMudou?.Invoke(Cena(Cfg.Programa)?.Som, 0));
        public void Renomear(string id, string nome) => Fazer(() =>
        {
            var c = Cena(id); if (c == null || string.IsNullOrWhiteSpace(nome)) return;
            c.Nome = nome.Trim().Length > 60 ? nome.Trim().Substring(0, 60) : nome.Trim();
            Salvar(); Avisar();
        });
        public void PedirEstado() => Fazer(Avisar);

        // (0.1.10) câmera da cena: escolher a câmera, o rastreio e a pessoa; e gravar/esquecer a posição
        public void DefinirCamera(string id, string camera, string rastreio, string pessoa) => Fazer(() =>
        {
            var c = Cena(id); if (c == null) return;
            camera ??= "";
            if (c.Ptz != null && c.Ptz.Camera != camera) c.Ptz = null;          // trocou de câmera: a posição era da outra
            c.Rastreio = rastreio is "achar" or "seguir" ? rastreio : "";
            c.Pessoa = pessoa ?? "";
            c.Camera = camera;
            Salvar(); Avisar();
        });
        public void GravarPtz(string id, PtzCena p) => Fazer(() =>
        {
            var c = Cena(id); if (c == null) return;
            c.Ptz = p?.Copia(); if (p != null) c.Camera = p.Camera;
            Registro.Info(p != null ? $"posição salva na cena {c.Nome}: {p.Texto()}" : $"posição esquecida na cena {c.Nome}");
            Salvar(); Avisar();
        });
        // a configuração de uma cena para quem está fora da linha da mesa (roteiro, botões): só leitura
        public CenaCfg CenaPara(string id) => string.IsNullOrEmpty(id) ? null : Cfg.Cenas.ToList().FirstOrDefault(x => x.Id == id);

        // (0.1.2) camadas de uma cena; gravar = false enquanto arrasta (só redesenha)
        public void DefinirCamadas(string id, string nome, float[] ret, List<Camada> camadas, bool semSobrepor, bool gravar) => Fazer(() =>
        {
            var c = Cena(id); if (c == null) return;
            if (!string.IsNullOrWhiteSpace(nome)) c.Nome = nome.Trim();
            c.Ret = Limpar(ret); c.Camadas = Limpar(camadas); c.SemSobrepor = semSobrepor;
            verCamadas++;
            if (gravar) { LimparFontes(); Salvar(); }
            Avisar();
        });
        public void DefinirSobrepor(List<Camada> camadas, bool gravar) => Fazer(() =>
        {
            Cfg.Sobrepor = Limpar(camadas); verCamadas++;
            if (gravar) { LimparFontes(); Salvar(); }
            Avisar();
        });
        static float[] Limpar(float[] r)
        {
            if (r == null || r.Length != 4) return null;
            float l = Math.Clamp(r[2], .02f, 2), a = Math.Clamp(r[3], .02f, 2);
            float x = Math.Clamp(r[0], Math.Min(0, 1 - l), Math.Max(0, 1 - l)), y = Math.Clamp(r[1], Math.Min(0, 1 - a), Math.Max(0, 1 - a));
            if (Math.Abs(x) < .001f && Math.Abs(y) < .001f && Math.Abs(l - 1) < .001f && Math.Abs(a - 1) < .001f) return null;   // tela toda
            return new[] { x, y, l, a };
        }
        static List<Camada> Limpar(List<Camada> l) => (l ?? new List<Camada>()).Where(k => k != null && !string.IsNullOrWhiteSpace(k.Fonte)
            && !k.Fonte.StartsWith("video:") && !k.Fonte.StartsWith("capa:")).Take(12).Select(k =>
        {
            k.Id = string.IsNullOrWhiteSpace(k.Id) ? "k" + Guid.NewGuid().ToString("N").Substring(0, 8) : k.Id;
            k.L = Math.Clamp(k.L, .02f, 2); k.A = Math.Clamp(k.A, .02f, 2);
            k.X = Math.Clamp(k.X, Math.Min(0, 1 - k.L), Math.Max(0, 1 - k.L)); k.Y = Math.Clamp(k.Y, Math.Min(0, 1 - k.A), Math.Max(0, 1 - k.A));   // sempre dentro do quadro
            k.Opacidade = Math.Clamp(k.Opacidade, 0, 100);
            return k;
        }).ToList();
        static object Js(Camada k) => new { id = k.Id, nome = k.Nome, fonte = k.Fonte, x = k.X, y = k.Y, l = k.L, a = k.A, preencher = k.Preencher, moldura = k.Moldura, opacidade = k.Opacidade, ligada = k.Ligada, atras = k.Atras };

        CenaCfg Cena(string id) => Cfg.Cenas.FirstOrDefault(c => c.Id == id);

        void Comecar(string alvo, bool trocar, string forcar = null)
        {
            if (alvo == null || Cena(alvo) == null || alvo == Cfg.Programa || relTrans != null) return;
            de = Cfg.Programa; para = alvo;
            efeitoTrans = forcar ?? Cfg.Efeito; durTrans = Cfg.DuracaoMs;
            // o som acompanha a imagem: no corte troca rápido (sem estalo), na transição no mesmo tempo dela
            try { SomMudou?.Invoke(Cena(alvo).Som, efeitoTrans == "corte" ? 30 : durTrans); } catch (Exception e) { Registro.Erro("som da cena", e); }
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
        public volatile string ProgramaAtual, ParaAtual, PreviewAtual;

        void Avisar()
        {
            foreach (var c in Cfg.Cenas) if (c.Fonte != null && !c.Fonte.StartsWith("ndi:")) FontePara(c.Fonte);   // as leves já ficam prontas
            string Est(string spec) => fontes.TryGetValue(spec ?? "", out var f) ? f.Estado : "";
            var e = new
            {
                tipo = "estado",
                cenas = Cfg.Cenas.Select(c => new
                {
                    id = c.Id, nome = c.Nome, fonte = c.Fonte, preencher = c.Preencher, estado = Est(c.Fonte),
                    ret = c.Ret, semSobrepor = c.SemSobrepor,
                    camera = c.Camera ?? "", ptz = c.Ptz == null ? null : new { quando = c.Ptz.Quando, texto = c.Ptz.Texto() }, rastreio = c.Rastreio ?? "", pessoa = c.Pessoa ?? "",
                    som = c.Som?.Select(s => new { tipo = s.Tipo, dispositivo = s.Dispositivo, nome = s.Nome, volumeDb = s.VolumeDb, ligada = s.Ligada }), camadas = (c.Camadas ?? new List<Camada>()).Select(Js), estCamadas = (c.Camadas ?? new List<Camada>()).Select(k => Est(k.Fonte))
                }),
                sobrepor = Cfg.Sobrepor.Select(Js), estSobrepor = Cfg.Sobrepor.Select(k => Est(k.Fonte)),
                marcas = new { brasao = File.Exists(Capa.Cfg.Marca1 ?? "") ? Capa.Cfg.Marca1 : null, pascom = File.Exists(Capa.Cfg.Marca2 ?? "") ? Capa.Cfg.Marca2 : null },
                preview = Cfg.Preview, programa = Cfg.Programa, efeito = Cfg.Efeito, duracao = Cfg.DuracaoMs,
                emTransicao = relTrans != null, para, miniaturas = Cfg.Miniaturas,
                saida = Cfg.Largura + "x" + Cfg.Altura + " " + Cfg.Qps + " q/s"
            };
            Retrato = Cfg.Cenas.ToDictionary(c => c.Id, c => (c.Nome, c.Fonte, fontes.TryGetValue(c.Fonte ?? "", out var f) ? f.Estado : ""));
            ProgramaAtual = Cfg.Programa; ParaAtual = para; PreviewAtual = Cfg.Preview;
            EstadoMudou?.Invoke(JsonSerializer.Serialize(e));
        }

        // ---------- desenho ----------
        Fonte FontePara(string spec)
        {
            spec ??= "barras";
            if (!fontes.TryGetValue(spec, out var f))
            {
                f = spec == "barras" ? new FonteBarras(Cfg.Largura, Cfg.Altura)
                  : spec.StartsWith("selo:") ? new FonteSelo(spec.Substring(5))
                  : spec.StartsWith("fundo:") ? new FonteFundo(spec.Substring(6), Cfg.Largura, Cfg.Altura)
                  : spec.StartsWith("video:") ? new FonteVideo(spec.Substring(6), Cfg.Largura, Cfg.Altura, Cfg.Qps)
                  : spec.StartsWith("capa:") ? new FonteCapa(spec.Substring(5), Cfg.Largura, Cfg.Altura)
                  : Fonte.Criar(spec);
                fontes[spec] = f;
            }
            return f;
        }
        void LimparFontes()
        {
            var usadas = new HashSet<string>(Cfg.Cenas.Select(c => c.Fonte ?? "barras")
                .Concat(Cfg.Cenas.SelectMany(c => c.Camadas ?? new List<Camada>()).Select(k => k.Fonte))
                .Concat(Cfg.Sobrepor.Select(k => k.Fonte)));
            foreach (var k in fontes.Keys.Where(k => !usadas.Contains(k)).ToList()) { lidas.Remove(fontes[k]); pms.Remove(fontes[k]); ampliados.Remove(fontes[k]); fontes[k].Dispose(); fontes.Remove(k); }
        }

        readonly Dictionary<Fonte, (long n, Imagem img)> lidas = new Dictionary<Fonte, (long, Imagem)>();
        readonly Dictionary<Fonte, (long seq, Imagem img)> pms = new Dictionary<Fonte, (long, Imagem)>();   // cópia pré-multiplicada (fontes com alfa)
        long quadroAtual;

        Imagem Ler(Fonte f)   // cada fonte é lida uma vez por quadro
        {
            if (!lidas.TryGetValue(f, out var lida) || lida.n != quadroAtual) { lida = (quadroAtual, f.Atual()); lidas[f] = lida; }
            return lida.img;
        }
        Imagem PreMultiplicada(Fonte f, Imagem img)
        {
            if (pms.TryGetValue(f, out var p) && p.seq == f.Seq && p.img.L == img.L && p.img.A == img.A) return p.img;
            var pm = p.img ?? new Imagem(); pm.Garantir(img.L, img.A); pm.TemAlfa = true;
            Desenho.PreMultiplicar(img, pm);
            pms[f] = (f.Seq, pm);
            return pm;
        }

        // camadas que vão por cima da cena: as dela e, se for o caso, as de "em todas as cenas"
        List<Camada> Extras(CenaCfg c)
        {
            var l = (c.Camadas ?? new List<Camada>()).Where(k => k.Ligada).ToList();
            if (!c.SemSobrepor && !(c.Fonte ?? "").StartsWith("capa:")) l.AddRange(Cfg.Sobrepor.Where(k => k.Ligada));
            return l;
        }
        System.Drawing.Rectangle Ret(float x, float y, float l, float a) =>
            new System.Drawing.Rectangle((int)Math.Round(x * Cfg.Largura), (int)Math.Round(y * Cfg.Altura), Math.Max(1, (int)Math.Round(l * Cfg.Largura)), Math.Max(1, (int)Math.Round(a * Cfg.Altura)));

        // a imagem da cena já no tamanho de saída (só redesenha se alguma fonte mudou)
        Imagem Desenhar(string id)
        {
            var c = Cena(id);
            if (c == null) return preto;
            var f = FontePara(c.Fonte);
            var img = Ler(f);
            if (!desenhadas.TryGetValue(id, out var d)) desenhadas[id] = d = new Desenhada { img = new Imagem(Cfg.Largura, Cfg.Altura) };
            var extras = Extras(c);
            if (extras.Count == 0 && c.Ret == null)
            {
                if (d.composta || d.seq != f.Seq || d.fonte != f || d.preencher != c.Preencher || d.img.L != Cfg.Largura)
                {
                    d.img.Garantir(Cfg.Largura, Cfg.Altura);
                    Desenho.Ajustar(img, d.img, c.Preencher);
                    d.seq = f.Seq; d.fonte = f; d.preencher = c.Preencher; d.composta = false;
                }
                return d.img;
            }
            // com camadas: redesenha quando qualquer uma das fontes muda ou quando as camadas são editadas
            var h = new HashCode(); h.Add(verCamadas); h.Add(f); h.Add(f.Seq); h.Add(c.Preencher);
            var fs = new List<(Camada k, Fonte f)>();
            foreach (var k in extras) { var fk = FontePara(k.Fonte); Ler(fk); fs.Add((k, fk)); h.Add(fk); h.Add(fk.Seq); h.Add(fk.Estado); }   // ler já aqui: a câmera NDI só procura a fonte quando é lida
            int chave = h.ToHashCode();
            if (d.composta && d.chave == chave && d.img.L == Cfg.Largura) return d.img;
            d.img.Garantir(Cfg.Largura, Cfg.Altura);
            // ordem: as camadas de trás (fundo), a principal, as da frente
            bool temAtras = fs.Exists(x => x.k.Atras);
            if (c.Ret == null && !temAtras) Desenho.Ajustar(img, d.img, c.Preencher);
            else
            {
                Desenho.Preto(d.img);
                foreach (var (k, fk) in fs) if (k.Atras) Camada(d.img, k, fk);
                var bi = img.TemAlfa ? PreMultiplicada(f, img) : img;     // letras do MiguelAngelus como principal: o fundo aparece por trás
                Desenho.AjustarEm(bi, d.img, c.Ret == null ? Ret(0, 0, 1, 1) : Ret(c.Ret[0], c.Ret[1], c.Ret[2], c.Ret[3]), c.Preencher, 256);
            }
            foreach (var (k, fk) in fs) if (!k.Atras) Camada(d.img, k, fk);
            d.chave = chave; d.composta = true; d.seq = -1;
            return d.img;
        }
        // fundo já ampliado para o tamanho da caixa: só é refeito quando o fundo muda (a luz suave anda a 15 q/s)
        readonly Dictionary<Fonte, (long seq, System.Drawing.Rectangle r, Imagem img)> ampliados = new Dictionary<Fonte, (long, System.Drawing.Rectangle, Imagem)>();
        void Camada(Imagem destino, Camada k, Fonte fk)
        {
            if (fk.Estado != "ok") return;                             // câmera ainda procurando: não desenha uma caixa preta
            var ik = Ler(fk);
            var rr = Ret(k.X, k.Y, k.L, k.A);
            if (fk is FonteFundo && !ik.TemAlfa && (ik.L != rr.Width || ik.A != rr.Height))
            {
                if (!ampliados.TryGetValue(fk, out var am) || am.seq != fk.Seq || am.r.Size != rr.Size)
                {
                    var im = am.img ?? new Imagem(); im.Garantir(rr.Width, rr.Height);
                    Desenho.Ajustar(ik, im, true);
                    ampliados[fk] = am = (fk.Seq, rr, im);
                }
                ik = am.img;
            }
            if (ik.TemAlfa) ik = PreMultiplicada(fk, ik);
            var feito = Desenho.AjustarEm(ik, destino, rr, k.Preencher, (int)Math.Round(k.Opacidade * 2.56));
            if (k.Moldura && !feito.IsEmpty) Desenho.Moldura(destino, feito, Math.Max(2, Cfg.Largura / 480));
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
                    // (0.1.9) miniaturas: nos quadros em que os monitores não são desenhados, duas cenas por vez
                    // (com 10 cenas, cada uma se atualiza 3 vezes por segundo); desligadas nos Ajustes, nada é feito
                    else if (Cfg.Miniaturas && Monitores != IntPtr.Zero && relTrans == null)
                    {
                        int total = Math.Min(Cfg.Cenas.Count, MiniMax);
                        for (int k = 0; k < Math.Min(2, total); k++)
                        {
                            int i = proxMini++ % total;
                            var c = Cfg.Cenas[i];
                            var img = Desenhar(c.Id);
                            Desenho.Miniatura(img, (byte*)Monitores + ML * MA * 4 * 2 + i * MiniL * MiniA * 4, MiniL, MiniA);
                        }
                        if (proxMini > 1_000_000) proxMini = 0;
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
