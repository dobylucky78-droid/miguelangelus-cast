// Mesas Soundcraft Ui (Ui12, Ui16, Ui24R) pela rede: SÓ LEITURA.
// A página da própria mesa conversa por WebSocket (ws://IP/) com linhas "3:::SETS^caminho^valor" / "3:::SETD^caminho^valor".
// Daqui vêm os nomes dados na mesa (aux e canais) e se a aux da transmissão está muda ou com o fader no mínimo.
// O Cast nunca manda SETS/SETD: só "ALIVE" (a cada 1 s, como a página da mesa) para a conexão não cair.
// Referência do protocolo: biblioteca soundcraft-ui-connection (MIT), github.com/fmalcher/soundcraft-ui
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class MesaUi : IDisposable
    {
        public string Ip { get; private set; } = "";
        public string Estado { get; private set; } = "desligada";
        public string Modelo { get; private set; } = "";
        public string Firmware { get; private set; } = "";
        public bool Conectada => Estado == "conectada";
        public event Action Mudou;

        // caminho -> valor (ex.: "a.5.name" -> "LIVE", "a.5.mute" -> "0")
        readonly ConcurrentDictionary<string, string> valores = new ConcurrentDictionary<string, string>();
        CancellationTokenSource cancelar;

        public void Conectar(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip == Ip && cancelar != null) return;
            Desligar();
            Ip = ip;
            if (ip == "") { Estado = "desligada"; Mudou?.Invoke(); return; }
            cancelar = new CancellationTokenSource();
            var ct = cancelar.Token;
            Task.Run(() => Laco(ip, ct));
        }

        async Task Laco(string ip, CancellationToken ct)
        {
            var buf = new byte[1 << 18];
            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    Estado = "conectando"; Mudou?.Invoke();
                    using (var tempo = CancellationTokenSource.CreateLinkedTokenSource(ct)) { tempo.CancelAfter(4000); await ws.ConnectAsync(new Uri($"ws://{ip}/"), tempo.Token); }
                    Estado = "conectada"; Registro.Info("mesa na rede: conectada em " + ip); Mudou?.Invoke();
                    var vivo = Task.Run(async () =>
                    {
                        var alive = Encoding.ASCII.GetBytes("3:::ALIVE");
                        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                        { await ws.SendAsync(new ArraySegment<byte>(alive), WebSocketMessageType.Text, true, ct); await Task.Delay(1000, ct); }
                    }, ct);
                    var sb = new StringBuilder(); bool avisar = false; var ultimoAviso = DateTime.Now;
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                        if (!r.EndOfMessage) continue;
                        foreach (var bruta in sb.ToString().Split('\n'))
                        {
                            // só a 1ª linha de cada mensagem traz o prefixo "3:::"; as outras vêm direto ("SETS^...")
                            var linha = bruta.StartsWith("3:::") ? bruta.Substring(4) : bruta;
                            var p = linha.TrimEnd('\r').Split('^');
                            if (p.Length < 3 || (p[0] != "SETS" && p[0] != "SETD")) continue;
                            var chave = p[1]; var valor = p[2];
                            if (!Interessa(chave)) continue;
                            if (!valores.TryGetValue(chave, out var antes) || antes != valor) { valores[chave] = valor; avisar = true; }
                            if (chave == "model") Modelo = valor; else if (chave == "firmware") Firmware = valor;
                        }
                        sb.Clear();
                        if (avisar && (DateTime.Now - ultimoAviso).TotalMilliseconds > 300) { avisar = false; ultimoAviso = DateTime.Now; Mudou?.Invoke(); }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { Estado = "sem resposta (" + e.Message + ")"; }
                if (ct.IsCancellationRequested) break;
                if (Estado == "conectada") { Estado = "caiu: reconectando"; Registro.Info("mesa na rede: caiu"); }
                Mudou?.Invoke();
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }

        // só guardamos o que o Cast usa (a mesa manda milhares de valores)
        static bool Interessa(string c) =>
            c == "model" || c == "firmware" || c == "var.currentShow" || c == "var.currentSnapshot" ||
            (c.StartsWith("i.") || c.StartsWith("a.") || c.StartsWith("l.") || c.StartsWith("p.")) &&
            (c.EndsWith(".name") || c.EndsWith(".mute") || c.EndsWith(".mix") || c.EndsWith(".delay") || c.EndsWith(".stereoIndex")) &&
            c.Split('.').Length == 3;

        public string Valor(string caminho) => valores.TryGetValue(caminho, out var v) ? v : null;
        // show e cena carregados na mesa (ex.: "SJO · MISSA 7:00 - SOL")
        public string Cena => string.Join(" · ", new[] { Valor("var.currentShow"), Valor("var.currentSnapshot") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        public string NomeAux(int aux1) => Valor($"a.{aux1 - 1}.name");          // aux 1-based
        public string NomeCanal(int canal1) => Valor($"i.{canal1 - 1}.name");    // canal 1-based

        // problema na aux que alimenta a transmissão (null = tudo certo ou sem informação)
        public string AvisoAux(int aux1)
        {
            if (!Conectada) return null;
            var nome = NomeAux(aux1); var rot = $"AUX {aux1}" + (string.IsNullOrEmpty(nome) ? "" : " · " + nome);
            if (Valor($"a.{aux1 - 1}.mute") == "1") return rot + " está MUDA na mesa";
            if (double.TryParse(Valor($"a.{aux1 - 1}.mix"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f < 0.01)
                return rot + ": o fader está no mínimo na mesa";
            return null;
        }

        public void Desligar()
        {
            try { cancelar?.Cancel(); } catch { }
            cancelar = null; valores.Clear(); Modelo = Firmware = ""; Estado = "desligada";
        }

        public void Dispose() => Desligar();
    }
}
