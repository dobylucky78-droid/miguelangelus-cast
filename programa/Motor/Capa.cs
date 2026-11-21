// Capa da live (Ajustes › Capa da live): a mesma arte vira a miniatura da live (PNG 1920×1080) e três cenas:
//  "capa:espera"  Tela de espera, com contagem regressiva até a hora da celebração
//  "capa:capa"    Abertura
//  "capa:fim"     Encerramento
// Cor litúrgica e nome da celebração vêm sozinhos do calendário (Liturgia.cs), a não ser que o usuário escolha à mão.
// A ilustração do folheto costuma ter direitos autorais: fica só na pasta de dados deste computador, nunca no programa.
// (0.1.17) Para cada paróquia ter a sua cara (backlog 8): 9 disposições, fundos (temas, foto desfocada, texturas
// desenhadas aqui), letras (só fontes que já vêm no Windows), divisor e moldura, cor da paróquia, nome da paróquia e
// padroeiro, posição e tamanho do brasão e do logo, sugestões que combinam e estilos guardados. Uma capa.json antiga
// (sem esses campos) continua igual: os valores-padrão são os da capa de antes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    /// <summary>(0.1.17) O "jeito" da capa, sem os textos e as datas: o que um estilo guardado ou uma sugestão troca.</summary>
    public sealed class EstiloCapa
    {
        public string Nome { get; set; } = "";
        public string Modelo { get; set; } = "arco";
        public string Fundo { get; set; } = "padrao";
        public string Letras { get; set; } = "classica";
        public string Divisor { get; set; } = "cruz";
        public string Moldura { get; set; } = "fio";
        public string MarcasPos { get; set; } = "texto";
        public double MarcasTam { get; set; } = 1;
        public string Cor { get; set; } = "";
        public string CorParoquia { get; set; } = "";
        public string Destaque { get; set; } = "";
        public bool MostrarParoquia { get; set; }

        public static EstiloCapa De(CapaCfg c, string nome = "") => new EstiloCapa
        {
            Nome = nome, Modelo = c.Modelo, Fundo = c.Fundo, Letras = c.Letras, Divisor = c.Divisor, Moldura = c.Moldura, MarcasPos = c.MarcasPos,
            MarcasTam = c.MarcasTam, Cor = c.Cor, CorParoquia = c.CorParoquia, Destaque = c.Destaque, MostrarParoquia = c.MostrarParoquia
        };
        public void Aplicar(CapaCfg c)
        {
            c.Modelo = Modelo; c.Fundo = Fundo; c.Letras = Letras; c.Divisor = Divisor; c.Moldura = Moldura; c.MarcasPos = MarcasPos;
            c.MarcasTam = MarcasTam; c.Cor = Cor; c.CorParoquia = CorParoquia; c.Destaque = Destaque; c.MostrarParoquia = MostrarParoquia;
        }
    }

    public sealed class CapaCfg
    {
        public string Modelo { get; set; } = "arco";          // arco | sangrada | central | faixa | dividida | vitral | cartao | letras | padroeiro
        public string Cor { get; set; } = "";                 // "" = automática pelo calendário · verde… · "paroquia" = CorParoquia
        public string Titulo { get; set; } = "Santa Missa";
        public string Chamada { get; set; } = "ao vivo, diretamente da nossa paróquia";
        public string Extra { get; set; } = "";
        public string Celebrante { get; set; } = "";
        public string Imagem { get; set; } = "simbolo";        // folheto | foto | simbolo
        public string Folheto { get; set; } = "";             // ilustração da semana (só neste computador)
        public string Foto { get; set; } = "";                // foto do altar ou da fachada
        public string Marca1 { get; set; } = "";              // brasão da paróquia
        public string Marca2 { get; set; } = "";              // logo da Pascom
        public bool UsarMarca1 { get; set; } = true;
        public bool UsarMarca2 { get; set; } = true;
        public DateTime Quando { get; set; } = ProximoDomingo();   // próxima celebração
        public string Celebracao { get; set; } = "";          // "" = automática
        public string Proxima { get; set; } = "";             // "Domingo, 07h00" no Encerramento
        public bool EsperaAuto { get; set; }                  // põe a Tela de espera no ar sozinho, X minutos antes
        public int EsperaMin { get; set; } = 15;
        // (0.0.5) agenda do MiguelAngelus: data/hora e "Próxima Missa" vêm de lá (Motor\Miguel.cs)
        public bool SeguirMiguel { get; set; } = true;
        public string MiguelLocal { get; set; } = "";          // sigla da capela que este Cast transmite ("" = todas)
        public bool MiguelSoMissas { get; set; } = true;       // Terço, Adoração… não viram "próxima transmissão"
        public bool MiguelFolheto { get; set; } = true;        // (0.0.6) ilustração do folheto vem do roteiro do MiguelAngelus
        public bool MiguelMarcas { get; set; } = true;         // (0.0.7) brasão e logo vêm dos cantos do telão do MiguelAngelus
        // (0.1.12) celebrações marcadas para transmitir ("2026-11-15T07:00|SJO"; nenhuma = todas) e a escolhida no Início ("" = a primeira)
        public List<string> MiguelPreparadas { get; set; } = new();
        public string MiguelEscolhida { get; set; } = "";
        public string YtPrivacidade { get; set; } = "public";   // (0.0.8) live criada pelo Cast: public | unlisted | private
        public string YtDescricao { get; set; } = "";
        // (0.1.17) a cara da paróquia
        public string Fundo { get; set; } = "padrao";          // padrao | degrade | liso | tema | foto | pergaminho | linho | pedra | madeira
        public string Letras { get; set; } = "classica";       // ver Capa.Letras
        public string Divisor { get; set; } = "cruz";          // cruz | ramos | filete | nenhum
        public string Moldura { get; set; } = "fio";           // fio | dupla | cantos | nenhuma
        public string MarcasPos { get; set; } = "texto";       // texto | sup-dir | sup-esq | inf-dir | inf-esq
        public double MarcasTam { get; set; } = 1;             // 0,75 · 1 · 1,35
        public string CorParoquia { get; set; } = "";          // "#rrggbb"
        public string Destaque { get; set; } = "";             // "" = dourado da cor · "paroquia" = CorParoquia
        public string Paroquia { get; set; } = "";             // "" = o nome que vem do MiguelAngelus
        public string Padroeiro { get; set; } = "";
        public bool MostrarParoquia { get; set; }
        public List<EstiloCapa> Estilos { get; set; } = new();
        // (0.1.19, backlog 8.7) ajustes à mão na prévia, guardados por disposição: modelo → item (titulo | textos | imagem | marcas)
        // → [dx, dy, escala], com dx/dy em fração da largura/altura. Sem ajuste, vale a disposição pronta.
        public Dictionary<string, Dictionary<string, double[]>> Ajustes { get; set; } = new();

        static DateTime ProximoDomingo()
        {
            var d = DateTime.Today.AddDays(1); while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
            return d.AddHours(7);
        }
    }

    public static class Capa
    {
        static readonly string arquivo = Path.Combine(Program.PastaDados, "capa.json");
        public static readonly string Pasta = Path.Combine(Program.PastaDados, "capa");
        static CapaCfg cfg;
        // no construtor estático (depois de todos os campos): Carregar pode sortear o estilo, que usa as listas de baixo
        static Capa() { cfg = Carregar(); }
        public static CapaCfg Cfg => cfg;
        public static int Versao { get; private set; } = 1;

        static CapaCfg Carregar()
        {
            try
            {
                if (File.Exists(arquivo))
                {
                    var c = JsonSerializer.Deserialize<CapaCfg>(File.ReadAllText(arquivo)) ?? new CapaCfg();
                    // backup vindo de outro computador: a pasta de dados lá era outra; as imagens estão na pasta "capa" daqui
                    string Achar(string p) => string.IsNullOrEmpty(p) || File.Exists(p) ? p : File.Exists(Path.Combine(Pasta, Path.GetFileName(p))) ? Path.Combine(Pasta, Path.GetFileName(p)) : "";
                    c.Folheto = Achar(c.Folheto); c.Foto = Achar(c.Foto); c.Marca1 = Achar(c.Marca1); c.Marca2 = Achar(c.Marca2);
                    while (c.Quando < DateTime.Now.AddHours(-3)) c.Quando = c.Quando.AddDays(7);   // já passou: a mesma hora na semana seguinte
                    c.Estilos ??= new();
                    return c;
                }
            }
            catch (Exception e) { Registro.Erro("ler capa.json", e); }
            // (0.1.17) primeira vez neste Cast: começa com uma combinação sorteada, para cada paróquia não sair igual às outras
            var nova = new CapaCfg();
            try { Sugerir(nova, 1, Environment.TickCount)[0].Aplicar(nova); } catch { }
            return nova;
        }
        public static void Trocar(CapaCfg nova, bool gravar)
        {
            nova.Estilos ??= new();
            cfg = nova; Versao++;
            if (!gravar) return;
            try { File.WriteAllText(arquivo, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar capa.json", e); }
        }
        // copia a imagem escolhida para a pasta de dados (o original pode sumir do pen drive)
        public static string GuardarImagem(string origem, string nome)
        {
            Directory.CreateDirectory(Pasta);
            var destino = Path.Combine(Pasta, nome + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(origem).ToLowerInvariant());
            File.Copy(origem, destino, true);
            return destino;
        }

        // ---------- cores ----------
        public sealed class Paleta { public string Nome; public Color F0, F1, F2, Amostra, Tinta, Acento, I1, I2; public float P1; }
        static Color Hex(string s) => ColorTranslator.FromHtml(s);
        public static readonly Dictionary<string, Paleta> Paletas = new Dictionary<string, Paleta>
        {
            ["verde"] = new Paleta { Nome = "Verde", F0 = Hex("#2a7a56"), F1 = Hex("#16503a"), F2 = Hex("#0b2d20"), P1 = .45f, Amostra = Hex("#1f6a4a"), Tinta = Hex("#f6f1e3"), Acento = Hex("#e3c27a"), I1 = Hex("#3d8a66"), I2 = Hex("#1a5038") },
            ["roxo"] = new Paleta { Nome = "Roxo", F0 = Hex("#64449a"), F1 = Hex("#3b2263"), F2 = Hex("#1d0f33"), P1 = .45f, Amostra = Hex("#4a2c78"), Tinta = Hex("#f4effa"), Acento = Hex("#e0c27e"), I1 = Hex("#7656a8"), I2 = Hex("#3a2262") },
            ["branco"] = new Paleta { Nome = "Branco", F0 = Hex("#fffdf7"), F1 = Hex("#f4ecd6"), F2 = Hex("#e3d3a8"), P1 = .5f, Amostra = Hex("#f3ead2"), Tinta = Hex("#3a2e16"), Acento = Hex("#a07a28"), I1 = Hex("#c9b074"), I2 = Hex("#8d6c2a") },
            ["vermelho"] = new Paleta { Nome = "Vermelho", F0 = Hex("#ad2a34"), F1 = Hex("#761822"), F2 = Hex("#3c0a10"), P1 = .45f, Amostra = Hex("#8e1f2a"), Tinta = Hex("#fbf0ee"), Acento = Hex("#efc77c"), I1 = Hex("#c04a52"), I2 = Hex("#6e1620") },
            ["rosa"] = new Paleta { Nome = "Rosa", F0 = Hex("#dc9db4"), F1 = Hex("#ad6884"), F2 = Hex("#6c3450"), P1 = .5f, Amostra = Hex("#c27a96"), Tinta = Hex("#fff6f8"), Acento = Hex("#fbe2a8"), I1 = Hex("#e2a9bd"), I2 = Hex("#8e4a68") },
        };
        public static string CorAuto(CapaCfg c) => Liturgia.Info(c.Quando).cor;
        /// <summary>A cor litúrgica usada (com "cor da paróquia" no fundo, continua a do calendário, para o Início e as cenas).</summary>
        public static string CorUsada(CapaCfg c) => Paletas.ContainsKey(c.Cor ?? "") ? c.Cor : CorAuto(c);
        public static string CelebracaoUsada(CapaCfg c) => string.IsNullOrWhiteSpace(c.Celebracao) ? Liturgia.Info(c.Quando).nome : c.Celebracao.Trim();
        public static string ParoquiaUsada(CapaCfg c) => !string.IsNullOrWhiteSpace(c.Paroquia) ? c.Paroquia.Trim() : (Miguel.Paroquia ?? "").Trim();

        public static bool CorValida(string s, out Color cor)
        {
            cor = Color.Empty;
            if (string.IsNullOrWhiteSpace(s) || !System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), "^#[0-9a-fA-F]{6}$")) return false;
            cor = Hex(s.Trim()); return true;
        }
        static Color Mistura(Color a, Color b, float t) => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        static float Luz(Color c) => (.299f * c.R + .587f * c.G + .114f * c.B) / 255f;
        /// <summary>Paleta inteira a partir de uma cor só (a da paróquia).</summary>
        public static Paleta PaletaDe(Color b)
        {
            bool clara = Luz(b) > .62f;
            return new Paleta
            {
                Nome = "Cor da paróquia", F0 = Mistura(b, Color.White, .22f), F1 = b, F2 = Mistura(b, Color.Black, clara ? .18f : .55f), P1 = .45f, Amostra = b,
                Tinta = clara ? Hex("#2a2216") : Hex("#f8f3e6"), Acento = clara ? Hex("#8d6c2a") : Hex("#e3c27a"),
                I1 = Mistura(b, Color.White, .15f), I2 = Mistura(b, Color.Black, .3f)
            };
        }
        public static Paleta PaletaUsada(CapaCfg c)
        {
            var P = c.Cor == "paroquia" && CorValida(c.CorParoquia, out var cp) ? PaletaDe(cp) : Paletas[CorUsada(c)];
            if (c.Destaque == "paroquia" && CorValida(c.CorParoquia, out var ca))
            {
                // a cor da paróquia nos detalhes: clareia ou escurece até aparecer bem sobre o fundo
                bool fundoClaro = Luz(P.F1) > .62f; var a = ca;
                for (int i = 0; i < 6 && Math.Abs(Luz(a) - Luz(P.F1)) < .32f; i++) a = Mistura(a, fundoClaro ? Color.Black : Color.White, .25f);
                P = new Paleta { Nome = P.Nome, F0 = P.F0, F1 = P.F1, F2 = P.F2, P1 = P.P1, Amostra = P.Amostra, Tinta = P.Tinta, Acento = a, I1 = P.I1, I2 = P.I2 };
            }
            return P;
        }

        // ---------- opções ----------
        public static readonly (string id, string nome)[] Modelos =
        {
            ("arco", "Arco"), ("sangrada", "Imagem inteira"), ("central", "Central"), ("faixa", "Faixa embaixo"), ("dividida", "Dividida"),
            ("vitral", "Vitral"), ("cartao", "Cartão"), ("letras", "Só letras"), ("padroeiro", "Padroeiro"),
        };
        public static readonly (string id, string nome)[] Fundos =
        {
            ("padrao", "Luz e riscos"), ("degrade", "Degradê"), ("liso", "Liso"), ("tema", "Tema do dia"), ("foto", "Foto desfocada"),
            ("pergaminho", "Pergaminho"), ("linho", "Linho"), ("pedra", "Pedra"), ("madeira", "Madeira"),
        };
        public sealed class Letra { public string Nome, Tit, Corpo, Forte; public FontStyle TitEstilo, ForteEstilo; public float TitEscala = 1; public bool TitMaiusc; }
        // só fontes que já vêm em todo Windows 10/11 (nada a baixar nem a distribuir)
        public static readonly Dictionary<string, Letra> Letras = new Dictionary<string, Letra>
        {
            ["classica"] = new Letra { Nome = "Clássica", Tit = "Georgia", TitEstilo = FontStyle.Italic, Corpo = "Segoe UI", Forte = "Segoe UI Semibold" },
            ["solene"] = new Letra { Nome = "Solene", Tit = "Sitka Banner", TitEstilo = FontStyle.Regular, Corpo = "Segoe UI", Forte = "Segoe UI Semibold", TitEscala = 1.02f },
            ["livro"] = new Letra { Nome = "Livro", Tit = "Palatino Linotype", TitEstilo = FontStyle.Italic, Corpo = "Candara", Forte = "Candara", ForteEstilo = FontStyle.Bold },
            ["moderna"] = new Letra { Nome = "Moderna", Tit = "Bahnschrift SemiBold", TitEstilo = FontStyle.Regular, Corpo = "Bahnschrift", Forte = "Bahnschrift SemiBold", TitEscala = .8f, TitMaiusc = true },
            ["suave"] = new Letra { Nome = "Suave", Tit = "Gabriola", TitEstilo = FontStyle.Regular, Corpo = "Corbel", Forte = "Corbel", ForteEstilo = FontStyle.Bold, TitEscala = 1.3f },
            ["manuscrita"] = new Letra { Nome = "Manuscrita", Tit = "Segoe Script", TitEstilo = FontStyle.Regular, Corpo = "Segoe UI", Forte = "Segoe UI Semibold", TitEscala = .78f },
        };
        public static Letra LetraDe(CapaCfg c) => Letras.TryGetValue(c?.Letras ?? "", out var l) ? l : Letras["classica"];
        static readonly Dictionary<string, bool> existe = new Dictionary<string, bool>();
        /// <summary>Fonte pelo nome; se este Windows não tiver, cai para Georgia (títulos) ou Segoe UI.</summary>
        public static Font Fonte(string familia, float tam, FontStyle estilo, bool titulo = false)
        {
            lock (existe)
                if (!existe.TryGetValue(familia, out var tem))
                {
                    try { using var ff = new FontFamily(familia); tem = true; } catch { tem = false; }
                    existe[familia] = tem;
                }
            if (!existe[familia]) familia = titulo ? "Georgia" : "Segoe UI";
            return new Font(familia, tam, estilo, GraphicsUnit.Pixel);
        }
        public static readonly string[] Divisores = { "cruz", "ramos", "filete", "nenhum" };
        public static readonly string[] Molduras = { "fio", "dupla", "cantos", "nenhuma" };

        static readonly CultureInfo br = new CultureInfo("pt-BR");
        public static string DataLonga(DateTime d)
        {
            var s = d.ToString("dddd, d 'de' MMMM 'de' yyyy", br);
            return char.ToUpper(s[0]) + s.Substring(1);
        }
        public static string Hora(DateTime d) => d.ToString("HH'h'mm");

        // ---------- sugestões (backlog 8.6) ----------
        static Bitmap ImagemDe(CapaCfg c) => c.Imagem == "folheto" ? Img(c.Folheto) : c.Imagem == "foto" ? Img(c.Foto) : null;
        /// <summary>Combinações que combinam entre si, diferentes umas das outras, para a paróquia escolher.</summary>
        public static List<EstiloCapa> Sugerir(CapaCfg baseCfg, int n, int semente)
        {
            var r = new Random(semente); var lista = new List<EstiloCapa>(); var vistos = new HashSet<string>();
            bool temFoto; lock (trava) temFoto = ImagemDe(baseCfg) != null;
            T Um<T>(IList<T> a) => a[r.Next(a.Count)];
            var modelos = temFoto ? Modelos.Select(m => m.id).ToList() : new List<string> { "arco", "central", "letras", "vitral", "padroeiro", "dividida", "letras" };
            var letras = Letras.Keys.ToList();
            for (int tentativa = 0; lista.Count < n && tentativa < 300; tentativa++)
            {
                var e = EstiloCapa.De(baseCfg);
                e.Modelo = Um(modelos);
                var fundos = new List<string> { "padrao", "degrade", "pergaminho", "linho", "pedra", "madeira" };
                if (e.Modelo is "letras" or "cartao") { fundos.Add("tema"); fundos.Add("tema"); }
                if (temFoto && e.Modelo is "letras" or "central") fundos.Add("foto");
                if (e.Modelo == "faixa") fundos = new List<string> { "padrao" };          // a foto cobre a tela inteira
                e.Fundo = Um(fundos);
                e.Letras = Um(letras);
                e.Divisor = Um(new[] { "cruz", "cruz", "ramos", "ramos", "filete", "nenhum" });
                e.Moldura = Um(new[] { "fio", "fio", "dupla", "cantos", "cantos", "nenhuma" });
                e.MarcasPos = e.Modelo is "letras" or "cartao" or "central" ? "texto" : Um(new[] { "texto", "texto", "sup-dir", "inf-dir" });
                if (e.Modelo == "sangrada" && e.MarcasPos != "texto") e.MarcasPos = "texto";
                e.MarcasTam = 1;
                var chave = e.Modelo + "|" + e.Fundo + "|" + e.Letras;
                // um de cada modelo (só repete se faltar modelo: sem foto há menos modelos que combinam)
                int max = modelos.Distinct().Count() >= n ? 1 : 2;
                if (vistos.Add(chave) && lista.Count(x => x.Modelo == e.Modelo) < max) lista.Add(e);
            }
            return lista;
        }
        static CapaCfg Clonar(CapaCfg c) => JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(c));
        /// <summary>A mesma capa, com outro estilo (para as miniaturas das sugestões).</summary>
        public static CapaCfg ComEstilo(CapaCfg c, EstiloCapa e) { var x = Clonar(c); e.Aplicar(x); return x; }

        // ---------- desenho ----------
        static readonly object trava = new object();
        static readonly Dictionary<string, (DateTime quando, Bitmap bmp)> imagens = new Dictionary<string, (DateTime, Bitmap)>();
        static Bitmap Img(string caminho)
        {
            if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
            var t = File.GetLastWriteTimeUtc(caminho);
            if (imagens.TryGetValue(caminho, out var x) && x.quando == t) return x.bmp;
            try
            {
                Bitmap b; using (var tmp = new Bitmap(caminho)) b = new Bitmap(tmp);   // cópia: não deixa o arquivo preso
                if (x.bmp != null) x.bmp.Dispose();
                imagens[caminho] = (t, b); return b;
            }
            catch (Exception e) { Registro.Erro("capa: imagem " + caminho, e); return null; }
        }

        sealed class Lay
        {
            public float iL, iT, iW, iH; public string forma = "reta";            // forma: arco | reta | circulo | ogiva | "" (sem imagem)
            public float tL, tT, tW, tB = .9f, tit; public bool centro, esticar, chamada, fade;
            public string ancora = "topo";                                          // topo | meio | baixo (na faixa tT…tB)
            public float zoom = 1, oX = .5f, oY = .5f, posY = .5f;
        }
        static Lay Layout(string m) => m switch
        {
            "sangrada" => new Lay { iL = .44f, iT = 0, iW = .56f, iH = 1, tL = .067f, tT = .11f, tW = .48f, tit = 10.8f, fade = true, forma = "reta", posY = .2f },
            "central" => new Lay { iL = .44f, iT = .06f, iW = .12f, iH = -1, tL = .08f, tT = .305f, tW = .84f, tit = 6.8f, centro = true, forma = "circulo", zoom = 1.9f, oX = .33f, oY = .14f, posY = 0 },
            "faixa" => new Lay { iL = 0, iT = 0, iW = 1, iH = 1, forma = "reta", posY = .3f, tL = .067f, tW = .6f, tT = .25f, tB = .9f, ancora = "baixo", tit = 8.2f, chamada = true },
            "dividida" => new Lay { iL = 0, iT = 0, iW = .46f, iH = 1, forma = "reta", posY = .3f, tL = .53f, tW = .41f, tT = .08f, tB = .92f, ancora = "meio", tit = 8.4f, chamada = true },
            "vitral" => new Lay { iL = .075f, iT = .09f, iW = .3f, iH = .82f, forma = "ogiva", posY = .25f, tL = .45f, tW = .48f, tT = .08f, tB = .92f, ancora = "meio", tit = 8.8f, chamada = true },
            "cartao" => new Lay { forma = "", tL = .2f, tW = .6f, tT = .13f, tB = .87f, centro = true, ancora = "meio", tit = 7.2f, chamada = true },
            "letras" => new Lay { forma = "", tL = .1f, tW = .8f, tT = .1f, tB = .9f, centro = true, ancora = "meio", tit = 10.5f, chamada = true },
            "padroeiro" => new Lay { iL = .1f, iT = .15f, iW = .3375f, iH = -1, forma = "circulo", posY = .3f, tL = .53f, tW = .41f, tT = .08f, tB = .92f, ancora = "meio", tit = 8f, chamada = true },
            _ => new Lay { iL = .568f, iT = .085f, iW = .344f, iH = .83f, tL = .067f, tT = .09f, tW = .47f, tit = 9.6f, esticar = true, chamada = true, forma = "arco", posY = .25f },
        };

        // (0.1.19, backlog 8.7) ajuste de um item: desloca (fração de W/H) e muda o tamanho a partir de um ponto de origem
        readonly struct Aj
        {
            public readonly float dx, dy, s;
            public Aj(float dx, float dy, float s) { this.dx = dx; this.dy = dy; this.s = s; }
            public bool Nada => dx == 0 && dy == 0 && s == 1;
        }
        static Aj AjDe(CapaCfg c, string item)
        {
            if (c.Ajustes != null && c.Ajustes.TryGetValue(c.Modelo ?? "arco", out var d) && d != null && d.TryGetValue(item, out var a) && a != null && a.Length >= 3
                && !double.IsNaN(a[0]) && !double.IsNaN(a[1]) && !double.IsNaN(a[2]))
                return new Aj((float)Math.Clamp(a[0], -1, 1), (float)Math.Clamp(a[1], -1, 1), (float)Math.Clamp(a[2] <= 0 ? 1 : a[2], .3, 3));
            return new Aj(0, 0, 1);
        }
        static RectangleF Tr(RectangleF r, float ox, float oy, Aj a, int W, int H) =>
            new RectangleF(ox + (r.X - ox) * a.s + a.dx * W, oy + (r.Y - oy) * a.s + a.dy * H, r.Width * a.s, r.Height * a.s);
        static void Transformar(Graphics g, float ox, float oy, Aj a, int W, int H)
        {
            if (a.Nada) return;
            g.TranslateTransform(ox + a.dx * W, oy + a.dy * H); g.ScaleTransform(a.s, a.s); g.TranslateTransform(-ox, -oy);
        }

        /// <summary>Desenha a capa. modo: capa | espera | fim. caixas: se vier, recebe onde ficou cada item ajustável (pixels).</summary>
        public static Bitmap Desenhar(string modo, int W = 1920, int H = 1080, CapaCfg c = null, DateTime? agora = null, Dictionary<string, RectangleF> caixas = null)
        {
            c ??= cfg;
            lock (trava)
            {
                var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float u = W / 100f, px = W / 960f;
                var P = PaletaUsada(c); bool claro = Luz(P.F1) > .62f;
                var L = Layout(c.Modelo); var LT = LetraDe(c);
                // o desenho do tema fica no terço da direita: no "Só letras", o texto vai para os dois terços da esquerda
                if (c.Modelo == "letras" && c.Fundo == "tema") { L.tL = .05f; L.tW = .6f; L.tit = 9.4f; }
                Color suave = claro ? Color.FromArgb(173, 58, 46, 22) : Color.FromArgb(184, 255, 255, 255);
                Color caixa = claro ? Color.FromArgb(31, 160, 122, 40) : Color.FromArgb(56, 0, 0, 0);
                Color fio = claro ? Color.FromArgb(89, 160, 122, 40) : Color.FromArgb(36, 255, 255, 255);
                Color tintaAcento = Luz(P.Acento) > .55f ? Hex("#1d1405") : Hex("#fffdf7");
                var foto = ImagemDe(c);
                Color tinta = P.Tinta;

                // fundo (desenhado uma vez por combinação e guardado: a tela de espera redesenha a cada segundo)
                string fundo = c.Modelo == "cartao" && foto != null ? "foto" : (c.Fundo ?? "padrao");
                g.DrawImage(FundoBmp(c, P, fundo, foto, W, H), 0, 0, W, H);

                // imagem (ilustração do folheto, foto ou o símbolo)
                if (L.forma != "")
                {
                    bool cheia = c.Modelo == "faixa" || c.Modelo == "dividida";
                    float iw = L.iW * W, ih = L.iH < 0 ? iw : L.iH * H, ix = L.iL * W, iy = L.iT * H;
                    var aI = AjDe(c, "imagem");
                    if (!aI.Nada) { ix += aI.dx * W; iy += aI.dy * H; iw *= aI.s; ih *= aI.s; }
                    var caixaImg = new RectangleF(ix, iy, iw, ih);
                    if (caixas != null) caixas["imagem"] = caixaImg;
                    if (cheia && foto == null)
                    {
                        // sem foto: o símbolo num canto livre, sem cobrir o texto
                        var r = c.Modelo == "faixa" ? Tr(new RectangleF(.66f * W, .1f * H, .28f * W, .8f * H), .66f * W, .1f * H, aI, W, H) : caixaImg;
                        if (caixas != null) caixas["imagem"] = r;
                        if (c.Modelo == "dividida") using (var lg = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), P.I1, P.I2, 70f)) g.FillRectangle(lg, r);
                        Simbolo(g, r, P.Acento, null, u);
                    }
                    else
                    {
                        using var forma = Forma(L.forma, caixaImg, u);
                        if (L.forma is "arco" or "circulo" or "ogiva")
                        {
                            float off = L.forma == "circulo" ? .5f * u : u;
                            using var fora = Forma(L.forma, RectangleF.Inflate(caixaImg, off, off), u, off);
                            using var pen = new Pen(P.Acento, (L.forma == "circulo" ? 2f : 1.5f) * px);
                            if (c.Modelo == "padroeiro") Raios(g, caixaImg, P.Acento, u);
                            g.DrawPath(pen, fora);
                            if (L.forma == "ogiva") { using var fora2 = Forma("ogiva", RectangleF.Inflate(caixaImg, 2 * off, 2 * off), u, 2 * off); using var p2 = new Pen(Color.FromArgb(120, P.Acento), px); g.DrawPath(p2, fora2); }
                        }
                        var st = g.Save(); g.SetClip(forma);
                        using (var lg = new LinearGradientBrush(RectangleF.Inflate(caixaImg, 1, 1), P.I1, P.I2, 70f)) g.FillRectangle(lg, caixaImg);
                        if (foto != null) Cobrir(g, foto, caixaImg, L);
                        else Simbolo(g, caixaImg, P.Acento, c.Imagem == "simbolo" ? null : "Escolha a imagem nos ajustes", u);
                        if (c.Modelo == "vitral") Vitral(g, caixaImg, P, u, px);
                        g.Restore(st);
                    }
                    if (L.fade)
                    {
                        var r = new RectangleF(ix, 0, W - ix, H);
                        using var fb = new LinearGradientBrush(new PointF(ix - 1, 0), new PointF(ix + .45f * r.Width + 2, 0), P.F1, Color.FromArgb(0, P.F1)) { WrapMode = WrapMode.TileFlipX };
                        g.FillRectangle(fb, ix, 0, .45f * r.Width + 1, H);
                    }
                    if (c.Modelo == "faixa" && foto != null)
                    {
                        // véu da cor embaixo, onde vai o texto (a foto continua aparecendo em cima)
                        var r = new RectangleF(0, .12f * H, W, .88f * H + 1);
                        var vc = claro ? P.F0 : P.F2;   // véu claro na cor branca (texto escuro), escuro nas outras
                        using var vb = new LinearGradientBrush(r, Color.FromArgb(0, vc), Color.FromArgb(250, vc), 90f);
                        vb.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, vc), Color.FromArgb(190, vc), Color.FromArgb(240, vc), Color.FromArgb(250, vc) }, Positions = new[] { 0f, .3f, .55f, 1f } };
                        g.FillRectangle(vb, r);
                        using var lado = new LinearGradientBrush(new RectangleF(0, 0, .8f * W, H), Color.FromArgb(200, vc), Color.FromArgb(0, vc), 0f);
                        lado.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(215, vc), Color.FromArgb(150, vc), Color.FromArgb(0, vc) }, Positions = new[] { 0f, .55f, 1f } };
                        g.FillRectangle(lado, 0, 0, .8f * W, H);
                    }
                    if (c.Modelo == "dividida") using (var pd = new Pen(P.Acento, .35f * u)) g.DrawLine(pd, iw + .17f * u, 0, iw + .17f * u, H);
                    if (c.Modelo == "padroeiro" && !string.IsNullOrWhiteSpace(c.Padroeiro))
                        using (var fp = Fonte(LT.Tit, 2.3f * u * Math.Min(LT.TitEscala, 1.15f), LT.TitEstilo, true)) using (var bp = new SolidBrush(P.Acento))
                            Bloco(g, c.Padroeiro.Trim(), fp, bp, ix - 4 * u, iy + ih + 2.2f * u, iw + 8 * u, true, 1.1f);
                }
                if (c.Modelo == "cartao")
                {
                    var r = new RectangleF(.16f * W, .09f * H, .68f * W, .82f * H);
                    using (var sombra = Arred(new RectangleF(r.X + .4f * u, r.Y + .6f * u, r.Width, r.Height), 1.6f * u)) using (var bs = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillPath(bs, sombra);
                    using (var card = Arred(r, 1.6f * u)) using (var bc = new SolidBrush(Color.FromArgb(foto != null ? 222 : 150, P.F1))) using (var pc = new Pen(Color.FromArgb(200, P.Acento), 1.5f * px))
                    { g.FillPath(bc, card); g.DrawPath(pc, card); }
                }

                // textos: eyebrow, título, divisor, celebração, linha extra, chamada, caixa da data, paróquia, marcas e celebrante.
                // Nos modelos com âncora no meio ou embaixo, primeiro mede (sem desenhar) e depois desenha na altura certa.
                bool fim = modo == "fim", espera = modo == "espera";
                var now = agora ?? DateTime.Now;
                float x0 = L.tL * W, tw = L.tW * W;
                bool marcasNoTexto = (c.MarcasPos ?? "texto") == "texto";
                float esc = (float)Math.Clamp(c.MarcasTam <= 0 ? 1 : c.MarcasTam, .5, 2);
                var marcas = new List<Bitmap>();
                if (c.UsarMarca1 && Img(c.Marca1) is Bitmap m1) marcas.Add(m1);
                if (c.UsarMarca2 && Img(c.Marca2) is Bitmap m2) marcas.Add(m2);
                Aj aTx = AjDe(c, "textos"), aTit = AjDe(c, "titulo"), aM = AjDe(c, "marcas");
                bool medindo = false;

                float Textos(float y)
                {
                    float yBloco = y;
                    string eyebrow = fim ? "Obrigado por participar" : espera ? "Em instantes, ao vivo" : "Transmissão ao vivo";
                    using var fEb = Fonte(LT.Forte, 1.15f * u, LT.ForteEstilo);
                    using var bAc = new SolidBrush(P.Acento); using var bTi = new SolidBrush(tinta); using var bSu = new SolidBrush(suave);
                    y += Espacado(g, eyebrow.ToUpperInvariant(), fEb, bAc, x0, y, tw, L.centro, .32f) + 1.3f * u;

                    float tam = L.tit * u * LT.TitEscala; var titulo = string.IsNullOrWhiteSpace(c.Titulo) ? "Santa Missa" : c.Titulo.Trim();
                    if (LT.TitMaiusc) titulo = titulo.ToUpper(br);
                    Font fTit = Fonte(LT.Tit, tam, LT.TitEstilo, true);
                    while (g.MeasureString(titulo, fTit, PointF.Empty, StringFormat.GenericTypographic).Width > tw && tam > L.tit * u * LT.TitEscala * .55f)
                    { fTit.Dispose(); tam *= .94f; fTit = Fonte(LT.Tit, tam, LT.TitEstilo, true); }
                    float lhTit = LT.Tit == "Gabriola" ? .78f : .95f;
                    {
                        // (0.1.19) o título pode ser movido e mudar de tamanho à parte; o resto continua no lugar dele
                        float oxT = L.centro ? x0 + tw / 2 : x0, yTit = y;
                        var stT = g.Save(); Transformar(g, oxT, yTit, aTit, W, H);
                        float hT = Bloco(g, titulo, fTit, bTi, x0, y - .08f * tam, tw, L.centro, lhTit) - .08f * tam;
                        g.Restore(stT);
                        if (caixas != null && !medindo)
                            caixas["titulo"] = Tr(Tr(new RectangleF(x0, yTit, tw, Math.Max(hT, u)), oxT, yTit, aTit, W, H), x0, yBloco, aTx, W, H);
                        y += hT * aTit.s;
                    }
                    fTit.Dispose();

                    // divisor
                    if (c.Divisor != "nenhum")
                    {
                        y += 1.5f * u;
                        Divisor(g, c.Divisor ?? "cruz", P.Acento, bAc, x0, y, tw, L.centro, u, px);
                        y += 1.8f * u + 1.3f * u;
                    }
                    else y += 1.6f * u;

                    using (var fCel = Fonte(LT.Forte, 2.7f * u, LT.ForteEstilo)) y += Bloco(g, CelebracaoUsada(c), fCel, bTi, x0, y, tw, L.centro, 1.15f);
                    if (!string.IsNullOrWhiteSpace(c.Extra))
                        using (var fEx = Fonte(LT.Tit, 2.2f * u * Math.Min(LT.TitEscala, 1.15f), LT.TitEstilo, true))
                            y += .6f * u + Bloco(g, c.Extra.Trim(), fEx, bAc, x0, y + .6f * u, tw, L.centro, 1.2f);
                    string chamada = fim && string.IsNullOrWhiteSpace(c.Proxima) ? "Deus abençoe a sua semana" : L.chamada ? c.Chamada : null;
                    if (!string.IsNullOrWhiteSpace(chamada))
                        using (var fCh = Fonte(LT.Corpo, 1.35f * u, FontStyle.Regular)) y += 1.2f * u + Bloco(g, chamada.Trim(), fCh, bSu, x0, y + 1.2f * u, tw, L.centro, 1.4f);

                    // caixa com a data e a hora
                    if (!(fim && string.IsNullOrWhiteSpace(c.Proxima)))
                    {
                        y += 2.6f * u;
                        string rot = fim ? "PRÓXIMA MISSA" : "AO VIVO", data = fim ? c.Proxima.Trim() : DataLonga(c.Quando), hora = fim ? "" : Hora(c.Quando);
                        string cont = null;
                        if (espera)
                        {
                            var falta = c.Quando - now;
                            cont = falta.TotalSeconds <= 0 ? "JÁ VAI COMEÇAR"
                                 : falta.TotalDays >= 1 ? (falta.Days == 1 ? "FALTA 1 DIA" : "FALTAM " + falta.Days + " DIAS") + (falta.Hours > 0 ? " E " + falta.Hours + "H" : "")
                                 : falta.TotalHours >= 1 ? "FALTAM " + (int)falta.TotalHours + "H" + falta.Minutes.ToString("00")
                                 : "FALTAM " + falta.Minutes + ":" + falta.Seconds.ToString("00");
                        }
                        var sf = StringFormat.GenericTypographic;
                        using var fRot = Fonte(LT.Corpo, 1.15f * u, FontStyle.Regular);
                        using var fHora = Fonte(LT.Corpo, 2.7f * u, FontStyle.Bold); using var fCont = Fonte(LT.Forte, 1.05f * u, LT.ForteEstilo);
                        float wHora = hora == "" ? 0 : g.MeasureString(hora, fHora, PointF.Empty, sf).Width, wCont = cont == null ? 0 : LarguraEspacada(g, cont, fCont, .1f);
                        float wDir = hora == "" ? 0 : Math.Max(wHora, wCont) + 3.4f * u;
                        // a data diminui (até 70%) para caber ao lado da hora, sem passar por baixo dela
                        float tData = 1.9f * u, wRot = LarguraEspacada(g, rot, fRot, .14f);
                        var fData = Fonte(LT.Forte, tData, LT.ForteEstilo);
                        float wData = g.MeasureString(data, fData, PointF.Empty, sf).Width;
                        while (Math.Max(wRot, wData) + 3.4f * u + wDir > tw && tData > 1.9f * u * .7f)
                        { fData.Dispose(); tData *= .95f; fData = Fonte(LT.Forte, tData, LT.ForteEstilo); wData = g.MeasureString(data, fData, PointF.Empty, sf).Width; }
                        float wEsq = Math.Max(wRot, wData) + 3.4f * u, hEsq = 2.5f * u + 1.15f * u * 1.25f + .3f * u + tData * 1.25f;
                        // hora + contagem: a hora (2,7u) e a contagem logo abaixo, com folga para não encostar
                        float hh = 2.7f * u * 1.2f + (cont != null ? .35f * u + 1.05f * u * 1.2f : 0);
                        float wBox = L.esticar ? Math.Max(tw, wEsq + wDir) : wEsq + wDir, hBox = Math.Max(hEsq, hh + 2.2f * u), bx = L.centro ? x0 + (tw - wBox) / 2 : x0;
                        using (var cx = Arred(new RectangleF(bx, y, wBox, hBox), 1.25f * u))
                        {
                            var st = g.Save(); g.SetClip(cx, CombineMode.Intersect);
                            using (var bc = new SolidBrush(caixa)) g.FillRectangle(bc, bx, y, wBox, hBox);
                            if (wDir > 0) using (var ba = new SolidBrush(P.Acento)) g.FillRectangle(ba, bx + wBox - wDir, y, wDir, hBox);
                            g.Restore(st);
                        }
                        float ty = y + (hBox - hEsq) / 2;
                        Espacado(g, rot, fRot, bSu, bx + 1.7f * u, ty + 1.25f * u, 0, false, .14f);
                        g.DrawString(data, fData, bTi, bx + 1.7f * u, ty + 1.25f * u + 1.15f * u * 1.25f + .3f * u, sf);
                        fData.Dispose();
                        if (wDir > 0)
                        {
                            using var bTa = new SolidBrush(tintaAcento);
                            float hx = bx + wBox - wDir, hy = y + (hBox - hh) / 2;
                            g.DrawString(hora, fHora, bTa, hx + (wDir - wHora) / 2, hy, sf);
                            if (cont != null) Espacado(g, cont, fCont, bTa, hx + (wDir - wCont) / 2, hy + 2.7f * u * 1.2f + .35f * u, 0, false, .1f);
                        }
                        y += hBox;
                    }

                    // nome da paróquia
                    var par = c.MostrarParoquia ? ParoquiaUsada(c) : "";
                    if (par != "")
                    {
                        y += 2.2f * u;
                        using var fPa = Fonte(LT.Forte, 1.2f * u, LT.ForteEstilo);
                        var s = par.ToUpper(br);
                        while (LarguraEspacada(g, s, fPa, .2f) > tw && s.Length > 10) s = s.Substring(0, s.Length - 2).TrimEnd() + "…";
                        y += Espacado(g, s, fPa, bSu, x0, y, tw, L.centro, .2f) - .2f * u;
                    }

                    // marcas e celebrante
                    var mm = marcasNoTexto ? marcas : new List<Bitmap>();
                    bool temCeleb = !string.IsNullOrWhiteSpace(c.Celebrante);
                    if (mm.Count > 0 || temCeleb)
                    {
                        y += 2.6f * u;
                        float lh = 4.8f * u * esc * aM.s, wPlaca = 0;
                        foreach (var m in mm) wPlaca += lh * m.Width / m.Height;
                        if (mm.Count > 0) wPlaca += 2 * u + (mm.Count - 1) * u;
                        using var fCe = Fonte(LT.Corpo, 1.35f * u, FontStyle.Regular); using var fCeB = Fonte(LT.Forte, 1.35f * u, LT.ForteEstilo);
                        float wCel = temCeleb ? Math.Max(g.MeasureString("Celebrante", fCe).Width, g.MeasureString(c.Celebrante.Trim(), fCeB).Width) : 0;
                        float total = wPlaca + (mm.Count > 0 && temCeleb ? 1.5f * u : 0) + wCel, hx = L.centro ? x0 + (tw - total) / 2 : x0, hPl = lh + 1.2f * u;
                        if (mm.Count > 0)
                        {
                            Placa(g, mm, hx + aM.dx * W, y + aM.dy * H, lh, u);
                            if (caixas != null && !medindo) caixas["marcas"] = Tr(new RectangleF(hx + aM.dx * W, y + aM.dy * H, wPlaca, hPl), x0, yBloco, aTx, W, H);
                            hx += wPlaca + 1.5f * u;
                        }
                        if (temCeleb)
                        {
                            float cy = y + (mm.Count > 0 ? hPl : 3.6f * u) / 2 - 1.35f * u * 1.3f;
                            g.DrawString("Celebrante", fCe, bSu, hx, cy);
                            g.DrawString(c.Celebrante.Trim(), fCeB, bTi, hx, cy + 1.35f * u * 1.3f);
                        }
                        y += mm.Count > 0 ? hPl : 3.6f * u;
                    }
                    return y;
                }

                float y0 = L.tT * H, alto = 0;
                if (L.ancora != "topo" || caixas != null)
                {
                    var st = g.Save(); g.SetClip(new Rectangle(0, 0, 0, 0)); medindo = true;
                    alto = Textos(0);
                    medindo = false; g.Restore(st);
                }
                if (L.ancora != "topo")
                {
                    float livre = (L.tB - L.tT) * H;
                    y0 = L.ancora == "baixo" ? L.tB * H - alto : L.tT * H + (livre - alto) / 2;
                    y0 = Math.Max(L.tT * H * (L.ancora == "baixo" ? .3f : 1f), y0);
                }
                {
                    // (0.1.19) o bloco de textos inteiro pode ser movido e mudar de tamanho (a partir do canto de cima, à esquerda)
                    var stB = g.Save(); Transformar(g, x0, y0, aTx, W, H);
                    Textos(y0);
                    g.Restore(stB);
                    if (caixas != null) caixas["textos"] = Tr(new RectangleF(x0, y0, tw, Math.Max(alto, u)), x0, y0, aTx, W, H);
                }

                // marcas num canto
                if (!marcasNoTexto && marcas.Count > 0)
                {
                    float lh = 3.6f * u * esc * aM.s, wPlaca = 2 * u + (marcas.Count - 1) * u;
                    foreach (var m in marcas) wPlaca += lh * m.Width / m.Height;
                    float hPl = lh + 1.2f * u, mx = 3.2f * u, my = 3.2f * u;
                    string pos = c.MarcasPos;
                    float px0 = (pos.EndsWith("dir") ? W - mx - wPlaca : mx) + aM.dx * W, py0 = (pos.StartsWith("inf") ? H - my - hPl : my) + aM.dy * H;
                    Placa(g, marcas, px0, py0, lh, u);
                    if (caixas != null) caixas["marcas"] = new RectangleF(px0, py0, wPlaca, hPl);
                }

                // moldura por cima de tudo
                Moldura(g, c.Moldura ?? "fio", fio, P.Acento, W, H, u, px);
                return bmp;
            }
        }

        public static byte[] Png(string modo, int W = 1920, int H = 1080)
        {
            using var b = Desenhar(modo, W, H); using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray();
        }
        public static string JpegBase64(string modo, CapaCfg c, int W = 960, int H = 540) => Convert.ToBase64String(Jpeg(modo, W, H, c));
        /// <summary>(0.1.19) a prévia e onde ficou cada item ajustável, em fração da tela: [x, y, largura, altura].</summary>
        public static string JpegBase64(string modo, CapaCfg c, out Dictionary<string, double[]> caixas, int W = 960, int H = 540)
        {
            var px = new Dictionary<string, RectangleF>();
            using var b = Desenhar(modo, W, H, c, null, px); using var ms = new MemoryStream();
            var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            b.Save(ms, enc, ps);
            caixas = px.ToDictionary(k => k.Key, k => new[] { Math.Round(k.Value.X / W, 4), Math.Round(k.Value.Y / H, 4), Math.Round(k.Value.Width / W, 4), Math.Round(k.Value.Height / H, 4) });
            return Convert.ToBase64String(ms.ToArray());
        }
        // (0.0.8) miniatura do YouTube: JPEG 1280×720 (o limite de lá é 2 MB; fica em ~200–400 KB)
        public static byte[] Jpeg(string modo, int W = 1280, int H = 720, CapaCfg c = null, long qualidade = 90)
        {
            using var b = Desenhar(modo, W, H, c); using var ms = new MemoryStream();
            var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qualidade);
            b.Save(ms, enc, ps); return ms.ToArray();
        }

        // ---------- fundos (backlog 8.3) ----------
        static readonly Dictionary<string, Bitmap> fundos = new Dictionary<string, Bitmap>();
        static Bitmap FundoBmp(CapaCfg c, Paleta P, string fundo, Bitmap foto, int W, int H)
        {
            string tema = fundo == "tema" ? Temas.DoDia(c.Quando) : ""; int velas = fundo == "tema" ? Temas.VelasAdvento(c.Quando) : 0;
            string fotoId = fundo == "foto" && foto != null ? (c.Imagem == "folheto" ? c.Folheto : c.Foto) + File.GetLastWriteTimeUtc(c.Imagem == "folheto" ? c.Folheto : c.Foto).Ticks : "";
            var chave = $"{W}x{H}|{fundo}|{P.F0.ToArgb()}|{P.F1.ToArgb()}|{P.F2.ToArgb()}|{P.Acento.ToArgb()}|{tema}|{velas}|{fotoId}";
            if (fundos.TryGetValue(chave, out var pronto)) return pronto;
            if (fundos.Count > 10) { foreach (var b in fundos.Values) b.Dispose(); fundos.Clear(); }
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float px = W / 960f;
                switch (fundo)
                {
                    case "liso": g.Clear(P.F1); break;
                    case "degrade": Degrade(g, P, W, H); break;
                    case "tema":
                        Degrade(g, P, W, H);
                        { var st = g.Save(); g.ScaleTransform(W / 1920f, H / 1080f); Temas.Desenhar(g, tema, P, velas); g.Restore(st); }
                        break;
                    case "foto":
                        if (foto != null) Desfocada(g, foto, P, W, H); else Padrao(g, P, W, H, px);
                        break;
                    case "pergaminho": Pergaminho(g, P, W, H); break;
                    case "linho": Degrade(g, P, W, H); Linho(g, P, W, H, px); break;
                    case "pedra": Pedra(g, P, W, H); break;
                    case "madeira": Madeira(g, P, W, H); break;
                    default: Padrao(g, P, W, H, px); break;
                }
            }
            fundos[chave] = bmp; return bmp;
        }
        // o fundo de antes: gradiente radial (120% 100% em 15% 0%) e a textura de riscos finos
        static void Padrao(Graphics g, Paleta P, int W, int H, float px)
        {
            g.Clear(P.F2);
            using (var cam = new GraphicsPath())
            {
                cam.AddEllipse(.15f * W - 1.2f * W, -H, 2.4f * W, 2f * H);
                using var pb = new PathGradientBrush(cam) { CenterPoint = new PointF(.15f * W, 0), CenterColor = P.F0 };
                pb.InterpolationColors = new ColorBlend { Colors = new[] { P.F2, P.F1, P.F0 }, Positions = new[] { 0f, 1 - P.P1, 1f } };
                g.FillPath(pb, cam);
            }
            using var risco = new Pen(Color.FromArgb(4, 255, 255, 255), 2 * px);
            var st = g.Save(); g.TranslateTransform(W / 2f, H / 2f); g.RotateTransform(25);
            float R = W + H; for (float x = -R; x < R; x += 9 * px) g.DrawLine(risco, x, -R, x, R);
            g.Restore(st);
        }
        static void Degrade(Graphics g, Paleta P, int W, int H)
        {
            using var lg = new LinearGradientBrush(new RectangleF(0, 0, W, H + 1), P.F0, P.F2, 70f);
            lg.InterpolationColors = new ColorBlend { Colors = new[] { P.F0, P.F1, P.F2 }, Positions = new[] { 0f, .5f, 1f } };
            g.FillRectangle(lg, 0, 0, W, H + 1);
        }
        static void Vinheta(Graphics g, int W, int H, int alfa)
        {
            using var cam = new GraphicsPath(); cam.AddEllipse(-.25f * W, -.35f * H, 1.5f * W, 1.7f * H);
            using var pb = new PathGradientBrush(cam) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(alfa, 0, 0, 0) } };
            pb.FocusScales = new PointF(.55f, .5f);
            g.FillPath(pb, cam);
        }
        // ruído desenhado pequeno e ampliado: manchas suaves (base do pergaminho e da pedra)
        static void Manchas(Graphics g, int W, int H, int semente, int div, Color a, Color b, int alfa)
        {
            int w = Math.Max(8, W / div), h = Math.Max(5, H / div); var r = new Random(semente);
            using var peq = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var t = (float)r.NextDouble(); peq.SetPixel(x, y, Color.FromArgb(alfa, Mistura(a, b, t))); }
            var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var at = new ImageAttributes(); at.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(peq, new Rectangle(0, 0, W, H), 0, 0, w, h, GraphicsUnit.Pixel, at);
            g.InterpolationMode = im;
        }
        static void Pergaminho(Graphics g, Paleta P, int W, int H)
        {
            Degrade(g, P, W, H);
            Manchas(g, W, H, 7, 60, Mistura(P.F0, Color.White, .35f), P.F2, 70);
            Manchas(g, W, H, 11, 14, Mistura(P.F0, Color.White, .2f), Mistura(P.F2, Color.Black, .2f), 40);
            // fibras finas
            var r = new Random(5);
            for (int i = 0; i < 260; i++)
            {
                float x = (float)r.NextDouble() * W, y = (float)r.NextDouble() * H, l = (float)(.02 + r.NextDouble() * .06) * W, a = (float)(r.NextDouble() * 30 - 15);
                using var p = new Pen(Color.FromArgb(r.Next(6, 16), r.Next(2) == 0 ? Color.White : Color.Black), Math.Max(1, W / 1400f));
                var st = g.Save(); g.TranslateTransform(x, y); g.RotateTransform(a); g.DrawLine(p, 0, 0, l, 0); g.Restore(st);
            }
            Vinheta(g, W, H, 120);
        }
        static void Linho(Graphics g, Paleta P, int W, int H, float px)
        {
            var r = new Random(3); float passo = Math.Max(2, 3 * px);
            for (float x = 0; x < W; x += passo) using (var p = new Pen(Color.FromArgb(r.Next(5, 15), r.Next(3) == 0 ? Color.Black : Color.White), Math.Max(1, px * .9f))) g.DrawLine(p, x, 0, x, H);
            for (float y = 0; y < H; y += passo) using (var p = new Pen(Color.FromArgb(r.Next(5, 15), r.Next(3) == 0 ? Color.Black : Color.White), Math.Max(1, px * .9f))) g.DrawLine(p, 0, y, W, y);
            // fios mais grossos de vez em quando (o tecido não é perfeito)
            for (int i = 0; i < 40; i++)
            {
                bool vert = r.Next(2) == 0; float k = (float)r.NextDouble() * (vert ? W : H);
                using var p = new Pen(Color.FromArgb(r.Next(10, 22), Color.White), Math.Max(1, 2 * px));
                if (vert) g.DrawLine(p, k, 0, k, H); else g.DrawLine(p, 0, k, W, k);
            }
            Vinheta(g, W, H, 90);
        }
        static void Pedra(Graphics g, Paleta P, int W, int H)
        {
            g.Clear(P.F1);
            Manchas(g, W, H, 13, 90, Mistura(P.F0, Color.White, .1f), Mistura(P.F2, Color.Black, .1f), 150);
            Manchas(g, W, H, 17, 24, P.F0, P.F2, 70);
            // blocos de pedra: fiadas desencontradas, junta escura com um filete de luz embaixo
            float fiada = H / 6.5f, u = W / 100f; var r = new Random(21);
            using var junta = new Pen(Color.FromArgb(90, Mistura(P.F2, Color.Black, .5f)), Math.Max(1, W / 700f));
            using var luz = new Pen(Color.FromArgb(28, Color.White), Math.Max(1, W / 1400f));
            int linha = 0;
            for (float y = 0; y < H + fiada; y += fiada, linha++)
            {
                g.DrawLine(junta, 0, y, W, y); g.DrawLine(luz, 0, y + W / 900f, W, y + W / 900f);
                float largura = fiada * 2.1f, x = linha % 2 == 0 ? 0 : -largura / 2;
                for (; x < W; x += largura * (float)(.8 + r.NextDouble() * .5)) { g.DrawLine(junta, x, y, x, y + fiada); g.DrawLine(luz, x + W / 900f, y, x + W / 900f, y + fiada); }
            }
            Vinheta(g, W, H, 140);
        }
        static void Madeira(Graphics g, Paleta P, int W, int H)
        {
            Degrade(g, P, W, H);
            int tabuas = 7; float alt = H / (float)tabuas; var r = new Random(9);
            for (int t = 0; t < tabuas; t++)
            {
                float y0 = t * alt;
                using (var tb = new SolidBrush(Color.FromArgb(r.Next(0, 26), r.Next(2) == 0 ? Color.Black : Color.White))) g.FillRectangle(tb, 0, y0, W, alt);
                // veios: linhas onduladas
                for (int v = 0; v < 14; v++)
                {
                    float yb = y0 + (float)r.NextDouble() * alt, amp = (float)(r.NextDouble() * .12 + .02) * alt, fase = (float)(r.NextDouble() * 6.28), per = (float)(.15 + r.NextDouble() * .4) * W;
                    var pts = new List<PointF>();
                    for (float x = 0; x <= W + 20; x += W / 60f) pts.Add(new PointF(x, Math.Clamp(yb + amp * (float)Math.Sin(x / per * 6.28 + fase), y0 + 1, y0 + alt - 1)));
                    using var p = new Pen(Color.FromArgb(r.Next(10, 26), Mistura(P.F2, Color.Black, .4f)), Math.Max(1, W / (900f + r.Next(900))));
                    g.DrawCurve(p, pts.ToArray(), .4f);
                }
                using var junta = new Pen(Color.FromArgb(110, Mistura(P.F2, Color.Black, .55f)), Math.Max(1, W / 640f));
                g.DrawLine(junta, 0, y0, W, y0);
                using var luz = new Pen(Color.FromArgb(24, Color.White), Math.Max(1, W / 1400f));
                g.DrawLine(luz, 0, y0 + W / 800f, W, y0 + W / 800f);
            }
            Vinheta(g, W, H, 150);
        }
        // a foto escolhida, bem desfocada (desenhada pequena e ampliada) e com o véu da cor por cima
        static void Desfocada(Graphics g, Bitmap foto, Paleta P, int W, int H)
        {
            int w = Math.Max(16, W / 28), h = Math.Max(9, H / 28);
            using (var peq = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var gp = Graphics.FromImage(peq))
                {
                    gp.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float e = Math.Max((float)w / foto.Width, (float)h / foto.Height), fw = foto.Width * e, fh = foto.Height * e;
                    gp.DrawImage(foto, (w - fw) / 2, (h - fh) / 2, fw, fh);
                }
                using var at = new ImageAttributes(); at.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(peq, new Rectangle(0, 0, W, H), 0, 0, w, h, GraphicsUnit.Pixel, at);
            }
            using (var veu = new SolidBrush(Color.FromArgb(150, P.F2))) g.FillRectangle(veu, 0, 0, W, H);
            using (var lg = new LinearGradientBrush(new RectangleF(0, 0, W, H + 1), Color.FromArgb(60, P.F0), Color.FromArgb(120, P.F2), 90f)) g.FillRectangle(lg, 0, 0, W, H + 1);
            Vinheta(g, W, H, 110);
        }

        // ---------- enfeites (backlog 8.5) ----------
        static void Divisor(Graphics g, string tipo, Color acento, Brush bAc, float x0, float y, float tw, bool centro, float u, float px)
        {
            using var fCruz = new Font("Segoe UI Symbol", 1.5f * u, GraphicsUnit.Pixel);
            using var pDiv = new Pen(Color.FromArgb(178, acento), Math.Max(1, px));
            float cw = g.MeasureString("✠", fCruz, PointF.Empty, StringFormat.GenericTypographic).Width, wl = 8 * u, gap = u;
            float total = wl * 2 + gap * 2 + cw, dx = centro ? x0 + (tw - total) / 2 : x0, my = y + .9f * u;
            switch (tipo)
            {
                case "filete":
                    {
                        float l = 12 * u, fx = centro ? x0 + (tw - l) / 2 : x0;
                        using var p2 = new Pen(Color.FromArgb(200, acento), Math.Max(1, 1.4f * px));
                        g.DrawLine(p2, fx, my, fx + l, my);
                        break;
                    }
                case "ramos":
                    g.DrawString("✠", fCruz, bAc, dx + wl + gap, y, StringFormat.GenericTypographic);
                    Ramo(g, dx + wl + gap * .4f, my, wl * .95f, -1, acento, u, px);
                    Ramo(g, dx + wl + gap * 1.6f + cw, my, wl * .95f, +1, acento, u, px);
                    break;
                default:
                    g.DrawLine(pDiv, dx, my, dx + wl, my);
                    g.DrawString("✠", fCruz, bAc, dx + wl + gap, y, StringFormat.GenericTypographic);
                    g.DrawLine(pDiv, dx + wl + gap * 2 + cw, my, dx + total, my);
                    break;
            }
        }
        // ramo de folhas saindo de (x, y): dir -1 para a esquerda, +1 para a direita
        static void Ramo(Graphics g, float x, float y, float comp, int dir, Color cor, float u, float px)
        {
            int n = 8; var pts = new PointF[n + 1];
            for (int i = 0; i <= n; i++) { float t = i / (float)n; pts[i] = new PointF(x + dir * comp * t, y - (float)Math.Sin(t * Math.PI) * .45f * u + t * t * .25f * u); }
            using var pen = new Pen(Color.FromArgb(200, cor), Math.Max(1, .9f * px)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawCurve(pen, pts);
            using var b = new SolidBrush(Color.FromArgb(215, cor));
            for (int i = 1; i <= n; i++)
            {
                var p = pts[i]; float tam = (1.15f - i / (float)n * .55f) * u;
                foreach (int s in new[] { -1, 1 })
                {
                    var st = g.Save(); g.TranslateTransform(p.X, p.Y);
                    g.RotateTransform(dir > 0 ? s * 38 : 180 - s * 38);
                    g.FillEllipse(b, 0, -.2f * tam, tam, .4f * tam);
                    g.Restore(st);
                }
            }
        }
        static void Moldura(Graphics g, string tipo, Color fio, Color acento, int W, int H, float u, float px)
        {
            switch (tipo)
            {
                case "nenhuma": return;
                case "dupla":
                    using (var a = Arred(RectangleF.Inflate(new RectangleF(0, 0, W, H), -1.3f * u, -1.3f * u), .5f * u)) using (var p = new Pen(Color.FromArgb(150, acento), Math.Max(1, 1.2f * px))) g.DrawPath(p, a);
                    using (var b = Arred(RectangleF.Inflate(new RectangleF(0, 0, W, H), -1.9f * u, -1.9f * u), .4f * u)) using (var p = new Pen(Color.FromArgb(90, acento), Math.Max(1, .8f * px))) g.DrawPath(p, b);
                    return;
                case "cantos":
                    {
                        float m = 1.8f * u, l = 7 * u, d = .55f * u;
                        using var p = new Pen(Color.FromArgb(200, acento), Math.Max(1, 1.4f * px));
                        using var bd = new SolidBrush(Color.FromArgb(220, acento));
                        foreach (var (cx, cy, sx, sy) in new[] { (m, m, 1, 1), (W - m, m, -1, 1), (m, H - m, 1, -1), (W - m, H - m, -1, -1) })
                        {
                            g.DrawLine(p, cx, cy, cx + sx * l, cy); g.DrawLine(p, cx, cy, cx, cy + sy * l);
                            g.DrawLine(p, cx + sx * .9f * u, cy + sy * .9f * u, cx + sx * l * .55f, cy + sy * .9f * u);
                            g.DrawLine(p, cx + sx * .9f * u, cy + sy * .9f * u, cx + sx * .9f * u, cy + sy * l * .55f);
                            g.FillPolygon(bd, new[] { new PointF(cx, cy - d), new PointF(cx + d, cy), new PointF(cx, cy + d), new PointF(cx - d, cy) });
                        }
                        return;
                    }
                default:
                    using (var mold = Arred(RectangleF.Inflate(new RectangleF(0, 0, W, H), -1.45f * u, -1.45f * u), .6f * u))
                    using (var pf = new Pen(fio, Math.Max(1, px))) g.DrawPath(pf, mold);
                    return;
            }
        }
        // raios atrás do medalhão do padroeiro
        static void Raios(Graphics g, RectangleF r, Color cor, float u)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, r0 = r.Width / 2 + 1.4f * u;
            using var p = new Pen(Color.FromArgb(60, cor), Math.Max(1, .18f * u));
            for (int i = 0; i < 48; i++)
            {
                double a = i * Math.PI * 2 / 48; float r1 = r0 + (i % 2 == 0 ? 4.2f : 2.4f) * u;
                g.DrawLine(p, cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
            }
        }
        // chumbo do vitral: losangos finos por cima da imagem
        static void Vitral(Graphics g, RectangleF r, Paleta P, float u, float px)
        {
            using var p = new Pen(Color.FromArgb(70, Mistura(P.F2, Color.Black, .5f)), Math.Max(1, 1.6f * px));
            using var luz = new Pen(Color.FromArgb(26, Color.White), Math.Max(1, .7f * px));
            float passo = r.Width / 4.2f;
            for (float k = -r.Height; k < r.Width + r.Height; k += passo)
            {
                g.DrawLine(p, r.X + k, r.Y, r.X + k - r.Height * .55f, r.Bottom); g.DrawLine(p, r.X + k, r.Y, r.X + k + r.Height * .55f, r.Bottom);
                g.DrawLine(luz, r.X + k + px, r.Y, r.X + k - r.Height * .55f + px, r.Bottom);
            }
        }
        // placa branca com o brasão e o logo
        static void Placa(Graphics g, List<Bitmap> marcas, float x, float y, float lh, float u)
        {
            float wPlaca = 2 * u + (marcas.Count - 1) * u; foreach (var m in marcas) wPlaca += lh * m.Width / m.Height;
            using (var pl = Arred(new RectangleF(x, y, wPlaca, lh + 1.2f * u), 1.25f * u)) using (var bw = new SolidBrush(Color.FromArgb(240, 255, 255, 255))) g.FillPath(bw, pl);
            float mx = x + u;
            foreach (var m in marcas) { float mw = lh * m.Width / m.Height; g.DrawImage(m, mx, y + .6f * u, mw, lh); mx += mw + u; }
        }

        // ---------- ajudantes de desenho ----------
        static GraphicsPath Arred(RectangleF r, float raio)
        {
            var p = new GraphicsPath(); float d = Math.Min(raio * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        static GraphicsPath Forma(string forma, RectangleF r, float u, float extra = 0)
        {
            if (forma == "circulo") { var p = new GraphicsPath(); p.AddEllipse(r); return p; }
            if (forma == "ogiva")
            {
                // arco gótico: dois arcos que se encontram em ponta no alto
                var o = new GraphicsPath(); float h = r.Width * .9f, top = r.Y, meio = r.X + r.Width / 2;
                o.AddLine(r.X, r.Bottom, r.X, top + h);
                o.AddBezier(r.X, top + h, r.X, top + h * .45f, meio - r.Width * .18f, top + h * .12f, meio, top);
                o.AddBezier(meio, top, meio + r.Width * .18f, top + h * .12f, r.Right, top + h * .45f, r.Right, top + h);
                o.AddLine(r.Right, top + h, r.Right, r.Bottom); o.CloseFigure();
                return o;
            }
            if (forma != "arco") { var p = new GraphicsPath(); p.AddRectangle(r); return p; }
            var a = new GraphicsPath(); float tp = Math.Min(17 * u + extra, r.Width / 2) * 2, bot = (u + extra) * 2;
            a.AddArc(r.X, r.Y, tp, tp, 180, 90); a.AddArc(r.Right - tp, r.Y, tp, tp, 270, 90);
            a.AddArc(r.Right - bot, r.Bottom - bot, bot, bot, 0, 90); a.AddArc(r.X, r.Bottom - bot, bot, bot, 90, 90); a.CloseFigure();
            return a;
        }
        // object-fit: cover, com a posição vertical do modelo e o zoom do medalhão
        static void Cobrir(Graphics g, Bitmap img, RectangleF r, Lay L)
        {
            float esc = Math.Max(r.Width / img.Width, r.Height / img.Height), w = img.Width * esc, h = img.Height * esc;
            float x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) * L.posY;
            if (L.zoom != 1)
            {
                float ox = r.X + r.Width * L.oX, oy = r.Y + r.Height * L.oY;
                x = ox + (x - ox) * L.zoom; y = oy + (y - oy) * L.zoom; w *= L.zoom; h *= L.zoom;
            }
            g.DrawImage(img, x, y, w, h);
        }
        // desenho próprio do Cast: raios, hóstia com cruz e cálice (o mesmo do protótipo)
        static void Simbolo(Graphics g, RectangleF r, Color cor, string aviso, float u)
        {
            float esc = Math.Min(r.Width * .66f / 220, r.Height * .66f / 300), w = 220 * esc, h = 300 * esc;
            var st = g.Save(); g.TranslateTransform(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2); g.ScaleTransform(esc, esc);
            using (var raio = new Pen(Color.FromArgb(140, cor), 1.2f))
                foreach (var (x2, y2) in new[] { (110, 0), (30, 20), (190, 20), (0, 80), (220, 80), (0, 150), (220, 150) }) g.DrawLine(raio, 110, 110, x2, y2);
            using (var hb = new SolidBrush(Hex("#fbf6e8"))) g.FillEllipse(hb, 64, 64, 92, 92);
            using (var p3 = new Pen(cor, 3)) g.DrawEllipse(p3, 64, 64, 92, 92);
            using (var p4 = new Pen(cor, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) { g.DrawLine(p4, 110, 84, 110, 136); g.DrawLine(p4, 88, 104, 132, 104); }
            using (var cb = new SolidBrush(Color.FromArgb(230, cor)))
            using (var calice = new GraphicsPath())
            {
                calice.AddLine(62, 190, 158, 190); calice.AddBezier(158, 190, 154, 201, 152, 207, 148, 218);
                calice.AddBezier(148, 218, 142, 234, 126, 244, 110, 244); calice.AddBezier(110, 244, 94, 244, 78, 234, 72, 218); calice.CloseFigure();
                g.FillPath(cb, calice); g.FillRectangle(cb, 104, 244, 12, 30);
                using var base0 = Arred(new RectangleF(78, 272, 64, 9), 4); g.FillPath(cb, base0);
            }
            g.Restore(st);
            if (aviso != null)
                using (var f = new Font("Segoe UI", 1.2f * u, GraphicsUnit.Pixel)) using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.DrawString(aviso, f, b, new RectangleF(r.X, r.Bottom - 4 * u, r.Width, 3 * u), new StringFormat { Alignment = StringAlignment.Center });
        }
        static float LarguraEspacada(Graphics g, string s, Font f, float em)
        {
            float w = 0; foreach (var ch in s) w += g.MeasureString(ch.ToString(), f, PointF.Empty, StringFormat.GenericTypographic).Width + (ch == ' ' ? f.Size * .28f : 0) + em * f.Size;
            return w - em * f.Size;
        }
        // texto de uma linha com espaço entre as letras; devolve a altura usada
        static float Espacado(Graphics g, string s, Font f, Brush b, float x, float y, float largura, bool centro, float em)
        {
            if (centro) x += (largura - LarguraEspacada(g, s, f, em)) / 2;
            foreach (var ch in s)
            {
                g.DrawString(ch.ToString(), f, b, x, y, StringFormat.GenericTypographic);
                x += g.MeasureString(ch.ToString(), f, PointF.Empty, StringFormat.GenericTypographic).Width + (ch == ' ' ? f.Size * .28f : 0) + em * f.Size;
            }
            return f.Size * 1.25f;
        }
        // parágrafo com quebra de linha dentro da largura; devolve a altura usada
        static float Bloco(Graphics g, string s, Font f, Brush b, float x, float y, float largura, bool centro, float alturaLinha)
        {
            var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = centro ? StringAlignment.Center : StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
            sf.FormatFlags &= ~StringFormatFlags.LineLimit; sf.FormatFlags |= StringFormatFlags.NoClip;   // (0.1.17) letras altas (Gabriola) sumiam inteiras
            var linhas = new List<string>(); var atual = "";
            foreach (var p in s.Split(' '))
            {
                var t = atual == "" ? p : atual + " " + p;
                if (atual != "" && g.MeasureString(t, f, PointF.Empty, sf).Width > largura) { linhas.Add(atual); atual = p; } else atual = t;
            }
            if (atual != "") linhas.Add(atual);
            if (linhas.Count > 3) { linhas = linhas.GetRange(0, 3); linhas[2] += "…"; }
            float lh = f.Size * alturaLinha;
            for (int i = 0; i < linhas.Count; i++)
                g.DrawString(linhas[i], f, b, new RectangleF(x, y + i * lh, largura, lh * 1.4f), sf);
            return lh * linhas.Count;
        }
    }

    // cena com a capa: redesenha quando os ajustes mudam e, na tela de espera, a cada segundo (contagem)
    public sealed class FonteCapa : Fonte
    {
        readonly string modo; Imagem img = new Imagem(16, 9); string feito = ""; int ocupado;
        readonly int L, A;
        public FonteCapa(string modo, int l = 1920, int a = 1080) { this.modo = modo; L = l; A = a; Spec = "capa:" + modo; Estado = "preparando"; }
        public override Imagem Atual()
        {
            var chave = Capa.Versao + (modo == "espera" ? "|" + DateTime.Now.ToString("HHmmss") : "");
            if (chave != feito && Interlocked.CompareExchange(ref ocupado, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        using var b = Capa.Desenhar(modo, L, A);
                        var nova = new Imagem(); FonteImagem.Carregar(b, nova);
                        img = nova; feito = chave; Seq++; Estado = "ok";
                    }
                    catch (Exception e) { Estado = "erro ao desenhar a capa"; Registro.Erro("capa " + modo, e); feito = chave; }
                    finally { Volatile.Write(ref ocupado, 0); }
                });
            }
            return img;
        }
    }
}
