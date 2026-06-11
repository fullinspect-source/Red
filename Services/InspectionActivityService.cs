using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Logs inspection activity events to local JSONL file for workflow analytics.
    /// Works offline - syncs via Dropbox when connected.
    /// </summary>
    public class InspectionActivityService
    {
        private readonly string _logFilePath;
        private static readonly object _fileLock = new object();
        
        private string? _currentInsFile;
        private string? _currentInspector;
        private string? _currentJobType;
        private bool _firstPhotoTaken;
        
        public InspectionActivityService()
        {
            // Log to Dropbox folder - syncs when online
            var dropboxPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Dropbox", "Inspections", "ActivityLogs");
            
            // Ensure directory exists
            Directory.CreateDirectory(dropboxPath);
            
            _logFilePath = Path.Combine(dropboxPath, "inspection_activity.jsonl");
        }
        
        /// <summary>
        /// Start tracking a new inspection session.
        /// </summary>
        public void StartSession(string insFilePath, string inspectorName, string jobType)
        {
            _currentInsFile = Path.GetFileName(insFilePath);
            _currentInspector = inspectorName;
            _currentJobType = jobType;
            _firstPhotoTaken = false;
            
            LogEvent("loaded");
        }
        
        /// <summary>
        /// Log when a photo is taken/added (only logs first photo per session).
        /// </summary>
        public void LogPhotoTaken()
        {
            if (!_firstPhotoTaken && !string.IsNullOrEmpty(_currentInsFile))
            {
                _firstPhotoTaken = true;
                LogEvent("first_photo");
            }
        }
        
        /// <summary>
        /// Log when the inspection is saved.
        /// </summary>
        public void LogSave()
        {
            if (!string.IsNullOrEmpty(_currentInsFile))
            {
                LogEvent("saved");
            }
        }
        
        /// <summary>
        /// Log when the inspection is closed.
        /// </summary>
        public void LogClose()
        {
            if (!string.IsNullOrEmpty(_currentInsFile))
            {
                LogEvent("closed");
                // Reset session
                _currentInsFile = null;
                _currentInspector = null;
                _currentJobType = null;
                _firstPhotoTaken = false;
            }
        }
        
        /// <summary>
        /// Log when an inspection is reopened (loaded again after being closed).
        /// </summary>
        public void LogReopen(string insFilePath, string inspectorName, string jobType)
        {
            _currentInsFile = Path.GetFileName(insFilePath);
            _currentInspector = inspectorName;
            _currentJobType = jobType;
            
            LogEvent("reopened");
        }
        
        private void LogEvent(string eventType)
        {
            try
            {
                var entry = new ActivityEntry
                {
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    LocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Inspector = _currentInspector ?? "unknown",
                    InsFile = _currentInsFile ?? "unknown",
                    JobType = _currentJobType ?? "unknown",
                    Event = eventType,
                    Machine = Environment.MachineName,
                    MachineId = GetMachineIdSafe()
                };

                var json = JsonSerializer.Serialize(entry);
                
                // Thread-safe append to file
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, json + Environment.NewLine);
                }
            }
            catch
            {
                // Silently fail - logging should never break the app
            }
        }
        
        private string GetMachineIdSafe()
        {
            try
            {
                return LicenseService.GetMachineId();
            }
            catch
            {
                return Environment.MachineName;
            }
        }
        
        private class ActivityEntry
        {
            [JsonPropertyName("timestamp")]
            public string Timestamp { get; set; } = "";
            
            [JsonPropertyName("local_time")]
            public string LocalTime { get; set; } = "";
            
            [JsonPropertyName("inspector")]
            public string Inspector { get; set; } = "";
            
            [JsonPropertyName("ins_file")]
            public string InsFile { get; set; } = "";
            
            [JsonPropertyName("job_type")]
            public string JobType { get; set; } = "";
            
            [JsonPropertyName("event")]
            public string Event { get; set; } = "";
            
            [JsonPropertyName("machine")]
            public string Machine { get; set; } = "";
            
            [JsonPropertyName("machine_id")]
            public string MachineId { get; set; } = "";
        }
    }
}
