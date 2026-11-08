using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MiguelAngelusCast.Rastreio
{
    // Detector (YuNet, MIT) + reconhecimento (SFace, Apache 2.0) com ONNX Runtime, sem OpenCV

    sealed class Rosto
    {
        public float X, Y, W, H, Nota;          // caixa no quadro original
        public float[] Pontos = new float[10];  // olho, olho, nariz, boca, boca
        public float Parecido = float.NaN;
        public float Cx => X + W / 2; public float Cy => Y + H / 2;
    }

    sealed class Rostos : IDisposable
    {
        const int T = 640;
        readonly InferenceSession det, rec;
        readonly float[] entrada = new float[3 * T * T];
        readonly float[] entradaRec = new float[3 * 112 * 112];
        static readonly float[] Molde = { 38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f };

        public Rostos(string pasta, int threads)
        {
            var o = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            det = new InferenceSession(Path.Combine(pasta, "face_detection_yunet_2023mar.onnx"), o);
            rec = new InferenceSession(Path.Combine(pasta, Environment.GetEnvironmentVariable("RASTREIO_SFACE_INT8") == "1" ? "face_recognition_sface_2021dec_int8.onnx" : "face_recognition_sface_2021dec.onnx"), o);   // int8 ficou 5x MAIS LENTO neste processador
        }

        // img = BGRX w x h; devolve rostos em coordenadas da imagem
        public unsafe List<Rosto> Detectar(byte[] img, int w, int h, float limiar = 0.6f)
        {
            float esc = Math.Min((float)T / w, (float)T / h);
            int sw = (int)(w * esc), sh = (int)(h * esc);
            Array.Clear(entrada, 0, entrada.Length);
            fixed (byte* src = img) fixed (float* dst = entrada)
            {
                for (int y = 0; y < sh; y++)
                {
                    byte* linha = src + (int)(y / esc) * w * 4;
                    for (int x = 0; x < sw; x++)
                    {
                        byte* p = linha + (int)(x / esc) * 4;
                        int i = y * T + x;
                        dst[i] = p[0]; dst[T * T + i] = p[1]; dst[2 * T * T + i] = p[2];   // BGR, 0-255
                    }
                }
            }
            var t = new DenseTensor<float>(entrada, new[] { 1, 3, T, T });
            var lista = new List<Rosto>();
            using (var r = det.Run(new[] { NamedOnnxValue.CreateFromTensor("input", t) }))
            {
                var saidas = r.ToDictionary(v => v.Name, v => ((DenseTensor<float>)v.AsTensor<float>()).Buffer.ToArray());
                foreach (int s in new[] { 8, 16, 32 })
                {
                    var cls = saidas["cls_" + s]; var obj = saidas["obj_" + s]; var bb = saidas["bbox_" + s]; var kp = saidas["kps_" + s];
                    int cols = T / s;
                    for (int i = 0; i < cls.Length; i++)
                    {
                        float c = Math.Min(Math.Max(cls[i], 0), 1), ob = Math.Min(Math.Max(obj[i], 0), 1);
                        float nota = (float)Math.Sqrt(c * ob);
                        if (nota < limiar) continue;
                        int lin = i / cols, col = i % cols;
                        float cx = (col + bb[i * 4]) * s, cy = (lin + bb[i * 4 + 1]) * s;
                        float bw = (float)Math.Exp(bb[i * 4 + 2]) * s, bh = (float)Math.Exp(bb[i * 4 + 3]) * s;
                        var f = new Rosto { X = (cx - bw / 2) / esc, Y = (cy - bh / 2) / esc, W = bw / esc, H = bh / esc, Nota = nota };
                        for (int k = 0; k < 5; k++) { f.Pontos[2 * k] = (kp[i * 10 + 2 * k] + col) * s / esc; f.Pontos[2 * k + 1] = (kp[i * 10 + 2 * k + 1] + lin) * s / esc; }
                        lista.Add(f);
                    }
                }
            }
            // NMS
            var fim = new List<Rosto>();
            foreach (var f in lista.OrderByDescending(f => f.Nota))
                if (fim.All(g => Iou(f, g) < 0.3f)) fim.Add(f);
            return fim;
        }

        public static float Iou(Rosto a, Rosto b)
        {
            float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y), x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
            float i = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            return i / (a.W * a.H + b.W * b.H - i + 1e-6f);
        }

        // alinha o rosto (5 pontos -> molde 112x112) e gera a assinatura normalizada
        public unsafe float[] Assinar(byte[] img, int w, int h, Rosto f)
        {
            // semelhança (escala+rotação+translação) por mínimos quadrados: origem -> molde
            float mx = 0, my = 0, mu = 0, mv = 0;
            for (int k = 0; k < 5; k++) { mx += f.Pontos[2 * k]; my += f.Pontos[2 * k + 1]; mu += Molde[2 * k]; mv += Molde[2 * k + 1]; }
            mx /= 5; my /= 5; mu /= 5; mv /= 5;
            double num1 = 0, num2 = 0, den = 0;
            for (int k = 0; k < 5; k++)
            {
                double sx = f.Pontos[2 * k] - mx, sy = f.Pontos[2 * k + 1] - my, du = Molde[2 * k] - mu, dv = Molde[2 * k + 1] - mv;
                num1 += sx * du + sy * dv; num2 += sx * dv - sy * du; den += sx * sx + sy * sy;
            }
            double a = num1 / den, b = num2 / den, n2 = a * a + b * b;
            double tx = mu - (a * mx - b * my), ty = mv - (b * mx + a * my);
            fixed (byte* src = img) fixed (float* dst = entradaRec)
            {
                for (int v = 0; v < 112; v++)
                    for (int u = 0; u < 112; u++)
                    {
                        double qx = u - tx, qy = v - ty;
                        double x = (a * qx + b * qy) / n2, y = (-b * qx + a * qy) / n2;
                        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                        float fx = (float)(x - x0), fy = (float)(y - y0);
                        int i = v * 112 + u;
                        for (int c = 0; c < 3; c++)
                        {
                            float p00 = Px(src, w, h, x0, y0, c), p10 = Px(src, w, h, x0 + 1, y0, c), p01 = Px(src, w, h, x0, y0 + 1, c), p11 = Px(src, w, h, x0 + 1, y0 + 1, c);
                            float val = (p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy;
                            dst[(2 - c) * 112 * 112 + i] = val;   // RGB (troca B<->R), 0-255
                        }
                    }
            }
            var t = new DenseTensor<float>(entradaRec, new[] { 1, 3, 112, 112 });
            using (var r = rec.Run(new[] { NamedOnnxValue.CreateFromTensor("data", t) }))
            {
                var v = ((DenseTensor<float>)r.First().AsTensor<float>()).Buffer.ToArray();
                return Normalizar(v);
            }
        }

        static unsafe float Px(byte* s, int w, int h, int x, int y, int c)
        {
            if (x < 0) x = 0; else if (x >= w) x = w - 1;
            if (y < 0) y = 0; else if (y >= h) y = h - 1;
            return s[(y * w + x) * 4 + c];
        }

        public static float[] Normalizar(float[] v)
        {
            double n = Math.Sqrt(v.Sum(x => (double)x * x)) + 1e-9;
            return v.Select(x => (float)(x / n)).ToArray();
        }

        public static float Cosseno(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return (float)s; }

        public void Dispose() { det.Dispose(); rec.Dispose(); }
    }
}
