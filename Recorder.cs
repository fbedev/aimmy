using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        private static int _droppedFrames = 0;
        private static long _startTicks = 0;
        private static long _lastEnqueueTicks = 0;
        private static List<float> _frameTimes = new List<float>();

        // Public status for UI
        public static bool IsRecording => _isRecording;
        public static bool IsProcessing { get; private set; } = false;
        public static int FrameCount => _frameIndex;
        public static int DroppedFrames => _droppedFrames;
        public static int QueueDepth => _frameQueue.Count;
        public static float RecordingDuration => _startTicks == 0 ? 0 : (float)(DateTime.Now.Ticks - _startTicks) / TimeSpan.TicksPerSecond;

        public class FrameData
        {
            public required float[] Buffer;
            public required int Size;
            public required List<Prediction> Preds;
            public required long Timestamp;
            public required Config.TeamColorType TeamColor;
            public required int BestTargetIndex;
            public required float OffsetX;
            public required float OffsetY;
            public required float Sensitivity;
            public required float Smoothing;
            public required float InputX;
            public required float InputY;
            public int FrameNumber;
            public float DeltaTime; // Time since last frame in seconds
        }

        public static void Start()
        {
            if (_isRecording || IsProcessing) return;

            string date = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _sessionPath = Path.Combine("recordings", date);
            Directory.CreateDirectory(_sessionPath);

            _isRecording = true;
            _frameIndex = 0;
            _droppedFrames = 0;
            _startTicks = DateTime.Now.Ticks;
            _lastEnqueueTicks = 0;
            _frameTimes.Clear();

            // Clear any leftover frames
            while (_frameQueue.TryDequeue(out _)) { }

            _workerThread = new Thread(WorkerLoop);
            _workerThread.IsBackground = true;
            _workerThread.Start();

            Console.WriteLine($"[Recorder] Started: {_sessionPath}");
        }

        public static void Stop()
        {
            if (!_isRecording) return;
            _isRecording = false;
            Console.WriteLine($"[Recorder] Stopping... {_frameIndex} frames captured, {_droppedFrames} dropped");
        }

        public static void EnqueueFrame(float[] buffer, int size, List<Prediction> preds, Config.TeamColorType team, int bestIdx, float ox, float oy, float sens, float smooth, float ix, float iy)
        {
            if (!_isRecording) return;

            // Adaptive queue limit: allow more buffering but cap memory
            int maxQueue = 120;
            if (_frameQueue.Count > maxQueue)
            {
                _droppedFrames++;
                return;
            }

            // Calculate delta time
            long now = DateTime.Now.Ticks;
            float dt = _lastEnqueueTicks == 0 ? 0.016f : (float)(now - _lastEnqueueTicks) / TimeSpan.TicksPerSecond;
            _lastEnqueueTicks = now;

            // Deep copy buffer
            float[] copy = new float[buffer.Length];
            Array.Copy(buffer, copy, buffer.Length);

            // Deep copy predictions
            var predsCopy = new List<Prediction>(preds.Count);
            foreach (var p in preds)
            {
                predsCopy.Add(new Prediction
                {
                    Rectangle = p.Rectangle,
                    Confidence = p.Confidence,
                    ScreenCenterX = p.ScreenCenterX,
                    ScreenCenterY = p.ScreenCenterY
                });
            }

            _frameQueue.Enqueue(new FrameData
            {
                Buffer = copy,
                Size = size,
                Preds = predsCopy,
                Timestamp = now,
                TeamColor = team,
                BestTargetIndex = bestIdx,
                OffsetX = ox,
                OffsetY = oy,
                Sensitivity = sens,
                Smoothing = smooth,
                InputX = ix,
                InputY = iy,
                FrameNumber = _frameIndex,
                DeltaTime = dt
            });

            _frameTimes.Add(dt);
        }

        private static void WorkerLoop()
        {
            IsProcessing = true;
            var sw = Stopwatch.StartNew();

            while (_isRecording || !_frameQueue.IsEmpty)
            {
                if (_frameQueue.TryDequeue(out var frame))
                {
                    SaveFrame(frame);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            sw.Stop();
            float saveDuration = (float)sw.Elapsed.TotalSeconds;
            Console.WriteLine($"[Recorder] Frames saved in {saveDuration:F1}s. Converting to video...");

            // Calculate actual FPS from frame times
            float avgFps = CalculateAverageFps();

            // Save session summary
            SaveSessionSummary(avgFps, saveDuration);

            // Convert to video with actual FPS
            ConvertToVideo(avgFps);

            // Clean up individual frames after successful video conversion
            CleanupFrames();

            IsProcessing = false;
            Console.WriteLine("[Recorder] Done.");
        }

        private static float CalculateAverageFps()
        {
            if (_frameTimes.Count < 2) return 30.0f;

            // Remove first frame (dt=0) and outliers
            var valid = new List<float>();
            for (int i = 1; i < _frameTimes.Count; i++)
            {
                float dt = _frameTimes[i];
                if (dt > 0.001f && dt < 0.5f) valid.Add(dt);
            }

            if (valid.Count == 0) return 30.0f;

            float sum = 0;
            foreach (float v in valid) sum += v;
            float avgDt = sum / valid.Count;

            return Math.Clamp(1.0f / avgDt, 10.0f, 120.0f);
        }

        private static void SaveSessionSummary(float avgFps, float saveDuration)
        {
            try
            {
                float totalDuration = _frameTimes.Count > 0 ? 0 : 0;
                foreach (float dt in _frameTimes) totalDuration += dt;

                var summary = new
                {
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    frames = _frameIndex,
                    droppedFrames = _droppedFrames,
                    durationSeconds = totalDuration,
                    averageFps = avgFps,
                    saveDurationSeconds = saveDuration,
                    path = _sessionPath
                };

                string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(_sessionPath, "session.json"), json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recorder] Failed to save summary: {ex.Message}");
            }
        }

        private static void ConvertToVideo(float fps)
        {
            try
            {
                string framesPattern = Path.Combine(_sessionPath, "frame_%05d.png");
                string outputPath = Path.Combine(_sessionPath, "recording.mp4");

                // Use actual captured FPS, with quality settings
                int fpsInt = Math.Max(10, (int)Math.Round(fps));

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -framerate {fpsInt} -i \"{framesPattern}\" -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(60000); // 60s timeout
                    if (p.ExitCode == 0)
                    {
                        Console.WriteLine($"[Recorder] Video saved: {outputPath} ({fpsInt} fps)");
                    }
                    else
                    {
                        string err = p.StandardError.ReadToEnd();
                        Console.WriteLine($"[Recorder] FFmpeg error (code {p.ExitCode}): {err}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recorder] FFmpeg failed (is it installed?): {ex.Message}");
            }
        }

        private static void CleanupFrames()
        {
            try
            {
                string videoPath = Path.Combine(_sessionPath, "recording.mp4");
                if (!File.Exists(videoPath)) return; // Don't delete frames if video failed

                var files = Directory.GetFiles(_sessionPath, "frame_*.png");
                foreach (string f in files)
                {
                    try { File.Delete(f); } catch { }
                }
                Console.WriteLine($"[Recorder] Cleaned up {files.Length} frame files");
            }
            catch { }
        }

        private static unsafe void SaveFrame(FrameData frame)
        {
            int size = frame.Size;
            Image img = Raylib.GenImageColor(size, size, Color.Black);

            fixed (float* src = frame.Buffer)
            {
                byte* dst = (byte*)img.Data;
                int total = size * size;

                for (int i = 0; i < total; i++)
                {
                    byte r = (byte)Math.Clamp(src[i] * 255f, 0, 255);
                    byte g = (byte)Math.Clamp(src[i + total] * 255f, 0, 255);
                    byte b = (byte)Math.Clamp(src[i + total * 2] * 255f, 0, 255);

                    dst[i * 4] = r;
                    dst[i * 4 + 1] = g;
                    dst[i * 4 + 2] = b;
                    dst[i * 4 + 3] = 255;
                }
            }

            DrawHUD(&img, frame);

            // Save as PNG for better quality (lossless)
            string path = Path.Combine(_sessionPath, $"frame_{_frameIndex:D5}.png");
            Raylib.ExportImage(img, path);
            Raylib.UnloadImage(img);

            _frameIndex++;
        }

        private static unsafe void DrawText(Image* img, string text, int x, int y, int fontSize, Color color)
        {
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

             Color colCyan = new Color(0, 255, 255, 200);
             Color colGreen = new Color(0, 255, 0, 200);
             Color colRed = new Color(255, 0, 0, 200);
             Color colYellow = new Color(255, 255, 0, 200);
             Color colDim = new Color(0, 255, 255, 50);
             Color colBg = new Color(0, 0, 0, 140);

             // Border + corner accents
             Raylib.ImageDrawRectangleLines(img, new Rectangle(4, 4, w - 8, h - 8), 1, colDim);
             int cLen = 20;
             Raylib.ImageDrawRectangle(img, 4, 4, cLen, 2, colCyan); Raylib.ImageDrawRectangle(img, 4, 4, 2, cLen, colCyan);
             Raylib.ImageDrawRectangle(img, w-4-cLen, 4, cLen, 2, colCyan); Raylib.ImageDrawRectangle(img, w-6, 4, 2, cLen, colCyan);
             Raylib.ImageDrawRectangle(img, 4, h-6, cLen, 2, colCyan); Raylib.ImageDrawRectangle(img, 4, h-4-cLen, 2, cLen, colCyan);
             Raylib.ImageDrawRectangle(img, w-4-cLen, h-6, cLen, 2, colCyan); Raylib.ImageDrawRectangle(img, w-6, h-4-cLen, 2, cLen, colCyan);

             // Top bar background
             Raylib.ImageDrawRectangle(img, 0, 0, w, 48, colBg);

             // Frame info
             DrawText(img, $"#{frame.FrameNumber:D5}  dt:{frame.DeltaTime*1000:F1}ms", 10, 6, 10, colCyan);

             // Detection count
             int detCount = frame.Preds.Count;
             Color detColor = detCount > 0 ? colGreen : colRed;
             DrawText(img, $"TARGETS: {detCount}", 10, 20, 10, detColor);

             // Team color indicator
             if (frame.TeamColor != Config.TeamColorType.None)
                 DrawText(img, $"TEAM: {frame.TeamColor}", 10, 34, 10, colYellow);

             // Config readout (top right)
             string sensStr = $"SENS:{frame.Sensitivity:F3}";
             string smthStr = $"SMTH:{frame.Smoothing:F2}";
             DrawText(img, sensStr, w - 120, 6, 10, colGreen);
             DrawText(img, smthStr, w - 120, 20, 10, colGreen);

             // Input vector readout (top right)
             string inputStr = $"IN: {frame.InputX:F1},{frame.InputY:F1}";
             DrawText(img, inputStr, w - 120, 34, 10,
                 (Math.Abs(frame.InputX) > 0.5 || Math.Abs(frame.InputY) > 0.5) ? colGreen : colDim);

             // Bottom bar background
             Raylib.ImageDrawRectangle(img, 0, h - 48, w, 48, colBg);

             // Offset info (bottom left)
             DrawText(img, $"OFFSET: {frame.OffsetX:F0}, {frame.OffsetY:F0}", 10, h - 40, 10, colGreen);

             // Input magnitude (bottom left)
             float mag = (float)Math.Sqrt(frame.InputX * frame.InputX + frame.InputY * frame.InputY);
             DrawText(img, $"MAG: {mag:F1}px", 10, h - 26, 10, mag > 5 ? colYellow : colGreen);

             // Mechanics visualizer (bottom right)
             int mechSize = 60;
             int mechX = w - mechSize - 10;
             int mechY = h - mechSize - 10;
             int mechCenter = mechSize / 2;

             Raylib.ImageDrawRectangle(img, mechX - 1, mechY - 1, mechSize + 2, mechSize + 2, colBg);
             Raylib.ImageDrawRectangleLines(img, new Rectangle(mechX, mechY, mechSize, mechSize), 1, colDim);
             Raylib.ImageDrawLine(img, mechX + mechCenter, mechY, mechX + mechCenter, mechY + mechSize, colDim);
             Raylib.ImageDrawLine(img, mechX, mechY + mechCenter, mechX + mechSize, mechY + mechCenter, colDim);

             // Scale input to fit in the box (normalize to ~20px max)
             float scale = mechCenter / 20.0f;
             int dotX = Math.Clamp((int)(mechX + mechCenter + frame.InputX * scale), mechX + 2, mechX + mechSize - 2);
             int dotY = Math.Clamp((int)(mechY + mechCenter + frame.InputY * scale), mechY + 2, mechY + mechSize - 2);

             Color mechColor = mag > 0.1f ? colGreen : colRed;
             Raylib.ImageDrawCircle(img, dotX, dotY, 3, mechColor);
             // Trail line from center to dot
             Raylib.ImageDrawLine(img, mechX + mechCenter, mechY + mechCenter, dotX, dotY, new Color(0, 255, 0, 80));

             // Center crosshair
             int cx = w / 2;
             int cy = h / 2;
             Raylib.ImageDrawLine(img, cx - 8, cy, cx - 3, cy, colDim);
             Raylib.ImageDrawLine(img, cx + 3, cy, cx + 8, cy, colDim);
             Raylib.ImageDrawLine(img, cx, cy - 8, cx, cy - 3, colDim);
             Raylib.ImageDrawLine(img, cx, cy + 3, cx, cy + 8, colDim);

             // Predictions
             int bestIdx = frame.BestTargetIndex;

             for (int i = 0; i < frame.Preds.Count; i++)
             {
                 var p = frame.Preds[i];
                 bool isBest = (i == bestIdx);
                 Color targetCol = isBest ? colGreen : colRed;

                 var box = new Rectangle(p.Rectangle.X, p.Rectangle.Y, p.Rectangle.Width, p.Rectangle.Height);

                 // Draw box
                 Raylib.ImageDrawRectangleLines(img, box, isBest ? 2 : 1, targetCol);

                 // Corner brackets for best target
                 if (isBest)
                 {
                     int bLen = 8;
                     int bx = (int)box.X; int by = (int)box.Y;
                     int bw = (int)box.Width; int bh = (int)box.Height;
                     // Top-left
                     Raylib.ImageDrawRectangle(img, bx-1, by-1, bLen, 2, colCyan);
                     Raylib.ImageDrawRectangle(img, bx-1, by-1, 2, bLen, colCyan);
                     // Top-right
                     Raylib.ImageDrawRectangle(img, bx+bw-bLen+1, by-1, bLen, 2, colCyan);
                     Raylib.ImageDrawRectangle(img, bx+bw-1, by-1, 2, bLen, colCyan);
                     // Bottom-left
                     Raylib.ImageDrawRectangle(img, bx-1, by+bh-1, bLen, 2, colCyan);
                     Raylib.ImageDrawRectangle(img, bx-1, by+bh-bLen+1, 2, bLen, colCyan);
                     // Bottom-right
                     Raylib.ImageDrawRectangle(img, bx+bw-bLen+1, by+bh-1, bLen, 2, colCyan);
                     Raylib.ImageDrawRectangle(img, bx+bw-1, by+bh-bLen+1, 2, bLen, colCyan);
                 }

                 // Label with confidence bar
                 string label = $"{(int)(p.Confidence*100)}%";
                 DrawText(img, label, (int)box.X, (int)box.Y - 12, 10, targetCol);

                 // Confidence bar
                 int barW = (int)(box.Width * p.Confidence);
                 Raylib.ImageDrawRectangle(img, (int)box.X, (int)(box.Y + box.Height + 2), barW, 3, targetCol);

                 if (isBest)
                 {
                     int boxCx = (int)(box.X + box.Width / 2);
                     int boxCy = (int)(box.Y + box.Height / 2);

                     // Connecting line
                     Raylib.ImageDrawLine(img, cx, cy, boxCx, boxCy, new Color(0, 255, 0, 80));

                     // Distance label
                     float dist = (float)Math.Sqrt(Math.Pow(boxCx - cx, 2) + Math.Pow(boxCy - cy, 2));
                     DrawText(img, $"LOCK {dist:F0}px", boxCx + (int)box.Width / 2 + 4, boxCy - 6, 10, colGreen);

                     // Aim point
                     int targetX = (int)(boxCx + frame.OffsetX);
                     int targetY = (int)(boxCy + frame.OffsetY);
                     Raylib.ImageDrawCircle(img, targetX, targetY, 3, colCyan);
                     Raylib.ImageDrawLine(img, boxCx, boxCy, targetX, targetY, colCyan);
                 }
             }
        }
    }
}
