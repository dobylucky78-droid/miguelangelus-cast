// Telemetria do teste: só números e eventos. Nunca grava imagem nem a "assinatura" de rosto.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

sealed class Telemetria : IDisposable
{
    public readonly string Pasta;
    readonly StreamWriter csv, eventos;
    readonly object trava = new object();
    readonly Stopwatch relogio = Stopwatch.StartNew();
    static readonly CultureInfo I = CultureInfo.InvariantCulture;
    public double Agora => relogio.Elapsed.TotalSeconds;

    public Telemetria()
    {
        Pasta = Path.Combine(Program.Pasta, "telemetria", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(Pasta);
        csv = new StreamWriter(Path.Combine(Pasta, "quadros.csv"), false, new UTF8Encoding(true)) { AutoFlush = false };
        csv.WriteLine("t;hora;larg;alt;det_ms;rec_ms;rostos;alvo_cx;alvo_cy;alvo_l;parecido;estado;erro_x;erro_y;pan_vel;pan_dir;tilt_vel;tilt_dir;camera_real;cpu_programa;cpu_total;ndi_qps;movimento;zoom");
        eventos = new StreamWriter(Path.Combine(Pasta, "eventos.txt"), false, new UTF8Encoding(true)) { AutoFlush = true };
        File.WriteAllText(Path.Combine(Pasta, "computador.txt"), InfoComputador(), new UTF8Encoding(true));
        Evento("Início da sessão — MiguelAngelus Cast " + typeof(Telemetria).Assembly.GetName().Version);
    }

    public void Evento(string texto)
    {
        lock (trava) eventos.WriteLine($"{Agora.ToString("0.000", I)}\t{DateTime.Now:HH:mm:ss.fff}\t{texto}");
    }

    public void Quadro(params object[] v)
    {
        var linha = string.Join(";", new object[] { Agora, DateTime.Now.ToString("HH:mm:ss.fff") }.Concat(v).Select(Fmt));
        lock (trava) csv.WriteLine(linha);
    }

    static string Fmt(object o) => o switch
    {
        null => "",
        double d => double.IsNaN(d) ? "" : d.ToString("0.###", I),
        float f => float.IsNaN(f) ? "" : f.ToString("0.###", I),
        bool b => b ? "1" : "0",
        _ => Convert.ToString(o, I).Replace(";", ",")
    };

    public void Salvar() { lock (trava) csv.Flush(); }

    // zip da sessão, para levar no pen drive ou mandar por e-mail
    public string Empacotar()
    {
        Salvar();
        var zip = Pasta.TrimEnd('\\') + ".zip";
        if (File.Exists(zip)) File.Delete(zip);
        lock (trava)
        {
            csv.Flush();
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var f in Directory.GetFiles(Pasta))
                    using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var e = z.CreateEntry(Path.GetFileName(f)).Open()) s.CopyTo(e);
        }
        return zip;
    }

    static string InfoComputador()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Computador: " + Environment.MachineName);
        try
        {
            const string k = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            sb.AppendLine($"Windows: {Microsoft.Win32.Registry.GetValue(k, "ProductName", "?")} {Microsoft.Win32.Registry.GetValue(k, "DisplayVersion", "")} (build {Microsoft.Win32.Registry.GetValue(k, "CurrentBuild", "?")})");
        }
        catch { sb.AppendLine("Windows: " + Environment.OSVersion); }
        sb.AppendLine("Núcleos lógicos: " + Environment.ProcessorCount);
        try { sb.AppendLine("Processador: " + Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "?")); } catch { }
        try { var m = new MEMORYSTATUSEX(); m.dwLength = (uint)Marshal.SizeOf(m); if (GlobalMemoryStatusEx(ref m)) sb.AppendLine($"Memória: {m.ullTotalPhys / 1024 / 1024 / 1024.0:0.0} GB"); } catch { }
        try { foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController").Get()) sb.AppendLine($"Vídeo: {o["Name"]} (driver {o["DriverVersion"]})"); } catch { }
        foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" }) sb.AppendLine(v + ": " + Environment.GetEnvironmentVariable(v));
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public void Dispose() { Evento("Fim da sessão"); lock (trava) { csv.Dispose(); eventos.Dispose(); } }
}

// uso do processador do computador inteiro (sem depender dos nomes traduzidos do Windows)
static class CpuTotal
{
    [DllImport("kernel32")] static extern bool GetSystemTimes(out long ocioso, out long kernel, out long usuario);
    static long o0, k0, u0;
    public static double Medir()
    {
        GetSystemTimes(out long o, out long k, out long u);
        long dO = o - o0, dT = (k - k0) + (u - u0); o0 = o; k0 = k; u0 = u;
        return dT <= 0 ? double.NaN : Math.Round(100.0 * (dT - dO) / dT, 1);
    }
}
