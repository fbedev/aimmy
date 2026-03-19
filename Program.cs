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
        static string _bindingText = "None"; // Will update on load
        
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

        static void Main(string[] args)
        {
            Console.WriteLine("Aimmy for macOS - Feature Complete (Refactored)");
            Console.WriteLine("---------------------------------------------");
            
            _config = Config.Load();
            
            // Refresh Models
            RefreshModelList();

            // Select Model if needed
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

                // Sync Model Index
                string name = Path.GetFileName(_config.ModelPath);
                _currentModelIndex = _modelFiles.FindIndex(x => Path.GetFileName(x) == name);
                if (_currentModelIndex == -1 && _modelFiles.Count > 0) _currentModelIndex = 0;

                // Start Loop
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

        static uint _selectedDisplayID = 0; // 0 = Main

        static void RunLoop()
        {
            // --- Select Display ---
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
                     _selectedDisplayID = displays[0]; // Default to Main/First
                }
            } else {
                _selectedDisplayID = NativeMethods.CGMainDisplayID();
            }

            // Init Window
            // Uses _selectedDisplayID for bounds
            var bounds = NativeMethods.CGDisplayBounds(_selectedDisplayID);
            int width = (int)bounds.Size.Width;
            int height = (int)bounds.Size.Height;
            
            // Set Raylib to match selected monitor content area
            Raylib.SetConfigFlags((ConfigFlags)(16 | 4096 | 256)); // Transparent, HighDPI, Topmost
            
            // Note: Raylib InitWindow centers by default or uses primary. 
            // We might need to SetWindowPosition if it's a secondary monitor to ensure it overlays correctly.
            Raylib.InitWindow(width, height, "Aimmy ESP");
            Raylib.SetWindowPosition((int)bounds.Origin.X, (int)bounds.Origin.Y);
            
            Raylib.SetTargetFPS(_config.MaxFps);

            // Init Helpers
            _recoilTimer.Start();
            _triggerTimer.Start();
            _bindingText = GetKeyName(_config.AimKey);

            // Initial Window State force
            SetWindowClickthrough(false); 
            
            long lastLevelCheck = 0;

            while (_running && !Raylib.WindowShouldClose())
            {
                _sw.Restart();
                
                // 1. UPDATE STATE
                if (_nsWindow == IntPtr.Zero) SetWindowClickthrough(_isPassthrough); // Retry until found
                
                // Force TopMost every 2 seconds
                if (System.DateTime.Now.Ticks / 10000 - lastLevelCheck > 2000) {
                     if (_nsWindow != IntPtr.Zero) {
                         IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);
                     }
                     lastLevelCheck = System.DateTime.Now.Ticks / 10000;
                }

                UpdateInput();
                
                // 2. DRAW
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Blank); 

                if (_showMenu)
                {
                    if (_isSelectingWindow)
                    {
                        DrawWindowSelection(width, height);
                    }
                    else
                    {
                        DrawMenu();
                    }
                }
                else
                {
                    DrawOverlayAndAim(width, height);
                }
                
                Raylib.EndDrawing();
            }
            
            Raylib.CloseWindow();
        }




        static void SetWindowClickthrough(bool clickthrough)
        {
            if (_nsWindow == IntPtr.Zero)
            {
                // Try to capture window handle
                try {
                    IntPtr cls_NSApplication = ObjCRuntime.objc_getClass("NSApplication");
                    IntPtr sel_sharedApp = ObjCRuntime.sel_registerName("sharedApplication");
                    IntPtr app = ObjCRuntime.objc_msgSend_IntPtr(cls_NSApplication, sel_sharedApp);
                    IntPtr sel_keyWindow = ObjCRuntime.sel_registerName("keyWindow");
                    _nsWindow = ObjCRuntime.objc_msgSend_IntPtr(app, sel_keyWindow);
                    
                    if (_nsWindow != IntPtr.Zero)
                    {
                         Console.WriteLine($"[Helpers] NSWindow Found: {_nsWindow}");
                         
                         // Apply Permanent Settings Once Found
                         // Allow joining all spaces (Essential for overlaying on top of Full Screen Games)
                         IntPtr sel_setCollectionBehavior = ObjCRuntime.sel_registerName("setCollectionBehavior:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setCollectionBehavior, 17); // CanJoinAllSpaces (1) + FullScreenAuxiliary (16)
                         
                         // Set Level to ScreenSaver (1002) to stay above Games
                         IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                         ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);
                    }
                    else
                    {
                         // Console.WriteLine("[Helpers] NSWindow NOT FOUND yet.");
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[Helpers] Error getting NSWindow: {ex.Message}");
                }
            }

            if (_nsWindow != IntPtr.Zero)
            {
                // Native macOS Clickthrough
                IntPtr sel_setIgnoresMouseEvents = ObjCRuntime.sel_registerName("setIgnoresMouseEvents:");
                ObjCRuntime.objc_msgSend(_nsWindow, sel_setIgnoresMouseEvents, clickthrough);
                
                // Re-apply Level just in case
                IntPtr sel_setLevel = ObjCRuntime.sel_registerName("setLevel:");
                ObjCRuntime.objc_msgSend_Long(_nsWindow, sel_setLevel, 1002);

                if (clickthrough) 
                {
                    Raylib.SetWindowState((ConfigFlags)8192); // Mouse Passthrough Flag in Raylib
                }
                else 
                {
                    Raylib.ClearWindowState((ConfigFlags)8192);
                }
                
                _isPassthrough = clickthrough;
            }
        }

        static void UpdateInput()
        {
            // Menu Toggle
            if (Raylib.IsKeyPressed(KeyboardKey.Insert) || Raylib.IsKeyPressed(KeyboardKey.Tab))
            {
                _showMenu = !_showMenu;
                // Apply State Change ONCE
                SetWindowClickthrough(!_showMenu);
            }

            // Aim Assist Toggle (Cmd+Z)
            bool cmd = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, 55);
            bool z = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, 6);
            if (cmd && z && !Raylib.IsKeyDown(KeyboardKey.LeftSuper)) // Simple debounce check
            {
                // We'd need a better debounce, but this is okay for now
                 _aimAssistActive = !_aimAssistActive;
                 Thread.Sleep(200); // Hacky debounce
            }

            // Record Toggle (Cmd+U)
            bool u = NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateHIDSystemState, 32);
            if (cmd && u && !Raylib.IsKeyDown(KeyboardKey.LeftSuper)) 
            {
                 if (Recorder.IsRecording) Recorder.Stop();
                 else Recorder.Start();
                 Thread.Sleep(200); 
            }
        }


        static void DrawMenu()
        {
            // Draggable Header Logic
            int panelW = 380;
            int headerH = 40;
            int panelH = 600; // Fixed Height for Scrolling
            
            bool overHeader = Raylib.GetMouseX() >= _menuX && Raylib.GetMouseX() <= _menuX + panelW &&
                              Raylib.GetMouseY() >= _menuY && Raylib.GetMouseY() <= _menuY + headerH;
            
            if (overHeader && Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                _menuX += (int)Raylib.GetMouseDelta().X;
                _menuY += (int)Raylib.GetMouseDelta().Y;
            }

            // Draw Background
            Raylib.DrawRectangle(_menuX, _menuY, panelW + 120, panelH, new Color(15, 15, 15, 250));
            Raylib.DrawRectangleLines(_menuX, _menuY, panelW + 120, panelH, new Color(200, 50, 50, 255));
            Raylib.DrawText("AIMMY v2 (Scrollable)", _menuX + 20, _menuY + 10, 24, Color.White);

            int startX = _menuX + 60;
            // SCROLL LOGIC
            // Content Height Approx 1200px -> Increased to 2500 for new controls
            int contentHeight = 2500;
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0) _menuScrollY -= wheel * 30; // Invert wheel
            if (_menuScrollY < 0) _menuScrollY = 0;
            if (_menuScrollY > contentHeight - panelH + 100) _menuScrollY = contentHeight - panelH + 100;
            
            // Scissor
            Raylib.BeginScissorMode(_menuX, _menuY + 50, panelW + 120, panelH - 110); // Reserve bottom for Save/Quit

            int y = _menuY + 60 - (int)_menuScrollY;
            int gap = 60;
            int mouseX = Raylib.GetMouseX();
            int mouseY = Raylib.GetMouseY();
            bool isDown = Raylib.IsMouseButtonDown(MouseButton.Left);

            // --- P1..P5 ---
            for (int i = 1; i <= 5; i++)
            {
                string pName = (i == 1) ? "config.json" : $"config_p{i}.json";
                bool isCurrent = _currentProfileName == pName;
                if (DrawButton(_menuX + 30 + ((i - 1) * 70), y, 60, 30, $"P{i}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left)))
                {
                    _currentProfileName = pName;
                    _config = Config.Load(_currentProfileName);
                    
                    // Reload Model if different
                    if (File.Exists(_config.ModelPath)) {
                        try {
                             _aiManager?.Dispose();
                             _aiManager = new AIManager(_config.ModelPath);
                             
                             // FIX: Update UI Index to match new model
                             for (int m = 0; m < _modelFiles.Count; m++) {
                                 if (Path.GetFullPath(_modelFiles[m]) == Path.GetFullPath(_config.ModelPath)) {
                                     _currentModelIndex = m;
                                     break;
                                 }
                             }
                        } catch {}
                    }
                }
                if (isCurrent) Raylib.DrawRectangleLines(_menuX + 30 + ((i - 1) * 70), y, 60, 30, Color.Green);
            }
            y += 50;
            
            // --- Binding Overlay (Must be outside Scissor? Actually simpler to effectively block it inside, but drawing it outside is better)
            // Deferred drawing logic? No, just handle input here.
            
            if (_isBinding)
            {
                // Ensure user releases mouse before capturing
                Raylib.EndScissorMode(); // Disable Scissor for Overlay
                
                if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Left))
                {
                    Raylib.DrawRectangle(50, 200, 400, 200, Color.Black);
                    Raylib.DrawRectangleLines(50, 200, 400, 200, Color.Gray);
                    Raylib.DrawText("Release Mouse...", 140, 290, 20, Color.White);
                    return; // Wait for release
                }

                Raylib.DrawRectangle(50, 200, 400, 200, Color.Black);
                Raylib.DrawRectangleLines(50, 200, 400, 200, Color.Green);
                Raylib.DrawText("PRESS ANY KEY/CLICK", 80, 250, 30, Color.White);
                Raylib.DrawText("ESC: Cancel | DELETE: Always On", 70, 300, 20, Color.Gray);
                
                int foundKey = -2;
                // Check keyboard
                for (ushort k = 0; k < 128; k++) {
                    if (NativeMethods.CGEventSourceKeyState(NativeMethods.kCGEventSourceStateCombinedSessionState, k)) { foundKey = k; break; }
                }
                
                // Check mouse if no key found
                if (foundKey == -2) {
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Left)) foundKey = 200;
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Right)) foundKey = 201;
                    if (NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateCombinedSessionState, NativeMethods.CGMouseButton.Center)) foundKey = 202;
                }
                
                if (foundKey != -2)
                {
                    if (foundKey == 53) { // ESC = Cancel
                        _isBinding = false; 
                    }
                    else if (foundKey == 51 || foundKey == 117) { // DELETE / Forward Delete = Always On
                        _config.AimKey = -1;
                        _bindingText = GetKeyName(-1);
                        _isBinding = false;
                        _config.Save(_currentProfileName);
                    }
                    else {
                        _config.AimKey = foundKey;
                        _bindingText = GetKeyName(foundKey);
                        _isBinding = false;
                        _config.Save(_currentProfileName);
                    }
                    
                    Thread.Sleep(200); // Debounce
                }
                return; // Block other menu interactions
            
            }

            // --- CONTROLS ---
            
            // Model Selector (Searchable Dropdown)
             string mName = (_modelFiles.Count > 0 && _currentModelIndex < _modelFiles.Count) ? Path.GetFileName(_modelFiles[_currentModelIndex]) : "None";
            if (mName.Length > 20) mName = mName.Substring(0, 17) + "...";
            
            // Store dropdown rect for later drawing
            Rectangle? deferredDropdown = null;

            if (!_isModelDropdownOpen) 
            {
                // Split into Dropdown (340px) and Refresh (30px)
                if (DrawButton(startX, y, 340, 30, $"Model: {mName} (v)", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left)))
                {
                    _isModelDropdownOpen = true;
                    _modelSearchQuery = ""; // Reset search
                    _modelScrollY = 0;
                }
                
                // Refresh Button
                if (DrawButton(startX + 350, y, 30, 30, "R", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left)))
                {
                    RefreshModelList();
                }
            }
            else 
            {
                // Placeholder button (visual only, to keep layout)
                Raylib.DrawRectangle(startX, y, 340, 30, new Color(50, 50, 50, 255));
                Raylib.DrawRectangleLines(startX, y, 340, 30, Color.Green);
                Raylib.DrawText($"Model: {mName} (^)", startX + 10, y + 5, 20, Color.Gray);
                
                // Refresh Button (Disabled or Active? Active is fine)
                if (DrawButton(startX + 350, y, 30, 30, "R", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left)))
                {
                     RefreshModelList();
                }
                
                // Clicking placeholder closes it?
                if (Raylib.IsMouseButtonPressed(MouseButton.Left) && 
                    mouseX >= startX && mouseX <= startX + 380 && 
                    mouseY >= y && mouseY <= y + 30)
                {
                    _isModelDropdownOpen = false;
                }

                // Schedule actual dropdown to draw LAST (on top)
                deferredDropdown = new Rectangle(startX, y, 380, 200);
            }
            y += gap;

            // Sens
            float sens = _config.MouseSensitivity;
            // Block input if dropdown is open? 
            // Ideally yes, but for now Z-order visual fix is priority.
            // Actually, if we just check !_isModelDropdownOpen for interactions below, it solves "click through"
            bool interact = !_isModelDropdownOpen;
            
            DrawSlider(startX, y, 380, 20, "Sensitivity", ref sens, 0.001f, 2.0f, mouseX, mouseY, isDown && interact); y += gap;
            DrawSlider(startX, y, 380, 20, "Sensitivity", ref sens, 0.001f, 2.0f, mouseX, mouseY, isDown && interact); y += gap;
            _config.MouseSensitivity = sens;
            
            // Dynamic Sens
             if (DrawButton(startX, y, 380, 30, $"Sticky Aim: {(_config.DynamicSensitivity ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.DynamicSensitivity = !_config.DynamicSensitivity;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            if (_config.DynamicSensitivity)
            {
                 float dscale = _config.DynamicSensScale;
                 DrawSlider(startX, y, 380, 20, "Sticky Scale", ref dscale, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
                 _config.DynamicSensScale = dscale;
            }
            
            // Smooth
            float smooth = _config.MouseSmoothing;
            DrawSlider(startX, y, 380, 20, "Smoothing", ref smooth, 0.0f, 0.95f, mouseX, mouseY, isDown && interact); y += gap;
            _config.MouseSmoothing = smooth;
            
            // Max FPS
            float maxFps = _config.MaxFps;
            DrawSlider(startX, y, 380, 20, "Max FPS", ref maxFps, 30, 360, mouseX, mouseY, isDown && interact); y += gap;
            if ((int)maxFps != _config.MaxFps)
            {
                _config.MaxFps = (int)maxFps;
                Raylib.SetTargetFPS(_config.MaxFps);
            }
            
            // Auto Calibrate
            bool calibClick = DrawButton(startX, y, 380, 30, _isCalibrating ? "Calibrating..." : "Auto Calibrate Sensitivity", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact); y+=gap;
            if (calibClick && !_isCalibrating) {
                _isCalibrating = true;
                _calibStep = 0;
                _showMenu = false; 
                SetWindowClickthrough(true);
            }

            // Conf
            float conf = _config.ConfidenceThreshold;
            DrawSlider(startX, y, 380, 20, "Confidence", ref conf, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
            _config.ConfidenceThreshold = conf;

            // Aim Key
            if (DrawButton(startX, y, 380, 30, $"Aim Key: {_bindingText}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) _isBinding = true;
            y += gap;
            
            // Team Color
            if (DrawButton(startX, y, 380, 30, $"Team Color: {_config.TeamColor}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.TeamColor++;
                if ((int)_config.TeamColor > 4) _config.TeamColor = Config.TeamColorType.None;
                _config.Save(_currentProfileName);
            }
            y += gap;

            // FOV
            float fov = _config.FovSize;
            DrawSlider(startX, y, 380, 20, "FOV", ref fov, 50, 1000, mouseX, mouseY, isDown && interact); y += gap;
            _config.FovSize = (int)fov;
            
            // FOV COLOR (RGBA)
            float fr = _config.FovColorR;
            DrawSlider(startX, y, 380, 20, "FOV Red", ref fr, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
            _config.FovColorR = (int)fr;
            
            float fg = _config.FovColorG;
            DrawSlider(startX, y, 380, 20, "FOV Green", ref fg, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
            _config.FovColorG = (int)fg;
            
            float fb = _config.FovColorB;
            DrawSlider(startX, y, 380, 20, "FOV Blue", ref fb, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
            _config.FovColorB = (int)fb;
            
            float fa = _config.FovColorA;
            DrawSlider(startX, y, 380, 20, "FOV Alpha", ref fa, 0, 255, mouseX, mouseY, isDown && interact); y += gap;
            _config.FovColorA = (int)fa;
            
            // Offsets
            float ox = _config.XOffset;
            DrawSlider(startX, y, 380, 20, "Offset X", ref ox, -500, 500, mouseX, mouseY, isDown && interact); y += gap;
            _config.XOffset = (int)ox;
            
            float oy = _config.YOffset;
            DrawSlider(startX, y, 380, 20, "Offset Y", ref oy, -500, 500, mouseX, mouseY, isDown && interact); y += gap;
            _config.YOffset = (int)oy;

            // Center Calibration Button
            if (DrawButton(startX, y, 380, 30, "Calibrate Screen Center", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) 
            {
                _isCalibratingCenter = true;
                _showMenu = false;
                SetWindowClickthrough(true);
            }
            y += gap;

            // Window Calibration Button
            if (DrawButton(startX, y, 380, 30, "Calibrate to Window (Smart)", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) 
            {
                _windowList = WindowHelper.GetWindows();
                _isSelectingWindow = true; // Switch to window selection mode (still in menu, but different view)
                _windowListScrollY = 0;
            }
            y += gap;

            // Force Reset Button for User Convenience
            if (DrawButton(startX, y, 380, 30, "Reset Offsets (Fix Aim)", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.XOffset = 0;
                _config.YOffset = 0;
                _config.CenterOffsetX = 0;
                _config.CenterOffsetY = 0;
                _config.Save(_currentProfileName);
            }
            y += gap;

            // Triggerbot
            if (DrawButton(startX, y, 380, 30, $"Triggerbot: {(_config.TriggerBot ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.TriggerBot = !_config.TriggerBot;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            if (_config.TriggerBot) 
            {
                float tDelay = _config.TriggerCooldown;
                DrawSlider(startX, y, 380, 20, "Trig Delay (ms)", ref tDelay, 10, 2000, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerCooldown = (int)tDelay;

                float tConf = _config.TriggerConfidence;
                DrawSlider(startX, y, 380, 20, "Trig Conf", ref tConf, 0.1f, 1.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerConfidence = tConf;
                
                float tRange = _config.TriggerRange;
                DrawSlider(startX, y, 380, 20, "Trig Range (px)", ref tRange, 0, 200, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerRange = (int)tRange;
                
                float tShotDelay = _config.TriggerDelay;
                DrawSlider(startX, y, 380, 20, "Shot Delay (ms)", ref tShotDelay, 0, 500, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerDelay = (int)tShotDelay;
                
                if (DrawButton(startX, y, 380, 30, $"Trig Mode: {(_config.TriggerAlways ? "Always" : "On Aim Key")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                    _config.TriggerAlways = !_config.TriggerAlways;
                     _config.Save(_currentProfileName);
                }
                y += gap;
                
                // Magnet
                if (DrawButton(startX, y, 380, 30, $"Magnet: {(_config.MagnetTrigger ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                     _config.MagnetTrigger = !_config.MagnetTrigger;
                     _config.Save(_currentProfileName);
                }
                y += gap;
                
                // Color Check
                if (DrawButton(startX, y, 380, 30, $"Strict Color: {(_config.TriggerColorCheck ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                     _config.TriggerColorCheck = !_config.TriggerColorCheck;
                     _config.Save(_currentProfileName);
                }
                y += gap;
                
                // Prediction Trigger
                if (DrawButton(startX, y, 380, 30, $"Trig Pred: {(_config.TriggerPrediction ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                     _config.TriggerPrediction = !_config.TriggerPrediction;
                     _config.Save(_currentProfileName);
                }
                y += gap;
                
                // Burst
                float tBurst = _config.TriggerBurst;
                DrawSlider(startX, y, 380, 20, "Burst Shots", ref tBurst, 0, 10, mouseX, mouseY, isDown && interact); y += gap;
                _config.TriggerBurst = (int)tBurst;
            }
            
            // ESP
            if (DrawButton(startX, y, 380, 30, $"ESP Info: {(_config.ShowESP ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.ShowESP = !_config.ShowESP;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            // Recoil
            if (DrawButton(startX, y, 380, 30, $"Recoil: {(_config.RecoilEnabled ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.RecoilEnabled = !_config.RecoilEnabled;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            float ry = _config.RecoilY;
            DrawSlider(startX, y, 380, 20, "Recoil Y", ref ry, 0, 50, mouseX, mouseY, isDown && interact); y += gap;
            _config.RecoilY = (int)ry;

            // Jitter
            if (DrawButton(startX, y, 380, 30, $"Jitter: {(_config.Jitter ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.Jitter = !_config.Jitter;
                _config.Save(_currentProfileName);
            }
            y += gap;

            if (_config.Jitter)
            {
                float ja = _config.JitterAmount;
                DrawSlider(startX, y, 380, 20, "Jitter Amt", ref ja, 0.0f, 20.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.JitterAmount = ja;
            }
            y += gap;
            
            // Humanize
            if (DrawButton(startX, y, 380, 30, $"Humanize: {(_config.HumanizeAim ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.HumanizeAim = !_config.HumanizeAim;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            if (_config.HumanizeAim)
            {
                 float hum = _config.HumanizeStrength;
                 DrawSlider(startX, y, 380, 20, "Curve Amt", ref hum, 1.0f, 20.0f, mouseX, mouseY, isDown && interact); y += gap;
                 _config.HumanizeStrength = hum;
            }

            // Prediction
            if (DrawButton(startX, y, 380, 30, $"Prediction: {(_config.PredictionEnabled ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.PredictionEnabled = !_config.PredictionEnabled;
                _config.Save(_currentProfileName);
            }
            y += gap;

            if (_config.PredictionEnabled)
            {
                float ps = _config.PredictionScale;
                DrawSlider(startX, y, 380, 20, "Pred Scale", ref ps, 0.1f, 10.0f, mouseX, mouseY, isDown && interact); y += gap;
                _config.PredictionScale = ps;
            }
            
            // Bone Selector
            if (DrawButton(startX, y, 380, 30, $"Aim Bone: {_config.AimBone}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.AimBone++;
                if ((int)_config.AimBone > 3) _config.AimBone = Config.BoneType.Head;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            // Jump Offset
            if (DrawButton(startX, y, 380, 30, $"Jump Offset: {(_config.JumpOffsetEnabled ? "ON" : "OFF")}", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) {
                _config.JumpOffsetEnabled = !_config.JumpOffsetEnabled;
                _config.Save(_currentProfileName);
            }
            y += gap;
            
            if (_config.JumpOffsetEnabled)
            {
                 float jOff = _config.JumpOffset;
                 DrawSlider(startX, y, 380, 20, "Jump Offset Y", ref jOff, -200, 200, mouseX, mouseY, isDown && interact); y += gap;
                 _config.JumpOffset = (int)jOff;
            }
            
            // Record
            if (DrawButton(startX, y, 380, 30, Recorder.IsRecording ? "STOP RECORDING" : "Record AI Vision", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) 
            {
                if (Recorder.IsRecording) Recorder.Stop();
                else Recorder.Start();
            }
            if (Recorder.IsRecording)
            {
                Raylib.DrawRectangleLines(startX, y, 380, 30, Color.Red);
                Raylib.DrawText("REC", startX + 10, y + 5, 20, Color.Red);
            }
            y += gap;
            
            // Save/Quit
            // Reusing existing variable from above if exists, or defining new if scope is clean.
            // Save/Quit MOVED to Fixed Bottom
            // if (DrawButton(startX, y, 140, 40, "Save", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) _config.Save(_currentProfileName);
            // if (DrawButton(startX + 240, y, 140, 40, "Quit", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) _running = false;
            
            Raylib.EndScissorMode(); // END SCISSOR
            
            // FIXED FOOTER
            int footY = _menuY + panelH - 50;
            if (DrawButton(startX, footY, 140, 40, "Save", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) _config.Save(_currentProfileName);
            if (DrawButton(startX + 240, footY, 140, 40, "Quit", mouseX, mouseY, Raylib.IsMouseButtonPressed(MouseButton.Left) && interact)) _running = false;
            
            // Scrollbar (Visual)
            float scrollPct = _menuScrollY / (contentHeight - panelH + 100);
            Raylib.DrawRectangle(_menuX + panelW + 110, _menuY + 50 + (int)(scrollPct * (panelH - 100)), 5, 40, Color.Gray);

            // Global Mouse Release Save check (Auto-Save)
            if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            {
                 // We don't want to save on *every* click, maybe just check if we were interacting?
                 // For simplicity, Auto-Save on release is fine for this app scale.
                 _config.Save(_currentProfileName);
            }

            // --- DRAW DEFERRED DROPDOWN (ON TOP) ---
            if (deferredDropdown.HasValue && _isModelDropdownOpen)
            {
                 // We must draw it *without* scissor clipping if we want it to float freely out of frame, 
                 // BUT if it floats out of the "Menu Window", it might look weird if background isn't there.
                 // However, "On Top of Sliders" is the main goal.
                 // Since we ended scissor above, this is perfect.
                 
                 DrawSearchableDropdown(deferredDropdown.Value, _modelFiles, ref _isModelDropdownOpen, ref _modelSearchQuery, ref _modelScrollY, (selectedIndex) => {
                    _currentModelIndex = selectedIndex;
                    _config.ModelPath = _modelFiles[_currentModelIndex];
                    try { 
                         _aiManager?.Dispose(); _aiManager = new AIManager(_config.ModelPath); 
                         _config.Save(_currentProfileName); 
                    } catch {}
                });
            }
        }

        static void DrawWindowSelection(int screenW, int screenH)
        {
             // Similar to DrawMenu but full overlay for list
             int panelW = 600;
             int panelH = 600;
             int x = (screenW - panelW) / 2;
             int y = (screenH - panelH) / 2;
             
             // Background
             Raylib.DrawRectangle(x, y, panelW, panelH, new Color(15, 15, 15, 250));
             Raylib.DrawRectangleLines(x, y, panelW, panelH, Color.Green);
             Raylib.DrawText("Select Window to Attach", x + 20, y + 20, 24, Color.White);
             
             if (DrawButton(x + panelW - 100, y + 20, 80, 30, "Cancel", Raylib.GetMouseX(), Raylib.GetMouseY(), Raylib.IsMouseButtonPressed(MouseButton.Left)))
             {
                 _isSelectingWindow = false;
             }
             
             if (DrawButton(x + panelW - 190, y + 20, 80, 30, "Refresh", Raylib.GetMouseX(), Raylib.GetMouseY(), Raylib.IsMouseButtonPressed(MouseButton.Left)))
             {
                 _windowList = WindowHelper.GetWindows();
                 _windowListScrollY = 0;
             }
             
             // List
             int listY = y + 70;
             int listH = panelH - 100;
             
             // Scissor
             Raylib.BeginScissorMode(x, listY, panelW, listH);
             
             int contentHeight = _windowList.Count * 60;
             float wheel = Raylib.GetMouseWheelMove();
             if (wheel != 0) _windowListScrollY -= wheel * 30;
             if (_windowListScrollY < 0) _windowListScrollY = 0;
             if (_windowListScrollY > contentHeight - listH) _windowListScrollY = contentHeight - listH;
             
             int itemY = listY - (int)_windowListScrollY;
             int mouseX = Raylib.GetMouseX();
             int mouseY = Raylib.GetMouseY();
             
             for (int i = 0; i < _windowList.Count; i++)
             {
                 var w = _windowList[i];
                 
                 // Show Name and Owner
                 string label = $"{w.OwnerName}: {w.Name}";
                 if (string.IsNullOrWhiteSpace(w.Name)) label = w.OwnerName;
                 if (label.Length > 50) label = label.Substring(0, 47) + "...";
                 label += $" ({w.Bounds.Size.Width}x{w.Bounds.Size.Height})";
                 
                 // Hover check
                 bool hover = mouseX >= x + 20 && mouseX <= x + panelW - 40 &&
                              mouseY >= itemY && mouseY <= itemY + 50;
                 
                 if (hover) Raylib.DrawRectangle(x + 20, itemY, panelW - 40, 50, new Color(50, 50, 50, 255));
                 Raylib.DrawRectangleLines(x + 20, itemY, panelW - 40, 50, Color.Gray);
                 
                 Raylib.DrawText(label, x + 30, itemY + 15, 20, Color.White);
                 
                 if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left))
                 {
                     // SELECT THIS WINDOW
                     // Calculate Center Offset relative to user screen center
                     // User Screen Center
                     int screenCenterX = screenW / 2;
                     int screenCenterY = screenH / 2;
                     
                     // Window Center
                     int winCenterX = (int)(w.Bounds.Origin.X + (w.Bounds.Size.Width / 2));
                     int winCenterY = (int)(w.Bounds.Origin.Y + (w.Bounds.Size.Height / 2));
                     
                     _config.CenterOffsetX = winCenterX - screenCenterX;
                     _config.CenterOffsetY = winCenterY - screenCenterY;
                     
                     // Also optionally set FOV to match height? No, might be too big.
                     
                     _config.Save(_currentProfileName);
                     _isSelectingWindow = false;
                 }
                 
                 itemY += 60;
             }
             
             Raylib.EndScissorMode();
             
             // Scrollbar
             if (contentHeight > listH)
             {
                 float pct = _windowListScrollY / (contentHeight - listH);
                 Raylib.DrawRectangle(x + panelW - 15, listY + (int)(pct * (listH - 40)), 10, 40, Color.Gray);
             }
        }


        static void DrawOverlayAndAim(int screenW, int screenH)
        {
            // Center Calibration Mode
            if (_isCalibratingCenter)
            {
                int cx = (screenW / 2) + _config.CenterOffsetX;
                int cy = (screenH / 2) + _config.CenterOffsetY;
                
                // Draw Crosshair
                Raylib.DrawLine(cx - 20, cy, cx + 20, cy, Color.Red);
                Raylib.DrawLine(cx, cy - 20, cx, cy + 20, Color.Red);
                Raylib.DrawCircleLines(cx, cy, 10, Color.Red);
                
                Raylib.DrawText("CALIBRATING SCREEN CENTER", cx - 100, cy - 60, 20, Color.Yellow);
                Raylib.DrawText("Use ARROW KEYS to Align Red Crosshair with Game Crosshair", cx - 200, cy - 40, 20, Color.White);
                Raylib.DrawText("PRESS ENTER TO SAVE", cx - 80, cy + 40, 20, Color.Green);
                
                // Input
                if (Raylib.IsKeyDown(KeyboardKey.Up)) _config.CenterOffsetY -= 1;
                if (Raylib.IsKeyDown(KeyboardKey.Down)) _config.CenterOffsetY += 1;
                if (Raylib.IsKeyDown(KeyboardKey.Left)) _config.CenterOffsetX -= 1;
                if (Raylib.IsKeyDown(KeyboardKey.Right)) _config.CenterOffsetX += 1;
                
                if (Raylib.IsKeyPressed(KeyboardKey.Enter))
                {
                    _isCalibratingCenter = false;
                    _showMenu = true;
                    SetWindowClickthrough(false);
                    _config.Save(_currentProfileName);
                }
                return; // Skip normal aiming logic while calibrating center
            }

            // Status
            bool isAimKey = IsAimKeyDown();
            
            Raylib.DrawText(_aimAssistActive ? "AIM: ON" : "AIM: OFF", 20, 20, 24, _aimAssistActive ? Color.Green : Color.Red);
            Raylib.DrawText($"FPS: {Raylib.GetFPS()} | Cap: {_lastCapMs}ms | AI: {_lastAiMs}ms", 20, 50, 18, Color.DarkGreen);
            Raylib.DrawText($"Key: {(isAimKey ? "HELD" : "---")} | Move: {_lastMoveX:F1}, {_lastMoveY:F1}", 20, 75, 18, isAimKey ? Color.Yellow : Color.Gray);
            
            if (!_aimAssistActive) return;

            // Updated Center with Offset
            int centerX = (screenW / 2) + _config.CenterOffsetX;
            int centerY = (screenH / 2) + _config.CenterOffsetY;
            
            // FOV Circle (Centered on calibrated center)
            Color fovColor = new Color(_config.FovColorR, _config.FovColorG, _config.FovColorB, _config.FovColorA);
            Raylib.DrawCircleLines(centerX, centerY, _config.FovSize / 2.0f, fovColor);
            
            // --- CAPTURE & AIM ---
            var buffer = _aiManager?.GetInputBuffer();
            if (buffer == null) return;
            
            int imgSize = _aiManager.IMAGE_SIZE;
            
            // Retina Scaling Optimization
            // Calculate scale: Pixels / Points
            long pixW = NativeMethods.CGDisplayPixelsWide(_selectedDisplayID);
            double pointsW = NativeMethods.CGDisplayBounds(_selectedDisplayID).Size.Width;
            double scale = (pointsW > 0) ? ((double)pixW / pointsW) : 1.0;
            if (scale < 1.0) scale = 1.0;
            
            // We want 'imgSize' PIXELS.
            // Screen API takes POINTS.
            // If scale is 2.0, we need imgSize / 2.0 POINTS to get imgSize PIXELS.
            double capSizePoints = (double)imgSize / scale;
            
            // Center the capture box (Points)
            double capX = centerX - (capSizePoints / 2.0);
            double capY = centerY - (capSizePoints / 2.0);
            
            // CLAMP to Screen Bounds (Crucial for CGDisplayCreateImageForRect)
            
            // 1. Ensure we don't go negative
            if (capX < 0) capX = 0;
            if (capY < 0) capY = 0;
            
            // 2. Ensure we don't go beyond screen width/height
            double pointsH = NativeMethods.CGDisplayBounds(_selectedDisplayID).Size.Height;
            
            if (capX + capSizePoints > pointsW) capX = pointsW - capSizePoints;
            if (capY + capSizePoints > pointsH) capY = pointsH - capSizePoints;
            
            // 3. Final Safety Check (Floating point epsilon)
            // Just to be absolutely safe, floor the coordinates to ensure alignment isn't weird? 
            // Actually, CoreGraphics handles doubles, but let's just ensure we are definitely inside.
            if (capX < 0) capX = 0; // In case size > screen? Unlikely but possible if config is bad.
            if (capY < 0) capY = 0;
            
            _perfTimer.Restart();
            var result = MacCapture.CaptureAndFillTensor(buffer, imgSize, new CGRect(capX, capY, capSizePoints, capSizePoints), _selectedDisplayID);
            _lastCapMs = _perfTimer.ElapsedMilliseconds;
            
            if (!result) return;
            
            _perfTimer.Restart();
            var preds = _aiManager.PredictFromBuffer(_config.ConfidenceThreshold);
            _lastAiMs = _perfTimer.ElapsedMilliseconds;
            
            // --- FIND BEST TARGET (Before Recording, so we can pass bestIndex) ---
            Prediction? best = null;
            double minDist = double.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < preds.Count; i++)
            {
                var p = preds[i];
                // Color Filter
                if (!IsColorMatch(p, buffer, imgSize, _config.TeamColor)) continue;
                
                // RETINA FIX: Scale AI Coords (Pixels) back to Screen Coords (Points)
                // If scale is 2.0, 640px = 320pts.
                float pX = (float)(p.Rectangle.X / scale);
                float pY = (float)(p.Rectangle.Y / scale);
                float pW = (float)(p.Rectangle.Width / scale);
                float pH = (float)(p.Rectangle.Height / scale);
                
                // Box Position (Points)
                float bx = (float)(capX + pX);
                float by = (float)(capY + pY);
                Raylib.DrawRectangleLinesEx(new Rectangle(bx, by, pW, pH), 2, Color.Red);
                
                // Dist from CALIBRATED center (Points)
                double dist = Math.Sqrt(Math.Pow((bx + pW/2) - centerX, 2) + Math.Pow((by + pH/2) - centerY, 2));
                if (dist < _config.FovSize / 2.0f)
                {
                    if (best == null || dist < minDist)
                    {
                        best = p;
                        minDist = dist;
                        bestIndex = i;
                    }
                }
            }
            
            double finalX = 0;
            double finalY = 0;

            if (best != null)
            {
                // Draw Target Box
                // Recalculate scaled properties for the best target
                float bestX = (float)(best.Rectangle.X / scale);
                float bestY = (float)(best.Rectangle.Y / scale);
                float bestW = (float)(best.Rectangle.Width / scale);
                float bestH = (float)(best.Rectangle.Height / scale);
                
                float bestCX = (float)(best.ScreenCenterX / scale);
                float bestCY = (float)(best.ScreenCenterY / scale);
                
                Raylib.DrawRectangleLinesEx(new Rectangle((float)(capX + bestX), (float)(capY + bestY), bestW, bestH), 3, Color.Green);
                
                double targetX = capX + bestCX + _config.XOffset;
                double targetY = capY + bestCY + _config.YOffset;
                
                // Bone Offset
                float boxH = bestH;
                if (_config.AimBone == Config.BoneType.Head) targetY -= boxH * 0.35; // Top 15% (Center is 0.5) -> 0.5 - 0.35 = 0.15
                else if (_config.AimBone == Config.BoneType.Neck) targetY -= boxH * 0.25;
                else if (_config.AimBone == Config.BoneType.Chest) targetY -= boxH * 0.15;
                // Body = 0 offset (Center)
                
                // Jump Offset
                if (_config.JumpOffsetEnabled && Raylib.IsKeyDown(KeyboardKey.Space))
                {
                     targetY += _config.JumpOffset;
                }
                
                double dx = targetX - centerX; // Error X relative to Calibrated Center
                double dy = targetY - centerY; // Error Y relative to Calibrated Center
                
                // UNIFIED AIM LOGIC CALCULATION (Run always for recording, apply if Aim Key Held)
                // --- NORMAL AIM MATH ---
                
                // Prediction Logic (Before calculating final moves)
                double predX = 0;
                double predY = 0;
                
                if (_config.PredictionEnabled)
                {
                     // Calculate Velocity
                     long now = _sw.ElapsedMilliseconds;
                     float dt = (now - _lastTargetTime) / 1000.0f;
                     if (dt > 0 && dt < 0.2f) // Only Valid if consecutive frames
                     {
                         // Target Position in Buffer/Relative Space (Scaled!)
                         Vector2 currentPos = new Vector2(bestCX, bestCY);
                         
                         float distMoved = (currentPos - _lastTargetPos).Length();
                         
                         // Target Protection: If distance is too large, it's a target switch.
                         if (distMoved < 150)
                         {
                             Vector2 velocity = (currentPos - _lastTargetPos) / dt;
                             
                             // Ignore massive velocities (sanity check)
                             if (velocity.Length() < 5000) 
                             {
                                 predX = velocity.X * _config.PredictionScale * 0.016f; 
                                 predY = velocity.Y * _config.PredictionScale * 0.016f;
                                 
                                 // Visual Debug
                                 Raylib.DrawLine((int)(capX + bestCX), (int)(capY + bestCY), 
                                                 (int)(capX + bestCX + predX*10), (int)(capY + bestCY + predY*10), Color.Yellow);
                                                 
                                // Ghost Box (Visual Debugging)
                                float ghostX = (float)(capX + bestX + predX);
                                float ghostY = (float)(capY + bestY + predY);
                                Raylib.DrawRectangleLinesEx(new Rectangle(ghostX, ghostY, bestW, bestH), 2, Color.Orange);
                                Raylib.DrawText("PREDICTION", (int)ghostX, (int)ghostY - 15, 12, Color.Orange);
                             }
                         }
                     }
                     
                     _lastTargetPos = new Vector2(bestCX, bestCY);
                     _lastTargetTime = now;
                }
                
                if (_config.PredictionEnabled)
                {
                     // (Prediction Logic Omitted for Brevity - unchanged)
                     // ...
                }
                
                // Dynamic Sensitivity (Sticky Aim)
                double currentSens = _config.MouseSensitivity;
                if (_config.DynamicSensitivity)
                {
                     // Reduce sensitivity when locked on
                     currentSens *= _config.DynamicSensScale;
                     Raylib.DrawText("STICKY", (int)(capX + bestCX), (int)(capY + bestCY) + 20, 10, Color.SkyBlue);
                }

                double rawMoveX = (dx + predX) * currentSens;
                double rawMoveY = (dy + predY) * currentSens;
                
                // --- HUMANIZATION (CURVE/SWAY) ---
                if (_config.HumanizeAim)
                {
                     // Only apply if we are actually moving significantly
                     double moveDist = Math.Sqrt(rawMoveX*rawMoveX + rawMoveY*rawMoveY);
                     if (moveDist > 2) 
                     {
                         double time = _sw.ElapsedMilliseconds / 100.0; // Speed factor
                         double sway = Math.Sin(time) * _config.HumanizeStrength;
                         
                         // Scale sway by distance (less sway when closer)
                         // If moveDist is 100, sway is full. If 0, sway is 0.
                         // But we want "Arced" path.
                         
                         // Calculate Perpendicular Vector (-Y, X) normalized
                         double px = -rawMoveY / moveDist;
                         double py = rawMoveX / moveDist;
                         
                         rawMoveX += px * sway;
                         rawMoveY += py * sway;
                     }
                }
                
                double smooth = _config.MouseSmoothing;
                if (Math.Sign(rawMoveX) != Math.Sign(_lastMoveX) && Math.Abs(rawMoveX) > 1) smooth = 0;
                if (Math.Sign(rawMoveY) != Math.Sign(_lastMoveY) && Math.Abs(rawMoveY) > 1) smooth = 0;

                finalX = (rawMoveX * (1.0 - smooth)) + (_lastMoveX * smooth);
                finalY = (rawMoveY * (1.0 - smooth)) + (_lastMoveY * smooth);
                
                // Unified Aim Key Check
                // (Already read above, but we use it here)
                if (isAimKey)
                {
                    if (_isCalibrating && _aimAssistActive)
                    {
                         // --- CALIBRATION MODE ---
                         if (Math.Abs(dx) < 300) 
                         {
                             string debugAction = "Tracking...";
                             Color debugColor = Color.Yellow;
                             
                             // Overwrite Normal Smooth Logic for Calibration
                             // (Or just use the same logic? Let's use the calculated one for consistency)
                             
                             _lastMoveX = finalX; _lastMoveY = finalY; // Update state
                             
                             MacInput.MoveMouseRelative((int)finalX, (int)finalY);
                             
                             // Tune Logic
                             if (_calibStep % 4 == 0) 
                             {
                                 bool overshootX = (finalX > 0 && dx < -2) || (finalX < 0 && dx > 2);
                                 bool overshootY = (finalY > 0 && dy < -2) || (finalY < 0 && dy > 2);
                                 
                                 if (overshootX || overshootY)
                                 {
                                     _config.MouseSensitivity *= 0.95f; 
                                     debugAction = "Overshoot! Dropping...";
                                     debugColor = Color.Red;
                                     _triggerTimer.Restart(); // reused as "Success" counter reset
                                 }
                                 else
                                 {
                                     if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                                     {
                                         _config.MouseSensitivity += 0.001f; 
                                         debugAction = "Tuning Up...";
                                         debugColor = Color.Orange;
                                         _triggerTimer.Restart(); // Reset success counter
                                     }
                                     else
                                     {
                                         debugAction = "Locked! (Perfect)";
                                         debugColor = Color.Green;
                                         if (_triggerTimer.ElapsedMilliseconds > 1500) // 1.5 Seconds stable
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
                                 if (_config.MouseSensitivity > 1.0f) _config.MouseSensitivity = 1.0f;
                             }
                             
                             _calibStep++;
                             
                             Raylib.DrawText($"SENS: {_config.MouseSensitivity:F4}", (int)centerX - 60, (int)centerY - 60, 20, Color.White);
                             Raylib.DrawText($"ERR: {dx:F0},{dy:F0}", (int)centerX - 60, (int)centerY - 40, 20, Color.White);
                             Raylib.DrawText(debugAction, (int)centerX - 60, (int)centerY + 40, 20, debugColor);
                             
                             if (_triggerTimer.ElapsedMilliseconds < 1500) {
                                 // Show Progress Bar for Lock
                                 float prog = Math.Clamp(_triggerTimer.ElapsedMilliseconds / 1500.0f, 0, 1);
                                 Raylib.DrawRectangle((int)centerX - 50, (int)centerY + 70, (int)(100 * prog), 5, Color.Green);
                             }
                         }
                    }
                    else 
                    {
                        // --- NORMAL AIM MODE ---
                        _lastMoveX = finalX; _lastMoveY = finalY; // Update State
                        
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
                } // End IsAimKeyDown check

                // --- TRIGGERBOT (Run outside of Aim Key check if "Always On" is selected, or if Aim Key matches) ---
                // Logic: Triggerbot can work independently of Aim Assist Key if configured.
                
                bool triggerActive = _config.TriggerBot;
                
                // If not "Always", check if Key is held (re-using IsAimKeyDown logic or checking Right Click specifically if user wants scope-only)
                // Current Config says: TriggerAlways = true/false.
                // If TriggerAlways is false, we only trigger if the "Aim Key" (usually Right Click) is held.
                
                if (triggerActive && !_config.TriggerAlways && !IsAimKeyDown()) triggerActive = false;
                
                if (triggerActive && best != null)
                {
                    double tDx = (capX + best.ScreenCenterX + _config.XOffset) - centerX;
                    
                    // PRO: Trigger Prediction
                    if (_config.TriggerPrediction) tDx += predX;
                    
                    double tY = capY + best.ScreenCenterY + _config.YOffset;
                    float tBoxH = best.Rectangle.Height;
                     if (_config.AimBone == Config.BoneType.Head) tY -= tBoxH * 0.35;
                    else if (_config.AimBone == Config.BoneType.Neck) tY -= tBoxH * 0.25;
                    else if (_config.AimBone == Config.BoneType.Chest) tY -= tBoxH * 0.15;
                    
                    double tDy = tY - centerY;
                    if (_config.TriggerPrediction) tDy += predY;
                    
                    // Magnet Check or Rectangle Check
                    // Standard logic: best.Rectangle.Contains? We don't have Contains.
                    // We check if crosshair (centerX, centerY) is inside the target box.
                    // TriggerRange is radius.
                    
                    bool inZone = false;
                    
                    if (_config.MagnetTrigger)
                    {
                         // Magnet: Fire if distance to center is less than range (Circular tolerance)
                         // This allows firing even if slight off "box" but "close enough" to center point.
                         inZone = (Math.Sqrt(tDx*tDx + tDy*tDy) < _config.TriggerRange);
                    }
                    else 
                    {
                         // Standard: Fire ONLY if crosshair is INSIDE the box
                         // Convert Crosshair to Target Space?
                         // Box is at (capX + best.X, capY + best.Y)
                         // Crosshair is at (centerX, centerY)
                         // Correct check: Is centerX inside [X, X+W]?
                         double bX = capX + best.ScreenCenterX - (best.Rectangle.Width / 2.0); // Recover Left
                         double bY = capY + best.ScreenCenterY - (best.Rectangle.Height / 2.0); // Recover Top
                         double bW = best.Rectangle.Width;
                         double bH = best.Rectangle.Height;
                         
                         // Apply offsets to comparison?
                         // Actually, tDx/tDy are (Target - Crosshair).
                         // If we are "inside", then abs(tDx) < W/2 and abs(tDy) < H/2.
                         
                         inZone = (Math.Abs(tDx) < bW/2 && Math.Abs(tDy) < bH/2);
                    }
                    
                    // Strict Color Check: Even if in zone, Crosshair PIXEL must match color.
                    if (inZone && _config.TriggerColorCheck)
                    {
                         // 1. Create a dummy prediction representing the 1x1 pixel at screen center
                         //    The buffer is valid for 'imgSize'.
                         //    Center of 'imgSize' is imgSize/2.
                         //    IsColorMatch logic takes Prediction.
                         //    We can construct a fake prediction.
                         var pixelPred = new Prediction { 
                             Rectangle = new Aimmy.Mac.RectF(imgSize/2, imgSize/2, 1, 1), 
                             Confidence = 1.0f 
                         };
                         if (!IsColorMatch(pixelPred, buffer, imgSize, _config.TeamColor)) inZone = false;
                    }

                    if (inZone && best.Confidence >= _config.TriggerConfidence)
                    {
                        // Check Delay
                        // We need a timer that tracks "Time Inside Zone"
                        // Since this is a simple loop, if we are NOT in zone, reset timer?
                        // Actually _triggerTimer is used for cooldown. We need a new state for "Delay Before Shot".
                        // Let's implement a simple blocking delay using a timestamp?
                        // No, we need non-blocking. 
                        
                        // For now, let's just use the Cooldown timer as the only mechanic to avoid complexity overload in this snippet.
                        // "TriggerDelay" (Before Shot) is hard to implement without a dedicated state variable per-frame.
                        // Actually, we can use a static "enteredZoneTime".
                        
                        if (_triggerTimer.ElapsedMilliseconds > _config.TriggerCooldown + _config.TriggerDelay)
                        {
                             // Start Shooting (Down Event)
                             if (!_isTriggerShooting)
                             {
                                  IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                                  CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                                  NativeMethods.CFRelease(ev);
                                  
                                  MacInput.SendLeftMouseDown(cur.X, cur.Y);
                                  _isTriggerShooting = true;
                                  _shootingTimer.Restart();
                                  _triggerTimer.Restart(); // Reset cooldown
                                  
                                  // Init Burst
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
                
                // --- TRIGGER RELEASE LOGIC ---
                // Manage Burst or Single Shot
                if (_isTriggerShooting)
                {
                     // Burst Handling
                     if (_isBursting)
                     {
                          if (_burstTimer.ElapsedMilliseconds > 100) // Time between burst shots
                          {
                               // Release previous click
                               IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                               CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                               NativeMethods.CFRelease(ev);
                               MacInput.SendLeftMouseUp(cur.X, cur.Y);
                               
                               _burstShotsRemaining--;
                               if (_burstShotsRemaining > 0)
                               {
                                    // Queue next shot
                                    MacInput.SendLeftMouseDown(cur.X, cur.Y); 
                                    _burstTimer.Restart();
                                    // _isTriggerShooting stays true
                               }
                               else
                               {
                                    // Burst Finished
                                    _isBursting = false;
                                    _isTriggerShooting = false;
                                    _shootingTimer.Stop(); // Just in case
                               }
                          }
                     }
                     else if (_shootingTimer.ElapsedMilliseconds > 60) // Standard Single Shot (60ms click)
                     {
                          IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
                          CGPoint cur = NativeMethods.CGEventGetLocation(ev);
                          NativeMethods.CFRelease(ev);
                          
                          MacInput.SendLeftMouseUp(cur.X, cur.Y);
                          _isTriggerShooting = false;
                          _shootingTimer.Stop();
                     }
                }

                // --- RECOIL CONTROL SYSTEM (RCS) ---
                // Works independently of Aim Assist Key, but requires Left Click (Shooting)
                if (_config.RecoilEnabled)
                {
                     // Check if Left Mouse is Down (Shooting)
                     bool isShooting = NativeMethods.CGEventSourceButtonState(NativeMethods.kCGEventSourceStateHIDSystemState, NativeMethods.CGMouseButton.Left);
                     
                     if (isShooting)
                     {
                         if (_recoilTimer.ElapsedMilliseconds > _config.RecoilDelay)
                         {
                             // Apply Recoil
                             // Simple: Just move by RecoilY.
                             MacInput.MoveMouseRelative(0, _config.RecoilY);
                         }
                     }
                     else
                     {
                         _recoilTimer.Restart(); // Reset timer when not shooting
                     }
                }
                
                // --- ESP DRAWING (After Aim lines) ---
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
                    // Calibrating but Key NOT Held
                    Raylib.DrawText("HOLD AIM KEY TO CALIBRATE", (int)centerX - 120, (int)centerY + 60, 24, Color.Orange);
                     _triggerTimer.Restart(); // Keep resetting timer so we don't accidentally exit
                }
            } // End if(best != null)
            
            // --- RECORDING HOOK (MOVED AFTER LOGIC) ---
            if (Recorder.IsRecording)
            {
               // Pass calculated finalX/Y as Input Mechanics
               Recorder.EnqueueFrame(buffer, imgSize, preds, _config.TeamColor, bestIndex, _config.XOffset, _config.YOffset, _config.MouseSensitivity, _config.MouseSmoothing, (float)finalX, (float)finalY);
               Raylib.DrawCircle(screenW - 30, 30, 10, Color.Red); // REC Indicator
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

            // Sample Center of Box
            int cx = (int)(p.Rectangle.X + p.Rectangle.Width / 2);
            int cy = (int)(p.Rectangle.Y + p.Rectangle.Height / 2);
            
            if (cx < 0) cx = 0; if (cx >= size) cx = size - 1;
            if (cy < 0) cy = 0; if (cy >= size) cy = size - 1;
            
            int idx = (cy * size) + cx;
            int len = size * size;
            
            // Buffer is Planar RRR GGG BBB
            // Values 0.0-1.0
            float r = buffer[idx];
            float g = buffer[idx + len];
            float b = buffer[idx + 2 * len];
            
            // Convert to HSV (Simple approximation)
            float h, s, v;
            ColorToHSV(r, g, b, out h, out s, out v);
            
            // Thresholds
            // H: 0-360, S: 0-1, V: 0-1
            
            if (s < 0.2f) return false; // Too grey/white/black to have a "Team Color"
            
            switch (targetColor)
            {
                case Config.TeamColorType.Red:
                    return (h >= 0 && h < 40) || (h > 320 && h <= 360);
                case Config.TeamColorType.Blue:
                    return (h > 180 && h < 260); // Cyan-Blue-DarkBlue
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
            
            if (delta < 0.00001f)
            {
                s = 0;
                h = 0;
                return;
            }
            
            if (max > 0.0) {
                s = (delta / max);
            } else {
                s = 0.0f;
                h = 0.0f;
                return;
            }
            
            if (r >= max)
                h = (g - b) / delta;
            else if (g >= max)
                h = 2.0f + (b - r) / delta;
            else
                h = 4.0f + (r - g) / delta;
                
            h *= 60.0f;
            
            if (h < 0.0) h += 360.0f;
        }

        static void DrawSlider(int x, int y, int w, int h, string label, ref float value, float min, float max, int mx, int my, bool mouseDown)
        {
             Raylib.DrawText(label, x, y - 18, 16, Color.Gray);
             Raylib.DrawText($"{value:F2}", x + w + 10, y + 5, 16, Color.Yellow);
             Raylib.DrawRectangle(x, y, w, h, new Color(40,40,40,255));
             
             float ratio = (value - min) / (max - min);
             Raylib.DrawRectangle(x, y, (int)(w * ratio), h, Color.Red);
             Raylib.DrawRectangleLines(x, y, w, h, Color.Gray);
             
             if (mouseDown && mx >= x && mx <= x + w && my >= y && my <= y + h)
             {
                 float nR = (float)(mx - x) / w;
                 value = min + (nR * (max - min));
                 if (value < min) value = min;
                 if (value > max) value = max;
             }
        }
        
        static bool DrawButton(int x, int y, int w, int h, string text, int mx, int my, bool clicked)
        {
             bool hover = mx >= x && mx <= x + w && my >= y && my <= y + h;
             Raylib.DrawRectangle(x, y, w, h, hover ? new Color(60,60,60,255) : new Color(30,30,30,255));
             Raylib.DrawRectangleLines(x, y, w, h, hover ? Color.White : Color.DarkGray);
             int tw = Raylib.MeasureText(text, 20);
             Raylib.DrawText(text, x + (w-tw)/2, y + (h-20)/2, 20, Color.White);
             return hover && clicked;
        }
        static string GetKeyName(int code)
        {
             if (code == -1) return "Always On (Risk)";
             if (code == 200) return "Left Click";
             if (code == 201) return "Right Click";
             if (code == 202) return "Middle Click";
             return $"Key {code}";
        }
        static void DrawSearchableDropdown(Rectangle rect, List<string> items, ref bool isOpen, ref string searchQuery, ref float scrollY, Action<int> onSelect)
        {
            int rX = (int)rect.X; int rY = (int)rect.Y; int rW = (int)rect.Width; int rH = (int)rect.Height;
            
            // Draw Background
            Raylib.DrawRectangle(rX, rY, rW, rH, new Color(20, 20, 20, 255));
            Raylib.DrawRectangleLines(rX, rY, rW, rH, Color.Green);
            
            // 1. Search Bar
            int searchH = 30;
            Raylib.DrawRectangle(rX + 5, rY + 5, rW - 10, searchH, Color.Black);
            Raylib.DrawRectangleLines(rX + 5, rY + 5, rW - 10, searchH, Color.DarkGray);
            
            Raylib.DrawText(searchQuery + "_", rX + 10, rY + 12, 20, Color.White);
            if (searchQuery.Length == 0) Raylib.DrawText("Search...", rX + 10, rY + 12, 20, Color.Gray);

            // Handle Key Input
            // Simple approach: Capture generic keys. 
            // Ideally we use Raylib.GetCharPressed()
            int key = Raylib.GetCharPressed();
            while (key > 0)
            {
                if ((key >= 32) && (key <= 125)) searchQuery += (char)key;
                key = Raylib.GetCharPressed();
            }
            // Backspace
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && searchQuery.Length > 0) searchQuery = searchQuery.Substring(0, searchQuery.Length - 1);
            // Enter/Esc to close?
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) isOpen = false;
            
            // 2. Filter List
            // We need original indices to call callback correctly
            var filtered = new List<(string Name, int OriginalIndex)>();
            for (int i = 0; i < items.Count; i++) {
                if (items[i].ToLower().Contains(searchQuery.ToLower())) filtered.Add((Path.GetFileName(items[i]), i));
            }
            
            // 3. Scrollable List Area
            int listY = rY + searchH + 10;
            int listH = rH - searchH - 15;
            
            Raylib.BeginScissorMode(rX, listY, rW, listH);
            
            // Mouse Wheel
            if (Raylib.GetMouseX() >= rX && Raylib.GetMouseX() <= rX + rW && Raylib.GetMouseY() >= listY && Raylib.GetMouseY() <= listY + listH)
            {
                 scrollY -= Raylib.GetMouseWheelMove() * 20;
            }
            if (scrollY < 0) scrollY = 0;
            int maxScroll = Math.Max(0, (filtered.Count * 30) - listH);
            if (scrollY > maxScroll) scrollY = maxScroll;
            
            int itemY = listY - (int)scrollY;
            
            for (int i = 0; i < filtered.Count; i++)
            {
                var item = filtered[i];
                // Hover Check
                bool isHover = (Raylib.GetMouseX() >= rX && Raylib.GetMouseX() <= rX + rW &&
                                Raylib.GetMouseY() >= itemY && Raylib.GetMouseY() <= itemY + 30);
                
                if (isHover) Raylib.DrawRectangle(rX, itemY, rW, 30, new Color(50, 50, 50, 255));
                Raylib.DrawText(item.Name, rX + 10, itemY + 5, 20, Color.White);
                
                if (isHover && Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    onSelect(item.OriginalIndex);
                    isOpen = false;
                }
                
                itemY += 30;
            }
            
            Raylib.EndScissorMode();
            
            // Click outside to close (Optional, handled by parent usually?)
            // If we click outside the rect, close.
            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                if (!(Raylib.GetMouseX() >= rX && Raylib.GetMouseX() <= rX + rW && Raylib.GetMouseY() >= rY && Raylib.GetMouseY() <= rY + rH))
                {
                    isOpen = false;
                }
            }
        }
    }
}
