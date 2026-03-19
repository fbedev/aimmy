# Aimmy.Mac

Aimmy natively ported to Mac. Contains AI-based targeting components and features natively optimized for macOS.

## Setup

1. Make sure you have the .NET 8.0 SDK installed.
2. Build the project using `dotnet build`.
3. Run with `dotnet run`.

## How to Use

When you run the application, an overlay menu will open. You can configure options such as sensitivity, FOV, Triggerbot, and Prediction right from the menu.

### Hotkeys

- **Insert** or **Tab**: Toggle the configuration menu
- **Cmd + Z**: Toggle Aim Assist
- **Cmd + U**: Toggle Recording AI Vision
- **Aim Key**: Configure your custom aim key inside the menu (default is often Left/Right Click or unassigned).

## Configuration

Configuration values are stored in `config.json` (and `config_p1` to `config_p5`). Ensure `model.onnx` is available in the expected path or place your custom `.onnx` models in a `models/` or `../models/` directory for the model selector to find them.

## Calibration Options

Aimmy needs to properly align with your screen and game to work perfectly. Inside the menu, you will see a few calibration tools:

1. **Auto Calibrate Sensitivity**: Calculates your game's mouse speed. When clicked, Aimmy will pause temporarily so you can follow the on-screen steps.
2. **Calibrate Screen Center**: Clicking this creates a visual overlay to ensure your center offsets (where it aims) align perfectly with your screen's crosshair. Useful if you're not playing full screen.
3. **Calibrate to Window (Smart)**: This option asks you to select your game from a list of open windows. It will then automatically calculate boundaries so the AI looks only at your specific game's content area (fixing issues where aim is shifted by titlebars).
4. **Reset Offsets (Fix Aim)**: If your aim starts randomly snapping to the floor or sky, click this to reset any bad offset calculations.

## Excluded Files

Model weights (`.onnx`), generated recordings, and local generated test images are excluded from version control to maintain repository size and privacy.
