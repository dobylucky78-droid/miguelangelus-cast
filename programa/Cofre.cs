// Guarda segredos (chaves de transmissão) criptografados pelo Windows (DPAPI), só para este usuário neste computador.
// O arquivo não serve em outro computador nem em outra conta; nunca vai para backup, nuvem ou repositório.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MiguelAngelusCast
{
    static class Cofre
    {
        static readonly string arquivo = Path.Combine(Program.PastaDados, "chaves.dat");
        static readonly object trava = new object();
        static Dictionary<string, string> itens;

        public static string Ler(string nome) { lock (trava) { Carregar(); return itens.TryGetValue(nome, out var v) ? v : null; } }
        public static bool Tem(string nome) => !string.IsNullOrEmpty(Ler(nome));
        public static void Gravar(string nome, string valor)
        {
            lock (trava)
            {
                Carregar();
                if (string.IsNullOrEmpty(valor)) itens.Remove(nome); else itens[nome] = valor.Trim();
                var cru = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(itens));
                File.WriteAllBytes(arquivo, Proteger(cru, true));
            }
        }
        // para mostrar na tela sem revelar: só os 4 últimos caracteres
        public static string Mascara(string nome) { var v = Ler(nome); return string.IsNullOrEmpty(v) ? "" : "••••••" + (v.Length > 4 ? v.Substring(v.Length - 4) : ""); }

        static void Carregar()
        {
            if (itens != null) return;
            itens = new Dictionary<string, string>();
            try { if (File.Exists(arquivo)) itens = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(Proteger(File.ReadAllBytes(arquivo), false))) ?? itens; }
            catch (Exception e) { Registro.Info("cofre ilegível (outro computador ou outra conta?): " + e.Message); }
        }

        [StructLayout(LayoutKind.Sequential)] struct Blob { public int n; public IntPtr p; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob entrada, string descricao, IntPtr entropia, IntPtr r, IntPtr prompt, int flags, ref Blob saida);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob entrada, IntPtr descricao, IntPtr entropia, IntPtr r, IntPtr prompt, int flags, ref Blob saida);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        internal static byte[] Proteger(byte[] dados, bool proteger)
        {
            var e = new Blob { n = dados.Length, p = Marshal.AllocHGlobal(Math.Max(1, dados.Length)) };
            var s = new Blob();
            try
            {
                Marshal.Copy(dados, 0, e.p, dados.Length);
                bool ok = proteger ? CryptProtectData(ref e, "MiguelAngelus Cast", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* sem interface */, ref s)
                                   : CryptUnprotectData(ref e, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref s);
                if (!ok) throw new InvalidOperationException("DPAPI falhou: " + Marshal.GetLastWin32Error());
                var r = new byte[s.n]; Marshal.Copy(s.p, r, 0, s.n); return r;
            }
            finally { Marshal.FreeHGlobal(e.p); if (s.p != IntPtr.Zero) LocalFree(s.p); }
        }
    }
}
