#!/bin/bash
# ============================================
# RED Release Publisher
# Builds, zips, and uploads to GitHub Releases
# Usage: ./publish-release.sh
# ============================================

set -e

REPO="fullinspect-source/Red"
PROJECT_DIR="/Users/trentfuller/Library/CloudStorage/Dropbox/P/PL"
BUILD_OUTPUT="$PROJECT_DIR/bin/Release/net8.0-windows10.0.19041.0"
PUBLISH_OUTPUT="$PROJECT_DIR/bin/Publish"
BAT_PATH="$PROJECT_DIR/scripts/update_red.bat"

# Get version from the project metadata used by the compiled app.
VERSION=$(grep -m1 '<Version>' "$PROJECT_DIR/InspectionEditor.csproj" | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/')
if [ -z "$VERSION" ]; then
    echo "❌ Could not detect version from InspectionEditor.csproj"
    exit 1
fi

TAG="v$VERSION"
ZIP_NAME="Red-$TAG.zip"
ZIP_PATH="/tmp/$ZIP_NAME"

echo ""
echo "🔴 RED Release Publisher"
echo "========================"
echo "  Version:  $VERSION"
echo "  Tag:      $TAG"
echo "  Repo:     $REPO"
echo ""

# Check if this version already exists
if gh release view "$TAG" --repo "$REPO" &>/dev/null; then
    echo "⚠️  Release $TAG already exists!"
    read -p "   Overwrite? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Aborted."
        exit 0
    fi
    echo "   Deleting existing release..."
    gh release delete "$TAG" --repo "$REPO" --yes 2>/dev/null || true
    gh api repos/$REPO/git/refs/tags/$TAG -X DELETE 2>/dev/null || true
fi

# Build the project
echo "🔨 Building Red..."
cd "$PROJECT_DIR"
dotnet clean -c Release -r win-x64 > /dev/null 2>&1
rm -rf "$PUBLISH_OUTPUT"
dotnet publish -c Release -r win-x64 --self-contained true -o "$PUBLISH_OUTPUT" 2>&1 | grep -E "error|warning|Error|Warning|Build succeeded|Build FAILED" | grep -v "NU1903\|NU1902" | tail -10

if [ ! -f "$PUBLISH_OUTPUT/Red.exe" ]; then
    echo "❌ Build failed - Red.exe not found in $PUBLISH_OUTPUT"
    exit 1
fi

echo "✅ Build complete"

# Create version.txt in publish folder
echo "$VERSION" > "$PUBLISH_OUTPUT/version.txt"

# Zip it up
echo "📦 Creating $ZIP_NAME..."
rm -f "$ZIP_PATH"
cd "$PUBLISH_OUTPUT"
zip -r -q "$ZIP_PATH" . -x "*.pdb" "*.xml"

SIZE=$(du -h "$ZIP_PATH" | cut -f1)
echo "✅ Zip created ($SIZE)"

# Ensure the .bat file has CRLF line endings (edited on macOS = LF only, which breaks cmd.exe)
CRLF_BAT="/tmp/update_red.bat"
perl -pe 's/\r?\n/\r\n/' "$BAT_PATH" > "$CRLF_BAT"

# Upload to GitHub
echo "🚀 Uploading to GitHub..."
gh release create "$TAG" "$ZIP_PATH" "$CRLF_BAT" \
    --repo "$REPO" \
    --title "Red $TAG" \
    --notes "Red v$VERSION release" \
    --latest

echo ""
echo "✅ Release $TAG published!"
echo "   https://github.com/$REPO/releases/tag/$TAG"
echo ""
echo "📱 Inspectors will get this update automatically next time they launch Red."
