using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Raylib_cs;

namespace Aimmy.Mac
{
    public static class Recorder
    {
        private static ConcurrentQueue<FrameData> _frameQueue = new ConcurrentQueue<FrameData>();
        private static bool _isRecording = false;
        private static Thread? _workerThread;
        private static string _sessionPath = "";
        private static int _frameIndex = 0;

        public class FrameData
        {
            public required float[] Buffer; // Raw Tensor Data
            public required int Size;
            public required List<Prediction> Preds;
            public required long Timestamp;
            public required Config.TeamColorType TeamColor;
            
            // Pro / Debug Stats
            public required int BestTargetIndex; // -1 if none
            public required float OffsetX;
            public required float OffsetY;
            public required float Sensitivity;
            public required float Smoothing;
            public required float InputX; // AI computed move X
            public required float InputY; // AI computed move Y
        }

        public static void Start()
        {
            if (_isRecording) return;
            
            string date = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _sessionPath = Path.Combine("recordings", date);
            Directory.CreateDirectory(_sessionPath);
            
            _isRecording = true;
            _frameIndex = 0;
            _workerThread = new Thread(WorkerLoop);
            _workerThread.IsBackground = true;
            _workerThread.Start();
            
            Console.WriteLine($"[Recorder] Started: {_sessionPath}");
        }

        public static void Stop()
        {
            _isRecording = false;
            // Thread will exit when queue empty
        }
        
        public static bool IsRecording => _isRecording;

        public static void EnqueueFrame(float[] buffer, int size, List<Prediction> preds, Config.TeamColorType team, int bestIdx, float ox, float oy, float sens, float smooth, float ix, float iy)
        {
            if (!_isRecording) return;
            
            // Limit queue size to avoid OOM
            if (_frameQueue.Count > 60) return; // Drop frames if lagging

            // Deep Copy Buffer (Costly but necessary for async)
            float[] copy = new float[buffer.Length];
            Array.Copy(buffer, copy, buffer.Length);
            
            _frameQueue.Enqueue(new FrameData 
            {
                Buffer = copy,
                Size = size,
                Preds = new List<Prediction>(preds), // Shallow copy list
                Timestamp = DateTime.Now.Ticks,
                TeamColor = team,
                BestTargetIndex = bestIdx,
                OffsetX = ox,
                OffsetY = oy,
                Sensitivity = sens,
                Smoothing = smooth,
                InputX = ix,
                InputY = iy
            });
        }

        private static void WorkerLoop()
        {
            while (_isRecording || !_frameQueue.IsEmpty)
            {
                if (_frameQueue.TryDequeue(out var frame))
                {
                    SaveFrame(frame);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            Console.WriteLine("[Recorder] Image Save Complete. Starting Video Conversion...");
            ConvertToVideo();
            Console.WriteLine("[Recorder] Stopped.");
        }
        
        private static void ConvertToVideo()
        {
            try 
            {
                // check if ffmpeg exists (simple check)
                // We assume it's in PATH since 'dotnet run' environment likely shares shell path
                
                string framesPattern = Path.Combine(_sessionPath, "frame_%05d.jpg");
                string outputPath = Path.Combine(_sessionPath, "recording.mp4");
                
                // standard ffmpeg command for image sequence
                // -framerate 30: assuming target fps (we might want actual FPS?)
                // -i ...: input pattern
                // -c:v libx264: encoder
                // -pix_fmt yuv420p: compatibility
                
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -framerate 30 -i \"{framesPattern}\" -c:v libx264 -pix_fmt yuv420p \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit();
                
                Console.WriteLine($"[Recorder] Video Saved: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recorder] FFmpeg failed (is it installed?): {ex.Message}");
            }
        }

        private static unsafe void SaveFrame(FrameData frame)
        {
            // Convert Tensor Logic (Planar RGB) -> Image
            // We can't use System.Drawing on Mac easily without libgdiplus.
            // But we have Raylib! 
            // WAIT - Raylib functions must run on MAIN THREAD usually if they touch GPU.
            // But Raylib Image manipulation (CPU) is thread safe? 
            // GenImageColor is CPU. ImageDraw is CPU. ExportImage is Disk IO.
            // Should be fine on worker thread.

            int size = frame.Size;
            Image img = Raylib.GenImageColor(size, size, Color.Black);
            
            // Fill Pixels
            // Tensor: RRR... GGG... BBB...
            // Raylib Image: R8G8B8A8 Packed
            
            // Pointer access for speed
            fixed (float* src = frame.Buffer)
            {
                // We have access to img.Data? 
                // Raylib-cs Image struct has `void* data`.
                byte* dst = (byte*)img.Data;
                
                int total = size * size;
                
                for (int i = 0; i < total; i++)
                {
                    // De-Normalize (0..1 -> 0..255)
                    // Note: LUT capture divides by 255. So mult by 255.
                    byte r = (byte)(src[i] * 255f);
                    byte g = (byte)(src[i + total] * 255f);
                    byte b = (byte)(src[i + total * 2] * 255f);
                    
                    // Destination is RGBA (4 bytes)
                    dst[i * 4] = r;
                    dst[i * 4 + 1] = g;
                    dst[i * 4 + 2] = b;
                    dst[i * 4 + 3] = 255; // Alpha
                }
            }
            
            // Draw HUD
            DrawHUD(&img, frame);

            // Save
            string path = Path.Combine(_sessionPath, $"frame_{_frameIndex:D5}.jpg");
            Raylib.ExportImage(img, path); // ExportImage usually takes Image (struct), not pointer.
            Raylib.UnloadImage(img);
            
            _frameIndex++;
        }
        
        private static unsafe void DrawText(Image* img, string text, int x, int y, int fontSize, Color color)
        {
             // Native Raylib ImageDrawText expects sbyte* (C string)
             byte[] b = System.Text.Encoding.ASCII.GetBytes(text + '\0');
             fixed (byte* p = b)
             {
                 Raylib.ImageDrawText(img, (sbyte*)p, x, y, fontSize, color);
             }
        }

        private static unsafe void DrawHUD(Image* img, FrameData frame)
        {
             int w = frame.Size;
             int h = frame.Size;
             
             // Colors
             Color colCyan = new Color(0, 255, 255, 200);
             Color colGreen = new Color(0, 255, 0, 200);
             Color colRed = new Color(255, 0, 0, 200);
             Color colDim = new Color(0, 255, 255, 50);
             
             // 1. Tech Borders / Grid
             Raylib.ImageDrawRectangleLines(img, new Rectangle(10, 10, w - 20, h - 20), 2, colCyan);
             // Corner accents
             int len = 30;
             Raylib.ImageDrawRectangle(img, 10, 10, len, 4, colCyan); Raylib.ImageDrawRectangle(img, 10, 10, 4, len, colCyan);
             Raylib.ImageDrawRectangle(img, w-10-len, h-14, len, 4, colCyan); Raylib.ImageDrawRectangle(img, w-14, h-10-len, 4, len, colCyan);

             // 2. System Stats
             DrawText(img, $"SYS: ONLINE | T: {frame.Timestamp % 10000}", 20, 20, 10, colCyan);
             DrawText(img, $"AI CONF: >0.5 | FOV: AUTO", 20, 32, 10, colCyan);
             
             // Bot Info (Telemetry)
             DrawText(img, $"SENS: {frame.Sensitivity:F3} | SMTH: {frame.Smoothing:F2}", 20, h - 40, 10, colGreen);
             DrawText(img, $"OFFSET: {frame.OffsetX:F0}, {frame.OffsetY:F0}", 20, h - 28, 10, colGreen);
             
             // MECHANICS VISUALIZER (Dot in Square)
             // Bottom Left: 20, h-140 (100x100)
             int mechX = 20;
             int mechY = h - 140;
             int mechSize = 100;
             int mechCenter = mechSize / 2;
             
             // Box
             Raylib.ImageDrawRectangleLines(img, new Rectangle(mechX, mechY, mechSize, mechSize), 1, colDim);
             Raylib.ImageDrawLine(img, mechX + mechCenter, mechY, mechX + mechCenter, mechY + mechSize, colDim); // V Line
             Raylib.ImageDrawLine(img, mechX, mechY + mechCenter, mechX + mechSize, mechY + mechCenter, colDim); // H Line
             
             // Dot (Input Vector)
             // Scale Input (e.g. assume max input ~ 20px per frame? or use non-scaled)
             // Let's clamp to box.
             int dotX = (int)(mechX + mechCenter + frame.InputX);
             int dotY = (int)(mechY + mechCenter + frame.InputY);
             
             // Clamp
             dotX = Math.Clamp(dotX, mechX + 2, mechX + mechSize - 2);
             dotY = Math.Clamp(dotY, mechY + 2, mechY + mechSize - 2);
             
             Color mechColor = (Math.Abs(frame.InputX) > 0.1 || Math.Abs(frame.InputY) > 0.1) ? colGreen : colRed;
             Raylib.ImageDrawCircle(img, dotX, dotY, 4, mechColor);
             
             DrawText(img, $"MECH", mechX, mechY - 12, 10, colCyan);

             // 3. Center Crosshair Logic
             int cx = w / 2;
             int cy = h / 2;
             Raylib.ImageDrawLine(img, cx - 10, cy, cx + 10, cy, colDim);
             Raylib.ImageDrawLine(img, cx, cy - 10, cx, cy + 10, colDim);
             
            // 4. Predictions
             int bestIdx = frame.BestTargetIndex;
             
             for(int i=0; i<frame.Preds.Count; i++)
             {
                 var p = frame.Preds[i];
                 bool isBest = (i == bestIdx);
                 Color targetCol = isBest ? colGreen : colRed;
                 
                 Raylib_cs.Rectangle box = new Raylib_cs.Rectangle(p.Rectangle.X, p.Rectangle.Y, p.Rectangle.Width, p.Rectangle.Height);
                 
                 // Draw Box
                 Raylib.ImageDrawRectangleLines(img, box, isBest ? 3 : 1, targetCol);
                 
                 // Stats Label
                 string label = $"ID:{i:D2} C:{(int)(p.Confidence*100)}%";
                 DrawText(img, label, (int)box.X, (int)box.Y - 12, 10, targetCol);
                 
                 if (isBest)
                 {
                     // Draw Connecting Line
                     int boxCx = (int)(box.X + box.Width/2);
                     int boxCy = (int)(box.Y + box.Height/2);
                     Raylib.ImageDrawLine(img, cx, cy, boxCx, boxCy, colGreen);
                     
                     // Draw Lock Info
                     string lockInfo = $"LOCKED | DIST: {Math.Sqrt(Math.Pow(boxCx-cx,2) + Math.Pow(boxCy-cy,2)):F0}";
                     DrawText(img, lockInfo, boxCx + 10, boxCy, 10, colGreen);
                     
                     // Visualize Offset
                     int targetX = (int)(boxCx + frame.OffsetX);
                     int targetY = (int)(boxCy + frame.OffsetY);
                     Raylib.ImageDrawCircle(img, targetX, targetY, 3, colCyan); // Computed Aim Point
                     Raylib.ImageDrawLine(img, boxCx, boxCy, targetX, targetY, colCyan); // Offset Vector
                 }
             }
        }
    }
}
