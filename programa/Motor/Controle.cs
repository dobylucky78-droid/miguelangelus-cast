// (0.1.5) Controle por botões (Stream Deck, Stream Dock da Mirabox/Ajazz, atalhos do Windows).
// Uma porta só deste computador (127.0.0.1, nunca a rede) recebe comandos curtos:
//     http://127.0.0.1:8940/cmd/transicao?chave=XXXX
// A chave (gerada uma vez, em controle.json na pasta de dados) impede que uma página aberta no navegador mande comandos.
// Para os aparelhos que só sabem "abrir um programa", o próprio Cast faz o papel de botão:
//     "MiguelAngelusCast.exe" /botao transicao            (manda o comando para o Cast aberto e sai, sem janela)
// /estado devolve JSON (Programa, Preview, gravando, no ar, mudo, cenas) para um plugin com retorno nas teclas, no futuro.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public static class Controle
    {
        public const int PortaPadrao = 8940;
        static readonly string arquivo = Path.Combine(Program.PastaDados, "controle.json");
        sealed class Cfg { public int Porta { get; set; } = PortaPadrao; public string Chave { get; set; } }

        public static int Porta { get; private set; }
        public static string Chave { get; private set; }
        public static string Erro { get; private set; } = "";
        public static string Ultimo { get; private set; } = "";   // o último comando recebido (para a tela)

        // (comando, parâmetros) → resposta curta; roda na thread da tela
        public static Func<string, Dictionary<string, string>, string> Executar;
        public static Func<object> Estado;

        static TcpListener ouvinte;

        static Cfg Ler()
        {
            try { return JsonSerializer.Deserialize<Cfg>(File.ReadAllText(arquivo)); } catch { return null; }
        }

        public static void Iniciar()
        {
            var c = Ler() ?? new Cfg();
            if (string.IsNullOrWhiteSpace(c.Chave) || c.Chave.Length < 8)
                c.Chave = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            Chave = c.Chave;
            // a porta combinada; se outro programa estiver nela, a próxima livre (até 8949), e fica guardada
            for (int p = c.Porta; p < c.Porta + 10; p++)
            {
                try { ouvinte = new TcpListener(IPAddress.Loopback, p); ouvinte.Start(); Porta = p; break; }
                catch (SocketException) { ouvinte = null; }
            }
            if (ouvinte == null) { Erro = "porta " + c.Porta + " ocupada"; Registro.Info("controle: " + Erro); return; }
            c.Porta = Porta;
            try { File.WriteAllText(arquivo, JsonSerializer.Serialize(c)); } catch (Exception e) { Registro.Erro("controle.json", e); }
            Registro.Info("controle por botões em 127.0.0.1:" + Porta);
            // (0.1.11) cópia de teste (CAST_DADOS) não mexe no plugin instalado: ele ficaria com a porta e a chave da cópia
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CAST_DADOS")))
                try { AtualizarPlugins(false); } catch (Exception e) { Registro.Erro("plugin do Stream Dock", e); }
            new Thread(Laco) { IsBackground = true, Name = "controle" }.Start();
        }

        public static void Parar() { try { ouvinte?.Stop(); } catch { } }

        static void Laco()
        {
            while (true)
            {
                TcpClient cli;
                try { cli = ouvinte.AcceptTcpClient(); } catch { return; }
                _ = Task.Run(() => Atender(cli));
            }
        }

        static async Task Atender(TcpClient cli)
        {
            using (cli)
            {
                try
                {
                    cli.ReceiveTimeout = 2000; cli.SendTimeout = 2000;
                    var s = cli.GetStream();
                    var buf = new byte[8192]; int n = 0;
                    // só o cabeçalho interessa (comandos vêm no endereço; o corpo de um POST é ignorado)
                    while (n < buf.Length)
                    {
                        int r = s.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r;
                        if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                    }
                    var cab = Encoding.ASCII.GetString(buf, 0, n);
                    var linha = cab.Split("\r\n")[0].Split(' ');
                    if (linha.Length < 2) return;
                    if (linha[0] == "OPTIONS") { Responder(s, 204, ""); return; }
                    var (caminho, q) = Dividir(linha[1]);
                    q.TryGetValue("chave", out var chave);
                    if (caminho == "/" || caminho == "")
                    { Responder(s, 200, JsonSerializer.Serialize(new { programa = "MiguelAngelus Cast", versao = Program.Versao })); return; }
                    if (chave != Chave) { Responder(s, 403, "{\"ok\":false,\"msg\":\"chave errada\"}"); return; }

                    if (caminho == "/estado")
                    {
                        var est = await NaTela(() => Estado?.Invoke());
                        Responder(s, 200, JsonSerializer.Serialize(est)); return;
                    }
                    if (caminho.StartsWith("/cmd/"))
                    {
                        var cmd = caminho.Substring(5).Trim('/').ToLowerInvariant();
                        var resp = await NaTela(() => Executar?.Invoke(cmd, q) ?? "Cast ainda abrindo");
                        bool ok = resp != null && !resp.StartsWith("!");
                        var msg = resp == null ? "" : resp.TrimStart('!');
                        Ultimo = cmd;
                        Responder(s, ok ? 200 : 400, JsonSerializer.Serialize(new { ok, cmd, msg }));
                        return;
                    }
                    Responder(s, 404, "{\"ok\":false,\"msg\":\"comando não existe\"}");
                }
                catch (IOException) { }          // o outro lado fechou no meio (ex.: o Stream Dock reabrindo): normal
                catch (SocketException) { }
                catch (Exception e) { Registro.Erro("controle", e); }
            }
        }

        // a tela (Principal) dá o jeito de rodar na thread dela
        public static Func<Func<object>, Task<object>> NaTelaFn;
        static async Task<T> NaTela<T>(Func<T> f) where T : class
        {
            if (NaTelaFn == null) return null;
            var t = NaTelaFn(() => f());
            var fim = await Task.WhenAny(t, Task.Delay(10000));     // ligar o codificador para gravar leva alguns segundos
            return fim == t ? (T)t.Result : null;
        }

        static (string, Dictionary<string, string>) Dividir(string alvo)
        {
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = alvo.IndexOf('?');
            var cam = i < 0 ? alvo : alvo.Substring(0, i);
            if (i >= 0)
                foreach (var par in alvo.Substring(i + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int e = par.IndexOf('=');
                    var k = Uri.UnescapeDataString((e < 0 ? par : par.Substring(0, e)).Replace('+', ' '));
                    var v = e < 0 ? "" : Uri.UnescapeDataString(par.Substring(e + 1).Replace('+', ' '));
                    q[k] = v;
                }
            return (Uri.UnescapeDataString(cam).ToLowerInvariant(), q);
        }

        static void Responder(Stream s, int cod, string corpo)
        {
            var b = Encoding.UTF8.GetBytes(corpo);
            var txt = cod == 200 ? "OK" : cod == 204 ? "No Content" : cod == 403 ? "Forbidden" : cod == 404 ? "Not Found" : "Bad Request";
            // (0.1.6) o plugin do Stream Dock roda numa página local (origem "null") e precisa ler o /estado;
            // quem protege é a chave, que uma página da internet não tem
            var h = $"HTTP/1.1 {cod} {txt}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            s.Write(Encoding.ASCII.GetBytes(h)); s.Write(b); s.Flush();
        }

        public static string Url(string cmd, string extra = null) =>
            $"http://127.0.0.1:{Porta}/cmd/{cmd}?{(string.IsNullOrEmpty(extra) ? "" : extra + "&")}chave={Chave}";

        // ---------- (0.1.6) plugin "MiguelAngelus Cast" para o Stream Dock (Mirabox/Ajazz) e o Stream Deck (Elgato) ----------
        // O plugin vai junto do Cast (pasta streamdock\). Instalar = copiar para a pasta de plugins do programa do aparelho
        // e escrever cast.js com a porta e a chave. A cada abertura do Cast, o cast.js é reescrito e, se o plugin que veio
        // com o Cast for mais novo, ele é copiado de novo.
        public const string PluginId = "com.miguelangelus.cast.sdPlugin";
        static string PluginOrigem => Path.Combine(Program.Pasta, "streamdock", PluginId);
        static readonly (string nome, string pasta, string exe)[] Programas =
        {
            ("Stream Dock", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotSpot", "StreamDock", "plugins"), "StreamDock"),
            ("Stream Deck", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "Plugins"), "StreamDeck"),
        };

        public static object PluginSituacao() => Programas.Select(p => new
        {
            p.nome, temPrograma = Directory.Exists(p.pasta), instalado = File.Exists(Path.Combine(p.pasta, PluginId, "manifest.json"))
        }).ToList();

        static string VersaoManifest(string pasta)
        {
            try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(pasta, "manifest.json"))); return d.RootElement.GetProperty("Version").GetString(); } catch { return ""; }
        }

        static void Copiar(string de, string para)
        {
            Directory.CreateDirectory(para);
            foreach (var f in Directory.GetFiles(de, "*", SearchOption.AllDirectories))
            {
                var alvo = Path.Combine(para, Path.GetRelativePath(de, f));
                Directory.CreateDirectory(Path.GetDirectoryName(alvo)); File.Copy(f, alvo, true);
            }
        }

        static void EscreverCast(string pasta) =>
            File.WriteAllText(Path.Combine(pasta, "cast.js"), $"// escrito pelo MiguelAngelus Cast {Program.Versao}: porta e chave deste computador\nconst CAST = {{ porta: {Porta}, chave: \"{Chave}\" }};\n", new UTF8Encoding(false));

        // instalar: true = o botão dos Ajustes (instala onde houver o programa e reabre o programa); false = só manter em dia
        public static string AtualizarPlugins(bool instalar)
        {
            if (!Directory.Exists(PluginOrigem)) return "O plugin não veio junto do Cast (pasta streamdock).";
            var feitos = new List<string>();
            foreach (var p in Programas)
            {
                if (!Directory.Exists(p.pasta)) continue;
                var destino = Path.Combine(p.pasta, PluginId);
                bool existe = File.Exists(Path.Combine(destino, "manifest.json"));
                if (!existe && !instalar) continue;
                bool copiar = instalar || VersaoManifest(destino) != VersaoManifest(PluginOrigem);
                if (copiar) Copiar(PluginOrigem, destino);
                EscreverCast(destino);
                if (copiar) Reabrir(p.exe);
                feitos.Add(p.nome);
                Registro.Info($"plugin {(copiar ? "instalado" : "em dia")} no {p.nome}");
            }
            return feitos.Count == 0 ? "Não achei o programa do Stream Dock nem o do Stream Deck neste computador. Instale o do aparelho e tente de novo."
                : "Plugin instalado no " + string.Join(" e no ", feitos) + ". Procure a categoria \"MiguelAngelus Cast\" na lista de ações.";
        }

        // o programa do aparelho só lê os plugins ao abrir
        static void Reabrir(string exe)
        {
            foreach (var pr in System.Diagnostics.Process.GetProcessesByName(exe))
            {
                try
                {
                    var caminho = pr.MainModule?.FileName;
                    pr.Kill(); pr.WaitForExit(5000);
                    if (caminho != null) { Thread.Sleep(800); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(caminho) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(caminho) }); }
                }
                catch (Exception e) { Registro.Erro("reabrir " + exe, e); }
            }
        }

        // ---------- atalhos prontos (.lnk) e as figuras das teclas (.png 144×144) ----------
        public sealed class Tecla { public string Arquivo, Rotulo, Cmd, Extra; public System.Drawing.Color Cor; }

        public static void CriarAtalhos(string pasta, IEnumerable<Tecla> teclas)
        {
            Directory.CreateDirectory(pasta);
            var exe = Environment.ProcessPath;
            var tipo = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(tipo);
            try
            {
                foreach (var t in teclas)
                {
                    var dir = Path.GetDirectoryName(Path.Combine(pasta, t.Arquivo)); Directory.CreateDirectory(dir);
                    var baseNome = Path.Combine(pasta, t.Arquivo);
                    dynamic lnk = sh.CreateShortcut(baseNome + ".lnk");
                    lnk.TargetPath = exe;
                    lnk.Arguments = "/botao " + t.Cmd + (string.IsNullOrEmpty(t.Extra) ? "" : " " + t.Extra);
                    lnk.WorkingDirectory = Path.GetDirectoryName(exe);
                    lnk.WindowStyle = 7;
                    lnk.Description = "MiguelAngelus Cast: " + t.Rotulo.Replace("\n", " ");
                    lnk.Save();
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
                    Figura(t.Rotulo, t.Cor, baseNome + ".png");
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
        }

        // (0.1.11) o Cast mudou de pasta (ex.: do pen drive para a instalação): os atalhos já feitos passam a apontar para este .exe
        public static int ReapontarAtalhos(string pasta)
        {
            if (!Directory.Exists(pasta)) return 0;
            var exe = Environment.ProcessPath; int n = 0;
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                foreach (var f in Directory.GetFiles(pasta, "*.lnk", SearchOption.AllDirectories))
                {
                    dynamic lnk = sh.CreateShortcut(f);
                    try
                    {
                        string alvo = lnk.TargetPath;
                        if (string.Equals(Path.GetFileName(alvo), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase) && !string.Equals(alvo, exe, StringComparison.OrdinalIgnoreCase))
                        { lnk.TargetPath = exe; lnk.WorkingDirectory = Path.GetDirectoryName(exe); lnk.Save(); n++; }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk); }
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
            if (n > 0) Registro.Info($"{n} atalhos de botões apontados para {exe}");
            return n;
        }

        static void Figura(string rotulo, System.Drawing.Color cor, string arquivo)
        {
            using var bmp = new System.Drawing.Bitmap(144, 144);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(System.Drawing.Color.FromArgb(15, 20, 32));
                using (var b = new System.Drawing.SolidBrush(cor)) g.FillRectangle(b, 0, 118, 144, 26);
                using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(70, cor))) g.FillRectangle(b, 0, 0, 144, 118);
                // nome comprido numa linha só: quebra no espaço mais perto do meio
                if (!rotulo.Contains('\n') && rotulo.Contains(' '))
                {
                    using var f1 = new System.Drawing.Font("Segoe UI", 23, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                    if (g.MeasureString(rotulo, f1).Width > 136)
                    {
                        int meio = rotulo.Length / 2, melhor = -1;
                        for (int i = 0; i < rotulo.Length; i++) if (rotulo[i] == ' ' && (melhor < 0 || Math.Abs(i - meio) < Math.Abs(melhor - meio))) melhor = i;
                        rotulo = rotulo.Substring(0, melhor) + "\n" + rotulo.Substring(melhor + 1);
                    }
                }
                var linhas = rotulo.Split('\n');
                float tam = linhas.Length >= 3 ? 19 : 23;
                foreach (var l in linhas) while (tam > 11) { using var f0 = new System.Drawing.Font("Segoe UI", tam, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel); if (g.MeasureString(l, f0).Width <= 136) break; tam--; }
                using var f = new System.Drawing.Font("Segoe UI", tam, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                using var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
                g.DrawString(rotulo, f, System.Drawing.Brushes.White, new System.Drawing.RectangleF(2, 2, 140, 116), sf);
                using var fp = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                g.DrawString("CAST", fp, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 118, 144, 26), sf);
            }
            bmp.Save(arquivo, System.Drawing.Imaging.ImageFormat.Png);
        }

        // ---------- o lado do botão: MiguelAngelusCast.exe /botao <comando> [k=v ...] ----------
        public static int Botao(string[] args)
        {
            var c = Ler();
            if (c == null || string.IsNullOrEmpty(c.Chave)) return 2;          // o Cast nunca abriu neste computador
            var cmd = args.Length > 1 ? args[1] : "";
            var extra = new StringBuilder();
            for (int i = 2; i < args.Length; i++)
            {
                var a = args[i]; int e = a.IndexOf('=');
                if (e <= 0) continue;
                extra.Append(Uri.EscapeDataString(a.Substring(0, e))).Append('=').Append(Uri.EscapeDataString(a.Substring(e + 1))).Append('&');
            }
            try
            {
                using var t = new TcpClient();
                if (!t.ConnectAsync(IPAddress.Loopback, c.Porta).Wait(800)) return 3;   // o Cast não está aberto
                t.ReceiveTimeout = 11000;
                var s = t.GetStream();
                var req = $"GET /cmd/{Uri.EscapeDataString(cmd)}?{extra}chave={c.Chave} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
                s.Write(Encoding.ASCII.GetBytes(req));
                var buf = new byte[512]; int r = s.Read(buf, 0, buf.Length);
                return r > 12 && Encoding.ASCII.GetString(buf, 0, r).Contains(" 200 ") ? 0 : 1;
            }
            catch { return 3; }
        }
    }
}
