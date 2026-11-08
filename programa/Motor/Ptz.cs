// Câmeras do Cast (Ajustes › Câmeras): nome, imagem (NDI), controle PTZ por VISCA na rede e posições prontas.
// Mesmo jeito do MiguelAngelus (app\Ptz.cs + lumen\js\ptz.js): a tela monta o comando VISCA e aqui só entrega na rede.
//  "visca-ip" = VISCA sobre IP (UDP com cabeçalho Sony), porta 52381
//  "udp"      = VISCA puro por UDP, porta 1259
//  "tcp"      = VISCA puro por TCP, porta 5678 (a F2.DP da Matriz)
// As posições ficam gravadas NA CÂMERA (presets por número); aqui só os nomes. As novas gravam do nº 20 em diante
// (as do OBS usam 0–15).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class Posicao { public int N { get; set; } public string Nome { get; set; } = ""; }

    public sealed class CameraCfg
    {
        public string Id { get; set; } = "c" + DateTime.Now.Ticks.ToString("x");
        public string Nome { get; set; } = "Câmera";
        public string Fonte { get; set; } = "";          // nome da fonte NDI
        public string Ip { get; set; } = "";
        public int Porta { get; set; } = 5678;
        public string Protocolo { get; set; } = "tcp";
        public List<Posicao> Posicoes { get; set; } = new List<Posicao>();
    }

    public static class Cameras
    {
        static readonly string arquivo = Path.Combine(Program.PastaDados, "cameras.json");
        public static List<CameraCfg> Lista { get; private set; } = Carregar();
        public static event Action Mudou;

        static List<CameraCfg> Carregar()
        {
            try { if (File.Exists(arquivo)) return JsonSerializer.Deserialize<List<CameraCfg>>(File.ReadAllText(arquivo)) ?? new List<CameraCfg>(); }
            catch (Exception e) { Registro.Erro("ler cameras.json", e); }
            return new List<CameraCfg>();
        }
        public static void Salvar()
        {
            try { File.WriteAllText(arquivo, JsonSerializer.Serialize(Lista, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar cameras.json", e); }
            Mudou?.Invoke();
        }
        public static CameraCfg Achar(string id) => Lista.FirstOrDefault(c => c.Id == id);
        public static CameraCfg PorFonte(string fonteNdi) => Lista.FirstOrDefault(c => c.Fonte == fonteNdi && !string.IsNullOrWhiteSpace(c.Ip));

        public static int PortaPadrao(string protocolo) => protocolo == "visca-ip" ? 52381 : protocolo == "udp" ? 1259 : 5678;
        public const int PrimeiraNova = 20;
        public static int ProximaLivre(CameraCfg c)
        {
            var usados = new HashSet<int>(c.Posicoes.Select(p => p.N));
            int n = PrimeiraNova; while (usados.Contains(n) || (n >= 95 && n <= 99)) n++;
            return n > 127 ? -1 : n;
        }
    }

    // entrega o comando VISCA na rede (uma conexão TCP por câmera, reaproveitada)
    public static class ViscaRede
    {
        static readonly UdpClient udp = new UdpClient();
        static readonly Dictionary<string, TcpClient> tcp = new Dictionary<string, TcpClient>();
        static readonly object trava = new object();
        static uint seq;

        public static Task<string> Enviar(CameraCfg c, byte[] cmd) => Enviar(c?.Ip, c?.Porta ?? 0, c?.Protocolo, cmd);

        public static Task<string> Enviar(string ip, int porta, string protocolo, byte[] cmd) => Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ip) || cmd == null || cmd.Length == 0) return "câmera sem IP";
                if (porta <= 0) porta = Cameras.PortaPadrao(protocolo);
                if (protocolo == "tcp")
                {
                    var chave = ip + ":" + porta;
                    lock (trava)
                    {
                        for (int tentativa = 0; tentativa < 2; tentativa++)
                        {
                            if (!tcp.TryGetValue(chave, out var c) || !c.Connected)
                            {
                                c?.Close();
                                c = new TcpClient { NoDelay = true };
                                if (!c.ConnectAsync(ip, porta).Wait(1500)) { c.Close(); tcp.Remove(chave); return "a câmera não respondeu (TCP " + porta + ")"; }
                                tcp[chave] = c;
                                var s0 = c.GetStream();
                                Task.Run(() => { var b = new byte[256]; try { while (s0.Read(b, 0, b.Length) > 0) { } } catch { } });   // descarta as respostas
                            }
                            try { c.GetStream().Write(cmd, 0, cmd.Length); return null; }
                            catch { c.Close(); tcp.Remove(chave); }   // conexão velha: tenta de novo uma vez
                        }
                        return "a conexão com a câmera caiu";
                    }
                }
                var pacote = cmd;
                if (protocolo != "udp")
                {
                    pacote = new byte[8 + cmd.Length];
                    pacote[0] = 0x01; pacote[1] = 0x00;
                    pacote[2] = (byte)(cmd.Length >> 8); pacote[3] = (byte)cmd.Length;
                    uint s; lock (trava) s = ++seq;
                    pacote[4] = (byte)(s >> 24); pacote[5] = (byte)(s >> 16); pacote[6] = (byte)(s >> 8); pacote[7] = (byte)s;
                    Array.Copy(cmd, 0, pacote, 8, cmd.Length);
                }
                lock (trava) udp.Send(pacote, pacote.Length, ip, porta);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

        public static void FecharTudo() { lock (trava) { foreach (var c in tcp.Values) try { c.Close(); } catch { } tcp.Clear(); } }
    }
}
