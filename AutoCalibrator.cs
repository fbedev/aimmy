using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Aimmy.Mac
{
    public static class AutoCalibrator
    {
        // ==================== STATE ====================
        public enum CalibState { Idle, WaitingForTarget, MeasuringBefore, Moving, MeasuringAfter, Done, Failed }
        public static CalibState State { get; private set; } = CalibState.Idle;
        public static string StatusText { get; private set; } = "";
        public static int Progress { get; private set; } = 0; // 0-100
        public static bool ShowDebugOverlay { get; set; } = false;

        // Phase 2: Closed-loop sensitivity
        private static int _sensTrials = 0;
        private const int SENS_TRIALS = 5;
        private static double _beforeX, _beforeY;
        private static double _moveAmount = 15.0;
        private static List<double> _sensResults = new List<double>();
        private static Stopwatch _waitTimer = new Stopwatch();

        // Phase 3: Auto-offset correction
        public static bool AutoOffsetEnabled { get; set; } = false;
        private static double _offsetAccumX = 0;
        private static double _offsetAccumY = 0;
        private static int _offsetSamples = 0;
        private const int OFFSET_WARMUP = 30; // frames before applying
        private const float OFFSET_DECAY = 0.98f; // exponential decay for rolling average

        // Phase 4: Resolution profiles
        private static string _currentResKey = "";

        // Phase 5: Debug overlay data
        public static double DbgCenterX { get; set; }
        public static double DbgCenterY { get; set; }
        public static double DbgCapX { get; set; }
        public static double DbgCapY { get; set; }
        public static double DbgCapSize { get; set; }
        public static double DbgTargetX { get; set; }
        public static double DbgTargetY { get; set; }
        public static double DbgErrorX { get; set; }
        public static double DbgErrorY { get; set; }
        public static double DbgOffsetDriftX { get; set; }
        public static double DbgOffsetDriftY { get; set; }

        // ==================== PHASE 1: WINDOW CONTENT BOUNDS ====================

        /// <summary>
        /// Calculates center offset accounting for title bar and content area.
        /// macOS title bars are typically 28pt, but some apps (Chrome, etc.) vary.
        /// We estimate by checking if the window has a standard title bar.
        /// </summary>
        public static (int offsetX, int offsetY) CalcWindowContentCenter(WindowInfo window, int screenW, int screenH)
        {
            double winX = window.Bounds.Origin.X;
            double winY = window.Bounds.Origin.Y;
            double winW = window.Bounds.Size.Width;
            double winH = window.Bounds.Size.Height;

            // Estimate title bar height:
            // - Most macOS apps: 28pt
            // - Full-screen / borderless: 0pt
            // - We detect by checking if the window starts at Y=0 (fullscreen) or Y>20 (menu bar)
            double titleBarH = 0;
            bool likelyFullscreen = (winW >= screenW - 2 && winH >= screenH - 2);
            bool likelyBorderless = (winY == 0 && winH >= screenH * 0.9);

            if (!likelyFullscreen && !likelyBorderless)
            {
                titleBarH = 28; // Standard macOS title bar
            }

            // Content area center
            double contentX = winX;
            double contentY = winY + titleBarH;
            double contentW = winW;
            double contentH = winH - titleBarH;

            double contentCenterX = contentX + contentW / 2.0;
            double contentCenterY = contentY + contentH / 2.0;

            int screenCenterX = screenW / 2;
            int screenCenterY = screenH / 2;

            return ((int)(contentCenterX - screenCenterX), (int)(contentCenterY - screenCenterY));
        }

        // ==================== PHASE 2: CLOSED-LOOP SENSITIVITY ====================

        public static void StartSensitivityCalibration()
        {
            if (State != CalibState.Idle) return;

            _sensTrials = 0;
            _sensResults.Clear();
            State = CalibState.WaitingForTarget;
            StatusText = "Aim at a target to begin...";
            Progress = 0;
        }

        /// <summary>
        /// Called each frame during sensitivity calibration.
        /// Returns true when calibration is complete.
        /// </summary>
        public static bool UpdateSensitivityCalibration(
            Config config,
            Prediction? best,
            double scale,
            double capX, double capY,
            int centerX, int centerY)
        {
            if (State == CalibState.Idle || State == CalibState.Done || State == CalibState.Failed)
                return false;

            if (State == CalibState.WaitingForTarget)
            {
                if (best == null)
                {
                    StatusText = "Aim at a target to begin...";
                    return false;
                }

                // Target found, record its position
                _beforeX = capX + best.ScreenCenterX / scale;
                _beforeY = capY + best.ScreenCenterY / scale;
                State = CalibState.MeasuringBefore;
                _waitTimer.Restart();
                StatusText = "Measuring target position...";
                return false;
            }

            if (State == CalibState.MeasuringBefore)
            {
                if (best == null) { State = CalibState.WaitingForTarget; return false; }

                // Wait a few frames to get stable reading
                if (_waitTimer.ElapsedMilliseconds < 100) return false;

                _beforeX = capX + best.ScreenCenterX / scale;
                _beforeY = capY + best.ScreenCenterY / scale;

                // Move mouse by known amount
                State = CalibState.Moving;
                _waitTimer.Restart();

                // Move right by _moveAmount pixels
                MacInput.MoveMouseRelative((int)_moveAmount, 0);
                StatusText = $"Trial {_sensTrials + 1}/{SENS_TRIALS}: Moving mouse...";
                return false;
            }

            if (State == CalibState.Moving)
            {
                // Wait for the game to respond to our mouse movement
                if (_waitTimer.ElapsedMilliseconds < 150) return false;

                State = CalibState.MeasuringAfter;
                _waitTimer.Restart();
                return false;
            }

            if (State == CalibState.MeasuringAfter)
            {
                if (best == null)
                {
                    // Lost target after move — retry
                    StatusText = "Target lost, retrying...";
                    State = CalibState.WaitingForTarget;
                    return false;
                }

                if (_waitTimer.ElapsedMilliseconds < 100) return false;

                double afterX = capX + best.ScreenCenterX / scale;
                double afterY = capY + best.ScreenCenterY / scale;

                // How much did the target shift in screen space?
                double shiftX = _beforeX - afterX; // Target moved left = we moved right
                double shiftY = _beforeY - afterY;
                double totalShift = Math.Sqrt(shiftX * shiftX + shiftY * shiftY);

                if (totalShift > 1.0) // Valid measurement
                {
                    // We moved _moveAmount mouse pixels, target shifted by totalShift screen points
                    // Ideal: if sens=1.0, moving 15px should shift target by 15px in screen
                    // Actual: target shifted by totalShift
                    // So correct sensitivity = _moveAmount / totalShift * currentSens
                    // But we want: dx * sens = correct mouse movement
                    // dx is in screen points, we want mouse move = dx * sens
                    // We moved _moveAmount, target shifted totalShift
                    // So: _moveAmount caused totalShift of screen shift
                    // The sens should map dx -> mouse move such that target shifts by dx
                    // Current: dx * currentSens = _moveAmount (mouse move we sent)
                    //          Result: target shifted by totalShift
                    // We want target to shift by the full error (dx). So:
                    // correctSens = currentSens * (totalShift / _moveAmount)
                    // Wait... let me reconsider.
                    //
                    // The aim loop does: mouseMove = dx * sens
                    // We want mouseMove to be exactly right so that in one frame the crosshair reaches the target.
                    // If we move the mouse by M pixels and the game moves the view by S screen-points:
                    //   game_sensitivity = S / M
                    // The aim loop sends: M = dx * sens
                    // The game then moves: S = game_sens * M = game_sens * dx * sens
                    // We want S = dx (the error goes to zero), so:
                    //   dx = game_sens * dx * sens
                    //   1 = game_sens * sens
                    //   sens = 1 / game_sens
                    // From our trial: game_sens = totalShift / _moveAmount
                    // So: idealSens = _moveAmount / totalShift

                    double gameSens = totalShift / _moveAmount;
                    double idealSens = 1.0 / gameSens;

                    _sensResults.Add(idealSens);
                    _sensTrials++;

                    Progress = (int)((_sensTrials / (float)SENS_TRIALS) * 100);
                    StatusText = $"Trial {_sensTrials}/{SENS_TRIALS}: shift={totalShift:F1}px, sens={idealSens:F4}";

                    // Move mouse back
                    MacInput.MoveMouseRelative(-(int)_moveAmount, 0);

                    if (_sensTrials >= SENS_TRIALS)
                    {
                        // Average results, ignoring outliers
                        _sensResults.Sort();
                        double median;
                        if (_sensResults.Count >= 3)
                        {
                            // Trim top and bottom
                            var trimmed = _sensResults.GetRange(1, _sensResults.Count - 2);
                            double sum = 0;
                            foreach (var v in trimmed) sum += v;
                            median = sum / trimmed.Count;
                        }
                        else
                        {
                            double sum = 0;
                            foreach (var v in _sensResults) sum += v;
                            median = sum / _sensResults.Count;
                        }

                        config.MouseSensitivity = (float)Math.Clamp(median, 0.001, 5.0);
                        State = CalibState.Done;
                        StatusText = $"Calibrated! Sensitivity: {config.MouseSensitivity:F4}";
                        Progress = 100;
                        return true;
                    }

                    // Next trial
                    State = CalibState.WaitingForTarget;
                    _waitTimer.Restart();
                }
                else
                {
                    // No significant shift detected, try larger movement
                    _moveAmount = Math.Min(_moveAmount * 1.5, 50);
                    MacInput.MoveMouseRelative(-(int)_moveAmount, 0); // undo
                    State = CalibState.WaitingForTarget;
                    StatusText = "Low shift detected, increasing movement...";
                }

                return false;
            }

            return false;
        }

        public static void CancelCalibration()
        {
            State = CalibState.Idle;
            StatusText = "";
            Progress = 0;
            _moveAmount = 15.0;
        }

        // ==================== PHASE 3: AUTO-OFFSET CORRECTION ====================

        /// <summary>
        /// Called each frame when auto-offset is enabled.
        /// Tracks the average error between where we aim and where the target is,
        /// and gradually adjusts XOffset/YOffset to compensate.
        /// </summary>
        public static void UpdateAutoOffset(Config config, double dx, double dy, double distToTarget)
        {
            if (!AutoOffsetEnabled) return;
            if (distToTarget > 200) return; // Only track when we're actively tracking close targets
            if (distToTarget < 2) return;   // Already on target

            // Accumulate error with exponential moving average
            _offsetAccumX = _offsetAccumX * OFFSET_DECAY + dx * (1.0 - OFFSET_DECAY);
            _offsetAccumY = _offsetAccumY * OFFSET_DECAY + dy * (1.0 - OFFSET_DECAY);
            _offsetSamples++;

            DbgOffsetDriftX = _offsetAccumX;
            DbgOffsetDriftY = _offsetAccumY;

            // After warmup, apply correction if drift is significant
            if (_offsetSamples > OFFSET_WARMUP)
            {
                // Only apply if the accumulated error is meaningful (>2px)
                if (Math.Abs(_offsetAccumX) > 2.0)
                {
                    config.XOffset -= (int)Math.Round(_offsetAccumX * 0.3); // Gentle correction
                    _offsetAccumX *= 0.5; // Dampen after applying
                }
                if (Math.Abs(_offsetAccumY) > 2.0)
                {
                    config.YOffset -= (int)Math.Round(_offsetAccumY * 0.3);
                    _offsetAccumY *= 0.5;
                }
            }
        }

        public static void ResetAutoOffset()
        {
            _offsetAccumX = 0;
            _offsetAccumY = 0;
            _offsetSamples = 0;
        }

        // ==================== PHASE 4: RESOLUTION PROFILES ====================

        /// <summary>
        /// Auto-detects resolution and loads/creates matching profile.
        /// Call on startup and when resolution changes.
        /// </summary>
        public static string GetResolutionProfileName(int screenW, int screenH)
        {
            return $"config_{screenW}x{screenH}.json";
        }

        public static Config LoadOrCreateResolutionProfile(int screenW, int screenH)
        {
            string profileName = GetResolutionProfileName(screenW, screenH);
            _currentResKey = $"{screenW}x{screenH}";

            if (File.Exists(profileName))
            {
                Console.WriteLine($"[AutoCalib] Loading resolution profile: {profileName}");
                return Config.Load(profileName);
            }

            // Check if default config exists, use as base
            var config = Config.Load("config.json");
            config.Save(profileName);
            Console.WriteLine($"[AutoCalib] Created new resolution profile: {profileName}");
            return config;
        }

        public static bool CheckResolutionChanged(int screenW, int screenH)
        {
            string key = $"{screenW}x{screenH}";
            if (_currentResKey != key && _currentResKey != "")
            {
                return true;
            }
            return false;
        }

        // ==================== PHASE 5: DEBUG OVERLAY ====================

        public static void DrawDebugOverlay(int screenW, int screenH)
        {
            if (!ShowDebugOverlay) return;

            var dimWhite = new Raylib_cs.Color(255, 255, 255, 40);
            var dimCyan = new Raylib_cs.Color(0, 200, 255, 80);
            var dimYellow = new Raylib_cs.Color(255, 255, 0, 80);
            var dimRed = new Raylib_cs.Color(255, 80, 80, 120);
            var textBg = new Raylib_cs.Color(0, 0, 0, 160);
            var textCol = new Raylib_cs.Color(200, 220, 255, 200);

            int cx = (int)DbgCenterX;
            int cy = (int)DbgCenterY;

            // Draw capture region
            if (DbgCapSize > 0)
            {
                Raylib_cs.Raylib.DrawRectangleLinesEx(
                    new Raylib_cs.Rectangle((float)DbgCapX, (float)DbgCapY, (float)DbgCapSize, (float)DbgCapSize),
                    1, dimCyan);

                // Corner markers
                int cm = 12;
                float cax = (float)DbgCapX, cay = (float)DbgCapY, cas = (float)DbgCapSize;
                Raylib_cs.Raylib.DrawLineEx(new Vector2(cax, cay), new Vector2(cax + cm, cay), 1, dimCyan);
                Raylib_cs.Raylib.DrawLineEx(new Vector2(cax, cay), new Vector2(cax, cay + cm), 1, dimCyan);
                Raylib_cs.Raylib.DrawLineEx(new Vector2(cax + cas, cay), new Vector2(cax + cas - cm, cay), 1, dimCyan);
                Raylib_cs.Raylib.DrawLineEx(new Vector2(cax + cas, cay), new Vector2(cax + cas, cay + cm), 1, dimCyan);
            }

            // Center crosshair (blue)
            Raylib_cs.Raylib.DrawLineEx(new Vector2(cx - 15, cy), new Vector2(cx - 4, cy), 1.5f, dimCyan);
            Raylib_cs.Raylib.DrawLineEx(new Vector2(cx + 4, cy), new Vector2(cx + 15, cy), 1.5f, dimCyan);
            Raylib_cs.Raylib.DrawLineEx(new Vector2(cx, cy - 15), new Vector2(cx, cy - 4), 1.5f, dimCyan);
            Raylib_cs.Raylib.DrawLineEx(new Vector2(cx, cy + 4), new Vector2(cx, cy + 15), 1.5f, dimCyan);

            // Error vector (yellow line from center to target)
            if (DbgTargetX != 0 || DbgTargetY != 0)
            {
                int tx = (int)DbgTargetX;
                int ty = (int)DbgTargetY;
                Raylib_cs.Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(tx, ty), 1, dimYellow);
                Raylib_cs.Raylib.DrawCircleLines(tx, ty, 4, dimYellow);
            }

            // Offset drift indicator
            if (AutoOffsetEnabled && _offsetSamples > 0)
            {
                float driftLen = (float)Math.Sqrt(DbgOffsetDriftX * DbgOffsetDriftX + DbgOffsetDriftY * DbgOffsetDriftY);
                if (driftLen > 0.5)
                {
                    Raylib_cs.Raylib.DrawLineEx(
                        new Vector2(cx, cy),
                        new Vector2(cx + (float)DbgOffsetDriftX * 5, cy + (float)DbgOffsetDriftY * 5),
                        2, dimRed);
                }
            }

            // Info panel (bottom-left)
            int px = 16, py = screenH - 130;
            Raylib_cs.Raylib.DrawRectangleRounded(
                new Raylib_cs.Rectangle(px, py, 280, 115), 0.1f, 8, textBg);

            int ty2 = py + 8;
            Raylib_cs.Raylib.DrawText("DEBUG OVERLAY", px + 8, ty2, 11, dimCyan); ty2 += 16;
            Raylib_cs.Raylib.DrawText($"Center: {cx}, {cy}", px + 8, ty2, 11, textCol); ty2 += 14;
            Raylib_cs.Raylib.DrawText($"Capture: {DbgCapX:F0},{DbgCapY:F0} [{DbgCapSize:F0}px]", px + 8, ty2, 11, textCol); ty2 += 14;
            Raylib_cs.Raylib.DrawText($"Error: {DbgErrorX:F1}, {DbgErrorY:F1}", px + 8, ty2, 11, textCol); ty2 += 14;

            if (AutoOffsetEnabled)
            {
                string offStatus = _offsetSamples < OFFSET_WARMUP
                    ? $"Auto-Offset: warming up ({_offsetSamples}/{OFFSET_WARMUP})"
                    : $"Auto-Offset: drift ({DbgOffsetDriftX:F1}, {DbgOffsetDriftY:F1})";
                Raylib_cs.Raylib.DrawText(offStatus, px + 8, ty2, 11, textCol); ty2 += 14;
            }

            Raylib_cs.Raylib.DrawText($"Res: {screenW}x{screenH} [{_currentResKey}]", px + 8, ty2, 11, textCol);

            // Calibration status
            if (State != CalibState.Idle)
            {
                int cpx = screenW / 2 - 160;
                int cpy = 80;
                Raylib_cs.Raylib.DrawRectangleRounded(
                    new Raylib_cs.Rectangle(cpx, cpy, 320, 50), 0.15f, 8, textBg);
                Raylib_cs.Raylib.DrawText(StatusText, cpx + 12, cpy + 8, 12, textCol);

                // Progress bar
                Raylib_cs.Raylib.DrawRectangle(cpx + 12, cpy + 30, 296, 8,
                    new Raylib_cs.Color(40, 44, 58, 200));
                if (Progress > 0)
                    Raylib_cs.Raylib.DrawRectangle(cpx + 12, cpy + 30, (int)(296 * Progress / 100.0), 8,
                        new Raylib_cs.Color(85, 130, 255, 200));
            }
        }
    }
}
