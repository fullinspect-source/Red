# Inspection Editor

A lightweight, modern Windows application for editing inspection form (.INS) files with AI-powered text suggestions using Grok API.

## Overview

This application provides a clean interface for field inspectors to:
- Open and edit .INS inspection files
- Capture or select photos for inspection items
- Get AI-generated text suggestions based on photos
- Save completed inspections back to .INS format

The app works alongside your existing INSPECT 2022 software:
1. **INSPECT 2022** creates the blank .INS file
2. **Inspection Editor** provides easy data entry with AI assistance
3. **INSPECT 2022** processes the completed file

## Features

✅ **Lightweight & Fast** - Built with WPF for minimal memory usage and stability
✅ **AI-Powered Suggestions** - Grok API analyzes photos and suggests 3 inspection note options
✅ **Simple Workflow** - Select item → Add photo → Get suggestions → Tap one → Done
✅ **Photo Management** - Attach photos directly to inspection items
✅ **Preserves Format** - Maintains all existing .INS file structure and metadata

## Requirements

- Windows 10 or later
- .NET 8.0 Runtime (will be bundled in release build)
- Grok API key from https://console.x.ai/

## Installation

### Option 1: Build from Source

1. Install .NET 8.0 SDK from https://dotnet.microsoft.com/download
2. Open terminal in project folder
3. Run: `dotnet restore`
4. Run: `dotnet build`
5. Run: `dotnet run`

### Option 2: Published Executable (Coming Soon)

Self-contained executable will be available for download.

## Setup

1. Launch the application
2. On first run, you'll be prompted to enter your Grok API key
3. Get your API key from https://console.x.ai/
4. Enter the key and click "Save"

You can change your API key anytime via the ⚙️ Settings button.

## Usage

### Opening an Inspection File

1. Click **"Open INS File"** button
2. Navigate to your .INS file (created by INSPECT 2022)
3. The file will load and display all sections/items in the left panel

### Editing an Item

1. Click on any item in the left tree view
2. The right panel shows the item details and question

### Adding a Photo & Getting AI Suggestions

1. Click **"Select Photo"** to choose an image file
   - Or click **"Capture from Camera"** (feature coming soon)
2. Once photo is loaded, click **"Get AI Suggestions"**
3. Wait a few seconds while Grok analyzes the image
4. Three suggested text options will appear
5. Click on one to use it, or click **"Try Again"** for 3 more options
6. The selected text appears in the Comments box
7. Photo and comment are automatically saved to the item

### Saving Your Work

- Click **"Save"** button to write changes back to the .INS file
- INSPECT 2022 can now process the completed file normally

## How the AI Works

When you click "Get AI Suggestions":

1. The app sends to Grok:
   - The photo you captured/selected
   - The item number (e.g., "3.2")
   - The inspection question (e.g., "Are beams free of loose soil and debris?")

2. Grok analyzes the image in context and returns 3 professional inspection notes

3. Each suggestion is:
   - Brief (1-2 sentences)
   - Professional and objective
   - Specific to what's visible in the photo
   - Suitable for an inspection report

## Tips

- 💡 The AI suggestions are starting points - you can always edit the text manually
- 💡 "Try Again" button gets 3 completely new suggestions if you don't like the first set
- 💡 You can type or edit text directly in the Comments box anytime
- 💡 Photos are embedded as base64 in the .INS file - no separate image files needed

## Troubleshooting

### API Key Issues
- Make sure your Grok API key is valid and has credits
- Check https://console.x.ai/ for your API status
- Update the key via ⚙️ Settings if needed

### File Won't Open
- Ensure the file is a valid .INS JSON file
- Check that INSPECT 2022 created the file correctly

### AI Suggestions Not Working
- Verify you have an active internet connection
- Ensure your Grok API key has available credits
- Check the photo file is a valid image (JPG, PNG)

## Future iOS Version

This codebase is designed to be portable. Future development will include:
- Native iOS app using .NET MAUI or Swift
- Camera integration optimized for mobile
- Cloud sync between Windows and iOS versions
- Offline mode with sync when back online

## Technical Details

**Built with:**
- .NET 8.0 + WPF
- Newtonsoft.Json for JSON parsing
- Grok Vision API for image analysis

**File Format:**
- Reads/writes standard .INS JSON format
- Preserves all metadata and structure
- Compatible with INSPECT 2022

## Support

For issues or questions:
- Check the troubleshooting section above
- Email: [Your support email]
- Create an issue on GitHub

## License

[Your license here]

---

Made with ❤️ for field inspectors who deserve better tools.
