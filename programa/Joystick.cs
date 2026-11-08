// Joysticks (controles de Xbox e compatíveis, como o GameSir) lidos direto do Windows (XInput), para a tela.
// O mesmo do MiguelAngelus (app\Joystick.cs): lendo aqui, o joystick continua mexendo na câmera com o Cast minimizado.
// Manda {tipo:'joy', pads:[...]} só quando algo muda. A configuração fica em joystick.json (a tela decide o que cada botão faz).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace MiguelAngelusCast
{
    sealed class LeitorJoystick : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Controle { public ushort Botoes; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)]
        struct Estado { public uint Pacote; public Controle C; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int Ler14(int i, out Estado e);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int Ler91(int i, out Estado e);
        static bool usar91;
        static int Ler(int i, out Estado e)
        {
            if (!usar91) try { return Ler14(i, out e); } catch (DllNotFoundException) { usar91 = true; }
            try { return Ler91(i, out e); } catch (DllNotFoundException) { e = default; return 1167; }   // sem XInput
        }

        readonly Timer relogio = new Timer { Interval = 25 };
        readonly Action<object> enviar;
        readonly long[] assinatura = new long[4];   // estado arredondado: o ruído da alavanca parada não conta como mudança
        readonly bool[] ligado = new bool[4];
        readonly int[] procurarEm = new int[4];      // controle desligado: procura de novo a cada ~1,5 s (a leitura dele é lenta)
        int volta;

        public LeitorJoystick(Action<object> enviar)
        {
            this.enviar = enviar;
            relogio.Tick += (s, e) => Ler();
            relogio.Start();
        }

        void Ler()
        {
            volta++;
            bool mudou = false;
            var pads = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                if (!ligado[i] && volta < procurarEm[i]) continue;
                bool ok = Ler(i, out var e) == 0;
                if (!ok) { if (ligado[i]) mudou = true; ligado[i] = false; procurarEm[i] = volta + 60; continue; }
                var c = e.C;
                long a = c.Botoes | (long)(c.LT >> 4) << 16 | (long)(c.RT >> 4) << 20 | (long)((c.LX + 32768) >> 11) << 24 | (long)((c.LY + 32768) >> 11) << 29
                         | (long)((c.RX + 32768) >> 11) << 34 | (long)((c.RY + 32768) >> 11) << 39;
                if (!ligado[i] || a != assinatura[i]) mudou = true;
                ligado[i] = true; assinatura[i] = a;
                pads.Add(new { i, b = (int)c.Botoes, lt = (int)c.LT, rt = (int)c.RT, lx = (int)c.LX, ly = (int)c.LY, rx = (int)c.RX, ry = (int)c.RY });
            }
            if (mudou) enviar(new { tipo = "joy", pads });
        }

        public void Dispose() { relogio.Stop(); relogio.Dispose(); }

        // ---------- configuração (o que cada botão faz, perfis): a tela monta, aqui só guarda ----------
        static readonly string arquivo = Path.Combine(Program.PastaDados, "joystick.json");
        public static JsonElement? Carregar()
        {
            try { if (File.Exists(arquivo)) using (var d = JsonDocument.Parse(File.ReadAllText(arquivo))) return d.RootElement.Clone(); }
            catch (Exception e) { Registro.Erro("ler joystick.json", e); }
            return null;
        }
        public static void Gravar(JsonElement dados)
        {
            if (dados.ValueKind != JsonValueKind.Object) return;
            var txt = dados.GetRawText(); if (txt.Length > 500_000) return;
            try { File.WriteAllText(arquivo, txt); } catch (Exception e) { Registro.Erro("gravar joystick.json", e); }
        }
    }
}
