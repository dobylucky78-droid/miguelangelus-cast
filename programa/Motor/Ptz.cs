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

    // (0.1.10) posição gravada NA CENA (não na câmera): as coordenadas que a câmera informou na hora do "Salvar posição".
    // Pan e tilt vão de volta exatamente como vieram (mesmo nº de dígitos), por isso servem para qualquer marca VISCA.
    public sealed class PtzCena
    {
        public string Camera { get; set; } = "";        // id da câmera (Ajustes › Câmeras)
        public int Pan { get; set; }
        public int Tilt { get; set; }
        public int NPan { get; set; } = 4;              // dígitos (nibbles) do pan na resposta da câmera: 4 ou 5
        public int NTilt { get; set; } = 4;
        public int Zoom { get; set; } = -1;             // -1 = a câmera não informou o zoom (só move pan/tilt)
        public string Quando { get; set; } = "";        // quando foi salva (dd/MM HH:mm)
        public PtzCena Copia() => (PtzCena)MemberwiseClone();
        public string Texto() => $"pan {Pan:X}, tilt {Tilt:X}" + (Zoom >= 0 ? $", zoom {Zoom:X}" : "");
    }

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
            try
            {
                if (File.Exists(arquivo))
                {
                    var l = JsonSerializer.Deserialize<List<CameraCfg>>(File.ReadAllText(arquivo)) ?? new List<CameraCfg>();
                    foreach (var c in l) if (string.IsNullOrWhiteSpace(c.Ip)) c.Ip = IpDoNdi(c.Fonte);   // (0.1.12)
                    return l;
                }
            }
            catch (Exception e) { Registro.Erro("ler cameras.json", e); }
            return new List<CameraCfg>();
        }
        // (0.1.12) "F2.DP (NDI HX2, 192.168.100.190)" → "192.168.100.190": a imagem NDI já diz onde a câmera está
        public static string IpDoNdi(string fonte)
        {
            var m = System.Text.RegularExpressions.Regex.Match(fonte ?? "", @"\((?:[^()]*?[,\s])?(\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?\)\s*$");
            return m.Success ? m.Groups[1].Value : "";
        }
        public static void Salvar()
        {
            foreach (var c in Lista)
                if (string.IsNullOrWhiteSpace(c.Ip) && IpDoNdi(c.Fonte) is { Length: > 0 } ip) { c.Ip = ip; Registro.Info("câmera " + c.Nome + ": IP " + ip + " tirado do nome da imagem NDI"); }
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

        // (0.1.14) qualquer comando de fora (controle, joystick, roteiro, posição) interrompe o movimento programado da câmera
        public static Task<string> Enviar(CameraCfg c, byte[] cmd) { MovimentoPtz.Soltar(c?.Id); return EnviarDoMovimento(c, cmd); }
        internal static Task<string> EnviarDoMovimento(CameraCfg c, byte[] cmd) => Enviar(c?.Ip, c?.Porta ?? 0, c?.Protocolo, cmd);

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
                                // lê as respostas: as de rotina são descartadas; a de uma pergunta (posição, zoom) vai para quem perguntou
                                Task.Run(() =>
                                {
                                    var b = new byte[256]; var msg = new List<byte>();
                                    try
                                    {
                                        int n;
                                        while ((n = s0.Read(b, 0, b.Length)) > 0)
                                            for (int i = 0; i < n; i++)
                                            {
                                                msg.Add(b[i]); if (b[i] != 0xFF) continue;
                                                var m = msg.ToArray(); msg.Clear();
                                                Resposta(chave, m);
                                            }
                                    }
                                    catch { }
                                });
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

        // ---------- (0.1.10) perguntar à câmera onde ela está, e mandá-la para coordenadas exatas ----------
        static readonly Dictionary<string, TaskCompletionSource<byte[]>> esperando = new Dictionary<string, TaskCompletionSource<byte[]>>();
        static readonly System.Threading.SemaphoreSlim umaPergunta = new System.Threading.SemaphoreSlim(1, 1);

        // resposta que chegou pela conexão TCP: 90 4y FF / 90 5y FF (recebido/feito) são rotina; 90 50 … FF com dados
        // (ou um erro 90 6y …) é a resposta de uma pergunta
        static void Resposta(string chave, byte[] m)
        {
            if (m.Length < 3 || (m[0] & 0xF0) != 0x90) return;
            bool dados = m[1] == 0x50 && m.Length > 3, erro = (m[1] & 0xF0) == 0x60;
            if (!dados && !erro) return;
            TaskCompletionSource<byte[]> t;
            lock (esperando) { if (!esperando.TryGetValue(chave, out t)) return; esperando.Remove(chave); }
            t.TrySetResult(m);
        }

        // manda uma pergunta VISCA e espera a resposta com dados (null = sem resposta)
        static async Task<byte[]> Perguntar(CameraCfg c, byte[] cmd)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Ip)) return null;
            int porta = c.Porta > 0 ? c.Porta : Cameras.PortaPadrao(c.Protocolo);
            await umaPergunta.WaitAsync().ConfigureAwait(false);
            try
            {
                if (c.Protocolo == "tcp")
                {
                    var chave = c.Ip + ":" + porta;
                    var t = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (esperando) esperando[chave] = t;
                    var e = await EnviarDoMovimento(c, cmd).ConfigureAwait(false);
                    if (e != null) { lock (esperando) esperando.Remove(chave); return null; }
                    var ok = await Task.WhenAny(t.Task, Task.Delay(1500)).ConfigureAwait(false);
                    lock (esperando) esperando.Remove(chave);
                    return ok == t.Task ? t.Task.Result : null;
                }
                // UDP: a resposta volta para a mesma porta de onde saiu o pedido
                var e2 = await EnviarDoMovimento(c, cmd).ConfigureAwait(false);
                if (e2 != null) return null;
                var fim = DateTime.Now.AddMilliseconds(1500);
                while (DateTime.Now < fim)
                {
                    var r = udp.ReceiveAsync();
                    if (await Task.WhenAny(r, Task.Delay(fim - DateTime.Now)).ConfigureAwait(false) != r) return null;
                    var res = r.Result; if (res.RemoteEndPoint.Address.ToString() != c.Ip) continue;
                    var d = res.Buffer;
                    if (c.Protocolo != "udp" && d.Length > 8 && d[0] == 0x01) d = d.Skip(8).ToArray();   // tira o cabeçalho do VISCA sobre IP
                    if (d.Length < 3 || (d[0] & 0xF0) != 0x90) continue;
                    if ((d[1] == 0x50 && d.Length > 3) || (d[1] & 0xF0) == 0x60) return d;
                }
                return null;
            }
            catch (Exception ex) { Registro.Erro("pergunta VISCA", ex); return null; }
            finally { umaPergunta.Release(); }
        }

        static int Nibbles(byte[] d, int de, int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 4 | (d[de + i] & 0x0F); return v; }

        // onde a câmera está agora: pan/tilt (8x 09 06 12 FF) e zoom (8x 09 04 47 FF)
        public static async Task<(PtzCena pos, string erro)> LerPosicao(CameraCfg c, bool comZoom = true)
        {
            if (c == null) return (null, "escolha a câmera");
            if (string.IsNullOrWhiteSpace(c.Ip)) return (null, $"a câmera \"{c.Nome}\" está sem IP (Ajustes › Câmeras)");
            var pt = await Perguntar(c, new byte[] { 0x81, 0x09, 0x06, 0x12, 0xFF }).ConfigureAwait(false);
            if (pt == null) return (null, $"a câmera \"{c.Nome}\" não respondeu onde está (sem resposta à pergunta de posição VISCA)");
            if ((pt[1] & 0xF0) == 0x60) return (null, $"a câmera \"{c.Nome}\" recusou a pergunta de posição (erro VISCA {pt[2]:X2})");
            int dig = pt.Length - 3;                       // 90 50 [dígitos] FF
            int np = dig == 8 ? 4 : dig == 9 ? 5 : dig == 10 ? 5 : -1, nt = dig - np;
            if (np < 0) return (null, $"resposta de posição estranha da câmera ({dig} dígitos)");
            var p = new PtzCena { Camera = c.Id, NPan = np, NTilt = nt, Pan = Nibbles(pt, 2, np), Tilt = Nibbles(pt, 2 + np, nt), Quando = DateTime.Now.ToString("dd/MM HH:mm") };
            if (!comZoom) return (p, null);
            var z = await Perguntar(c, new byte[] { 0x81, 0x09, 0x04, 0x47, 0xFF }).ConfigureAwait(false);
            if (z != null && z.Length == 7 && z[1] == 0x50) p.Zoom = Nibbles(z, 2, 4);
            return (p, null);
        }

        static byte[] Digitos(int v, int n) { var b = new byte[n]; for (int i = n - 1; i >= 0; i--) { b[i] = (byte)(v & 0x0F); v >>= 4; } return b; }

        // leva a câmera às coordenadas salvas (primeiro o zoom, depois pan/tilt — são comandos de grupos diferentes e andam juntos)
        // doMovimento = quem manda é o próprio movimento programado (não o interrompe)
        public static async Task<string> IrPara(CameraCfg c, PtzCena p, int velPan = 0x18, int velTilt = 0x14, bool doMovimento = false)
        {
            if (c == null || p == null) return "sem câmera ou sem posição";
            if (!doMovimento) MovimentoPtz.Soltar(c.Id);
            string e = null;
            if (p.Zoom >= 0)
            {
                e = await Zoom(c, p.Zoom).ConfigureAwait(false);
                await Task.Delay(40).ConfigureAwait(false);
            }
            return await PanTilt(c, p, velPan, velTilt).ConfigureAwait(false) ?? e;
        }
        internal static Task<string> Zoom(CameraCfg c, int zoom)
        {
            var zc = new List<byte> { 0x81, 0x01, 0x04, 0x47 }; zc.AddRange(Digitos(Math.Clamp(zoom, 0, 0xFFFF), 4)); zc.Add(0xFF);
            return EnviarDoMovimento(c, zc.ToArray());
        }
        internal static Task<string> PanTilt(CameraCfg c, PtzCena p, int velPan, int velTilt)
        {
            var pc = new List<byte> { 0x81, 0x01, 0x06, 0x02, (byte)Math.Clamp(velPan, 1, 0x18), (byte)Math.Clamp(velTilt, 1, 0x14) };
            pc.AddRange(Digitos(p.Pan, p.NPan)); pc.AddRange(Digitos(p.Tilt, p.NTilt)); pc.Add(0xFF);
            return EnviarDoMovimento(c, pc.ToArray());
        }
    }

    // (0.1.14) Movimento programado da cena (backlog 5.6): quando a cena entra no ar, a câmera anda devagar do ponto inicial
    // (a posição salva na cena) até o ponto final. Pan/tilt num só comando de posição com velocidades proporcionais à distância
    // de cada eixo (anda quase em linha reta); o zoom acompanha em passos pequenos, na mesma proporção do caminho já andado
    // (o Cast pergunta à câmera onde ela está a cada ~¼ s). Vai e volta: repete até a cena sair do ar.
    // Qualquer outro comando para a mesma câmera (controle, joystick, roteiro, outra cena) interrompe o movimento.
    public static class MovimentoPtz
    {
        sealed class Rodando { public string Cena; public CameraCfg Cam; public System.Threading.CancellationTokenSource Cts; }
        static readonly Dictionary<string, Rodando> porCam = new Dictionary<string, Rodando>();
        static readonly int[] velPT = { 1, 1, 2, 4, 8 };          // velocidade VISCA do eixo que anda mais, por nível (1 a 4)
        static readonly int[] segZoom = { 40, 40, 25, 12, 5 };    // só zoom (sem pan/tilt): duração em segundos por nível

        public static bool Andando(string cena) { lock (porCam) return porCam.Values.Any(r => r.Cena == cena); }

        public static void Iniciar(string cena, CameraCfg cam, PtzCena ini, PtzCena fim, int nivel, bool vaiVolta)
        {
            if (cam == null || ini == null || fim == null) return;
            Soltar(cam.Id);
            var r = new Rodando { Cena = cena, Cam = cam, Cts = new System.Threading.CancellationTokenSource() };
            lock (porCam) porCam[cam.Id] = r;
            Registro.Info($"movimento programado: {cam.Nome} ({ini.Texto()} → {fim.Texto()}, nível {nivel}{(vaiVolta ? ", vai e volta" : "")})");
            _ = Task.Run(() => Rodar(r, ini.Copia(), fim.Copia(), Math.Clamp(nivel, 1, 4), vaiVolta));
        }
        // para o movimento desta câmera (sem frear: o próximo comando, se houver, já manda nela)
        public static void Soltar(string camId)
        {
            if (string.IsNullOrEmpty(camId)) return;
            Rodando r; lock (porCam) { if (!porCam.TryGetValue(camId, out r)) return; porCam.Remove(camId); }
            r.Cts.Cancel();
        }
        public static void PararCena(string cena)
        {
            if (string.IsNullOrEmpty(cena)) return;
            List<string> cams; lock (porCam) cams = porCam.Where(k => k.Value.Cena == cena).Select(k => k.Key).ToList();
            foreach (var id in cams) Soltar(id);
        }

        static int Sinal(int v, int n) { int lim = 1 << (4 * n); return v >= lim / 2 ? v - lim : v; }
        static int Dp(PtzCena a, PtzCena b) => Sinal(b.Pan, b.NPan) - Sinal(a.Pan, a.NPan);
        static int Dt(PtzCena a, PtzCena b) => Sinal(b.Tilt, b.NTilt) - Sinal(a.Tilt, a.NTilt);
        static bool Perto(PtzCena a, PtzCena b, int tol) => Math.Abs(Dp(a, b)) <= tol && Math.Abs(Dt(a, b)) <= tol;

        static async Task Rodar(Rodando r, PtzCena ini, PtzCena fim, int nivel, bool vaiVolta)
        {
            var ct = r.Cts.Token; var cam = r.Cam;
            try
            {
                // primeiro, o ponto inicial (normalmente a câmera já está lá: foi no Preview)
                var (agora, _) = await ViscaRede.LerPosicao(cam, false).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (agora == null || !Perto(agora, ini, 8))
                {
                    await ViscaRede.IrPara(cam, ini, doMovimento: true).ConfigureAwait(false);
                    await Chegar(cam, ini, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
                await Task.Delay(400, ct).ConfigureAwait(false);
                var de = ini; var para = fim;
                while (true)
                {
                    await Andar(cam, de, para, nivel, ct).ConfigureAwait(false);
                    if (!vaiVolta) break;
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    (de, para) = (para, de);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Registro.Erro("movimento programado", e); }
            finally { lock (porCam) if (porCam.TryGetValue(cam.Id, out var x) && x == r) porCam.Remove(cam.Id); }
        }

        // espera a câmera chegar (ou parar de mexer)
        static async Task Chegar(CameraCfg cam, PtzCena alvo, TimeSpan limite, System.Threading.CancellationToken ct)
        {
            var fimEm = DateTime.Now + limite; PtzCena ant = null; int parado = 0;
            while (DateTime.Now < fimEm)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var (p, _) = await ViscaRede.LerPosicao(cam, false).ConfigureAwait(false);
                if (p == null) continue;
                if (Perto(p, alvo, 8)) return;
                if (ant != null && Perto(p, ant, 0)) { if (++parado >= 4) return; } else parado = 0;
                ant = p;
            }
        }

        static async Task Andar(CameraCfg cam, PtzCena de, PtzCena para, int nivel, System.Threading.CancellationToken ct)
        {
            int adp = Math.Abs(Dp(de, para)), adt = Math.Abs(Dt(de, para));
            bool zoom = de.Zoom >= 0 && para.Zoom >= 0 && de.Zoom != para.Zoom;
            int zUlt = -1;
            async Task Z(double f)
            {
                if (!zoom) return;
                int z = (int)Math.Round(de.Zoom + (para.Zoom - de.Zoom) * Math.Clamp(f, 0, 1));
                if (z == zUlt) return; zUlt = z;
                ct.ThrowIfCancellationRequested();
                await ViscaRede.Zoom(cam, z).ConfigureAwait(false);
            }
            var t0 = DateTime.Now;
            if (adp + adt > 0)
            {
                int v = velPT[nivel], vp, vt;
                if (adp >= adt) { vp = v; vt = Math.Max(1, (int)Math.Round(v * (double)adt / adp)); }
                else { vt = Math.Min(v, 0x14); vp = Math.Max(1, (int)Math.Round(vt * (double)adp / adt)); }
                ct.ThrowIfCancellationRequested();
                await ViscaRede.PanTilt(cam, para, vp, vt).ConfigureAwait(false);
                int tol = Math.Max(3, Math.Max(adp, adt) / 100); PtzCena ant = null; int parado = 0;
                while ((DateTime.Now - t0).TotalMinutes < 10)
                {
                    await Task.Delay(220, ct).ConfigureAwait(false);
                    var (p, _) = await ViscaRede.LerPosicao(cam, false).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (p == null) continue;
                    double f = adp >= adt ? 1 - Math.Abs(Dp(p, para)) / (double)adp : 1 - Math.Abs(Dt(p, para)) / (double)adt;
                    await Z(f).ConfigureAwait(false);
                    if (Perto(p, para, tol)) break;
                    // parou antes de chegar (bateu no limite, ou alguém mexeu): termina este trecho
                    if (ant != null && Perto(p, ant, 0)) { if (++parado >= 10 && (DateTime.Now - t0).TotalSeconds > 3) break; } else parado = 0;
                    ant = p;
                }
                await Z(1).ConfigureAwait(false);
            }
            else if (zoom)
            {
                double dur = segZoom[nivel];
                while (true)
                {
                    double f = (DateTime.Now - t0).TotalSeconds / dur;
                    await Z(f).ConfigureAwait(false);
                    if (f >= 1) break;
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
