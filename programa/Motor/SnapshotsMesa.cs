// (0.1.22) Snapshot certo da mesa para cada celebração — SÓ AVISA (nunca carrega: carregar troca a mesa inteira,
// inclusive os retornos dos músicos). Item 16 do backlog.
// Aprende sozinho: quando uma transmissão passa de 15 min, guarda o snapshot que a mesa está usando para aquela
// celebração (dia da semana + hora + igreja, da agenda do MiguelAngelus). Da próxima vez, se a mesa estiver em outro
// snapshot perto da hora (de 90 min antes a 30 min depois do início), avisa no Ao vivo. Dá para dispensar o aviso
// ou marcar na hora "esta celebração usa o snapshot atual".
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MiguelAngelusCast.Motor
{
    public sealed class SnapshotsMesa : IDisposable
    {
        public sealed class Lembrado { public string Cena { get; set; } = ""; public string Descricao { get; set; } = ""; public DateTime Quando { get; set; } }
        static readonly string arquivo = Path.Combine(Program.PastaDados, "snapshots-mesa.json");
        static readonly string[] dias = { "domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado" };

        readonly Som som; readonly MesaUi mesa; readonly Func<bool> aoVivo;
        readonly Timer relogio;
        Dictionary<string, Lembrado> mem;
        DateTime aoVivoDesde = DateTime.MinValue; bool aprendidoNesta; string dispensado = "";

        public string Aviso { get; private set; }
        public string ChaveAgora { get; private set; }
        public string DescricaoAgora { get; private set; } = "";
        public IReadOnlyDictionary<string, Lembrado> Lista { get { lock (this) return new Dictionary<string, Lembrado>(mem); } }

        public SnapshotsMesa(Som som, MesaUi mesa, Func<bool> aoVivo)
        {
            this.som = som; this.mesa = mesa; this.aoVivo = aoVivo;
            try { if (File.Exists(arquivo)) mem = JsonSerializer.Deserialize<Dictionary<string, Lembrado>>(File.ReadAllText(arquivo)); } catch (Exception e) { Registro.Erro("ler snapshots-mesa.json", e); }
            mem ??= new Dictionary<string, Lembrado>();
            relogio = new Timer(_ => { try { Tique(); } catch (Exception e) { Registro.Erro("snapshots da mesa", e); } }, null, 5000, 5000);
        }

        void Gravar() { try { lock (this) File.WriteAllText(arquivo, JsonSerializer.Serialize(mem, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { Registro.Erro("gravar snapshots-mesa.json", e); } }

        static Celebracao Agora()
        {
            var agora = DateTime.Now;
            return Miguel.Lista?.Where(c => (agora - c.Quando).TotalMinutes <= 90 && (c.Quando - agora).TotalMinutes <= 30)
                               .OrderBy(c => Math.Abs((c.Quando - agora).TotalMinutes)).FirstOrDefault();
        }
        static string Chave(Celebracao c) => $"{(int)c.Quando.DayOfWeek}|{c.Quando:HH\\:mm}|{c.LocalSigla}";
        static string Descr(Celebracao c) => $"{(string.IsNullOrEmpty(c.Tipo) ? "a celebração" : "a " + c.Tipo)} de {dias[(int)c.Quando.DayOfWeek]} {c.Quando:H\\:mm}" + (string.IsNullOrEmpty(c.LocalSigla) ? "" : $" ({c.LocalSigla})");

        void Tique()
        {
            if (!som.Cfg.AvisoSnapshot || !mesa.Conectada || string.IsNullOrWhiteSpace(mesa.Cena)) { Aviso = null; return; }
            var c = Agora();
            if (c == null) { Aviso = null; ChaveAgora = null; DescricaoAgora = ""; aoVivoDesde = DateTime.MinValue; aprendidoNesta = false; return; }
            string chave = Chave(c), desc = Descr(c);
            ChaveAgora = chave; DescricaoAgora = desc;
            // aprender: transmissão no ar há 15 min
            if (aoVivo())
            {
                if (aoVivoDesde == DateTime.MinValue) aoVivoDesde = DateTime.Now;
                if (!aprendidoNesta && (DateTime.Now - aoVivoDesde).TotalMinutes >= 15) { aprendidoNesta = true; Lembrar(chave, desc, mesa.Cena, "aprendido na transmissão"); }
            }
            else { aoVivoDesde = DateTime.MinValue; aprendidoNesta = false; }

            Lembrado l; lock (this) mem.TryGetValue(chave, out l);
            string esta = chave + "|" + c.Quando.ToString("yyyyMMddHHmm");
            Aviso = l != null && !string.Equals(l.Cena, mesa.Cena, StringComparison.OrdinalIgnoreCase) && dispensado != esta
                ? $"a mesa está no snapshot \"{mesa.Cena}\", mas {desc} costuma usar \"{l.Cena}\": se for o caso, carregue esse snapshot na mesa"
                : null;
        }

        void Lembrar(string chave, string desc, string cena, string como)
        {
            lock (this) mem[chave] = new Lembrado { Cena = cena, Descricao = desc, Quando = DateTime.Now };
            Gravar(); Registro.Info($"snapshot da mesa: {desc} → \"{cena}\" ({como})");
        }

        // botões do aviso
        public void Dispensar() { var c = Agora(); if (c != null) dispensado = Chave(c) + "|" + c.Quando.ToString("yyyyMMddHHmm"); Aviso = null; }
        public void UsarAtual() { var c = Agora(); if (c != null && mesa.Conectada && !string.IsNullOrWhiteSpace(mesa.Cena)) { Lembrar(Chave(c), Descr(c), mesa.Cena, "marcado pelo operador"); Aviso = null; } }
        public void Esquecer(string chave) { bool tirou; lock (this) tirou = mem.Remove(chave ?? ""); if (tirou) { Gravar(); Registro.Info("snapshot da mesa: esquecido " + chave); } }

        public void Dispose() { try { relogio.Dispose(); } catch { } }
    }
}
