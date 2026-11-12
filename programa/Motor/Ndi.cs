// Recepção NDI (usa o NDI Runtime instalado, o mesmo do MiguelAngelus e do OBS)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiguelAngelusCast.Motor
{
    static class Ndi
    {
        const string Dll = "Processing.NDI.Lib.x64.dll";

        [StructLayout(LayoutKind.Sequential)] struct FindCreate { [MarshalAs(UnmanagedType.U1)] public bool locais; public IntPtr grupos; public IntPtr ips; }
        [StructLayout(LayoutKind.Sequential)] public struct Origem { public IntPtr nome; public IntPtr url; }
        [StructLayout(LayoutKind.Sequential)] struct RecvCreate { public Origem fonte; public int cor; public int banda; [MarshalAs(UnmanagedType.U1)] public bool campos; public IntPtr nome; }
        [StructLayout(LayoutKind.Sequential)]
        public struct QuadroVideo
        {
            public int largura, altura; public uint fourCC; public int taxaN, taxaD; public float proporcao; public int formato;
            public long timecode; public IntPtr dados; public int bytesPorLinha; public IntPtr metadados; public long timestamp;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] static extern bool NDIlib_initialize();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr NDIlib_find_create_v2(ref FindCreate c);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr NDIlib_find_get_current_sources(IntPtr f, out uint n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr NDIlib_recv_create_v3(ref RecvCreate c);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int NDIlib_recv_capture_v2(IntPtr r, ref QuadroVideo v, IntPtr a, IntPtr m, uint espera);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_free_video_v2(IntPtr r, ref QuadroVideo v);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_destroy(IntPtr r);

        static IntPtr busca;
        static bool tentou;
        static string erro;
        public static string Erro { get => inicio != null && !inicio.IsCompleted ? "O NDI ainda está iniciando… (alguns segundos)" : erro; private set => erro = value; }
        static readonly object trava = new object();

        // (0.1.1) O NDI leva vários segundos para iniciar (11 s num PC de casa). Começa ao abrir o Cast, num fio à parte, e
        // ninguém espera por ele: enquanto não fica pronto, a lista de fontes vem vazia e quem precisa tenta de novo.
        static System.Threading.Tasks.Task<bool> inicio;
        static readonly object travaInicio = new object();
        public static System.Threading.Tasks.Task<bool> IniciarAoFundo() { lock (travaInicio) return inicio ??= System.Threading.Tasks.Task.Run(Iniciar); }
        public static bool Pronto => inicio != null && inicio.IsCompleted && inicio.Result;

        public static bool Iniciar()
        {
            lock (trava)
            {
                if (tentou) return busca != IntPtr.Zero;
                tentou = true;
                foreach (var p in new[] { Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"), Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"), @"C:\Program Files\NDI\NDI 6 Tools\Runtime", @"C:\Program Files\NDI\NDI 6 Runtime\v6" })
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    var dll = Path.Combine(p, Dll);
                    if (!File.Exists(dll) || !NativeLibrary.TryLoad(dll, out _)) continue;
                    if (!NDIlib_initialize()) { Erro = "O NDI não iniciou neste computador."; return false; }
                    var c = new FindCreate { locais = true };
                    busca = NDIlib_find_create_v2(ref c);
                    Registro.Info("NDI pronto: " + dll);
                    return true;
                }
                Erro = "NDI Runtime não encontrado (instale o NDI Tools, o mesmo usado pelo OBS).";
                Registro.Info(Erro);
                return false;
            }
        }

        public static List<(string nome, Origem origem)> Fontes()
        {
            var l = new List<(string, Origem)>();
            var t = IniciarAoFundo();
            if (!t.IsCompleted || !t.Result) return l;       // ainda iniciando (ou sem NDI): não trava quem chamou
            var p = NDIlib_find_get_current_sources(busca, out uint n);
            int tam = Marshal.SizeOf<Origem>();
            for (int i = 0; i < n; i++)
            {
                var f = Marshal.PtrToStructure<Origem>(p + i * tam);
                l.Add((Marshal.PtrToStringUTF8(f.nome) ?? "", f));
            }
            return l;
        }

        // Recebe uma fonte e guarda sempre o quadro mais novo (BGRA). Cor 0 = BGRX/BGRA; banda 100 = qualidade total.
        public sealed class Receptor : IDisposable
        {
            IntPtr recv; readonly Thread laco; volatile bool rodando = true;
            readonly object trava = new object();
            byte[] buf = Array.Empty<byte>(); int w, h; long seq; bool alfa;
            public long Recebidos;

            public Receptor(Origem o, string nomeReceptor)
            {
                var nome = Marshal.StringToHGlobalAnsi(nomeReceptor);
                var c = new RecvCreate { fonte = o, cor = 0, banda = 100, campos = false, nome = nome };
                recv = NDIlib_recv_create_v3(ref c);
                laco = new Thread(Rodar) { IsBackground = true, Name = "ndi " + nomeReceptor };
                laco.Start();
            }

            void Rodar()
            {
                while (rodando)
                {
                    var q = new QuadroVideo();
                    if (NDIlib_recv_capture_v2(recv, ref q, IntPtr.Zero, IntPtr.Zero, 200) != 1) continue;
                    lock (trava)
                    {
                        int linha = q.largura * 4;
                        if (buf.Length != linha * q.altura) buf = new byte[linha * q.altura];
                        if (q.bytesPorLinha == linha) Marshal.Copy(q.dados, buf, 0, linha * q.altura);
                        else for (int y = 0; y < q.altura; y++) Marshal.Copy(q.dados + y * q.bytesPorLinha, buf, y * linha, linha);
                        w = q.largura; h = q.altura; seq++; alfa = q.fourCC == 0x41524742;   // BGRA (com alfa) ou BGRX
                    }
                    Recebidos++;
                    NDIlib_recv_free_video_v2(recv, ref q);
                }
            }

            // copia o quadro mais novo para "destino", se chegou um novo desde "ultimo"
            public bool Pegar(Imagem destino, ref long ultimo)
            {
                lock (trava)
                {
                    if (seq == ultimo || w == 0) return false;
                    destino.Garantir(w, h);
                    Buffer.BlockCopy(buf, 0, destino.Px, 0, buf.Length);
                    destino.TemAlfa = alfa;
                    ultimo = seq;
                    return true;
                }
            }

            public void Dispose()
            {
                rodando = false; laco.Join(1000);
                if (recv != IntPtr.Zero) NDIlib_recv_destroy(recv);
                recv = IntPtr.Zero;
            }
        }
    }
}
