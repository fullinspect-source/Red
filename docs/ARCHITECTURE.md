# Project Architecture - Inspection Editor

## Overview

A lightweight Windows desktop application that integrates with INSPECT 2022 workflow, providing AI-assisted inspection data entry.

## Technology Stack

### Core Framework
- **.NET 8.0** - Latest LTS version, excellent performance
- **WPF (Windows Presentation Foundation)** - Mature, stable UI framework
- **C#** - Type-safe, modern language features

### Key Libraries
- **Newtonsoft.Json** - Robust JSON serialization/deserialization
- **Grok Vision API** - AI-powered image analysis

## Architecture

```
InspectionEditor/
├── Models/
│   └── InspectionModels.cs       # Data models for .INS file structure
├── Services/
│   └── GrokApiClient.cs          # Grok API integration
├── MainWindow.xaml/.cs           # Main application window
├── SettingsWindow.xaml/.cs       # API key configuration
├── App.xaml/.cs                  # Application entry point
└── InspectionEditor.csproj       # Project configuration
```

## Key Components

### 1. Data Models (`InspectionModels.cs`)

Represents the .INS file structure:
- **InspectionFile** - Root object with metadata
- **Section** - Groups of related items
- **Item** - Individual inspection points (questions)
- **Picture** - Photo with base64 data and metadata

**Design Decision:** Uses `[JsonExtensionData]` to preserve unknown properties, ensuring we don't lose any data when round-tripping files.

### 2. Grok API Client (`GrokApiClient.cs`)

Handles communication with Grok Vision API:
- Converts images to base64
- Constructs prompts with context (item number + question)
- Parses AI responses into 3 suggestions
- Provides fallback responses on error

**Key Features:**
- Async/await for non-blocking API calls
- Error handling with graceful degradation
- Structured prompt engineering for consistent results

### 3. Main Window (`MainWindow.xaml/.cs`)

Primary UI with three main sections:

**Left Panel:** TreeView navigation
- Displays sections and items
- Only shows items that can have pictures/comments
- Expandable tree structure

**Right Panel:** Item editor
- Shows current item details
- Photo capture/selection
- AI suggestion display
- Comments/notes editing

**Top Bar:** Actions
- File operations (Open, Save)
- Settings access
- Status display

### 4. Settings Window (`SettingsWindow.xaml/.cs`)

Simple configuration dialog:
- API key input
- Persistent storage (settings.txt)
- Validation

## Data Flow

### Opening a File
```
User clicks "Open" 
→ File dialog
→ Read JSON from .INS file
→ Deserialize to InspectionFile object
→ Populate TreeView
→ Enable Save button
```

### AI Suggestion Workflow
```
User selects photo
→ Enable "Get AI Suggestions"
→ User clicks button
→ Show loading state
→ Send to Grok API (image + context)
→ Parse 3 suggestions
→ Display as clickable buttons
→ User selects one
→ Update item's Comments and Pictures
→ User saves file
```

### Saving
```
User clicks "Save"
→ Serialize InspectionFile to JSON
→ Write to original .INS file path
→ Preserve all original structure
```

## Key Design Decisions

### 1. Why WPF Instead of WinForms?
- Better data binding support
- Modern XAML-based UI
- Easier styling and theming
- Better scaling/DPI support
- Path to .NET MAUI for cross-platform

### 2. Why Preserve Unknown JSON Properties?
- INSPECT 2022 may add new fields
- Different inspection types may have unique fields
- Safer to preserve everything than risk data loss
- Using `[JsonExtensionData]` is elegant solution

### 3. Why Base64 for Images?
- Already used in .INS format
- Self-contained files (no external image references)
- Simpler file management
- Easy to embed and extract

### 4. Why Separate Settings Window?
- Security: API keys shouldn't be in code
- Flexibility: Easy to update without restart
- Simple persistence with text file

### 5. Why Three Suggestions?
- Not too few (limited choice)
- Not too many (decision paralysis)
- "Try Again" provides more if needed
- Balanced UX for field inspectors

## Memory & Performance

### Optimization Strategies

1. **Lazy Loading**
   - Only display relevant items in tree
   - Don't load all images at once
   - Parse JSON only when needed

2. **Image Handling**
   - Use BitmapImage.Freeze() for memory efficiency
   - Dispose streams properly
   - Cache only current item's photo

3. **API Calls**
   - Async/await prevents UI blocking
   - Single request per suggestion set
   - Timeout handling for slow networks

### Memory Footprint
- Base app: ~50-80 MB
- Per open inspection: ~5-15 MB
- Per loaded image: ~1-5 MB (varies by size)

Much lighter than INSPECT 2022!

## Security Considerations

1. **API Key Storage**
   - Stored in local text file
   - Should be encrypted in production version
   - File permissions should be set appropriately

2. **Image Data**
   - No server-side storage
   - Images go direct to Grok API
   - Privacy-conscious design

3. **File Validation**
   - Should add JSON schema validation
   - Verify file integrity before loading
   - Sanitize user inputs

## Future Enhancements

### Short Term
- [ ] Camera capture integration
- [ ] Batch processing multiple items
- [ ] Custom suggestion templates
- [ ] Offline mode with queue

### Medium Term
- [ ] Cloud sync capability
- [ ] Team collaboration features
- [ ] Custom AI model training
- [ ] Advanced photo editing

### Long Term
- [ ] iOS version (.NET MAUI or Swift)
- [ ] Real-time sync between devices
- [ ] Voice-to-text for notes
- [ ] OCR for serial numbers

## iOS Migration Path

### Approach 1: .NET MAUI
**Pros:**
- Share 90%+ of code
- Same models and business logic
- Consistent UX patterns

**Cons:**
- MAUI still maturing
- Some platform-specific UI needed
- Larger app size

### Approach 2: Native Swift
**Pros:**
- Best iOS performance
- Native look and feel
- Smaller app size

**Cons:**
- Complete rewrite
- Maintain two codebases
- Different skill set needed

**Recommendation:** Start with .NET MAUI, evaluate after initial release.

## Testing Strategy

### Unit Tests (Recommended)
- Models serialization/deserialization
- Grok API client (with mocking)
- Settings persistence

### Integration Tests
- Full file load/save cycle
- API integration (with test account)
- UI workflow automation

### Manual Testing Checklist
- [ ] Open various .INS file formats
- [ ] Test with different image sizes
- [ ] API error handling
- [ ] Save/reload verification
- [ ] Settings persistence

## Deployment

### Development Build
```bash
dotnet build --configuration Debug
dotnet run
```

### Release Build
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Creates self-contained executable with all dependencies.

### Installer (Future)
- Use WiX Toolset or Inno Setup
- Include .NET runtime
- Register file associations (.ins)
- Add Start Menu shortcuts

## Contributing

When adding features:
1. Follow existing code patterns
2. Update this documentation
3. Add error handling
4. Test with real .INS files
5. Consider iOS portability

## Support Contacts

- Development: [Your contact]
- API Issues: https://console.x.ai/support
- Bug Reports: [Your issue tracker]

---

Last Updated: January 2026
Version: 1.0 Beta
