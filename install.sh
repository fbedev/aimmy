#!/bin/bash
set -e

echo "=============================="
echo "  Aimmy Mac - Easy Installer"
echo "=============================="
echo ""

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

ok()   { echo -e "${GREEN}[OK]${NC} $1"; }
warn() { echo -e "${YELLOW}[!]${NC} $1"; }
fail() { echo -e "${RED}[X]${NC} $1"; exit 1; }

# 1. Check macOS
if [[ "$(uname)" != "Darwin" ]]; then
    fail "This installer is for macOS only."
fi
ok "macOS detected"

# 2. Check/install Homebrew
if ! command -v brew &>/dev/null; then
    warn "Homebrew not found. Installing..."
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    # Add to path for Apple Silicon
    if [[ -f /opt/homebrew/bin/brew ]]; then
        eval "$(/opt/homebrew/bin/brew shellenv)"
    fi
    ok "Homebrew installed"
else
    ok "Homebrew found"
fi

# 3. Check/install .NET 8 SDK
if command -v dotnet &>/dev/null && dotnet --list-sdks 2>/dev/null | grep -q "^8\."; then
    ok ".NET 8 SDK found"
else
    warn ".NET 8 SDK not found. Installing..."
    brew install dotnet@8
    # Link if needed
    if ! command -v dotnet &>/dev/null; then
        brew link dotnet@8 --force 2>/dev/null || true
        export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
        export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
    fi
    if ! command -v dotnet &>/dev/null; then
        fail "dotnet still not found after install. Add it to your PATH manually."
    fi
    ok ".NET 8 SDK installed"
fi

# 4. Optional: ffmpeg for recording
if command -v ffmpeg &>/dev/null; then
    ok "ffmpeg found (recording → video enabled)"
else
    warn "ffmpeg not found. Installing (optional, for video recording)..."
    brew install ffmpeg 2>/dev/null || warn "ffmpeg install failed — recording will save frames only"
fi

# 5. Restore NuGet packages
echo ""
echo "Restoring packages..."
cd "$(dirname "$0")"
dotnet restore
ok "Packages restored"

# 6. Build
echo ""
echo "Building..."
dotnet build -c Release --no-restore
ok "Build complete"

# 7. Grant accessibility (reminder)
echo ""
echo "=============================="
echo -e "${GREEN}  Installation Complete!${NC}"
echo "=============================="
echo ""
echo "To run:"
echo "  cd $(pwd)"
echo "  dotnet run"
echo ""
echo -e "${YELLOW}IMPORTANT:${NC} Grant accessibility permissions:"
echo "  System Settings → Privacy & Security → Accessibility"
echo "  Add your Terminal app (Terminal.app, iTerm, etc.)"
echo ""
echo -e "${YELLOW}IMPORTANT:${NC} Grant screen recording permissions:"
echo "  System Settings → Privacy & Security → Screen Recording"
echo "  Add your Terminal app"
echo ""
echo "Place your model.onnx in this folder before running."
echo ""
