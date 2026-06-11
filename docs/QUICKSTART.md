# Quick Start Guide - Inspection Editor

## 🚀 Get Up and Running in 5 Minutes

### Step 1: Install .NET 8.0

If you don't have .NET 8.0 installed:
1. Go to https://dotnet.microsoft.com/download
2. Download ".NET 8.0 SDK" for Windows
3. Run the installer
4. Restart your terminal/command prompt

### Step 2: Get Your Grok API Key

1. Visit https://console.x.ai/
2. Sign up or log in
3. Go to API Keys section
4. Create a new API key
5. Copy it (you'll need this in Step 4)

### Step 3: Build & Run the App

**Option A - Using the Build Script (Easiest):**
1. Open the `InspectionEditor` folder
2. Double-click `build-and-run.bat`
3. Wait for it to build and launch

**Option B - Manual Command Line:**
1. Open Command Prompt or PowerShell
2. Navigate to the project folder:
   ```
   cd path\to\InspectionEditor
   ```
3. Run these commands:
   ```
   dotnet restore
   dotnet build
   dotnet run
   ```

### Step 4: Configure Your API Key

When the app launches:
1. A settings window will appear
2. Paste your Grok API key
3. Click "Save"

### Step 5: Test It Out

1. Click "Open INS File"
2. Select the `sample.ins` file included in the project folder
3. Click on any item in the left panel (like "2.1 Kitchens...")
4. Click "Select Photo" and choose any image
5. Click "Get AI Suggestions"
6. Wait a few seconds
7. Three AI-generated suggestions will appear!
8. Click one to use it
9. Click "Save" to save your changes

## 🎯 That's It!

You now have a working inspection editor with AI-powered suggestions.

## Next Steps

- Try it with real .INS files from INSPECT 2022
- Experiment with different photos to see AI suggestions
- Customize the workflow for your team

## Troubleshooting

**App won't build?**
- Make sure you installed .NET 8.0 SDK (not just Runtime)
- Try running `dotnet --version` to confirm installation

**API suggestions not working?**
- Check your internet connection
- Verify your API key is correct in Settings
- Make sure you have credits on your Grok account

**Sample file won't open?**
- Make sure `sample.ins` is in the same folder as the app

---

Need help? Check the full README.md for detailed documentation.
