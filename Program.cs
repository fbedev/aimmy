using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Numerics; // For Vector2 if needed, though Raylib has one
using Raylib_cs;

namespace Aimmy.Mac
{
    class Program
    {
        // --- GLOBAL STATE ---
        static Config _config = new Config();
        static AIManager? _aiManager;
        static bool _running = true;

        // Window State
        static IntPtr _nsWindow = IntPtr.Zero;
        static bool _showMenu = true;
        static bool _isPassthrough = false;

        // AI / Game State
        static bool _aimAssistActive = true;
        static bool _isCalibrating = false;
        static bool _isCalibratingCenter = false;
        static int _calibStep = 0;
        static Stopwatch _sw = new Stopwatch();

        // Profiling
        static long _lastCapMs = 0;
        static long _lastAiMs = 0;
        static Stopwatch _perfTimer = new Stopwatch();
        static Random _rng = new Random();

        // UI State
        static int _menuX = 50;
        static int _menuY = 50;
        static float _menuScrollY = 0;
        static string _currentProfileName = "config.json";
        static bool _isBinding = false;
        static string _bindingText = "None";
        static int _bindingTarget = 0; // 0=AimKey, 1=Menu, 2=Aim, 3=Record

        // Dropdown State
        static bool _isModelDropdownOpen = false;
        static string _modelSearchQuery = "";
        static float _modelScrollY = 0;

        // Window Selection State
        static bool _isSelectingWindow = false;
        static List<WindowInfo> _windowList = new List<WindowInfo>();
        static float _windowListScrollY = 0;

        // Input Smoothing
        static double _lastMoveX = 0;
        static double _lastMoveY = 0;

        // Prediction State
        static Vector2 _lastTargetPos = Vector2.Zero;
        static long _lastTargetTime = 0;
        static Vector2 _lastVelocity = Vector2.Zero;

        // Target Stickiness State
        static int _lastBestIndex = -1;

        // Flick Shot State
        static bool _isFlicking = false;
        static double _flickStartDist = 0;

        // Calibration Binary Search State
        static float _calibSensLow = 0.001f;
        static float _calibSensHigh = 2.0f;

        // Center Calibration State
        static float _calibHoldTime = 0;
        static int _calibSavedOffsetX = 0;
        static int _calibSavedOffsetY = 0;

        // Frame Timing
        static Stopwatch _frameTimer = new Stopwatch();

        // Timers
        static Stopwatch _recoilTimer = new Stopwatch();
        static Stopwatch _triggerTimer = new Stopwatch();
        static Stopwatch _shootingTimer = new Stopwatch();

        // Burst Fire State
        static Stopwatch _burstTimer = new Stopwatch();
        static int _burstShotsRemaining = 0;
        static bool _isBursting = false;
        static bool _isTriggerShooting = false;

        // Model List
        static List<string> _modelFiles = new List<string>();
        static int _currentModelIndex = 0;

        // ========== MODERN UI THEME ==========
        static readonly Color UI_BG = new Color(12, 13, 20, 255);
        static readonly Color UI_SURFACE = new Color(20, 22, 32, 255);
        static readonly Color UI_ELEVATED = new Color(30, 33, 46, 255);
        static readonly Color UI_BORDER = new Color(45, 50, 65, 255);
        static readonly Color UI_ACCENT = new Color(85, 130, 255, 255);
        static readonly Color UI_ACCENT_HOVER = new Color(115, 155, 255, 255);
        static readonly Color UI_ACCENT_DIM = new Color(85, 130, 255, 40);
        static readonly Color UI_GREEN = new Color(70, 190, 105, 255);
        static readonly Color UI_RED = new Color(230, 70, 70, 255);
        static readonly Color UI_ORANGE = new Color(235, 155, 55, 255);
        static readonly Color UI_TEXT = new Color(210, 215, 225, 255);
        static readonly Color UI_TEXT_MUTED = new Color(125, 132, 155, 255);
        static readonly Color UI_SLIDER_TRACK = new Color(38, 42, 56, 255);
        static readonly Color UI_SLIDER_FILL = new Color(85, 130, 255, 180);

        // Animation State
        static int _activeTab = 0;
        static float _tabIndicatorPos = 0f;
        static float[] _tabScrollY = new float[4];
        static float _menuFade = 0f;
        static Dictionary<string, float> _hoverStates = new();
        static Dictionary<string, float> _toggleStates = new();
        static readonly string[] _tabNames = { "Aim", "Detect", "Trigger", "Config" };

        static void Main(string[] args)
        {
            Console.WriteLine("Aimmy for macOS - Feature Complete (Refactored)");
            Console.WriteLine("---------------------------------------------");

            _config = Config.Load();

            RefreshModelList();

            bool forceSelect = args.Length > 0 && args[0] != "--realtime";
            if (!File.Exists(_config.ModelPath) || forceSelect)
            {
                SelectModel();
            }

            Console.WriteLine($"Loading Model: {_config.ModelPath}");
            using (_aiManager = new AIManager(_config.ModelPath))
            {
                if (!_aiManager.IsLoaded) {
                    Console.WriteLine("Failed to load model. Please restart and select a valid model.");
                    return;
                }

                string name = Path.GetFileName(_config.ModelPath);
                _currentModelIndex = _modelFiles.FindIndex(x => Path.GetFileName(x) == name);
                if (_currentModelIndex == -1 && _modelFiles.Count > 0) _currentModelIndex = 0;

                RunLoop();
            }
        }

        static void SelectModel()
        {
            if (_modelFiles.Count == 0) {
                Console.WriteLine("No ONNX models found!");
                return;
            }
            Console.WriteLine("Select a Model:");
            for (int i = 0; i < _modelFiles.Count; i++) Console.WriteLine($"[{i}] {Path.GetFileName(_modelFiles[i])}");
            Console.Write("Enter Choice (0): ");
            if (int.TryParse(Console.ReadLine(), out int c) && c >= 0 && c < _modelFiles.Count) {
                _config.ModelPath = _modelFiles[c];
                _config.Save();
            } else {
                _config.ModelPath = _modelFiles[0];
            }
        }

        static void RefreshModelList()
        {
            _modelFiles.Clear();
            if (File.Exists("model.onnx")) _modelFiles.Add("model.onnx");

            var newModels = new List<string>();
            if (Directory.Exists("../models")) newModels.AddRange(Directory.GetFiles("../models", "*.onnx"));
            if (Directory.Exists("models")) newModels.AddRange(Directory.GetFiles("models", "*.onnx"));

            foreach (var m in newModels)
            {
                if (!_modelFiles.Contains(m)) _modelFiles.Add(m);
            }
        }

        static uint _selectedDisplayID = 0;

        static void RunLoop()
        {
            uint[] displays = new uint[8];
            uint count;
            NativeMethods.CGGetActiveDisplayList(8, displays, out count);

            if (count > 1) {
                Console.WriteLine("\nMultiple Displays Detected:");
                for (int i = 0; i < count; i++) {
                     var b = NativeMethods.CGDisplayBounds(displays[i]);
                     Console.WriteLine($"[{i}] Display ID: {displays[i]} - {b.Size.Width}x{b.Size.Height} at ({b.Origin.X}, {b.Origin.Y})");
                }
                Console.Write("Select Display (0): ");
                if (int.TryParse(Console.ReadLine(), out int c) && c >= 0 && c < count) {
                     _selectedDisplayID = displays[c];
                } else {
                     _selectedDisplayID = displays[0];
                }
            } else {
                _selectedDisplayID = NativeMethods.CGMainDisplayID();
            }

            var bounds = NativeMethods.CGDisplayBounds(_selectedDisplayID);
            int width = (int)bounds.Size.Width;
            int height = (int)bounds.Size.Height;

            Raylib.SetConfigFlags((ConfigFlags)(16 | 4096 | 256));
            Raylib.InitWindow(width, height, "Aimmy ESP");
            Raylib.SetWindowPosition((int)bounds.Origin.X, (int)bounds.Origin.Y);
            Raylib.SetTargetFPS(_config.MaxFps);

            // Phase 4: Auto-load resolution profile
            if (_config.AutoResProfile)
            {
                string resProfile = AutoCalibrator.GetResolutionProfileName(width, height);
                if (System.IO.File.Exists(resProfile))
                {
                    _config = AutoCalibrator.LoadOrCreateResolutionProfile(width, height);
                    _currentProfileName = resProfile;
                }
            }

            _recoilTimer.Start();
            _triggerTimer.Start();
            _frameTimer.Start();
            _bindingText = GetKeyName(_config.AimKey);
            SetWindowClickthrough(false);

            long lastLevelCheck = 0;

            Raylib.SetExitKey(0); // Disable ESC = quit

            while (_running)
            {
                _sw.Restart();
                _frameTimer.Restart();

                if (_nsWindow == IntPtr.Zero) SetWindowClickthrough(_isPassthrough);

                if (System.DateTime.Now.Ticks / 10000 - lastLevelCheck > 2000) {
                     if (_nsWindow != IntPtr.Zero) {
                         IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);
                     }
                     lastLevelCheck = System.DateTime.Now.Ticks / 10000;
                }

                UpdateInput();

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Blank);

                // Always run aim assist (even with menu open)
                DrawOverlayAndAim(width, height);

                if (_showMenu)
                {
                    if (_isSelectingWindow) DrawWindowSelection(width, height);
                    else DrawMenu();
                }

                Raylib.EndDrawing();
            }

            Raylib.CloseWindow();
        }

        static void SetWindowClickthrough(bool clickthrough)
        {
            if (_nsWindow == IntPtr.Zero)
            {
                try {
                    IntPtr cls_NSApplication = ObjCRuntime.objc_getClass("NSApplication");
                    IntPtr sel_sharedApp = ObjCRuntime.sel_registerName("sharedApplication");
                    IntPtr app = ObjCRuntime.objc_msgSend_IntPtr(cls_NSApplication, sel_sharedApp);
                    IntPtr sel_keyWindow = ObjCRuntime.sel_registerName("keyWindow");
                    _nsWindow = ObjCRuntime.objc_msgSend_IntPtr(app, sel_keyWindow);

                    if (_nsWindow != IntPtr.Zero)
                    {
                         Console.WriteLine($"[Helpers] NSWindow Found: {_nsWindow}");
                         IntPtr sel_setCollectionBehavior = ObjCRuntime.sel_registerName("setCollectionBehavior:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setCollectionBehavior, 17);
                         IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[Helpers] Error getting NSWindow: {ex.Message}");
                }
            }

            if (_nsWindow != IntPtr.Zero)
            {
                IntPtr sel_setIgnoresMouseEvents = ObjCRuntime.sel_registerName("setIgnoresMouseEvents:");
                ObjCRuntime.objc_msgSend(_nsWindow, sel_setIgnoresMouseEvents, clickthrough);
                IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);

                if (clickthrough) Raylib.SetWindowState((ConfigFlags)8192);
                else Raylib.ClearWindowState((ConfigFlags)8192);

                _isPassthrough = clickthrough;
            }
        }

        static bool IsNativeKeyDown(int code)
        {
            if (code < 0) return false;
            if (code == 200) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Left);
            if (code == 201) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Right);
            if (code == 202) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Center);
            return NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, (ushort)code);
        }

        static bool CheckHotkey(int key, int mod)
        {
            if (key < 0) return false;
            bool keyDown = IsNativeKeyDown(key);
            if (mod > 0) return keyDown && IsNativeKeyDown(mod);
            return keyDown;
        }

        static void UpdateInput()
        {
            // Menu toggle (no modifier — Insert or configured key)
            if (Raylib.IsKeyPressed(KeyboardKey.Insert) || CheckHotkey(_config.KeyToggleMenu, 0))
            {
                _showMenu = !_showMenu;
                if (_showMenu) _menuFade = 0f;
                SetWindowClickthrough(!_showMenu);
                Thread.Sleep(150);
            }

            // Cmd+ESC = quit
            bool escPressed = Raylib.IsKeyPressed(KeyboardKey.Escape);
            bool cmdHeld = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, 55);
            if (escPressed && cmdHeld)
            {
                _running = false;
                return;
            }

            // ESC alone cancels auto-calibration
            if (escPressed &&
                AutoCalibrator.State != AutoCalibrator.CalibState.Idle &&
                AutoCalibrator.State != AutoCalibrator.CalibState.Done)
            {
                AutoCalibrator.CancelCalibration();
                _showMenu = true;
                SetWindowClickthrough(false);
            }

            if (CheckHotkey(_config.KeyToggleAim, _config.KeyToggleAimMod))
            {
                 _aimAssistActive = !_aimAssistActive;
                 Thread.Sleep(200);
            }

            if (CheckHotkey(_config.KeyToggleRecord, _config.KeyToggleRecordMod))
            {
                 if (Recorder.IsRecording) Recorder.Stop();
                 else Recorder.Start();
                 Thread.Sleep(200);
            }
        }

        // ========== UI ANIMATION HELPERS ==========

        static float AnimLerp(float current, float target, float speed)
        {
            float dt = Raylib.GetFrameTime();
            if (dt <= 0) dt = 0.016f;
            return current + (target - current) * Math.Min(speed * dt, 1f);
        }

        static Color ColorLerp(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Color(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t),
                (int)(a.A + (b.A - a.A) * t));
        }

        static float GetHoverAnim(string id, bool hovered, float speed = 10f)
        {
            if (!_hoverStates.ContainsKey(id)) _hoverStates[id] = 0f;
            _hoverStates[id] = AnimLerp(_hoverStates[id], hovered ? 1f : 0f, speed);
            return _hoverStates[id];
        }

        static float GetToggleAnim(string id, bool isOn, float speed = 8f)
        {
            if (!_toggleStates.ContainsKey(id)) _toggleStates[id] = isOn ? 1f : 0f;
            _toggleStates[id] = AnimLerp(_toggleStates[id], isOn ? 1f : 0f, speed);
            return _toggleStates[id];
        }

        static void DrawSectionHeader(int x, ref int y, int w, string text)
        {
            y += 8;
            Raylib.DrawText(text.ToUpper(), x, y, 11, UI_ACCENT);
            y += 16;
            Raylib.DrawRectangle(x, y, w, 1, UI_BORDER);
            y += 10;
        }

        static bool DrawToggle(int x, ref int y, int w, string label, bool value, int mx, int my, bool clicked)
        {
            int h = 32;
            Raylib.DrawText(label, x + 4, y + 7, 15, UI_TEXT);

            int toggleW = 42;
            int toggleH = 22;
            int toggleX = x + w - toggleW - 4;
            int toggleY = y + (h - toggleH) / 2;

            bool hover = mx >= x && mx <= x + w && my >= y && my <= y + h;
            string id = $"tgl_{label}";
            float t = GetToggleAnim(id, value);
            float ht = GetHoverAnim(id + "_h", hover);

            Color trackColor = ColorLerp(new Color(55, 58, 72, 255), UI_GREEN, t);
            trackColor = ColorLerp(trackColor, ColorLerp(new Color(70, 73, 87, 255), new Color(85, 205, 125, 255), t), ht * 0.3f);

            Raylib.DrawRectangleRounded(new Rectangle(toggleX, toggleY, toggleW, toggleH), 1.0f, 16, trackColor);

            float thumbX = toggleX + 3 + t * (toggleW - toggleH + 2);
            float thumbY = toggleY + toggleH / 2f;
            Raylib.DrawCircleV(new System.Numerics.Vector2(thumbX + (toggleH - 6) / 2f, thumbY), (toggleH - 6) / 2f, Color.White);

            y += h + 6;
            return hover && clicked;
        }

        // ========== MODERN DRAW MENU ==========

        static void DrawMenu()
        {
            float dt = Raylib.GetFrameTime();
            if (dt <= 0) dt = 0.016f;
            _menuFade = AnimLerp(_menuFade, 1f, 6f);

            int panelW = 520;
            int headerH = 48;
            int tabBarH = 38;
            int panelH = 680;
            int footerH = 52;
            int contentW = panelW - 48;

            // Dragging
            bool overHeader = Raylib.GetMouseX() >= _menuX && Raylib.GetMouseX() <= _menuX + panelW &&
                              Raylib.GetMouseY() >= _menuY && Raylib.GetMouseY() <= _menuY + headerH;
            if (overHeader && Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                _menuX += (int)Raylib.GetMouseDelta().X;
                _menuY += (int)Raylib.GetMouseDelta().Y;
            }

            // Fade + slide animation
            int animOffsetY = (int)((1f - _menuFade) * 15);
            int px = _menuX;
            int py = _menuY + animOffsetY;
            byte alpha = (byte)(255 * _menuFade);

            // Shadow
            Raylib.DrawRectangleRounded(new Rectangle(px + 6, py + 6, panelW, panelH), 0.08f, 16, new Color(0, 0, 0, (int)(100 * _menuFade)));
            // Solid opaque background (blocks terminal bleed-through)
            Raylib.DrawRectangleRounded(new Rectangle(px - 1, py - 1, panelW + 2, panelH + 2), 0.08f, 16, new Color(8, 9, 14, 255));
            // Main panel
            Raylib.DrawRectangleRounded(new Rectangle(px, py, panelW, panelH), 0.08f, 16, UI_BG);
            Raylib.DrawRectangleRoundedLines(new Rectangle(px, py, panelW, panelH), 0.08f, 16, UI_BORDER);

            // Accent gradient strip at top
            Raylib.DrawRectangleRounded(new Rectangle(px, py, panelW, 3), 1f, 4, UI_ACCENT);

            // Title
            Raylib.DrawText("AIMMY", px + 20, py + 14, 22, UI_TEXT);
            Raylib.DrawText("v2", px + 92, py + 18, 14, UI_TEXT_MUTED);

            // Status indicator
            Color statusColor = _aimAssistActive ? UI_GREEN : UI_RED;
            Raylib.DrawCircleV(new System.Numerics.Vector2(px + panelW - 24, py + 24), 5, statusColor);

            int mouseX = Raylib.GetMouseX();
            int mouseY = Raylib.GetMouseY();
            bool isDown = Raylib.IsMouseButtonDown(MouseButton.Left);
            bool clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);

            // --- BINDING OVERLAY ---
            if (_isBinding)
            {
                int bx = px + 30, by = py + 180, bw = panelW - 60, bh = 220;
                Raylib.DrawRectangleRounded(new Rectangle(bx, by, bw, bh), 0.12f, 16, UI_SURFACE);
                Raylib.DrawRectangleRoundedLines(new Rectangle(bx, by, bw, bh), 0.12f, 16, UI_ACCENT);

                if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Left))
                {
                    Raylib.DrawText("Release Mouse...", bx + bw/2 - 70, by + bh/2 - 8, 18, UI_TEXT_MUTED);
                    return;
                }

                string bindLabel = _bindingTarget switch {
                    0 => "AIM KEY", 1 => "MENU TOGGLE", 2 => "AIM TOGGLE", 3 => "RECORD TOGGLE", _ => "KEY"
                };
                Raylib.DrawText($"BIND: {bindLabel}", bx + bw/2 - Raylib.MeasureText($"BIND: {bindLabel}", 16)/2, by + 20, 16, UI_ACCENT);
                Raylib.DrawText("PRESS ANY KEY OR CLICK", bx + bw/2 - 110, by + 60, 18, UI_TEXT);
                Raylib.DrawText("ESC: Cancel", bx + 40, by + 160, 14, UI_TEXT_MUTED);
                Raylib.DrawText(_bindingTarget == 0 ? "DELETE: Always On" : "DELETE: Clear", bx + bw - 160, by + 160, 14, UI_TEXT_MUTED);
                if (_bindingTarget >= 2)
                    Raylib.DrawText("Held modifier (Cmd/Shift/Opt) auto-detected", bx + 20, by + 100, 12, UI_TEXT_MUTED);

                int foundKey = -2;
                int foundMod = 0;
                // Check modifier keys being held (store separately)
                bool modCmd = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, 55);
                bool modShift = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, 56);
                bool modOpt = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, 58);
                bool modCtrl = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, 59);

                for (ushort k = 0; k < 128; k++) {
                    if (k == 55 || k == 56 || k == 58 || k == 59) continue; // skip modifiers
                    if (NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, k)) { foundKey = k; break; }
                }
                if (foundKey == -2) {
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Left)) foundKey = 200;
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Right)) foundKey = 201;
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Center)) foundKey = 202;
                }
                // Determine modifier
                if (foundKey != -2 && foundKey != 53 && foundKey != 51 && foundKey != 117) {
                    if (modCmd) foundMod = 55;
                    else if (modCtrl) foundMod = 59;
                    else if (modOpt) foundMod = 58;
                    else if (modShift) foundMod = 56;
                }

                if (foundKey != -2)
                {
                    if (foundKey == 53) { _isBinding = false; } // ESC cancel
                    else if (foundKey == 51 || foundKey == 117) { // Delete/Backspace clear
                        ApplyBinding(-1, 0);
                        _isBinding = false;
                    } else {
                        ApplyBinding(foundKey, foundMod);
                        _isBinding = false;
                    }
                    Thread.Sleep(200);
                }
                return;
            }

            // --- TAB BAR ---
            int tabY = py + headerH;
            int tabW = panelW / _tabNames.Length;

            _tabIndicatorPos = AnimLerp(_tabIndicatorPos, _activeTab * tabW, 12f);

            Raylib.DrawRectangle(px, tabY, panelW, tabBarH, UI_SURFACE);
            Raylib.DrawRectangle(px, tabY + tabBarH - 1, panelW, 1, UI_BORDER);

            for (int i = 0; i < _tabNames.Length; i++)
            {
                int tx = px + i * tabW;
                bool tabHover = mouseX >= tx && mouseX <= tx + tabW && mouseY >= tabY && mouseY <= tabY + tabBarH;
                float ht = GetHoverAnim($"tab_{i}", tabHover || _activeTab == i);

                Color textColor = ColorLerp(UI_TEXT_MUTED, UI_TEXT, ht);
                int tw = Raylib.MeasureText(_tabNames[i], 14);
                Raylib.DrawText(_tabNames[i], tx + (tabW - tw) / 2, tabY + 12, 14, textColor);

                if (tabHover && clicked) _activeTab = i;
            }

            // Animated underline
            Raylib.DrawRectangleRounded(
                new Rectangle(px + _tabIndicatorPos + 8, tabY + tabBarH - 3, tabW - 16, 3),
                1f, 4, UI_ACCENT);

            // --- CONTENT AREA ---
            int contentY = tabY + tabBarH + 8;
            int contentH = panelH - headerH - tabBarH - footerH - 16;
            int startX = px + 24;

            // Scroll for active tab
            float wheel = Raylib.GetMouseWheelMove();
            if (mouseX >= px && mouseX <= px + panelW && mouseY >= contentY && mouseY <= contentY + contentH)
            {
                if (wheel != 0) _tabScrollY[_activeTab] -= wheel * 35;
            }
            if (_tabScrollY[_activeTab] < 0) _tabScrollY[_activeTab] = 0;

            Raylib.BeginScissorMode(px, contentY, panelW, contentH);

            int y = contentY + 4 - (int)_tabScrollY[_activeTab];
            int gap = 48;
            bool interact = !_isModelDropdownOpen;
            Rectangle? deferredDropdown = null;

            // ==================== TAB 0: AIM ====================
            if (_activeTab == 0)
            {
                DrawSectionHeader(startX, ref y, contentW, "Sensitivity");

                float sens = _config.MouseSensitivity;
                DrawSlider(startX, y, contentW, 20, "Sensitivity", ref sens, 0.001f, 2.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.MouseSensitivity = sens;

                if (DrawToggle(startX, ref y, contentW, "Sticky Aim", _config.DynamicSensitivity, mouseX, mouseY, clicked && interact)) {
                    _config.DynamicSensitivity = !_config.DynamicSensitivity; _config.Save(_currentProfileName);
                }
                if (_config.DynamicSensitivity) {
                    float dscale = _config.DynamicSensScale;
                    DrawSlider(startX, y, contentW, 20, "Sticky Scale", ref dscale, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.DynamicSensScale = dscale;
                }

                DrawSectionHeader(startX, ref y, contentW, "Smoothing");

                float smooth = _config.MouseSmoothing;
                DrawSlider(startX, y, contentW, 20, "Smoothing", ref smooth, 0.0f, 0.95f, mouseX, mouseY, isDown && interact); y += gap;
                _config.MouseSmoothing = smooth;

                if (DrawToggle(startX, ref y, contentW, "Adaptive Smooth", _config.AdaptiveSmoothing, mouseX, mouseY, clicked && interact)) {
                    _config.AdaptiveSmoothing = !_config.AdaptiveSmoothing; _config.Save(_currentProfileName);
                }
                if (_config.AdaptiveSmoothing) {
                    float asNear = _config.AdaptiveSmoothNear;
                    DrawSlider(startX, y, contentW, 20, "Near (Precise)", ref asNear, 0.0f, 0.95f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.AdaptiveSmoothNear = asNear;
                    float asFar = _config.AdaptiveSmoothFar;
                    DrawSlider(startX, y, contentW, 20, "Far (Snappy)", ref asFar, 0.0f, 0.95f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.AdaptiveSmoothFar = asFar;
                    float asRange = _config.AdaptiveSmoothRange;
                    DrawSlider(startX, y, contentW, 20, "Range", ref asRange, 10.0f, 500.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.AdaptiveSmoothRange = asRange;
                }

                DrawSectionHeader(startX, ref y, contentW, "Prediction");

                if (DrawToggle(startX, ref y, contentW, "Prediction", _config.PredictionEnabled, mouseX, mouseY, clicked && interact)) {
                    _config.PredictionEnabled = !_config.PredictionEnabled; _config.Save(_currentProfileName);
                }
                if (_config.PredictionEnabled) {
                    float ps = _config.PredictionScale;
                    DrawSlider(startX, y, contentW, 20, "Scale", ref ps, 0.1f, 10.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.PredictionScale = ps;
                    if (DrawToggle(startX, ref y, contentW, "Acceleration", _config.PredictionUseAcceleration, mouseX, mouseY, clicked && interact)) {
                        _config.PredictionUseAcceleration = !_config.PredictionUseAcceleration; _config.Save(_currentProfileName);
                    }
                }

                DrawSectionHeader(startX, ref y, contentW, "Targeting");

                if (DrawButton(startX, y, contentW, 32, $"Aim Bone: {_config.AimBone}", mouseX, mouseY, clicked && interact)) {
                    _config.AimBone++; if ((int)_config.AimBone > 3) _config.AimBone = Config.BoneType.Head;
                    _config.Save(_currentProfileName);
                } y += 40;

                if (DrawToggle(startX, ref y, contentW, "Jump Offset", _config.JumpOffsetEnabled, mouseX, mouseY, clicked && interact)) {
                    _config.JumpOffsetEnabled = !_config.JumpOffsetEnabled; _config.Save(_currentProfileName);
                }
                if (_config.JumpOffsetEnabled) {
                    float jOff = _config.JumpOffset;
                    DrawSlider(startX, y, contentW, 20, "Offset Y", ref jOff, -200, 200, mouseX, mouseY, isDown && interact); y += gap;
                    _config.JumpOffset = (int)jOff;
                }

                DrawSectionHeader(startX, ref y, contentW, "Flick Shot");

                if (DrawToggle(startX, ref y, contentW, "Flick Shot", _config.FlickEnabled, mouseX, mouseY, clicked && interact)) {
                    _config.FlickEnabled = !_config.FlickEnabled; _config.Save(_currentProfileName);
                }
                if (_config.FlickEnabled) {
                    float fSpd = _config.FlickSpeedMultiplier;
                    DrawSlider(startX, y, contentW, 20, "Flick Speed", ref fSpd, 1.0f, 10.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.FlickSpeedMultiplier = fSpd;
                    float fThr = _config.FlickThreshold;
                    DrawSlider(startX, y, contentW, 20, "Flick Range (px)", ref fThr, 20, 300, mouseX, mouseY, isDown && interact); y += gap;
                    _config.FlickThreshold = fThr;
                    float fCorr = _config.FlickCorrectionSmooth;
                    DrawSlider(startX, y, contentW, 20, "Correction Smooth", ref fCorr, 0.0f, 0.95f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.FlickCorrectionSmooth = fCorr;
                }

                if (DrawToggle(startX, ref y, contentW, "Humanize Aim", _config.HumanizeAim, mouseX, mouseY, clicked && interact)) {
                    _config.HumanizeAim = !_config.HumanizeAim; _config.Save(_currentProfileName);
                }
                if (_config.HumanizeAim) {
                    float hum = _config.HumanizeStrength;
                    DrawSlider(startX, y, contentW, 20, "Curve Strength", ref hum, 1.0f, 20.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.HumanizeStrength = hum;
                }
            }
            // ==================== TAB 1: DETECT ====================
            else if (_activeTab == 1)
            {
                DrawSectionHeader(startX, ref y, contentW, "Model");

                string mName = (_modelFiles.Count > 0 && _currentModelIndex < _modelFiles.Count) ? Path.GetFileName(_modelFiles[_currentModelIndex]) : "None";
                if (mName.Length > 28) mName = mName.Substring(0, 25) + "...";

                if (!_isModelDropdownOpen) {
                    if (DrawButton(startX, y, contentW - 40, 32, $"{mName}", mouseX, mouseY, clicked)) {
                        _isModelDropdownOpen = true; _modelSearchQuery = ""; _modelScrollY = 0;
                    }
                    if (DrawButton(startX + contentW - 34, y, 34, 32, "R", mouseX, mouseY, clicked)) RefreshModelList();
                } else {
                    Raylib.DrawRectangleRounded(new Rectangle(startX, y, contentW - 40, 32), 0.35f, 16, UI_ELEVATED);
                    Raylib.DrawRectangleRoundedLines(new Rectangle(startX, y, contentW - 40, 32), 0.35f, 16, UI_ACCENT);
                    Raylib.DrawText(mName, startX + 12, y + 8, 14, UI_TEXT_MUTED);
                    if (DrawButton(startX + contentW - 34, y, 34, 32, "R", mouseX, mouseY, clicked)) RefreshModelList();
                    if (clicked && mouseX >= startX && mouseX <= startX + contentW - 40 && mouseY >= y && mouseY <= y + 32)
                        _isModelDropdownOpen = false;
                    deferredDropdown = new Rectangle(startX, y + 36, contentW, 200);
                }
                y += 42;

                DrawSectionHeader(startX, ref y, contentW, "Detection");

                float conf = _config.ConfidenceThreshold;
                DrawSlider(startX, y, contentW, 20, "Confidence", ref conf, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.ConfidenceThreshold = conf;

                float fov = _config.FovSize;
                DrawSlider(startX, y, contentW, 20, "FOV", ref fov, 50, 1000, mouseX, mouseY, isDown && interact); y += gap;
                _config.FovSize = (int)fov;

                float sticky = _config.TargetStickinessRadius;
                DrawSlider(startX, y, contentW, 20, "Target Sticky", ref sticky, 0, 100, mouseX, mouseY, isDown && interact); y += gap;
                _config.TargetStickinessRadius = sticky;

                if (DrawButton(startX, y, contentW, 32, $"Team Color: {_config.TeamColor}", mouseX, mouseY, clicked && interact)) {
                    _config.TeamColor++; if ((int)_config.TeamColor > 4) _config.TeamColor = Config.TeamColorType.None;
                    _config.Save(_currentProfileName);
                } y += 40;

                if (DrawButton(startX, y, contentW, 32, $"Priority: {_config.Priority}", mouseX, mouseY, clicked && interact)) {
                    _config.Priority = _config.Priority == Config.TargetPriority.Distance ? Config.TargetPriority.Confidence : Config.TargetPriority.Distance;
                    _config.Save(_currentProfileName);
                } y += 40;

                DrawSectionHeader(startX, ref y, contentW, "Auto Calibration");

                // Phase 2: Closed-loop sensitivity calibration
                {
                    string sensLabel = AutoCalibrator.State switch {
                        AutoCalibrator.CalibState.Idle => "Auto Calibrate Sensitivity",
                        AutoCalibrator.CalibState.Done => "Calibrated!",
                        AutoCalibrator.CalibState.Failed => "Failed — Retry",
                        _ => $"Calibrating... {AutoCalibrator.Progress}%"
                    };
                    bool sensActive = AutoCalibrator.State == AutoCalibrator.CalibState.Idle ||
                                      AutoCalibrator.State == AutoCalibrator.CalibState.Done ||
                                      AutoCalibrator.State == AutoCalibrator.CalibState.Failed;
                    if (DrawButton(startX, y, contentW, 32, sensLabel, mouseX, mouseY, clicked && interact && sensActive)) {
                        AutoCalibrator.StartSensitivityCalibration();
                        _showMenu = false; SetWindowClickthrough(true);
                    } y += 36;
                    if (!string.IsNullOrEmpty(AutoCalibrator.StatusText) && AutoCalibrator.State != AutoCalibrator.CalibState.Idle) {
                        Raylib.DrawText(AutoCalibrator.StatusText, startX, y, 11, UI_TEXT_MUTED);
                        y += 16;
                    }
                }

                // Phase 3: Auto-offset toggle
                if (DrawToggle(startX, ref y, contentW, "Auto-Correct Offsets", _config.AutoOffsetEnabled, mouseX, mouseY, clicked && interact)) {
                    _config.AutoOffsetEnabled = !_config.AutoOffsetEnabled;
                    if (_config.AutoOffsetEnabled) AutoCalibrator.ResetAutoOffset();
                    _config.Save(_currentProfileName);
                }

                // Window calibration
                if (DrawButton(startX, y, contentW, 32, "Calibrate to Window", mouseX, mouseY, clicked && interact)) {
                    _windowList = WindowHelper.GetWindows(); _isSelectingWindow = true; _windowListScrollY = 0;
                } y += 38;

                // Manual screen center
                if (DrawButton(startX, y, contentW, 32, "Manual Center Calibration", mouseX, mouseY, clicked && interact)) {
                    _calibSavedOffsetX = _config.CenterOffsetX; _calibSavedOffsetY = _config.CenterOffsetY;
                    _calibHoldTime = 0;
                    _isCalibratingCenter = true; _showMenu = false; SetWindowClickthrough(true);
                } y += 38;

                DrawSectionHeader(startX, ref y, contentW, "Manual Offsets");

                float ox = _config.XOffset;
                DrawSlider(startX, y, contentW, 20, "Offset X", ref ox, -500, 500, mouseX, mouseY, isDown && interact); y += gap;
                _config.XOffset = (int)ox;
                float oy = _config.YOffset;
                DrawSlider(startX, y, contentW, 20, "Offset Y", ref oy, -500, 500, mouseX, mouseY, isDown && interact); y += gap;
                _config.YOffset = (int)oy;

                if (DrawButton(startX, y, contentW, 32, "Reset Offsets", mouseX, mouseY, clicked && interact)) {
                    _config.XOffset = 0; _config.YOffset = 0; _config.CenterOffsetX = 0; _config.CenterOffsetY = 0;
                    AutoCalibrator.ResetAutoOffset();
                    _config.Save(_currentProfileName);
                } y += 40;

                DrawSectionHeader(startX, ref y, contentW, "Debug");

                // Phase 5: Debug overlay toggle
                if (DrawToggle(startX, ref y, contentW, "Debug Overlay", _config.DebugOverlay, mouseX, mouseY, clicked && interact)) {
                    _config.DebugOverlay = !_config.DebugOverlay; _config.Save(_currentProfileName);
                }

                // Phase 4: Resolution profiles
                if (DrawToggle(startX, ref y, contentW, "Auto Resolution Profile", _config.AutoResProfile, mouseX, mouseY, clicked && interact)) {
                    _config.AutoResProfile = !_config.AutoResProfile; _config.Save(_currentProfileName);
                }
            }
            // ==================== TAB 2: TRIGGER ====================
            else if (_activeTab == 2)
            {
                if (DrawToggle(startX, ref y, contentW, "Triggerbot", _config.TriggerBot, mouseX, mouseY, clicked && interact)) {
                    _config.TriggerBot = !_config.TriggerBot; _config.Save(_currentProfileName);
                }

                y += 4;
                float tDelay = _config.TriggerCooldown;
                DrawSlider(startX, y, contentW, 20, "Cooldown (ms)", ref tDelay, 10, 2000, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerCooldown = (int)tDelay;

                float tConf = _config.TriggerConfidence;
                DrawSlider(startX, y, contentW, 20, "Confidence", ref tConf, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerConfidence = tConf;

                float tRange = _config.TriggerRange;
                DrawSlider(startX, y, contentW, 20, "Range (px)", ref tRange, 0, 200, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerRange = (int)tRange;

                float tShotDelay = _config.TriggerDelay;
                DrawSlider(startX, y, contentW, 20, "Shot Delay (ms)", ref tShotDelay, 0, 500, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerDelay = (int)tShotDelay;

                if (DrawButton(startX, y, contentW, 32, $"Mode: {(_config.TriggerAlways ? "Always" : "On Aim Key")}", mouseX, mouseY, clicked && interact)) {
                    _config.TriggerAlways = !_config.TriggerAlways; _config.Save(_currentProfileName);
                } y += 40;

                if (DrawToggle(startX, ref y, contentW, "Magnet", _config.MagnetTrigger, mouseX, mouseY, clicked && interact)) {
                    _config.MagnetTrigger = !_config.MagnetTrigger; _config.Save(_currentProfileName);
                }
                if (DrawToggle(startX, ref y, contentW, "Strict Color", _config.TriggerColorCheck, mouseX, mouseY, clicked && interact)) {
                    _config.TriggerColorCheck = !_config.TriggerColorCheck; _config.Save(_currentProfileName);
                }
                if (DrawToggle(startX, ref y, contentW, "Prediction", _config.TriggerPrediction, mouseX, mouseY, clicked && interact)) {
                    _config.TriggerPrediction = !_config.TriggerPrediction; _config.Save(_currentProfileName);
                }

                float tBurst = _config.TriggerBurst;
                DrawSlider(startX, y, contentW, 20, "Burst Shots", ref tBurst, 0, 10, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerBurst = (int)tBurst;
            }
            // ==================== TAB 3: CONFIG ====================
            else if (_activeTab == 3)
            {
                DrawSectionHeader(startX, ref y, contentW, "Profiles");

                int profileBtnW = (contentW - 20) / 5;
                for (int i = 1; i <= 5; i++)
                {
                    string pName = (i == 1) ? "config.json" : $"config_p{i}.json";
                    bool isCurrent = _currentProfileName == pName;
                    int bx = startX + (i - 1) * (profileBtnW + 4);

                    bool hover = mouseX >= bx && mouseX <= bx + profileBtnW && mouseY >= y && mouseY <= y + 30;
                    float ht = GetHoverAnim($"prof_{i}", hover || isCurrent);
                    Color bg = isCurrent ? UI_ACCENT : ColorLerp(UI_ELEVATED, new Color(45, 48, 62, 255), ht);
                    Color border = isCurrent ? UI_ACCENT : ColorLerp(UI_BORDER, UI_ACCENT, ht);

                    Raylib.DrawRectangleRounded(new Rectangle(bx, y, profileBtnW, 30), 0.4f, 16, bg);
                    Raylib.DrawRectangleRoundedLines(new Rectangle(bx, y, profileBtnW, 30), 0.4f, 16, border);
                    int tw = Raylib.MeasureText($"P{i}", 14);
                    Raylib.DrawText($"P{i}", bx + (profileBtnW - tw) / 2, y + 8, 14, isCurrent ? UI_BG : UI_TEXT);

                    if (hover && clicked) {
                        _currentProfileName = pName; _config = Config.Load(_currentProfileName);
                        if (File.Exists(_config.ModelPath)) {
                            try {
                                _aiManager?.Dispose(); _aiManager = new AIManager(_config.ModelPath);
                                for (int m = 0; m < _modelFiles.Count; m++) {
                                    if (Path.GetFullPath(_modelFiles[m]) == Path.GetFullPath(_config.ModelPath)) {
                                        _currentModelIndex = m; break;
                                    }
                                }
                            } catch {}
                        }
                    }
                }
                y += 42;

                DrawSectionHeader(startX, ref y, contentW, "Input");

                float maxFps = _config.MaxFps;
                DrawSlider(startX, y, contentW, 20, "Max FPS", ref maxFps, 30, 360, mouseX, mouseY, isDown && interact); y += gap;
                if ((int)maxFps != _config.MaxFps) {
                    _config.MaxFps = (int)maxFps; Raylib.SetTargetFPS(_config.MaxFps);
                }

                DrawSectionHeader(startX, ref y, contentW, "Visuals");

                if (DrawToggle(startX, ref y, contentW, "ESP Info", _config.ShowESP, mouseX, mouseY, clicked && interact)) {
                    _config.ShowESP = !_config.ShowESP; _config.Save(_currentProfileName);
                }

                float fr = _config.FovColorR;
                DrawSlider(startX, y, contentW, 20, "FOV Red", ref fr, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
                _config.FovColorR = (int)fr;
                float fg = _config.FovColorG;
                DrawSlider(startX, y, contentW, 20, "FOV Green", ref fg, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
                _config.FovColorG = (int)fg;
                float fb = _config.FovColorB;
                DrawSlider(startX, y, contentW, 20, "FOV Blue", ref fb, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
                _config.FovColorB = (int)fb;
                float fa = _config.FovColorA;
                DrawSlider(startX, y, contentW, 20, "FOV Alpha", ref fa, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
                _config.FovColorA = (int)fa;

                DrawSectionHeader(startX, ref y, contentW, "Extras");

                if (DrawToggle(startX, ref y, contentW, "Recoil Control", _config.RecoilEnabled, mouseX, mouseY, clicked && interact)) {
                    _config.RecoilEnabled = !_config.RecoilEnabled; _config.Save(_currentProfileName);
                }
                if (_config.RecoilEnabled) {
                    float ry = _config.RecoilY;
                    DrawSlider(startX, y, contentW, 20, "Recoil Y", ref ry, 0, 50, mouseX, mouseY, isDown && interact); y += gap;
                    _config.RecoilY = (int)ry;
                    float rx = _config.RecoilX;
                    DrawSlider(startX, y, contentW, 20, "Recoil X", ref rx, -50, 50, mouseX, mouseY, isDown && interact); y += gap;
                    _config.RecoilX = (int)rx;
                    float rd = _config.RecoilDelay;
                    DrawSlider(startX, y, contentW, 20, "Recoil Delay (ms)", ref rd, 10, 500, mouseX, mouseY, isDown && interact); y += gap;
                    _config.RecoilDelay = (int)rd;
                }

                if (DrawToggle(startX, ref y, contentW, "Jitter", _config.Jitter, mouseX, mouseY, clicked && interact)) {
                    _config.Jitter = !_config.Jitter; _config.Save(_currentProfileName);
                }
                if (_config.Jitter) {
                    float ja = _config.JitterAmount;
                    DrawSlider(startX, y, contentW, 20, "Amount", ref ja, 0.0f, 20.0f, mouseX, mouseY, isDown && interact); y += gap;
                    _config.JitterAmount = ja;
                }

                y += 8;
                string recLabel = Recorder.IsRecording ? "STOP RECORDING" : (Recorder.IsProcessing ? "Processing..." : "Record AI Vision");
                if (DrawButton(startX, y, contentW, 32, recLabel, mouseX, mouseY, clicked && !Recorder.IsProcessing)) {
                    if (Recorder.IsRecording) Recorder.Stop(); else Recorder.Start();
                }
                if (Recorder.IsRecording) {
                    Raylib.DrawCircleV(new System.Numerics.Vector2(startX + contentW - 12, y + 16), 5, UI_RED);
                    y += 36;
                    string stats = $"Frames: {Recorder.FrameCount}  Dropped: {Recorder.DroppedFrames}  Queue: {Recorder.QueueDepth}  Time: {Recorder.RecordingDuration:F1}s";
                    Raylib.DrawText(stats, startX, y, 12, UI_TEXT_MUTED);
                } else if (Recorder.IsProcessing) {
                    y += 36;
                    Raylib.DrawText("Converting to video...", startX, y, 12, UI_TEXT_MUTED);
                }
                y += 40;

                DrawSectionHeader(startX, ref y, contentW, "Keybinds");

                // Menu Toggle
                if (DrawButton(startX, y, contentW, 32, $"Toggle Menu: {GetKeyName(_config.KeyToggleMenu)}", mouseX, mouseY, clicked && interact)) {
                    _bindingTarget = 1; _isBinding = true;
                } y += 38;

                // Aim Toggle
                if (DrawButton(startX, y, contentW, 32, $"Toggle Aim: {GetBindDisplay(_config.KeyToggleAim, _config.KeyToggleAimMod)}", mouseX, mouseY, clicked && interact)) {
                    _bindingTarget = 2; _isBinding = true;
                } y += 38;

                // Record Toggle
                if (DrawButton(startX, y, contentW, 32, $"Toggle Record: {GetBindDisplay(_config.KeyToggleRecord, _config.KeyToggleRecordMod)}", mouseX, mouseY, clicked && interact)) {
                    _bindingTarget = 3; _isBinding = true;
                } y += 38;

                // Aim Key (existing)
                if (DrawButton(startX, y, contentW, 32, $"Aim Key: {_bindingText}", mouseX, mouseY, clicked && interact)) {
                    _bindingTarget = 0; _isBinding = true;
                } y += 40;

                DrawSectionHeader(startX, ref y, contentW, "Updates");

                // Check / Update button
                string updateLabel = Updater.State switch {
                    Updater.UpdateState.Checking => "Checking...",
                    Updater.UpdateState.Updating => "Updating...",
                    Updater.UpdateState.UpdateAvailable => "Install Update",
                    Updater.UpdateState.Success => "Restart to Apply",
                    _ => "Check for Updates"
                };
                bool canClick = Updater.State != Updater.UpdateState.Checking &&
                                Updater.State != Updater.UpdateState.Updating &&
                                Updater.State != Updater.UpdateState.Success;
                if (DrawButton(startX, y, contentW, 32, updateLabel, mouseX, mouseY, clicked && interact && canClick)) {
                    if (Updater.State == Updater.UpdateState.UpdateAvailable)
                        Updater.ApplyUpdate();
                    else
                        Updater.CheckForUpdates();
                }
                y += 36;

                // Status line
                if (!string.IsNullOrEmpty(Updater.StatusMessage)) {
                    Color statusCol = Updater.State switch {
                        Updater.UpdateState.UpdateAvailable => UI_ACCENT,
                        Updater.UpdateState.Success => UI_GREEN,
                        Updater.UpdateState.Failed => UI_RED,
                        Updater.UpdateState.NoUpdate => UI_GREEN,
                        _ => UI_TEXT_MUTED
                    };
                    Raylib.DrawText(Updater.StatusMessage, startX, y, 13, statusCol);
                    y += 18;
                }
                if (!string.IsNullOrEmpty(Updater.LatestCommitMessage) && Updater.State == Updater.UpdateState.UpdateAvailable) {
                    string msg = Updater.LatestCommitMessage.Length > 50 ? Updater.LatestCommitMessage.Substring(0, 47) + "..." : Updater.LatestCommitMessage;
                    Raylib.DrawText($"Latest: {msg}", startX, y, 12, UI_TEXT_MUTED);
                    y += 18;
                }

                y += 10;
            }

            // Clamp scroll
            int maxScroll = Math.Max(0, (y + (int)_tabScrollY[_activeTab]) - contentY - contentH + 20);
            if (_tabScrollY[_activeTab] > maxScroll) _tabScrollY[_activeTab] = maxScroll;

            Raylib.EndScissorMode();

            // --- FOOTER ---
            int footY = py + panelH - footerH;
            Raylib.DrawRectangle(px + 1, footY, panelW - 2, 1, UI_BORDER);

            if (DrawButton(startX, footY + 10, contentW / 2 - 6, 34, "Save", mouseX, mouseY, clicked))
                _config.Save(_currentProfileName);
            if (DrawButton(startX + contentW / 2 + 6, footY + 10, contentW / 2 - 6, 34, "Quit", mouseX, mouseY, clicked))
                _running = false;

            // Scrollbar
            if (maxScroll > 0) {
                float scrollPct = _tabScrollY[_activeTab] / maxScroll;
                int barH = Math.Max(20, contentH * contentH / (contentH + maxScroll));
                int barY = contentY + (int)(scrollPct * (contentH - barH));
                Raylib.DrawRectangleRounded(new Rectangle(px + panelW - 6, barY, 3, barH), 1f, 4, new Color(80, 85, 100, 120));
            }

            // Auto-save on mouse release
            if (Raylib.IsMouseButtonReleased(MouseButton.Left)) _config.Save(_currentProfileName);

            // Deferred dropdown (on top)
            if (deferredDropdown.HasValue && _isModelDropdownOpen)
            {
                DrawSearchableDropdown(deferredDropdown.Value, _modelFiles, ref _isModelDropdownOpen, ref _modelSearchQuery, ref _modelScrollY, (selectedIndex) => {
                    _currentModelIndex = selectedIndex;
                    _config.ModelPath = _modelFiles[_currentModelIndex];
                    try { _aiManager?.Dispose(); _aiManager = new AIManager(_config.ModelPath); _config.Save(_currentProfileName); } catch {}
                });
            }
        }

        static void DrawWindowSelection(int screenW, int screenH)
        {
            int panelW = 560;
            int panelH = 520;
            int x = (screenW - panelW) / 2;
            int y = (screenH - panelH) / 2;

            // Shadow + Background
            Raylib.DrawRectangleRounded(new Rectangle(x + 4, y + 4, panelW, panelH), 0.08f, 16, new Color(0, 0, 0, 80));
            Raylib.DrawRectangleRounded(new Rectangle(x, y, panelW, panelH), 0.08f, 16, UI_BG);
            Raylib.DrawRectangleRoundedLines(new Rectangle(x, y, panelW, panelH), 0.08f, 16, UI_BORDER);
            Raylib.DrawRectangleRounded(new Rectangle(x, y, panelW, 3), 1f, 4, UI_ACCENT);

            Raylib.DrawText("Select Window", x + 20, y + 16, 20, UI_TEXT);

            if (DrawButton(x + panelW - 90, y + 12, 70, 28, "Cancel", Raylib.GetMouseX(), Raylib.GetMouseY(), Raylib.IsMouseButtonPressed(MouseButton.Left)))
                _isSelectingWindow = false;
            if (DrawButton(x + panelW - 175, y + 12, 75, 28, "Refresh", Raylib.GetMouseX(), Raylib.GetMouseY(), Raylib.IsMouseButtonPressed(MouseButton.Left)))
            { _windowList = WindowHelper.GetWindows(); _windowListScrollY = 0; }

            int listY = y + 56;
            int listH = panelH - 72;
            Raylib.BeginScissorMode(x, listY, panelW, listH);

            int contentHeight = _windowList.Count * 54;
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0) _windowListScrollY -= wheel * 30;
            if (_windowListScrollY < 0) _windowListScrollY = 0;
            if (_windowListScrollY > Math.Max(0, contentHeight - listH)) _windowListScrollY = Math.Max(0, contentHeight - listH);

            int itemY = listY - (int)_windowListScrollY;
            int mouseX = Raylib.GetMouseX();
            int mouseY = Raylib.GetMouseY();

            for (int i = 0; i < _windowList.Count; i++)
            {
                var w = _windowList[i];
                string label = $"{w.OwnerName}: {w.Name}";
                if (string.IsNullOrWhiteSpace(w.Name)) label = w.OwnerName;
                if (label.Length > 45) label = label.Substring(0, 42) + "...";
                label += $"  ({w.Bounds.Size.Width}x{w.Bounds.Size.Height})";

                bool hover = mouseX >= x + 16 && mouseX <= x + panelW - 16 &&
                             mouseY >= itemY && mouseY <= itemY + 46;
                float ht = GetHoverAnim($"win_{i}", hover);
                Color bg = ColorLerp(UI_SURFACE, UI_ELEVATED, ht);

                Raylib.DrawRectangleRounded(new Rectangle(x + 16, itemY, panelW - 32, 46), 0.25f, 16, bg);
                Raylib.DrawText(label, x + 28, itemY + 15, 15, UI_TEXT);

                if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    // Phase 1: Content-aware window calibration (accounts for title bar)
                    var (offX, offY) = AutoCalibrator.CalcWindowContentCenter(w, screenW, screenH);
                    _config.CenterOffsetX = offX;
                    _config.CenterOffsetY = offY;
                    _config.Save(_currentProfileName);
                    _isSelectingWindow = false;
                }
                itemY += 54;
            }
            Raylib.EndScissorMode();
        }


        static void DrawOverlayAndAim(int screenW, int screenH)
        {
            // Center Calibration Mode
            if (_isCalibratingCenter)
            {
                int cx = (screenW / 2) + _config.CenterOffsetX;
                int cy = (screenH / 2) + _config.CenterOffsetY;
                float dt = Raylib.GetFrameTime();

                // Movement: Shift = fine 1px tap-only, normal = 1px with slow acceleration
                bool fine = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
                bool anyArrow = Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.Down) ||
                    Raylib.IsKeyDown(KeyboardKey.Left) || Raylib.IsKeyDown(KeyboardKey.Right);

                if (fine)
                {
                    // Fine mode: only on key press (tap), 1px per tap
                    if (Raylib.IsKeyPressed(KeyboardKey.Up))    _config.CenterOffsetY -= 1;
                    if (Raylib.IsKeyPressed(KeyboardKey.Down))  _config.CenterOffsetY += 1;
                    if (Raylib.IsKeyPressed(KeyboardKey.Left))  _config.CenterOffsetX -= 1;
                    if (Raylib.IsKeyPressed(KeyboardKey.Right)) _config.CenterOffsetX += 1;
                    _calibHoldTime = 0;
                }
                else if (anyArrow)
                {
                    // Normal mode: start at 1px, gently accelerate to max 4px over ~3s
                    _calibHoldTime += dt;
                    float accel = 1.0f + Math.Min(_calibHoldTime * 1.0f, 3.0f); // 1x -> 4x over 3s
                    float moveF = accel * dt * 60.0f; // frame-rate independent (~1px at 60fps base)
                    int moveAmt = Math.Max(1, (int)moveF);
                    if (Raylib.IsKeyDown(KeyboardKey.Up))    _config.CenterOffsetY -= moveAmt;
                    if (Raylib.IsKeyDown(KeyboardKey.Down))  _config.CenterOffsetY += moveAmt;
                    if (Raylib.IsKeyDown(KeyboardKey.Left))  _config.CenterOffsetX -= moveAmt;
                    if (Raylib.IsKeyDown(KeyboardKey.Right)) _config.CenterOffsetX += moveAmt;
                }
                else
                {
                    _calibHoldTime = 0;
                }

                // ESC to cancel (revert offsets)
                if (Raylib.IsKeyPressed(KeyboardKey.Escape))
                {
                    _config.CenterOffsetX = _calibSavedOffsetX;
                    _config.CenterOffsetY = _calibSavedOffsetY;
                    _isCalibratingCenter = false;
                    _showMenu = true;
                    SetWindowClickthrough(false);
                    return;
                }

                // R to reset offsets to zero
                if (Raylib.IsKeyPressed(KeyboardKey.R))
                {
                    _config.CenterOffsetX = 0;
                    _config.CenterOffsetY = 0;
                }

                // Dim background overlay
                Raylib.DrawRectangle(0, 0, screenW, screenH, new Color(0, 0, 0, 120));

                // Fullscreen crosshair lines (thin, semi-transparent)
                var guideColor = new Color(255, 255, 255, 30);
                Raylib.DrawLine(cx, 0, cx, screenH, guideColor);
                Raylib.DrawLine(0, cy, screenW, cy, guideColor);

                // Main crosshair — layered for visibility
                var crossOuter = new Color(0, 0, 0, 200);
                var crossInner = new Color(255, 60, 60, 255);
                int len = 18;
                int gap = 4;
                // Outer (shadow)
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx - len, cy), new System.Numerics.Vector2(cx - gap, cy), 3, crossOuter);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx + gap, cy), new System.Numerics.Vector2(cx + len, cy), 3, crossOuter);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx, cy - len), new System.Numerics.Vector2(cx, cy - gap), 3, crossOuter);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx, cy + gap), new System.Numerics.Vector2(cx, cy + len), 3, crossOuter);
                // Inner
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx - len, cy), new System.Numerics.Vector2(cx - gap, cy), 1.5f, crossInner);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx + gap, cy), new System.Numerics.Vector2(cx + len, cy), 1.5f, crossInner);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx, cy - len), new System.Numerics.Vector2(cx, cy - gap), 1.5f, crossInner);
                Raylib.DrawLineEx(new System.Numerics.Vector2(cx, cy + gap), new System.Numerics.Vector2(cx, cy + len), 1.5f, crossInner);
                // Center dot
                Raylib.DrawCircleV(new System.Numerics.Vector2(cx, cy), 2.5f, crossInner);
                // Outer ring
                Raylib.DrawCircleLines(cx, cy, 24, new Color(255, 60, 60, 100));

                // Coordinate readout
                string coordText = $"Offset: ({_config.CenterOffsetX}, {_config.CenterOffsetY})";
                int coordW = Raylib.MeasureText(coordText, 16);
                Raylib.DrawRectangle(cx - coordW / 2 - 8, cy + 36, coordW + 16, 24, new Color(0, 0, 0, 180));
                Raylib.DrawText(coordText, cx - coordW / 2, cy + 40, 16, new Color(255, 200, 100, 255));

                // HUD panel at top
                int hudW = 460;
                int hudH = 80;
                int hudX = (screenW - hudW) / 2;
                int hudY = 30;
                Raylib.DrawRectangleRounded(new Rectangle(hudX, hudY, hudW, hudH), 0.15f, 16, new Color(12, 13, 20, 230));
                Raylib.DrawRectangleRoundedLines(new Rectangle(hudX, hudY, hudW, hudH), 0.15f, 16, new Color(255, 60, 60, 100));

                int ty = hudY + 12;
                Raylib.DrawText("SCREEN CENTER CALIBRATION", hudX + hudW / 2 - Raylib.MeasureText("SCREEN CENTER CALIBRATION", 18) / 2, ty, 18, new Color(255, 200, 100, 255));
                ty += 24;
                string helpLine = fine ? "SHIFT held: Fine mode (1px)" : "Arrow Keys: Move | Shift: Fine | R: Reset";
                Raylib.DrawText(helpLine, hudX + hudW / 2 - Raylib.MeasureText(helpLine, 14) / 2, ty, 14, new Color(200, 200, 200, 200));
                ty += 20;
                string saveLine = "ENTER: Save  |  ESC: Cancel";
                Raylib.DrawText(saveLine, hudX + hudW / 2 - Raylib.MeasureText(saveLine, 14) / 2, ty, 14, new Color(100, 220, 120, 220));

                if (Raylib.IsKeyPressed(KeyboardKey.Enter))
                {
                    _isCalibratingCenter = false;
                    _showMenu = true;
                    SetWindowClickthrough(false);
                    _config.Save(_currentProfileName);
                }
                return;
            }

            bool isAimKey = IsAimKeyDown();

            // HUD overlay — compact pill style
            {
                int hx = 16, hy = 16;

                // Status pill
                string aimLabel = _aimAssistActive ? "AIM" : "OFF";
                Color pillBg = _aimAssistActive ? new Color(70, 190, 105, 180) : new Color(230, 70, 70, 160);
                Color pillText = new Color(255, 255, 255, 240);
                int pillW = Raylib.MeasureText(aimLabel, 13) + 20;
                Raylib.DrawRectangleRounded(new Rectangle(hx, hy, pillW, 26), 0.5f, 12, pillBg);
                Raylib.DrawText(aimLabel, hx + 10, hy + 6, 13, pillText);

                // Key status pill
                int kx = hx + pillW + 6;
                string keyLabel = isAimKey ? "KEY" : "---";
                Color keyBg = isAimKey ? new Color(85, 130, 255, 160) : new Color(40, 44, 58, 140);
                Color keyText = isAimKey ? new Color(255, 255, 255, 230) : new Color(140, 145, 160, 180);
                int keyW = Raylib.MeasureText(keyLabel, 13) + 20;
                Raylib.DrawRectangleRounded(new Rectangle(kx, hy, keyW, 26), 0.5f, 12, keyBg);
                Raylib.DrawText(keyLabel, kx + 10, hy + 6, 13, keyText);

                // Stats line (subtle, below pills)
                string stats = $"{Raylib.GetFPS()} fps  {_lastCapMs}ms cap  {_lastAiMs}ms ai";
                Raylib.DrawText(stats, hx + 2, hy + 32, 11, new Color(160, 165, 180, 120));

                // Move vector (only when active and moving)
                if (_aimAssistActive && (Math.Abs(_lastMoveX) > 0.1 || Math.Abs(_lastMoveY) > 0.1))
                {
                    string moveStr = $"{_lastMoveX:F1}, {_lastMoveY:F1}";
                    Raylib.DrawText(moveStr, hx + 2, hy + 46, 11, new Color(160, 165, 180, 90));
                }
            }

            // Phase 5: Debug overlay (draws even when aim off)
            AutoCalibrator.ShowDebugOverlay = _config.DebugOverlay;
            AutoCalibrator.DrawDebugOverlay(screenW, screenH);

            if (!_aimAssistActive) return;

            int centerX = (screenW / 2) + _config.CenterOffsetX;
            int centerY = (screenH / 2) + _config.CenterOffsetY;

            // Phase 5: Feed debug data
            AutoCalibrator.DbgCenterX = centerX;
            AutoCalibrator.DbgCenterY = centerY;

            Color fovColor = new Color(_config.FovColorR, _config.FovColorG, _config.FovColorB, _config.FovColorA);
            Raylib.DrawCircleLines(centerX, centerY, _config.FovSize / 2.0f, fovColor);

            var buffer = _aiManager?.GetInputBuffer();
            if (buffer == null) return;

            int imgSize = _aiManager.IMAGE_SIZE;

            long pixW = NativeMethods.CGDisplayPixelsWide(_selectedDisplayID);
            double pointsW = NativeMethods.CGDisplayBounds(_selectedDisplayID).Size.Width;
            double scale = (pointsW > 0) ? ((double)pixW / pointsW) : 1.0;
            if (scale < 1.0) scale = 1.0;

            double capSizePoints = (double)imgSize / scale;
            double capX = centerX - (capSizePoints / 2.0);
            double capY = centerY - (capSizePoints / 2.0);

            if (capX < 0) capX = 0;
            if (capY < 0) capY = 0;
            double pointsH = NativeMethods.CGDisplayBounds(_selectedDisplayID).Size.Height;
            if (capX + capSizePoints > pointsW) capX = pointsW - capSizePoints;
            if (capY + capSizePoints > pointsH) capY = pointsH - capSizePoints;
            if (capX < 0) capX = 0;
            if (capY < 0) capY = 0;

            // Phase 5: Feed capture region to debug
            AutoCalibrator.DbgCapX = capX;
            AutoCalibrator.DbgCapY = capY;
            AutoCalibrator.DbgCapSize = capSizePoints;

            _perfTimer.Restart();
            var result = MacCapture.CaptureAndFillTensor(buffer, imgSize, new CGRect(capX, capY, capSizePoints, capSizePoints), _selectedDisplayID);
            _lastCapMs = _perfTimer.ElapsedMilliseconds;

            if (!result) return;

            _perfTimer.Restart();
            var preds = _aiManager.PredictFromBuffer(_config.ConfidenceThreshold);
            _lastAiMs = _perfTimer.ElapsedMilliseconds;

            // --- FIND BEST TARGET ---
            Prediction? best = null;
            double minDist = double.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < preds.Count; i++)
            {
                var p = preds[i];
                if (!IsColorMatch(p, buffer, imgSize, _config.TeamColor)) continue;

                float pX = (float)(p.Rectangle.X / scale);
                float pY = (float)(p.Rectangle.Y / scale);
                float pW = (float)(p.Rectangle.Width / scale);
                float pH = (float)(p.Rectangle.Height / scale);

                float bx = (float)(capX + pX);
                float by = (float)(capY + pY);
                Raylib.DrawRectangleLinesEx(new Rectangle(bx, by, pW, pH), 2, Color.Red);

                double dist = Math.Sqrt(Math.Pow((bx + pW/2) - centerX, 2) + Math.Pow((by + pH/2) - centerY, 2));

                double effectiveDist = dist;
                if (_lastBestIndex >= 0 && i != _lastBestIndex && _config.TargetStickinessRadius > 0)
                {
                    effectiveDist += _config.TargetStickinessRadius;
                }

                if (dist < _config.FovSize / 2.0f)
                {
                    if (best == null || effectiveDist < minDist)
                    {
                        best = p;
                        minDist = effectiveDist;
                        bestIndex = i;
                    }
                }
            }
            _lastBestIndex = bestIndex;

            double finalX = 0;
            double finalY = 0;

            if (best == null) _isFlicking = false;

            if (best != null)
            {
                float bestX = (float)(best.Rectangle.X / scale);
                float bestY = (float)(best.Rectangle.Y / scale);
                float bestW = (float)(best.Rectangle.Width / scale);
                float bestH = (float)(best.Rectangle.Height / scale);

                float bestCX = (float)(best.ScreenCenterX / scale);
                float bestCY = (float)(best.ScreenCenterY / scale);

                Raylib.DrawRectangleLinesEx(new Rectangle((float)(capX + bestX), (float)(capY + bestY), bestW, bestH), 3, Color.Green);

                double targetX = capX + bestCX + _config.XOffset;
                double targetY = capY + bestCY + _config.YOffset;

                float boxH = bestH;
                if (_config.AimBone == Config.BoneType.Head) targetY -= boxH * 0.35;
                else if (_config.AimBone == Config.BoneType.Neck) targetY -= boxH * 0.25;
                else if (_config.AimBone == Config.BoneType.Chest) targetY -= boxH * 0.15;

                if (_config.JumpOffsetEnabled && Raylib.IsKeyDown(KeyboardKey.Space))
                {
                     targetY += _config.JumpOffset;
                }

                double dx = targetX - centerX;
                double dy = targetY - centerY;

                // Phase 5: Feed target/error to debug
                AutoCalibrator.DbgTargetX = targetX;
                AutoCalibrator.DbgTargetY = targetY;
                AutoCalibrator.DbgErrorX = dx;
                AutoCalibrator.DbgErrorY = dy;

                double distForAutoOffset = Math.Sqrt(dx * dx + dy * dy);

                // Phase 2: Closed-loop sensitivity calibration
                if (AutoCalibrator.State != AutoCalibrator.CalibState.Idle &&
                    AutoCalibrator.State != AutoCalibrator.CalibState.Done &&
                    AutoCalibrator.State != AutoCalibrator.CalibState.Failed)
                {
                    bool done = AutoCalibrator.UpdateSensitivityCalibration(
                        _config, best, scale, capX, capY, centerX, centerY);
                    if (done || AutoCalibrator.State == AutoCalibrator.CalibState.Done ||
                        AutoCalibrator.State == AutoCalibrator.CalibState.Failed)
                    {
                        _config.Save(_currentProfileName);
                        _showMenu = true;
                        SetWindowClickthrough(false);
                    }
                    return; // Don't move mouse during calibration
                }

                // Phase 3: Auto-offset correction
                AutoCalibrator.AutoOffsetEnabled = _config.AutoOffsetEnabled;
                AutoCalibrator.UpdateAutoOffset(_config, dx, dy, distForAutoOffset);

                double predX = 0;
                double predY = 0;

                if (_config.PredictionEnabled)
                {
                     float frameDt = (float)_frameTimer.Elapsed.TotalSeconds;
                     if (frameDt <= 0) frameDt = 0.016f;

                     long now = _sw.ElapsedMilliseconds;
                     float dt = (now - _lastTargetTime) / 1000.0f;
                     if (dt > 0 && dt < 0.2f)
                     {
                         Vector2 currentPos = new Vector2(bestCX, bestCY);
                         float distMoved = (currentPos - _lastTargetPos).Length();

                         if (distMoved < 150)
                         {
                             Vector2 velocity = (currentPos - _lastTargetPos) / dt;

                             if (velocity.Length() < 5000)
                             {
                                 float predDt = frameDt * _config.PredictionScale;

                                 if (_config.PredictionUseAcceleration && _lastVelocity.Length() > 0)
                                 {
                                     Vector2 accel = (velocity - _lastVelocity) / dt;
                                     if (accel.Length() > 10000) accel = Vector2.Normalize(accel) * 10000;
                                     predX = (velocity.X * predDt) + (0.5 * accel.X * predDt * predDt);
                                     predY = (velocity.Y * predDt) + (0.5 * accel.Y * predDt * predDt);
                                 }
                                 else
                                 {
                                     predX = velocity.X * predDt;
                                     predY = velocity.Y * predDt;
                                 }

                                 Raylib.DrawLine((int)(capX + bestCX), (int)(capY + bestCY),
                                                 (int)(capX + bestCX + predX*10), (int)(capY + bestCY + predY*10), Color.Yellow);
                                 float ghostX = (float)(capX + bestX + predX);
                                 float ghostY = (float)(capY + bestY + predY);
                                 Raylib.DrawRectangleLinesEx(new Rectangle(ghostX, ghostY, bestW, bestH), 2, Color.Orange);
                                 Raylib.DrawText(_config.PredictionUseAcceleration ? "PRED+ACCEL" : "PREDICTION", (int)ghostX, (int)ghostY - 15, 12, Color.Orange);

                                 _lastVelocity = velocity;
                             }
                         }
                         else
                         {
                             _lastVelocity = Vector2.Zero;
                         }
                     }

                     _lastTargetPos = new Vector2(bestCX, bestCY);
                     _lastTargetTime = now;
                }

                double distToTarget = Math.Sqrt(dx * dx + dy * dy);
                double currentSens = _config.MouseSensitivity;
                if (_config.DynamicSensitivity)
                {
                     double fovRadius = _config.FovSize / 2.0;
                     double t = Math.Clamp(distToTarget / fovRadius, 0.0, 1.0);
                     double sensMultiplier = _config.DynamicSensScale + t * (1.0 - _config.DynamicSensScale);
                     currentSens *= sensMultiplier;
                     Raylib.DrawText($"STICKY x{sensMultiplier:F2}", (int)(capX + bestCX), (int)(capY + bestCY) + 20, 10, Color.SkyBlue);
                }

                double rawMoveX = (dx + predX) * currentSens;
                double rawMoveY = (dy + predY) * currentSens;

                if (_config.HumanizeAim)
                {
                     double moveDist = Math.Sqrt(rawMoveX*rawMoveX + rawMoveY*rawMoveY);
                     if (moveDist > 2)
                     {
                         double time = _sw.ElapsedMilliseconds / 100.0;
                         double sway = Math.Sin(time) * _config.HumanizeStrength;
                         double px = -rawMoveY / moveDist;
                         double py = rawMoveX / moveDist;
                         rawMoveX += px * sway;
                         rawMoveY += py * sway;
                     }
                }

                // Flick shot: fast snap when far, precise correction when close
                if (_config.FlickEnabled && distToTarget >= _config.FlickThreshold && !_isFlicking)
                {
                    _isFlicking = true;
                    _flickStartDist = distToTarget;
                }
                if (_isFlicking && distToTarget < _config.FlickThreshold * 0.3)
                {
                    _isFlicking = false; // Flick complete, switch to correction
                }

                double smooth;
                if (_isFlicking && _config.FlickEnabled)
                {
                    // During flick: boost speed, minimal smoothing
                    rawMoveX *= _config.FlickSpeedMultiplier;
                    rawMoveY *= _config.FlickSpeedMultiplier;
                    smooth = 0.0; // No smoothing during flick for instant snap
                    Raylib.DrawText("FLICK", (int)centerX + 30, (int)centerY - 10, 14, Color.Orange);
                }
                else if (_config.FlickEnabled && distToTarget < _config.FlickThreshold)
                {
                    // Post-flick correction: high smoothing for precision
                    smooth = _config.FlickCorrectionSmooth;
                }
                else if (_config.AdaptiveSmoothing)
                {
                    double t = Math.Clamp(distToTarget / _config.AdaptiveSmoothRange, 0.0, 1.0);
                    smooth = _config.AdaptiveSmoothNear + t * (_config.AdaptiveSmoothFar - _config.AdaptiveSmoothNear);
                    smooth = Math.Clamp(smooth, 0.0, 0.95);
                }
                else
                {
                    smooth = _config.MouseSmoothing;
                }
                if (Math.Sign(rawMoveX) != Math.Sign(_lastMoveX) && Math.Abs(rawMoveX) > 1) smooth = 0;
                if (Math.Sign(rawMoveY) != Math.Sign(_lastMoveY) && Math.Abs(rawMoveY) > 1) smooth = 0;

                finalX = (rawMoveX * (1.0 - smooth)) + (_lastMoveX * smooth);
                finalY = (rawMoveY * (1.0 - smooth)) + (_lastMoveY * smooth);

                if (isAimKey)
                {
                    if (_isCalibrating && _aimAssistActive)
                    {
                         if (Math.Abs(dx) < 300)
                         {
                             string debugAction = "Tracking...";
                             Color debugColor = Color.Yellow;

                             _lastMoveX = finalX; _lastMoveY = finalY;
                             MacInput.MoveMouseRelative((int)finalX, (int)finalY);

                             if (_calibStep % 4 == 0)
                             {
                                 bool overshootX = (finalX > 0 && dx < -2) || (finalX < 0 && dx > 2);
                                 bool overshootY = (finalY > 0 && dy < -2) || (finalY < 0 && dy > 2);

                                 if (overshootX || overshootY)
                                 {
                                     _calibSensHigh = _config.MouseSensitivity;
                                     _config.MouseSensitivity = (_calibSensLow + _calibSensHigh) / 2.0f;
                                     debugAction = $"Overshoot! [{_calibSensLow:F4}-{_calibSensHigh:F4}]";
                                     debugColor = Color.Red;
                                     _triggerTimer.Restart();
                                 }
                                 else
                                 {
                                     if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                                     {
                                         _calibSensLow = _config.MouseSensitivity;
                                         _config.MouseSensitivity = (_calibSensLow + _calibSensHigh) / 2.0f;
                                         debugAction = $"Too slow [{_calibSensLow:F4}-{_calibSensHigh:F4}]";
                                         debugColor = Color.Orange;
                                         _triggerTimer.Restart();
                                     }
                                     else
                                     {
                                         debugAction = "Locked! (Perfect)";
                                         debugColor = Color.Green;
                                         if (_triggerTimer.ElapsedMilliseconds > 1000)
                                         {
                                              _isCalibrating = false;
                                              _showMenu = true;
                                              _config.Save(_currentProfileName);
                                              SetWindowClickthrough(false);
                                              debugAction = "Calibration Complete!";
                                         }
                                     }
                                 }

                                 if (_config.MouseSensitivity < 0.001f) _config.MouseSensitivity = 0.001f;
                                 if (_config.MouseSensitivity > 2.0f) _config.MouseSensitivity = 2.0f;
                             }

                             _calibStep++;

                             Raylib.DrawText($"SENS: {_config.MouseSensitivity:F4}", (int)centerX - 60, (int)centerY - 60, 20, Color.White);
                             Raylib.DrawText($"ERR: {dx:F0},{dy:F0}", (int)centerX - 60, (int)centerY - 40, 20, Color.White);
                             Raylib.DrawText(debugAction, (int)centerX - 60, (int)centerY + 40, 20, debugColor);

                             if (_triggerTimer.ElapsedMilliseconds < 1500) {
                                 float prog = Math.Clamp(_triggerTimer.ElapsedMilliseconds / 1500.0f, 0, 1);
                                 Raylib.DrawRectangle((int)centerX - 50, (int)centerY + 70, (int)(100 * prog), 5, Color.Green);
                             }
                         }
                    }
                    else
                    {
                        _lastMoveX = finalX; _lastMoveY = finalY;
                        double moveX = finalX;
                        double moveY = finalY;

                        if (_config.Jitter)
                        {
                            double jx = (_rng.NextDouble() - 0.5) * _config.JitterAmount;
                            double jy = (_rng.NextDouble() - 0.5) * _config.JitterAmount;
                            moveX += jx;
                            moveY += jy;
                        }

                        MacInput.MoveMouseRelative((int)moveX, (int)moveY);
                    }
                }

                bool triggerActive = _config.TriggerBot;
                if (triggerActive && !_config.TriggerAlways && !IsAimKeyDown()) triggerActive = false;

                if (triggerActive && best != null)
                {
                    double tDx = (capX + best.ScreenCenterX + _config.XOffset) - centerX;
                    if (_config.TriggerPrediction) tDx += predX;

                    double tY = capY + best.ScreenCenterY + _config.YOffset;
                    float tBoxH = best.Rectangle.Height;
                    if (_config.AimBone == Config.BoneType.Head) tY -= tBoxH * 0.35;
                    else if (_config.AimBone == Config.BoneType.Neck) tY -= tBoxH * 0.25;
                    else if (_config.AimBone == Config.BoneType.Chest) tY -= tBoxH * 0.15;

                    double tDy = tY - centerY;
                    if (_config.TriggerPrediction) tDy += predY;

                    bool inZone = false;
                    if (_config.MagnetTrigger)
                    {
                         inZone = (Math.Sqrt(tDx*tDx + tDy*tDy) < _config.TriggerRange);
                    }
                    else
                    {
                         double bX = capX + best.ScreenCenterX - (best.Rectangle.Width / 2.0);
                         double bY = capY + best.ScreenCenterY - (best.Rectangle.Height / 2.0);
                         double bW = best.Rectangle.Width;
                         double bH = best.Rectangle.Height;
                         inZone = (Math.Abs(tDx) < bW/2 && Math.Abs(tDy) < bH/2);
                    }

                    if (inZone && _config.TriggerColorCheck)
                    {
                         var pixelPred = new Prediction {
                             Rectangle = new Aimmy.Mac.RectF(imgSize/2, imgSize/2, 1, 1),
                             Confidence = 1.0f
                         };
                         if (!IsColorMatch(pixelPred, buffer, imgSize, _config.TeamColor)) inZone = false;
                    }

                    if (inZone && best.Confidence >= _config.TriggerConfidence)
                    {
                        if (_triggerTimer.ElapsedMilliseconds > _config.TriggerCooldown + _config.TriggerDelay)
                        {
                             if (!_isTriggerShooting)
                             {
                                  IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                                  CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                                  NativeMethods.CFRelease(ev);

                                  MacInput.SendLeftMouseDown(cur.X, cur.Y);
                                  _isTriggerShooting = true;
                                  _shootingTimer.Restart();
                                  _triggerTimer.Restart();

                                  if (_config.TriggerBurst > 0)
                                  {
                                       _isBursting = true;
                                       _burstShotsRemaining = _config.TriggerBurst;
                                       _burstTimer.Restart();
                                  }

                                  Raylib.DrawCircle((int)centerX, (int)centerY, 8, Color.Red);
                             }
                        }
                    }
                }

                if (_isTriggerShooting)
                {
                     if (_isBursting)
                     {
                          if (_burstTimer.ElapsedMilliseconds > 100)
                          {
                               IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                               CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                               NativeMethods.CFRelease(ev);
                               MacInput.SendLeftMouseUp(cur.X, cur.Y);

                               _burstShotsRemaining--;
                               if (_burstShotsRemaining > 0)
                               {
                                    MacInput.SendLeftMouseDown(cur.X, cur.Y);
                                    _burstTimer.Restart();
                               }
                               else
                               {
                                    _isBursting = false;
                                    _isTriggerShooting = false;
                                    _shootingTimer.Stop();
                               }
                          }
                     }
                     else if (_shootingTimer.ElapsedMilliseconds > 60)
                     {
                          IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                          CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                          NativeMethods.CFRelease(ev);

                          MacInput.SendLeftMouseUp(cur.X, cur.Y);
                          _isTriggerShooting = false;
                          _shootingTimer.Stop();
                     }
                }

                if (_config.RecoilEnabled)
                {
                     bool isShooting = NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Left);
                     if (isShooting)
                     {
                         if (_recoilTimer.ElapsedMilliseconds > _config.RecoilDelay)
                             MacInput.MoveMouseRelative(0, _config.RecoilY);
                     }
                     else
                     {
                         _recoilTimer.Restart();
                     }
                }

                if (_config.ShowESP)
                {
                    for (int i = 0; i < preds.Count; i++)
                    {
                        var p = preds[i];
                        if (!IsColorMatch(p, buffer, imgSize, _config.TeamColor)) continue;
                        float bx = (float)(capX + p.Rectangle.X);
                        float by = (float)(capY + p.Rectangle.Y);
                        Raylib.DrawText($"{p.ClassName} {p.Confidence:F2}", (int)bx, (int)by - 20, 16, Color.Green);
                    }
                }
                else if (_isCalibrating && _aimAssistActive)
                {
                    Raylib.DrawText("HOLD AIM KEY TO CALIBRATE", (int)centerX - 120, (int)centerY + 60, 24, Color.Orange);
                     _triggerTimer.Restart();
                }
            }

            if (Recorder.IsRecording)
            {
               Recorder.EnqueueFrame(buffer, imgSize, preds, _config.TeamColor, bestIndex, _config.XOffset, _config.YOffset, _config.MouseSensitivity, _config.MouseSmoothing, (float)finalX, (float)finalY);
               Raylib.DrawCircle(screenW - 30, 30, 10, Color.Red);
            }
        }

        static bool IsAimKeyDown()
        {
            int k = _config.AimKey;
            if (k == -1) return true;
            if (k == 200) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Left);
            if (k == 201) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Right);
            if (k == 202) return NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Center);
            return NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, (ushort)k);
        }

        static bool IsColorMatch(Prediction p, float[] buffer, int size, Config.TeamColorType targetColor)
        {
            if (targetColor == Config.TeamColorType.None) return true;

            int cx = (int)(p.Rectangle.X + p.Rectangle.Width / 2);
            int cy = (int)(p.Rectangle.Y + p.Rectangle.Height / 2);

            if (cx < 0) cx = 0; if (cx >= size) cx = size - 1;
            if (cy < 0) cy = 0; if (cy >= size) cy = size - 1;

            int idx = (cy * size) + cx;
            int len = size * size;

            float r = buffer[idx];
            float g = buffer[idx + len];
            float b = buffer[idx + 2 * len];

            float h, s, v;
            ColorToHSV(r, g, b, out h, out s, out v);

            if (s < 0.2f) return false;

            switch (targetColor)
            {
                case Config.TeamColorType.Red:
                    return (h >= 0 && h < 40) || (h > 320 && h <= 360);
                case Config.TeamColorType.Blue:
                    return (h > 180 && h < 260);
                case Config.TeamColorType.Purple:
                    return (h > 260 && h < 320);
                case Config.TeamColorType.Yellow:
                    return (h > 40 && h < 80);
            }
            return true;
        }

        static void ColorToHSV(float r, float g, float b, out float h, out float s, out float v)
        {
            float min = Math.Min(r, Math.Min(g, b));
            float max = Math.Max(r, Math.Max(g, b));
            float delta = max - min;

            v = max;

            if (delta < 0.00001f) { s = 0; h = 0; return; }
            if (max > 0.0) { s = (delta / max); } else { s = 0.0f; h = 0.0f; return; }

            if (r >= max) h = (g - b) / delta;
            else if (g >= max) h = 2.0f + (b - r) / delta;
            else h = 4.0f + (r - g) / delta;

            h *= 60.0f;
            if (h < 0.0) h += 360.0f;
        }

        // ========== MODERN UI DRAWING PRIMITIVES ==========

        static void DrawSlider(int x, int y, int w, int h, string label, ref float value, float min, float max, int mx, int my, bool mouseDown)
        {
            // Smart formatting: show integers for large ranges, 2 decimals otherwise
            string valText;
            if (max - min >= 10 && value == (int)value) valText = $"{value:F0}";
            else if (max - min >= 10) valText = $"{value:F1}";
            else valText = $"{value:F2}";

            int vtw = Raylib.MeasureText(valText, 13);
            int valBoxW = vtw + 16;
            int sliderW = w - valBoxW - 8; // Reserve space for value display

            // Label
            Raylib.DrawText(label, x, y - 16, 12, UI_TEXT_MUTED);

            // Value box (right side)
            int vbX = x + sliderW + 8;
            Raylib.DrawRectangleRounded(new Rectangle(vbX, y - 1, valBoxW, h + 2), 0.4f, 8, UI_SURFACE);
            Raylib.DrawText(valText, vbX + (valBoxW - vtw) / 2, y + (h - 13) / 2, 13, UI_ACCENT);

            // Track
            int trackH = 5;
            int trackY = y + (h - trackH) / 2;
            Raylib.DrawRectangleRounded(new Rectangle(x, trackY, sliderW, trackH), 1.0f, 16, UI_SLIDER_TRACK);

            float ratio = Math.Clamp((value - min) / (max - min), 0f, 1f);
            if (ratio > 0.005f)
                Raylib.DrawRectangleRounded(new Rectangle(x, trackY, Math.Max(trackH, (int)(sliderW * ratio)), trackH), 1.0f, 16, UI_SLIDER_FILL);

            // Thumb
            int thumbCx = x + (int)(sliderW * ratio);
            bool hover = mx >= x - 12 && mx <= x + sliderW + 12 && my >= y - 8 && my <= y + h + 8;
            string id = $"s_{label}_{x}";
            float ht = GetHoverAnim(id, hover);
            float thumbR = 7f + ht * 2f;

            Color thumbColor = ColorLerp(UI_ACCENT, UI_ACCENT_HOVER, ht);
            if (ht > 0.01f) Raylib.DrawCircleV(new System.Numerics.Vector2(thumbCx, trackY + trackH / 2f), thumbR + 4, new Color(UI_ACCENT.R, UI_ACCENT.G, UI_ACCENT.B, (int)(30 * ht)));
            Raylib.DrawCircleV(new System.Numerics.Vector2(thumbCx, trackY + trackH / 2f), thumbR, thumbColor);

            // Interaction
            if (mouseDown && mx >= x && mx <= x + sliderW && my >= y - 12 && my <= y + h + 12)
            {
                float nR = (float)(mx - x) / sliderW;
                value = min + (nR * (max - min));
                value = Math.Clamp(value, min, max);
            }
        }

        static bool DrawButton(int x, int y, int w, int h, string text, int mx, int my, bool clicked)
        {
            bool hover = mx >= x && mx <= x + w && my >= y && my <= y + h;
            string id = $"b_{text}_{x}_{y}";
            float ht = GetHoverAnim(id, hover);

            Color bg = ColorLerp(UI_ELEVATED, new Color(42, 46, 62, 255), ht);
            Color border = ColorLerp(UI_BORDER, UI_ACCENT, ht * 0.6f);

            Raylib.DrawRectangleRounded(new Rectangle(x, y, w, h), 0.35f, 16, bg);
            Raylib.DrawRectangleRoundedLines(new Rectangle(x, y, w, h), 0.35f, 16, border);

            int fontSize = h > 30 ? 15 : 13;
            int tw = Raylib.MeasureText(text, fontSize);
            Raylib.DrawText(text, x + (w - tw) / 2, y + (h - fontSize) / 2, fontSize, UI_TEXT);

            return hover && clicked;
        }

        static void ApplyBinding(int key, int mod)
        {
            switch (_bindingTarget)
            {
                case 0:
                    _config.AimKey = key;
                    _bindingText = GetKeyName(key);
                    break;
                case 1:
                    _config.KeyToggleMenu = key;
                    break;
                case 2:
                    _config.KeyToggleAim = key;
                    _config.KeyToggleAimMod = mod;
                    break;
                case 3:
                    _config.KeyToggleRecord = key;
                    _config.KeyToggleRecordMod = mod;
                    break;
            }
            _config.Save(_currentProfileName);
        }

        static string GetKeyName(int code)
        {
             if (code == -1) return "None";
             if (code == 200) return "Left Click";
             if (code == 201) return "Right Click";
             if (code == 202) return "Middle Click";
             if (code == 203) return "Mouse 4";
             if (code == 204) return "Mouse 5";
             // Common macOS CGKeyCode mappings
             return code switch {
                 0 => "A", 1 => "S", 2 => "D", 3 => "F", 4 => "H", 5 => "G",
                 6 => "Z", 7 => "X", 8 => "C", 9 => "V", 11 => "B", 12 => "Q",
                 13 => "W", 14 => "E", 15 => "R", 16 => "Y", 17 => "T",
                 18 => "1", 19 => "2", 20 => "3", 21 => "4", 22 => "6", 23 => "5",
                 24 => "=", 25 => "9", 26 => "7", 27 => "-", 28 => "8", 29 => "0",
                 30 => "]", 31 => "O", 32 => "U", 33 => "[", 34 => "I", 35 => "P",
                 36 => "Return", 37 => "L", 38 => "J", 39 => "'", 40 => "K",
                 41 => ";", 42 => "\\", 43 => ",", 44 => "/", 45 => "N", 46 => "M",
                 47 => ".", 48 => "Tab", 49 => "Space", 50 => "`",
                 51 => "Delete", 53 => "Escape",
                 55 => "Cmd", 56 => "Shift", 57 => "CapsLock", 58 => "Option", 59 => "Control",
                 96 => "F5", 97 => "F6", 98 => "F7", 99 => "F3", 100 => "F8",
                 101 => "F9", 103 => "F11", 105 => "F13", 107 => "F14",
                 109 => "F10", 111 => "F12", 113 => "F15",
                 115 => "Home", 116 => "PgUp", 117 => "FwdDel", 118 => "F4",
                 119 => "End", 120 => "F2", 121 => "PgDn", 122 => "F1", 123 => "Left",
                 124 => "Right", 125 => "Down", 126 => "Up",
                 _ => $"Key({code})"
             };
        }

        static string GetModName(int mod)
        {
            return mod switch {
                55 => "Cmd", 56 => "Shift", 58 => "Opt", 59 => "Ctrl", _ => ""
            };
        }

        static string GetBindDisplay(int key, int mod)
        {
            if (key == -1) return "None";
            string modStr = GetModName(mod);
            string keyStr = GetKeyName(key);
            return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
        }

        static void DrawSearchableDropdown(Rectangle rect, List<string> items, ref bool isOpen, ref string searchQuery, ref float scrollY, Action<int> onSelect)
        {
            int rX = (int)rect.X; int rY = (int)rect.Y; int rW = (int)rect.Width; int rH = (int)rect.Height;

            Raylib.DrawRectangleRounded(new Rectangle(rX, rY, rW, rH), 0.10f, 16, UI_SURFACE);
            Raylib.DrawRectangleRoundedLines(new Rectangle(rX, rY, rW, rH), 0.10f, 16, UI_ACCENT);

            int searchH = 30;
            Raylib.DrawRectangleRounded(new Rectangle(rX + 6, rY + 6, rW - 12, searchH), 0.4f, 16, UI_ELEVATED);
            Raylib.DrawText(searchQuery.Length > 0 ? searchQuery + "_" : "Search...", rX + 14, rY + 13, 14,
                searchQuery.Length > 0 ? UI_TEXT : UI_TEXT_MUTED);

            int key = Raylib.GetCharPressed();
            while (key > 0) { if ((key >= 32) && (key <= 125)) searchQuery += (char)key; key = Raylib.GetCharPressed(); }
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && searchQuery.Length > 0) searchQuery = searchQuery.Substring(0, searchQuery.Length - 1);
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) isOpen = false;

            var filtered = new List<(string Name, int OriginalIndex)>();
            for (int i = 0; i < items.Count; i++) {
                if (items[i].ToLower().Contains(searchQuery.ToLower())) filtered.Add((Path.GetFileName(items[i]), i));
            }

            int listY = rY + searchH + 12;
            int listH = rH - searchH - 16;
            Raylib.BeginScissorMode(rX, listY, rW, listH);

            if (Raylib.GetMouseX() >= rX && Raylib.GetMouseX() <= rX + rW && Raylib.GetMouseY() >= listY && Raylib.GetMouseY() <= listY + listH)
                scrollY -= Raylib.GetMouseWheelMove() * 20;
            if (scrollY < 0) scrollY = 0;
            int maxScroll = Math.Max(0, (filtered.Count * 30) - listH);
            if (scrollY > maxScroll) scrollY = maxScroll;

            int itemY = listY - (int)scrollY;
            for (int i = 0; i < filtered.Count; i++)
            {
                var item = filtered[i];
                bool isHover = (Raylib.GetMouseX() >= rX + 4 && Raylib.GetMouseX() <= rX + rW - 4 &&
                                Raylib.GetMouseY() >= itemY && Raylib.GetMouseY() <= itemY + 28);
                float ht = GetHoverAnim($"dd_{item.OriginalIndex}", isHover);

                if (ht > 0.01f)
                    Raylib.DrawRectangleRounded(new Rectangle(rX + 4, itemY, rW - 8, 28), 0.4f, 16, ColorLerp(Color.Blank, UI_ELEVATED, ht));
                Raylib.DrawText(item.Name, rX + 14, itemY + 6, 14, UI_TEXT);

                if (isHover && Raylib.IsMouseButtonPressed(MouseButton.Left)) { onSelect(item.OriginalIndex); isOpen = false; }
                itemY += 30;
            }
            Raylib.EndScissorMode();

            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                if (!(Raylib.GetMouseX() >= rX && Raylib.GetMouseX() <= rX + rW && Raylib.GetMouseY() >= rY && Raylib.GetMouseY() <= rY + rH))
                    isOpen = false;
            }
        }
    }
}
