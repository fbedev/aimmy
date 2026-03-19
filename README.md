# Aimmy.Mac

Aimmy natively ported to Mac. Contains AI-based targeting components and features natively optimized for macOS.

## Quick Install (One Command)

```bash
git clone https://github.com/fbedev/aimmy.git && cd aimmy/Aimmy.Mac && bash install.sh
```

This will automatically install everything you need (.NET 8, ffmpeg, packages) and build the app.

## Run

```bash
cd aimmy/Aimmy.Mac && dotnet run
```

## Permissions (Required)

You **must** grant these permissions or the app won't work:

1. **System Settings → Privacy & Security → Accessibility** → Add your Terminal app
2. **System Settings → Privacy & Security → Screen Recording** → Add your Terminal app

## Model

Place your `model.onnx` file in the `Aimmy.Mac` folder. You can also put `.onnx` models in a `models/` directory and select them from the in-app dropdown.

## How to Use

When you run the application, an overlay menu will open. You can configure options such as sensitivity, FOV, Triggerbot, and Prediction right from the menu.

### Hotkeys

- **Insert** or **Tab**: Toggle the configuration menu
- **Cmd + Z**: Toggle Aim Assist
- **Cmd + U**: Toggle Recording AI Vision
- **Aim Key**: Configure your custom aim key inside the menu (default is Right Click)

## Configuration

Configuration values are stored in `config.json` (and profile variants like `config_p2.json`). All settings are editable from the in-app UI across four tabs: Aim, Detect, Trigger, and Config.

## Calibration

Aimmy needs to properly align with your screen and game to work perfectly. Inside the Detect tab:

1. **Auto Calibrate Sensitivity**: Uses binary search to calculate your game's mouse speed automatically.
2. **Calibrate Screen Center**: Opens a crosshair overlay — use arrow keys to align with your game's crosshair. Hold Shift for fine (1px) adjustments. Press Enter to save, Escape to cancel, R to reset.
3. **Calibrate to Window**: Select your game window from a list to automatically set the correct center offsets.
4. **Reset Offsets**: Resets all offset values to zero if aim gets misaligned.

## Updating

Use the **Check for Updates** button in the Config tab, or run:

```bash
cd aimmy/Aimmy.Mac && git pull && dotnet build
```

## Excluded Files

Model weights (`.onnx`), generated recordings, and local generated test images are excluded from version control to maintain repository size and privacy.
