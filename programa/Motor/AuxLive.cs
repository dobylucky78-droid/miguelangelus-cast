// (0.1.22) Volume automático da aux da live NA MESA (camada 1 do item 16 do backlog).
// O Cast mede o som como chega da mesa (antes do Ganho e do nivelador) e mexe só no fader (master) da aux
// escolhida como "aux da live", para o som chegar numa faixa boa: picos perto de −12 dBFS.
//   estourando (pico ≥ −1 dBFS)      → baixa 2 dB (no máximo 1 vez por segundo)
//   quente (pico em 3 s > alvo + 5)   → baixa 1 dB (no máximo 1 vez a cada 2 s)
//   baixo (som de verdade, picos em 8 s < alvo − 6) → sobe 0,5 dB (no máximo 1 vez a cada 4 s; nunca logo depois de baixar)
//   muito baixo (picos em 8 s < alvo − 14)          → sobe 1 dB (no máximo 1 vez a cada 3 s)
//   silêncio                          → não sobe nada
// Nunca passa de ±Limite dB do ponto de partida. Se alguém mexer no fader na mesa, o Cast respeita: aquele
// vira o novo ponto de partida e o automático espera 20 s. Modo "simular" só registra o que faria.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MiguelAngelusCast.Motor
{
    // fader (0–1) das mesas Ui ↔ dB, medido na Ui24R (firmware 3.5) em 22/11/2026: tom pelo USB, aux 6 voltando pelo USB 8
    public static class FaderUi
    {
        static readonly double[] F = { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.7637, 0.80, 0.85, 0.90, 0.95, 1.00 };
        static readonly double[] D = { -61.6, -52.8, -45.0, -38.1, -32.1, -26.8, -22.1, -18.1, -14.6, -11.6, -8.9, -6.5, -4.4, -2.4, -0.5, 0.0, 1.4, 3.3, 5.4, 7.6, 10.0 };
        public const double MaxDb = 10, MinDb = -61.6;
        public static double ParaDb(double f)
        {
            if (f <= F[0]) return f <= 0.001 ? -90 : MinDb;
            for (int i = 1; i < F.Length; i++) if (f <= F[i]) return D[i - 1] + (D[i] - D[i - 1]) * (f - F[i - 1]) / (F[i] - F[i - 1]);
            return MaxDb;
        }
        public static double DeDb(double db)
        {
            if (db <= D[0]) return F[0];
            for (int i = 1; i < D.Length; i++) if (db <= D[i]) return F[i - 1] + (F[i] - F[i - 1]) * (db - D[i - 1]) / (D[i] - D[i - 1]);
            return 1.0;
        }
    }

    public sealed class AuxLive : IDisposable
    {
        readonly Som som; readonly MesaUi mesa; readonly Func<int> auxSugerida;
        readonly Timer relogio;
        readonly object trava = new object();
        readonly Queue<(DateTime t, double pico, double media)> hist = new Queue<(DateTime, double, double)>();

        public bool Travado { get; set; }
        public int Aux { get; private set; }
        public double? AtualDb { get; private set; }     // onde o fader está (no modo simular: onde estaria)
        public double? PartidaDb { get; private set; }
        public string Situacao { get; private set; } = "";
        public string Aviso { get; private set; }         // vai para o aviso do Ao vivo (null = nada)
        public string UltimaAcao { get; private set; } = "";
        public int Mudancas { get; private set; }
        public string Indisponivel { get; private set; }  // null = dá para falar com a mesa e a aux é válida
        public string Bloqueio { get; private set; }      // null = pode mexer (a trava está desligada ou a aux não vai para outras saídas)
        public string AlimentaOutras { get; private set; } // informação: outras saídas (matrizes) que recebem a aux da live neste snapshot
        public readonly AuxMistura Mistura;
        string bloqueioAvisado, estadoVisto = "";
        public event Action Mudou;   // trava/matrizes mudaram (para a tela de Ajustes se atualizar)

        double? ultimoMandado, ultimoVisto; double simDb;
        DateTime ultimaBaixa = DateTime.MinValue, ultimaSubida = DateTime.MinValue, pausaAte = DateTime.MinValue;
        string modoAntes = ""; int ocupado;

        public AuxLive(Som som, MesaUi mesa, Func<int> auxSugerida)
        {
            this.som = som; this.mesa = mesa; this.auxSugerida = auxSugerida;
            Mistura = new AuxMistura(som, mesa);
            relogio = new Timer(_ => { if (Interlocked.Exchange(ref ocupado, 1) == 1) return; try { Tique(); } catch (Exception e) { Registro.Erro("aux da live", e); } finally { ocupado = 0; } }, null, 1000, 250);
        }

        string Modo => som.Cfg.AuxLiveModo ?? "desligado";
        string Rot => $"AUX {Aux}" + (string.IsNullOrEmpty(mesa.NomeAux(Aux)) ? "" : " · " + mesa.NomeAux(Aux));
        static string Db(double v) => (v > 0.05 ? "+" : "") + v.ToString("0.0");

        string Onde(double db, string modo) => $"{Rot}: " + (modo == "simular" ? "estaria em " : "") + $"{Db(db)} dB" + (mesa.AuxComoMatrix(Aux) ? " · como Matrix" : "");
        void Zerar() { PartidaDb = null; ultimoMandado = ultimoVisto = null; AtualDb = null; Aviso = null; lock (trava) hist.Clear(); }

        void Tique()
        {
            var (pico, media) = som.NivelEntrada();
            var agora = DateTime.Now;
            lock (trava) { hist.Enqueue((agora, pico, media)); while (hist.Count > 0 && (agora - hist.Peek().t).TotalSeconds > 10) hist.Dequeue(); }

            var modo = Modo;
            if (modo != modoAntes) { if (modoAntes != "") Registro.Info($"aux da live: modo {modoAntes} → {modo}"); modoAntes = modo; Zerar(); }
            Aux = som.Cfg.AuxLive > 0 ? som.Cfg.AuxLive : auxSugerida();
            // (0.1.22) pode mexer? (vale para o volume automático e para a mistura canal a canal)
            Indisponivel = string.IsNullOrWhiteSpace(som.Cfg.MesaIp) ? "indisponível: informe o endereço da mesa (só mesas Soundcraft Ui por enquanto)"
                : !mesa.Conectada ? "esperando a mesa conectar"
                : !mesa.PodeControlar ? "indisponível com esta mesa"
                : Aux < 1 || Aux > mesa.QuantasAux ? "escolha qual é a aux da live" : null;
            // a aux da live também vai para outras saídas (matrizes: ALTAR, caixas…) neste snapshot? então mexer nela mudaria essas saídas
            Bloqueio = null;
            if (Indisponivel == null)
            {
                var outras = mesa.AlimentaOutras(Aux);
                AlimentaOutras = null;
                if (outras.Count > 0)
                {
                    var nomes = string.Join(", ", outras.Select(a => $"AUX {a}" + (string.IsNullOrEmpty(mesa.NomeAux(a)) ? "" : " · " + mesa.NomeAux(a))));
                    AlimentaOutras = $"pela mesa, a {Rot} também vai para {nomes} neste snapshot ({mesa.Cena})";
                    // (0.1.22) a trava é uma chave em Ajustes › Som, desligada de fábrica (pedido do usuário: nas paróquias as matrizes usam o LR)
                    if (som.Cfg.TravarSeAlimentaOutras)
                        Bloqueio = AlimentaOutras + $": mexer nela mudaria essas saídas. O Cast não mexe; na mesa, tire a {Rot} dessas matrizes ou desligue esta trava em Ajustes › Som";
                    var reg = (Bloqueio != null ? "BLOQUEADO — " : "aviso (trava desligada, o Cast mexe mesmo assim) — ") + AlimentaOutras;
                    if (reg != bloqueioAvisado) { bloqueioAvisado = reg; Registro.Info("aux da live: " + reg); }
                }
            }
            var estado = (Bloqueio ?? "") + "|" + (AlimentaOutras ?? "") + "|" + (Indisponivel ?? "");
            if (estado != estadoVisto) { estadoVisto = estado; try { Mudou?.Invoke(); } catch { } }
            bool algumAutomatico = modo == "automatico" || som.Cfg.AuxMisturaModo == "automatico";
            mesa.AuxPermitida = Indisponivel == null && Bloqueio == null && algumAutomatico && !Travado ? Aux : 0;
            const string curto = "⛔ parado: a aux da live também vai para outras saídas neste snapshot (veja o aviso)";
            try { Mistura.Tique(agora, Aux, Indisponivel ?? (Bloqueio != null ? curto : null), Travado); } catch (Exception e) { Registro.Erro("mistura da aux da live", e); }
            if (modo == "desligado") { Situacao = ""; Aviso = null; return; }
            if (Indisponivel != null) { Situacao = Indisponivel; Aviso = null; return; }
            if (Bloqueio != null) { Situacao = curto; Aviso = Bloqueio; return; }
            var f = mesa.MasterAux(Aux);
            if (f == null) { Situacao = "esperando os valores da mesa"; return; }
            double realDb = FaderUi.ParaDb(f.Value);

            // alguém mexeu no fader na mesa (ou pelo celular): respeitar
            if (ultimoVisto != null && Math.Abs(f.Value - ultimoVisto.Value) > 0.002 && (ultimoMandado == null || Math.Abs(f.Value - ultimoMandado.Value) > 0.002))
            {
                Registro.Info($"aux da live: alguém mexeu na mesa ({Rot} em {Db(realDb)} dB) — vira o novo ponto de partida; automático espera 20 s");
                PartidaDb = realDb; simDb = realDb; ultimoMandado = null; pausaAte = agora.AddSeconds(20);
                UltimaAcao = $"{agora:HH:mm:ss} alguém mexeu na mesa: {Db(realDb)} dB";
            }
            ultimoVisto = f.Value;
            if (PartidaDb == null) { PartidaDb = realDb; simDb = realDb; Registro.Info($"aux da live: {Rot} começa em {Db(realDb)} dB (modo {modo})"); }
            double atual = modo == "simular" ? simDb : realDb;
            AtualDb = atual;
            double lim = Math.Clamp(som.Cfg.AuxLiveLimiteDb, 3, 20), alvo = Math.Clamp(som.Cfg.AuxLiveAlvoDb, -24, -6);
            double min = Math.Max(PartidaDb.Value - lim, FaderUi.MinDb), max = Math.Min(PartidaDb.Value + lim, FaderUi.MaxDb);
            string onde = Onde(atual, modo);

            if (Travado) { Situacao = onde + " · travado"; return; }
            if (mesa.AuxMuda(Aux)) { Situacao = onde + " · aux muda na mesa"; return; }
            if (agora < pausaAte) { Situacao = onde + " · esperando (mexeram na mesa)"; return; }

            // no modo simular, o som medido não mudou: corrige a medida pelo que já teria mexido
            double comp = atual - realDb;
            (DateTime t, double pico, double media)[] h; lock (trava) h = hist.ToArray();
            double Pico(double seg) { var x = h.Where(v => (agora - v.t).TotalSeconds <= seg).Select(v => v.pico).DefaultIfEmpty(-120).Max(); return x + comp; }
            bool cheio = h.Length > 0 && (agora - h[0].t).TotalSeconds >= 7.5;
            bool somDeVerdade = h.Where(v => (agora - v.t).TotalSeconds <= 8).Select(v => v.media).DefaultIfEmpty(-120).Max() + comp > -50;

            double delta = 0; string motivo = "";
            double p1 = Pico(1), p3 = Pico(3), p8 = Pico(8);
            if (p1 >= -1.0 && (agora - ultimaBaixa).TotalSeconds >= 1) { delta = -2; motivo = $"estourando (pico {p1:0.0} dBFS)"; }
            else if (p3 > alvo + 5 && (agora - ultimaBaixa).TotalSeconds >= 2) { delta = -1; motivo = $"alto (pico {p3:0.0} dBFS)"; }
            else if (cheio && somDeVerdade && p8 < alvo - 14 && (agora - ultimaSubida).TotalSeconds >= 3 && (agora - ultimaBaixa).TotalSeconds >= 8) { delta = 1; motivo = $"muito baixo (pico {p8:0.0} dBFS em 8 s)"; }
            else if (cheio && somDeVerdade && p8 < alvo - 6 && (agora - ultimaSubida).TotalSeconds >= 4 && (agora - ultimaBaixa).TotalSeconds >= 8) { delta = 0.5; motivo = $"baixo (pico {p8:0.0} dBFS em 8 s)"; }

            Aviso = null;
            if (delta != 0)
            {
                double novo = Math.Clamp(atual + delta, min, max);
                if (Math.Abs(novo - atual) < 0.05)
                {
                    Aviso = delta < 0
                        ? $"a {Rot} já baixou o máximo do automático ({Db(min)} dB) e o som ainda chega alto: baixe o ganho dos canais na mesa"
                        : $"a {Rot} já subiu o máximo do automático ({Db(max)} dB) e o som ainda chega baixo: suba o ganho dos canais na mesa";
                }
                else
                {
                    if (delta < 0) ultimaBaixa = agora; else ultimaSubida = agora;
                    Mudancas++;
                    UltimaAcao = $"{agora:HH:mm:ss} {(delta < 0 ? "baixou" : "subiu")} para {Db(novo)} dB: {motivo}";
                    if (modo == "simular")
                    {
                        simDb = novo;
                        Registro.Info($"aux da live (simulando): {(delta < 0 ? "baixaria" : "subiria")} {Rot} de {Db(atual)} para {Db(novo)} dB — {motivo}");
                    }
                    else
                    {
                        // em passos de até 0,5 dB, para não dar tranco
                        int passos = Math.Max(1, (int)Math.Ceiling(Math.Abs(novo - atual) / 0.5));
                        for (int i = 1; i <= passos; i++)
                        {
                            double fv = FaderUi.DeDb(atual + (novo - atual) * i / passos);
                            if (!mesa.DefinirMasterAux(Aux, fv)) break;
                            ultimoMandado = ultimoVisto = fv;
                            if (i < passos) Thread.Sleep(30);
                        }
                        Registro.Info($"aux da live: {Rot} de {Db(atual)} para {Db(novo)} dB — {motivo}");
                        AtualDb = novo;
                    }
                    atual = novo;
                    onde = Onde(atual, modo);
                }
            }
            Situacao = onde + (modo == "simular" ? " · simulando" : " · automático") + $" · partida {Db(PartidaDb.Value)} dB";
        }

        public void Dispose()
        {
            try { relogio.Dispose(); } catch { }
            try { while (ocupado == 1) Thread.Sleep(20); Mistura.Restaurar("o Cast está fechando"); } catch { }
        }
    }
}
