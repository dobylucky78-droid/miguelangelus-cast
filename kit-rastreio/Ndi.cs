// Recepção NDI mínima (usa o NDI Runtime já instalado, igual ao MiguelAngelus)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class NdiRx
{
    const string Dll = "Processing.NDI.Lib.x64.dll";
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string c);

    [StructLayout(LayoutKind.Sequential)] public struct FindCreate { [MarshalAs(UnmanagedType.U1)] public bool locais; public IntPtr grupos; public IntPtr ips; }
    [StructLayout(LayoutKind.Sequential)] public struct Fonte { public IntPtr nome; public IntPtr url; }
    [StructLayout(LayoutKind.Sequential)] public struct RecvCreate { public Fonte fonte; public int cor; public int banda; [MarshalAs(UnmanagedType.U1)] public bool campos; public IntPtr nome; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Quadro
    {
        public int largura, altura; public uint fourCC; public int taxaN, taxaD; public float proporcao; public int formato;
        public long timecode; public IntPtr dados; public int bytesPorLinha; public IntPtr metadados; public long timestamp;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool NDIlib_initialize();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_find_create_v2(ref FindCreate c);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_find_get_current_sources(IntPtr f, out uint n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_recv_create_v3(ref RecvCreate c);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int NDIlib_recv_capture_v2(IntPtr r, ref Quadro v, IntPtr a, IntPtr m, uint espera);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_free_video_v2(IntPtr r, ref Quadro v);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_destroy(IntPtr r);

    static IntPtr busca;
    public static string Iniciar()
    {
        foreach (var p in new[] { Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"), Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"), @"C:\Program Files\NDI\NDI 6 Tools\Runtime" })
            if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, Dll)) && LoadLibrary(Path.Combine(p, Dll)) != IntPtr.Zero)
            {
                if (!NDIlib_initialize()) return "NDI não inicializou";
                var c = new FindCreate { locais = true };
                busca = NDIlib_find_create_v2(ref c);
                return null;
            }
        return "NDI Runtime não encontrado";
    }

    public static List<KeyValuePair<string, Fonte>> Fontes()
    {
        var l = new List<KeyValuePair<string, Fonte>>();
        if (busca == IntPtr.Zero) return l;
        var p = NDIlib_find_get_current_sources(busca, out uint n);
        int tam = Marshal.SizeOf<Fonte>();
        for (int i = 0; i < n; i++)
        {
            var f = Marshal.PtrToStructure<Fonte>(p + i * tam);
            l.Add(new KeyValuePair<string, Fonte>(Utf8(f.nome), f));
        }
        return l;
    }

    static string Utf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n]; Marshal.Copy(p, b, 0, n); return System.Text.Encoding.UTF8.GetString(b);
    }
}

// Guarda sempre o quadro mais recente (BGRX), copiado do NDI
sealed class Receptor : IDisposable
{
    IntPtr recv; Thread laco; volatile bool rodando = true;
    readonly object trava = new object();
    byte[] buf = new byte[0]; int w, h, seq;
    public int Recebidos;
    public volatile bool Querer = true;   // a análise pede o próximo quadro

    public Receptor(NdiRx.Fonte f, bool leve)
    {
        var nome = Marshal.StringToHGlobalAnsi("Teste de rastreio");
        var c = new NdiRx.RecvCreate { fonte = f, cor = 0 /* BGRX/BGRA */, banda = leve ? 0 /* lowest: proxy */ : 100, campos = false, nome = nome };
        recv = NdiRx.NDIlib_recv_create_v3(ref c);
        laco = new Thread(Rodar) { IsBackground = true, Name = "ndi" }; laco.Start();
    }

    void Rodar()
    {
        while (rodando)
        {
            var q = new NdiRx.Quadro();
            if (NdiRx.NDIlib_recv_capture_v2(recv, ref q, IntPtr.Zero, IntPtr.Zero, 500) != 1) continue;
            int n = q.bytesPorLinha * q.altura;
            if (!Querer) { Recebidos++; NdiRx.NDIlib_recv_free_video_v2(recv, ref q); continue; }   // ninguém pediu: não copia
            Querer = false;
            lock (trava)
            {
                if (buf.Length != q.largura * 4 * q.altura) buf = new byte[q.largura * 4 * q.altura];
                if (q.bytesPorLinha == q.largura * 4) System.Runtime.InteropServices.Marshal.Copy(q.dados, buf, 0, n);
                else for (int y = 0; y < q.altura; y++) Marshal.Copy(q.dados + y * q.bytesPorLinha, buf, y * q.largura * 4, q.largura * 4);
                w = q.largura; h = q.altura; seq++;
            }
            Recebidos++;
            NdiRx.NDIlib_recv_free_video_v2(recv, ref q);
        }
    }

    // copia o quadro mais novo, se mudou desde a última leitura
    public bool Pegar(ref byte[] destino, out int largura, out int altura, ref int ultimo)
    {
        lock (trava)
        {
            largura = w; altura = h;
            if (seq == ultimo || w == 0) return false;
            if (destino.Length != buf.Length) destino = new byte[buf.Length];
            Buffer.BlockCopy(buf, 0, destino, 0, buf.Length); ultimo = seq; return true;
        }
    }

    public void Dispose() { rodando = false; laco?.Join(1500); if (recv != IntPtr.Zero) NdiRx.NDIlib_recv_destroy(recv); recv = IntPtr.Zero; }
}
