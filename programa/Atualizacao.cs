// (0.1.11) Atualização automática do MiguelAngelus Cast — o mesmo jeito do MiguelAngelus (app\Atualizacao.cs).
// Procura a versão mais nova nas "Releases" do repositório público no GitHub, baixa o instalador e SÓ instala se ele
// estiver assinado (Authenticode válido) pelo certificado do desenvolvedor. Fica desligada enquanto o próprio programa
// não estiver assinado (versões de teste nunca procuram atualização).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiguelAngelusCast
{
    static class Atualizacao
    {
        // Para testes: CAST_ATUALIZACAO_URL (uma página igual à da API do GitHub) e CAST_ATUALIZACAO_ASSINANTE
        const string UrlPadrao = "https://api.github.com/repos/dobylucky78-droid/miguelangelus-cast/releases/latest";
        const string AssinantePadrao = "Open Source Developer Marcio Aurelio Rios Martins";
        // o anexo com nome fixo é o do site (conta as instalações novas); a atualização prefere o com o número
        public const string NomeFixo = "Instalar-MiguelAngelus-Cast.exe";

        static string Url => Environment.GetEnvironmentVariable("CAST_ATUALIZACAO_URL") ?? UrlPadrao;
        static string Assinante => Environment.GetEnvironmentVariable("CAST_ATUALIZACAO_ASSINANTE") ?? AssinantePadrao;
        public static bool Teste => Environment.GetEnvironmentVariable("CAST_ATUALIZACAO_URL") != null;

        static readonly HttpClient http = CriarHttp();
        static HttpClient CriarHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MiguelAngelusCast/" + Program.Versao);
            return c;
        }

        static Version VersaoAtual => typeof(Atualizacao).Assembly.GetName().Version;

        // Só procura atualização se ESTE programa já estiver assinado (ou em teste)
        static bool? ligada;
        public static bool Ligada => Teste || (ligada ??= AssinadoPor(Environment.ProcessPath, Assinante, out _));

        // Achada na última verificação (para o aviso no topo e os Ajustes)
        public static object Ultima { get; private set; }

        static Version LerVersao(string tag)
        {
            var t = new string((tag ?? "").Trim().TrimStart('v', 'V', '.', ' ', '-').TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).TrimEnd('.');
            if (!t.Contains('.')) t += ".0";
            return Version.TryParse(t, out var v) ? v : null;
        }
        static Version Normalizar(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        // {tipo:"atualizacao", ok, ligada, atual, nova, notas, url, nome, tamanho, erro}
        public static async Task<object> Verificar()
        {
            var atual = Program.Versao;
            object r;
            try
            {
                if (!Ligada) r = new { tipo = "atualizacao", ok = true, ligada = false, atual, nova = (string)null };
                else
                {
                    using var resp = await http.GetAsync(Url).ConfigureAwait(false);
                    if ((int)resp.StatusCode == 404) r = new { tipo = "atualizacao", ok = true, ligada = true, atual, nova = (string)null };
                    else
                    {
                        resp.EnsureSuccessStatusCode();
                        using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                        var j = d.RootElement;
                        var v = LerVersao(j.TryGetProperty("tag_name", out var tg) ? tg.GetString() : null);
                        var exes = j.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                            ? a.EnumerateArray().Where(x => (x.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList()
                            : new System.Collections.Generic.List<JsonElement>();
                        var anexo = exes.Cast<JsonElement?>().FirstOrDefault(x => !string.Equals(x.Value.GetProperty("name").GetString(), NomeFixo, StringComparison.OrdinalIgnoreCase))
                            ?? exes.Cast<JsonElement?>().FirstOrDefault();
                        if (v == null || Normalizar(v) <= Normalizar(VersaoAtual) || anexo == null)
                            r = new { tipo = "atualizacao", ok = true, ligada = true, atual, nova = (string)null };
                        else
                            r = new
                            {
                                tipo = "atualizacao", ok = true, ligada = true, atual, nova = v.ToString(3),
                                notas = j.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "",
                                url = anexo.Value.GetProperty("browser_download_url").GetString(),
                                nome = anexo.Value.GetProperty("name").GetString(),
                                tamanho = anexo.Value.GetProperty("size").GetInt64()
                            };
                    }
                }
            }
            catch (Exception e)
            {
                Registro.Erro("procurar atualização", e);
                r = new { tipo = "atualizacao", ok = false, ligada = true, atual, nova = (string)null, erro = "Não consegui ver se há versão nova (sem internet?)." };
            }
            Ultima = r;
            return r;
        }

        // Baixa e confere a assinatura. Devolve o caminho baixado.
        public static async Task<string> BaixarEConferir(string url, Action<long, long> progresso)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && !(Teste && u.IsLoopback))) throw new InvalidOperationException("endereço inválido");
            var pasta = Path.Combine(Path.GetTempPath(), "MiguelAngelusCast-atualizacao");
            Directory.CreateDirectory(pasta);
            var destino = Path.Combine(pasta, "Instalar MiguelAngelus Cast.exe");
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                r.EnsureSuccessStatusCode();
                var total = r.Content.Headers.ContentLength ?? 0;
                using var de = await r.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var para = new FileStream(destino, FileMode.Create, FileAccess.Write);
                var buf = new byte[256 * 1024]; long feito = 0; int n;
                while ((n = await de.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0) { await para.WriteAsync(buf, 0, n).ConfigureAwait(false); feito += n; progresso(feito, total); }
            }
            if (!AssinadoPor(destino, Assinante, out var motivo))
            {
                try { File.Delete(destino); } catch { }
                throw new InvalidOperationException("o instalador baixado não tem a assinatura do MiguelAngelus Cast (" + motivo + "); nada foi instalado");
            }
            return destino;
        }

        // Abre o instalador sem perguntas; ele espera o Cast fechar, instala por cima e reabre (ver [Run] no .iss)
        public static void Instalar(string instalador)
        {
            if (Teste) return;   // em teste, só baixa e confere
            Process.Start(new ProcessStartInfo(instalador, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }

        // ---------- assinatura digital (Authenticode) ----------
        public static bool AssinadoPor(string arquivo, string nome, out string motivo)
        {
            motivo = null;
            if (!AssinaturaValida(arquivo)) { motivo = "sem assinatura válida"; return false; }
            try
            {
#pragma warning disable SYSLIB0057
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(arquivo));
#pragma warning restore SYSLIB0057
                var sujeito = cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
                if (cert.Subject.IndexOf(nome, StringComparison.OrdinalIgnoreCase) < 0 && sujeito.IndexOf(nome, StringComparison.OrdinalIgnoreCase) < 0)
                { motivo = "assinado por outra pessoa: " + sujeito; return false; }
                return true;
            }
            catch (Exception ex) { motivo = ex.Message; return false; }
        }

        static bool AssinaturaValida(string arquivo)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = arquivo };
            var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf(info));
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                var dados = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0,
                    dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pInfo, dwStateAction = 0, dwProvFlags = 0x00000080 /* revogação só da cadeia, em cache */
                };
                var acao = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, ref acao, ref dados) == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pInfo); }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid acao, ref WINTRUST_DATA dados);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
