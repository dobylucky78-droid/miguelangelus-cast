// Mesas Soundcraft Ui (Ui12, Ui16, Ui24R) pela rede: leitura de tudo e, desde a 0.1.22, comandos SÓ na aux escolhida como
// "aux da live" (AuxLive.cs / AuxMistura.cs): o fader (master) dela e o envio de cada canal PARA ELA. Nada de canais
// (fader, ganho, EQ), LR, outras aux, show ou snapshot. A aux permitida é a de AuxPermitida; qualquer outra é recusada aqui.
// A página da própria mesa conversa por WebSocket (ws://IP/) com linhas "3:::SETS^caminho^valor" / "3:::SETD^caminho^valor".
// Daqui vêm os nomes dados na mesa (aux e canais) e se a aux da transmissão está muda ou com o fader no mínimo.
// Além do master da aux da live, o Cast só manda "ALIVE" (a cada 1 s, como a página da mesa) para a conexão não cair.
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
        volatile ClientWebSocket atual; readonly SemaphoreSlim envio = new SemaphoreSlim(1, 1);

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
                    atual = ws; Estado = "conectada"; Registro.Info("mesa na rede: conectada em " + ip); Mudou?.Invoke();
                    var vivo = Task.Run(async () =>
                    {
                        var alive = Encoding.ASCII.GetBytes("3:::ALIVE");
                        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                        { await Mandar(ws, alive, ct); await Task.Delay(1000, ct); }
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
                            if (p.Length >= 2 && p[0] == "VU2") { LerVu(p[1]); continue; }
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
                atual = null;
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
            (c.EndsWith(".name") || c.EndsWith(".mute") || c.EndsWith(".mix") || c.EndsWith(".delay") || c.EndsWith(".stereoIndex") || c.EndsWith(".matrix")) &&
            c.Split('.').Length == 3 ||
            // (0.1.22) envios dos canais para as aux e as matrizes (quem recebe a aux da live)
            c.StartsWith("i.") && c.Contains(".aux.") && (c.EndsWith(".value") || c.EndsWith(".mute") || c.EndsWith(".post")) ||
            c.StartsWith("a.") && c.Contains(".mtx.") && (c.EndsWith(".value") || c.EndsWith(".mute"));

        // ---------- (0.1.22) medidores (mensagem "VU2^base64"), lidos na Ui24R fw 3.5 em 22/11/2026 ----------
        // cabeçalho de 8 bytes: quantos canais, players, subgrupos, fx, aux, masters, line in; depois cada um em sequência:
        // canal 6 bytes [entrada, depois do processamento, depois do fader, …], player 6, subgrupo 7, fx 7, aux 5 [antes do master, depois do master, …]
        // valor: 0 = nada; senão dBFS = valor/3 − 80 (240 = 0 dBFS)
        public double[] VuCanal { get; private set; } = new double[0];       // depois do processamento, antes do fader (o que vai num envio pré-fader)
        public double[] VuCanalPos { get; private set; } = new double[0];    // depois do fader
        public double[] VuAux { get; private set; } = new double[0];         // depois do master
        public DateTime VuEm { get; private set; }
        static double VuDb(byte v) => v == 0 ? -120 : v / 3.0 - 80;
        void LerVu(string b64)
        {
            try
            {
                var b = Convert.FromBase64String(b64.Trim());
                if (b.Length < 8) return;
                int nIn = b[0], nPl = b[1], nSg = b[2], nFx = b[3], nAux = b[4];
                int oIn = 8, oAux = oIn + nIn * 6 + nPl * 6 + nSg * 7 + nFx * 7;
                if (b.Length < oAux + nAux * 5) return;
                var c = new double[nIn]; var cp = new double[nIn]; var ax = new double[nAux];
                for (int i = 0; i < nIn; i++) { c[i] = VuDb(b[oIn + i * 6 + 1]); cp[i] = VuDb(b[oIn + i * 6 + 2]); }
                for (int a = 0; a < nAux; a++) ax[a] = VuDb(b[oAux + a * 5 + 1]);
                VuCanal = c; VuCanalPos = cp; VuAux = ax; VuEm = DateTime.Now;
            }
            catch { }
        }

        public int QuantosCanais => Modelo.Equals("ui12", StringComparison.OrdinalIgnoreCase) ? 12 : Modelo.Equals("ui16", StringComparison.OrdinalIgnoreCase) ? 16 : 24;
        // o envio (0–1) do canal para a aux, se está mudo e se é depois do fader
        public double? Envio(int canal1, int aux1) => double.TryParse(Valor($"i.{canal1 - 1}.aux.{aux1 - 1}.value"), System.Globalization.NumberStyles.Float, inv, out var f) ? f : (double?)null;
        public bool EnvioMudo(int canal1, int aux1) => Valor($"i.{canal1 - 1}.aux.{aux1 - 1}.mute") == "1" || Valor($"i.{canal1 - 1}.mute") == "1";
        public bool EnvioPosFader(int canal1, int aux1) => Valor($"i.{canal1 - 1}.aux.{aux1 - 1}.post") == "1";
        // (0.1.22) as outras saídas (matrizes) que recebem esta aux — mexer nela mudaria também essas saídas (ex.: caixas da igreja)
        public System.Collections.Generic.List<int> AlimentaOutras(int aux1)
        {
            var l = new System.Collections.Generic.List<int>();
            for (int m = 1; m <= QuantasAux; m++)
            {
                if (m == aux1) continue;
                if (double.TryParse(Valor($"a.{aux1 - 1}.mtx.{m - 1}.value"), System.Globalization.NumberStyles.Float, inv, out var v) && v > 0.001 &&
                    Valor($"a.{aux1 - 1}.mtx.{m - 1}.mute") != "1" && AuxComoMatrix(m)) l.Add(m);
            }
            return l;
        }
        // a aux em que o Cast pode mexer (0 = nenhuma); posta pelo AuxLive a cada tique
        public int AuxPermitida { get; set; }

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

        async Task Mandar(ClientWebSocket ws, byte[] b, CancellationToken ct)
        {
            await envio.WaitAsync(ct);
            try { await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, ct); } finally { envio.Release(); }
        }

        // (0.1.22) a mesa é da família Ui e está conectada: dá para mexer no master da aux da live
        public bool PodeControlar => Conectada && atual != null && Modelo.StartsWith("ui", StringComparison.OrdinalIgnoreCase);
        public int QuantasAux => Modelo.Equals("ui12", StringComparison.OrdinalIgnoreCase) ? 4 : Modelo.Equals("ui16", StringComparison.OrdinalIgnoreCase) ? 6 : 10;
        static readonly System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        public double? MasterAux(int aux1) => double.TryParse(Valor($"a.{aux1 - 1}.mix"), System.Globalization.NumberStyles.Float, inv, out var f) ? f : (double?)null;
        public bool AuxMuda(int aux1) => Valor($"a.{aux1 - 1}.mute") == "1";
        public bool AuxComoMatrix(int aux1) => Valor($"a.{aux1 - 1}.matrix") == "1";

        // ÚNICO comando que o Cast manda para a mesa: o fader (0–1) do master de uma aux
        public bool DefinirMasterAux(int aux1, double fader)
        {
            var ws = atual;
            if (!PodeControlar || ws == null || aux1 < 1 || aux1 > QuantasAux || aux1 != AuxPermitida) return false;
            fader = Math.Clamp(fader, 0, 1);
            var chave = $"a.{aux1 - 1}.mix"; var txt = fader.ToString("0.##########", inv);
            valores[chave] = txt;   // a mesa não devolve o eco para quem mandou
            var b = Encoding.ASCII.GetBytes($"3:::SETD^{chave}^{txt}");
            try { return Mandar(ws, b, CancellationToken.None).Wait(1500); }   // em ordem: quem chama espera
            catch (Exception e) { Registro.Erro("mesa: mandar fader", e); return false; }
        }

        // ÚNICO outro comando: o envio (0–1) de um canal PARA a aux da live
        public bool DefinirEnvio(int canal1, int aux1, double valor)
        {
            var ws = atual;
            if (!PodeControlar || ws == null || aux1 < 1 || aux1 > QuantasAux || aux1 != AuxPermitida || canal1 < 1 || canal1 > QuantosCanais) return false;
            valor = Math.Clamp(valor, 0, 1);
            var chave = $"i.{canal1 - 1}.aux.{aux1 - 1}.value"; var txt = valor.ToString("0.##########", inv);
            valores[chave] = txt;
            var b = Encoding.ASCII.GetBytes($"3:::SETD^{chave}^{txt}");
            try { return Mandar(ws, b, CancellationToken.None).Wait(1500); }
            catch (Exception e) { Registro.Erro("mesa: mandar envio", e); return false; }
        }

        public void Desligar()
        {
            try { cancelar?.Cancel(); } catch { }
            cancelar = null; valores.Clear(); Modelo = Firmware = ""; Estado = "desligada";
        }

        public void Dispose() => Desligar();
    }
}
