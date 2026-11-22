// (0.1.22) Mistura canal a canal SÓ na aux da live (camada 3 do item 16 do backlog).
// Mexe apenas no ENVIO de cada canal para a aux da live (nunca no fader do canal, no LR, nas outras aux).
// O que faz (cada parte pode ser desligada):
//  a) Instrumentos abaixam quando alguém fala SOZINHO (desligado de fábrica): um canal de FALA (padre, ambão…) com som
//     de verdade por 0,3 s E nenhum cantor do ministério cantando → só os INSTRUMENTOS descem (padrão −8 dB, em ~1 s);
//     1,5 s depois de a fala parar (ou de um cantor entrar), voltam (em ~2 s). As vozes nunca abaixam: o padre canta
//     junto com o ministério e a live não pode parecer que só ele canta (pedido do usuário, 22/11/2026).
//  b) Vozes acima dos instrumentos: com música e sem fala, compara (média de ~8 s) o som das VOZES com o dos
//     INSTRUMENTOS que chega na aux; se os instrumentos estiverem acima de "vozes − 3 dB", desce os instrumentos 0,5 dB
//     a cada 5 s (no máximo −6 dB); se sobrar folga, devolve aos poucos.
//  c) Lembrar o ajuste por ministério: guarda o ajuste dos instrumentos para cada conjunto de canais de música que
//     tocaram (mistura.json) e começa dele da próxima vez que o mesmo conjunto tocar.
// Papel de cada canal: pelo nome dado na mesa (dá para trocar em Ajustes › Som); sem nome = não mexe.
// Respeito: se alguém mexer num envio na mesa, aquele vira o novo ponto de partida do canal. Ao travar, desligar,
// trocar de snapshot ou fechar o Cast, os envios voltam ao ponto de partida.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiguelAngelusCast.Motor
{
    public sealed class AuxMistura
    {
        readonly Som som; readonly MesaUi mesa;
        static readonly string arquivo = Path.Combine(Program.PastaDados, "mistura.json");

        sealed class Canal { public double Base; public double? Mandado; public double Visto = -1; public string Nome = ""; }
        readonly Dictionary<int, Canal> canais = new Dictionary<int, Canal>();
        int aux; string cena = ""; string modoAntes = "";
        int ticksFala; DateTime falaAte = DateTime.MinValue, cantandoAte = DateTime.MinValue; string quemFala = "";
        double duckDb, instDb;               // abaixamento atual da música (fala) e ajuste atual dos instrumentos
        double difMedia = double.NaN; DateTime ultimoPasso = DateTime.MinValue;
        readonly HashSet<string> tocaram = new HashSet<string>();
        DateTime musicaDesde = DateTime.MinValue, ultimoLembrar = DateTime.MinValue; bool lembrado;
        Dictionary<string, double> memoria;

        public string Situacao { get; private set; } = "";
        public string UltimaAcao { get; private set; } = "";
        public IReadOnlyList<(int n, string nome, string papel, bool automatico, double envioDb, double nivel)> Lista { get; private set; } = new List<(int, string, string, bool, double, double)>();

        public AuxMistura(Som som, MesaUi mesa)
        {
            this.som = som; this.mesa = mesa;
            try { if (File.Exists(arquivo)) memoria = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(arquivo)); } catch (Exception e) { Registro.Erro("ler mistura.json", e); }
            memoria ??= new Dictionary<string, double>();
        }

        string Modo => som.Cfg.AuxMisturaModo ?? "desligado";
        static string Db(double v) => (v > 0.05 ? "+" : "") + v.ToString("0.0");

        // ---------- papel de cada canal ----------
        static readonly Regex reFala = new Regex(@"PADRE|CELEBR|PRESID|AMB[AÃ]O|COMENT|LEITOR|LEITURA|SHURE|SEM ?FIO|HEADSET|LAPELA|DI[AÁ]CONO|SACERD|MINISTRO|PALAVRA|MESTRE", RegexOptions.IgnoreCase);
        static readonly Regex reFora = new Regex(@"ASSEMBL|AMBIEN|POVO|PLAT|TALK|RETORNO|PONTO|^CH ?\d+$|^CANAL ?\d+$|^INPUT|^\s*$", RegexOptions.IgnoreCase);
        static readonly Regex reInst = new Regex(@"VIOL|TECLAD|PIANO|[OÓ]RG[AÃ]O|BAIXO|BATER|GUITAR|SAX|FLAUT|CAJ[OÓ]N|PERCUS|CAVAQ|SANFON|ACORDE|CELLO|MESA EXT|PLAYBACK|SYNTH|KEY|BASS|GTR|DRUM|BUMBO|CAIXA CLARA|PRATO|TROMP|CLARINETE|UKULELE|BANDOL|INSTRUM", RegexOptions.IgnoreCase);
        public static string PapelPeloNome(string nome)
        {
            nome = (nome ?? "").Trim();
            if (reFora.IsMatch(nome)) return "fora";
            if (reFala.IsMatch(nome)) return "fala";
            if (reInst.IsMatch(nome)) return "instrumento";
            return "voz";   // VOCAL, VOZ, nomes de pessoas (cantores)
        }
        public string Papel(int n)
        {
            var nome = (mesa.NomeCanal(n) ?? "").Trim();
            if (som.Cfg.PapelCanais != null && (som.Cfg.PapelCanais.TryGetValue(nome.ToUpperInvariant(), out var p) || som.Cfg.PapelCanais.TryGetValue("#" + n, out p)) && p is "fala" or "voz" or "instrumento" or "fora") return p;   // pelo nome; "#21" = canal sem nome
            return PapelPeloNome(nome);
        }

        // ---------- a cada 250 ms (chamado pelo AuxLive) ----------
        public void Tique(DateTime agora, int auxLive, string impedimento, bool travado)
        {
            var modo = Modo;
            bool podeMexer = modo == "automatico" && impedimento == null && !travado;
            if (modo != modoAntes) { if (modoAntes == "automatico") Restaurar("modo mudou"); if (modoAntes != "") Registro.Info($"mistura da aux da live: modo {modoAntes} → {modo}"); modoAntes = modo; Zerar(); }
            if (modo == "desligado") { Situacao = ""; Lista = new List<(int, string, string, bool, double, double)>(); return; }
            if (impedimento != null) { if (TemMudanca()) Restaurar("impedido"); Situacao = impedimento; return; }
            if (mesa.AuxComoMatrix(auxLive)) { Situacao = $"a AUX {auxLive} está como Matrix neste snapshot: não há envio por canal para mexer"; Lista = new List<(int, string, string, bool, double, double)>(); return; }
            if (auxLive != aux || mesa.Cena != cena)
            {
                // trocou de aux: devolve a antiga; trocou de snapshot: a mesa já carregou os envios do snapshot novo (não devolver por cima)
                if (auxLive != aux && TemMudanca()) Restaurar("trocou a aux da live");
                else if (mesa.Cena != cena && cena != "") Registro.Info($"mistura: snapshot trocado ({mesa.Cena}): recomeça do que a mesa carregou");
                aux = auxLive; cena = mesa.Cena; Zerar();
            }
            if (travado) { if (TemMudanca()) Restaurar("travado"); Situacao = "travado"; return; }

            int nCanais = mesa.QuantosCanais;
            var vu = mesa.VuCanal; var vuPos = mesa.VuCanalPos;
            bool vuOk = vu.Length >= nCanais && (agora - mesa.VuEm).TotalSeconds < 2;
            var lista = new List<(int, string, string, bool, double, double)>();
            double pVoz = 0, pInst = 0; bool falaAgora = false, cantando = false; string falante = "";
            for (int n = 1; n <= nCanais; n++)
            {
                var env = mesa.Envio(n, aux); if (env == null) continue;
                if (!canais.TryGetValue(n, out var c)) { c = new Canal { Base = env.Value, Visto = env.Value }; canais[n] = c; }
                c.Nome = mesa.NomeCanal(n) ?? "";
                // alguém mexeu neste envio (mesa, celular, snapshot): vira o novo ponto de partida do canal
                if (Math.Abs(env.Value - c.Visto) > 0.002 && (c.Mandado == null || Math.Abs(env.Value - c.Mandado.Value) > 0.002))
                {
                    c.Base = env.Value; c.Mandado = null;
                    if (modo == "automatico") Registro.Info($"mistura: alguém mexeu no envio do canal {n} ({c.Nome}) para a AUX {aux}: vira o novo ponto de partida");
                }
                c.Visto = env.Value;
                string papel = Papel(n);
                bool mudo = mesa.EnvioMudo(n, aux);
                double baseDb = c.Base <= 0.001 ? -120 : FaderUi.ParaDb(c.Base);
                double nivel = !vuOk ? -120 : (mesa.EnvioPosFader(n, aux) ? vuPos[n - 1] : vu[n - 1]);
                double contrib = mudo || c.Base <= 0.001 ? -120 : nivel + baseDb;   // o que chega na aux (com o envio de partida)
                if (c.Base > 0.001 && !mudo)
                {
                    if (papel == "fala" && nivel > som.Cfg.MisturaLimiarFala) { falaAgora = true; falante = string.IsNullOrWhiteSpace(c.Nome) ? "canal " + n : c.Nome.Trim(); }
                    if (papel == "voz" && nivel > som.Cfg.MisturaLimiarFala) cantando = true;
                    if (papel == "voz" && contrib > -70) pVoz += Math.Pow(10, contrib / 10);
                    if (papel == "instrumento" && contrib > -70) pInst += Math.Pow(10, contrib / 10);
                    if ((papel == "voz" || papel == "instrumento") && contrib > -55 && !string.IsNullOrWhiteSpace(c.Nome)) tocaram.Add(c.Nome.Trim().ToUpperInvariant());
                }
                lista.Add((n, c.Nome, papel, c.Base > 0.001 && (papel == "voz" || papel == "instrumento"), baseDb, nivel));
            }
            Lista = lista;
            if (!vuOk) { Situacao = "esperando os medidores da mesa"; return; }

            // a) fala
            if (som.Cfg.MisturaAbaixarFala)
            {
                if (cantando) cantandoAte = agora.AddSeconds(1.5);
                bool ministerioCanta = agora < cantandoAte;
                ticksFala = falaAgora && !ministerioCanta ? ticksFala + 1 : 0;
                if (ticksFala >= 2) { falaAte = agora.AddSeconds(1.5); quemFala = falante; }
                if (ministerioCanta) falaAte = DateTime.MinValue;   // cantor entrou: volta na hora
            }
            else falaAte = DateTime.MinValue;
            bool falando = agora < falaAte;
            double alvoDuck = falando ? Math.Clamp(som.Cfg.MisturaAbaixarDb, -20, -3) : 0;
            double duckAntes = duckDb;
            duckDb = alvoDuck < duckDb ? Math.Max(alvoDuck, duckDb - 2) : Math.Min(alvoDuck, duckDb + 1);
            if (duckAntes == 0 && duckDb < 0) Acao($"instrumentos {Db(alvoDuck)} dB: fala no {quemFala}, sem cantor");
            if (duckAntes < 0 && duckDb == 0) Acao("instrumentos de volta");

            // b) vozes acima dos instrumentos (só com música e sem fala)
            bool musica = pVoz > 1e-7 && pInst > 1e-7;   // > −70 dBFS os dois
            if (musica && !falando)
            {
                if (musicaDesde == DateTime.MinValue) musicaDesde = agora;
                // o que chega com o ajuste atual dos instrumentos
                double dif = 10 * Math.Log10(pVoz) - (10 * Math.Log10(pInst) + instDb);
                difMedia = double.IsNaN(difMedia) ? dif : difMedia + (dif - difMedia) * (0.25 / 8);
                if (som.Cfg.MisturaEquilibrio && (agora - ultimoPasso).TotalSeconds >= 5 && (agora - musicaDesde).TotalSeconds >= 8)
                {
                    double alvo = som.Cfg.MisturaVozesAcimaDb;
                    if (difMedia < alvo - 2 && instDb > -6) { instDb = Math.Max(-6, instDb - 0.5); ultimoPasso = agora; Acao($"instrumentos {Db(instDb)} dB: {Vozes(difMedia)}"); }
                    else if (difMedia > alvo + 4 && instDb < 0) { instDb = Math.Min(0, instDb + 0.5); ultimoPasso = agora; Acao($"instrumentos {Db(instDb)} dB: {Vozes(difMedia)}, sobra folga"); }
                }
                // c) lembrar: depois de 30 s de música, começa do ajuste guardado deste conjunto
                if (som.Cfg.MisturaLembrar && !lembrado && tocaram.Count > 0 && (agora - musicaDesde).TotalSeconds >= 30)
                {
                    lembrado = true;
                    var ass = Assinatura();
                    if (memoria.TryGetValue(ass, out var guardado) && Math.Abs(guardado - instDb) > 0.4) { instDb = Math.Clamp(guardado, -6, 0); Acao($"instrumentos {Db(instDb)} dB: ajuste guardado deste ministério"); }
                }
            }
            if (som.Cfg.MisturaLembrar && lembrado && tocaram.Count > 0 && (agora - ultimoLembrar).TotalSeconds >= 60)
            {
                ultimoLembrar = agora; var ass = Assinatura();
                if (!memoria.TryGetValue(ass, out var g) || Math.Abs(g - instDb) > 0.1) { memoria[ass] = Math.Round(instDb, 1); GravarMemoria(); }
            }

            // aplicar
            if (podeMexer)
                foreach (var (n, _, papel, auto, _, _) in lista)
                {
                    if (!auto) continue;
                    var c = canais[n];
                    double off = papel == "instrumento" ? duckDb + instDb : 0;   // as vozes nunca são mexidas
                    double quer = off == 0 ? c.Base : FaderUi.DeDb(FaderUi.ParaDb(c.Base) + off);
                    double tem = c.Mandado ?? c.Visto;
                    if (Math.Abs(quer - tem) > 0.002 && mesa.DefinirEnvio(n, aux, quer)) { c.Mandado = quer; c.Visto = quer; }
                }
            Situacao = string.Join(" · ", new[] { modo == "simular" ? "simulando" : "automático",
                falando ? $"instrumentos {Db(duckDb)} dB (fala no {quemFala})" : "",
                musica && !double.IsNaN(difMedia) ? Vozes(difMedia) : "",
                instDb != 0 ? $"instrumentos {Db(instDb)} dB (equilíbrio)" : "" }.Where(x => x != ""));
        }

        static string Vozes(double d) => d >= 0 ? $"vozes {d:0.0} dB acima dos instrumentos" : $"vozes {-d:0.0} dB abaixo dos instrumentos";
        void Acao(string t)
        {
            UltimaAcao = $"{DateTime.Now:HH:mm:ss} {t}";
            Registro.Info((Modo == "simular" ? "mistura (simulando): faria " : "mistura: ") + t);
        }
        string Assinatura() => string.Join("+", tocaram.OrderBy(x => x));
        void GravarMemoria() { try { File.WriteAllText(arquivo, JsonSerializer.Serialize(memoria, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { Registro.Erro("gravar mistura.json", e); } }

        bool TemMudanca() => canais.Values.Any(c => c.Mandado != null && Math.Abs(c.Mandado.Value - c.Base) > 0.002);
        // devolve todos os envios mexidos ao ponto de partida
        public void Restaurar(string motivo)
        {
            int k = 0;
            foreach (var (n, c) in canais)
                if (c.Mandado != null && Math.Abs(c.Mandado.Value - c.Base) > 0.002)
                {
                    int permitidaAntes = mesa.AuxPermitida;
                    mesa.AuxPermitida = aux;   // devolver vale mesmo travado/desligado: é desfazer o que o Cast fez
                    if (mesa.DefinirEnvio(n, aux, c.Base)) { c.Mandado = null; c.Visto = c.Base; k++; }
                    mesa.AuxPermitida = permitidaAntes;
                }
            if (k > 0) Registro.Info($"mistura: {k} envio(s) para a AUX {aux} devolvidos ao ponto de partida ({motivo})");
            duckDb = 0;
        }
        void Zerar()
        {
            canais.Clear(); duckDb = 0; ticksFala = 0; falaAte = DateTime.MinValue; difMedia = double.NaN; musicaDesde = DateTime.MinValue; lembrado = false; tocaram.Clear();
            instDb = 0;
        }
    }
}
