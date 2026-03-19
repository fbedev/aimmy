using System;
using System.IO;
using System.Text.Json;

namespace Aimmy.Mac
{
    public class Config
    {
        public float MouseSensitivity { get; set; } = 0.5f;
        public float MouseSmoothing { get; set; } = 0.0f; // 0.0 = No Smoothing, 0.9 = Heavy Momentum
        public float ConfidenceThreshold { get; set; } = 0.55f;
        public int FovSize { get; set; } = 640;
        public int MaxFps { get; set; } = 60; // Default 60 for safety
        
        // FOV Color
        public int FovColorR { get; set; } = 255;
        public int FovColorG { get; set; } = 255;
        public int FovColorB { get; set; } = 255;
        public int FovColorA { get; set; } = 50;
        
        public int YOffset { get; set; } = 0;

        public int XOffset { get; set; } = 0;
        public int CenterOffsetX { get; set; } = 0;
        public int CenterOffsetY { get; set; } = 0;
        public int AimKey { get; set; } = 201; // Default to Right Click (201) to prevent "Always On" shaking, < -1 = MouseButton? 
        // Strategy: 
        // 0-127 = Keyboard (CGCharCode)
        // 200 = Mouse Left
        // 201 = Mouse Right
        // 202 = Mouse Middle
        // 203 = Mouse Side 1
        // 204 = Mouse Side 2
        // Default -1: Always Active (if Toggle is ON)

        // Target Priority
        public enum TargetPriority { Distance, Confidence }
        public TargetPriority Priority { get; set; } = TargetPriority.Distance;
        
        // Recoil
        
        // Recoil
        public bool RecoilEnabled { get; set; } = false;
        public int RecoilX { get; set; } = 0;
        public int RecoilY { get; set; } = 10;
        public int RecoilDelay { get; set; } = 100; // ms
        
        // Advanced
        public string ModelPath { get; set; } = "model.onnx";
        public bool TriggerBot { get; set; } = false;
        public int TriggerCooldown { get; set; } = 150; // ms
        public float TriggerConfidence { get; set; } = 0.6f; // Min Confidence for Trigger
        public int TriggerRange { get; set; } = 30; // Pixel radius
        public int TriggerDelay { get; set; } = 0; // ms before shot
        public bool TriggerAlways { get; set; } = true; // If false, requires Right Click held
        public bool Jitter { get; set; } = false;
        public float JitterAmount { get; set; } = 4.0f;
        public bool MagnetTrigger { get; set; } = false;
        public bool TriggerColorCheck { get; set; } = false;
        public bool TriggerPrediction { get; set; } = false; // Use prediction for trigger?
        public int TriggerBurst { get; set; } = 0; // 0 = Single, 3 = Burst, etc.
        public bool DynamicSensitivity { get; set; } = false; // "Sticky Aim"
        public float DynamicSensScale { get; set; } = 0.5f; // Multiplier when close to target
        
        // Prediction
        public bool PredictionEnabled { get; set; } = false;
        public float PredictionScale { get; set; } = 1.0f;
        
        // Humanization (Curve)
        public bool HumanizeAim { get; set; } = false;
        public float HumanizeStrength { get; set; } = 5.0f; // Magnitude of the sway
        
        // Aim Bone
        public enum BoneType { Head, Neck, Chest, Body }
        public BoneType AimBone { get; set; } = BoneType.Body;
        
        // Visuals
        public bool ShowESP { get; set; } = false;

        // Jump Offset
        public bool JumpOffsetEnabled { get; set; } = false;
        public int JumpOffset { get; set; } = 20;

        // Team Color
        public enum TeamColorType { None, Red, Blue, Purple, Yellow }
        public TeamColorType TeamColor { get; set; } = TeamColorType.None;

        private static string ConfigPath = "config.json";

        public void Save(string profileName = "config.json")
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(profileName, json);
                Console.WriteLine($"Configuration saved to {profileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        public static Config Load(string profileName = "config.json")
        {
            try
            {
                if (File.Exists(profileName))
                {
                    string json = File.ReadAllText(profileName);
                    var cfg = JsonSerializer.Deserialize<Config>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }
            return new Config(); // Default
        }
    }
}
