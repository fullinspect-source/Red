using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public class LicenseInfo
    {
        public string MachineId { get; set; } = "";
        public string ExpirationDate { get; set; } = "";
        public string IssuedTo { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    public class LicenseValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; } = "";
        public int DaysRemaining { get; set; }
        public string MachineId { get; set; } = "";
        public string IssuedTo { get; set; } = "";
        public bool WasTampered { get; set; }
    }

    public static class LicenseService
    {
        // Public key for signature verification (base64 encoded)
        // The private key stays with Trent for generating licenses
        private const string PublicKey = "REDLicensePublicKey2026";
        
        private static readonly string LicenseFileName = "license.lic";
        private static readonly string TimestampFileName = ".red_ts";
        private static readonly string TamperFlagFileName = ".red_x";

        /// <summary>
        /// Generates a unique machine fingerprint based on hardware IDs
        /// </summary>
        public static string GetMachineId()
        {
            try
            {
                var sb = new StringBuilder();
                
                // Get CPU ID
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (var item in searcher.Get())
                    {
                        sb.Append(item["ProcessorId"]?.ToString() ?? "");
                        break;
                    }
                }

                // Get disk serial number
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0"))
                {
                    foreach (var item in searcher.Get())
                    {
                        sb.Append(item["SerialNumber"]?.ToString()?.Trim() ?? "");
                        break;
                    }
                }

                // Get motherboard serial
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                {
                    foreach (var item in searcher.Get())
                    {
                        sb.Append(item["SerialNumber"]?.ToString()?.Trim() ?? "");
                        break;
                    }
                }

                // Hash the combined hardware info
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    // Return first 16 chars in groups of 4 for readability
                    var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
                    return $"{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
                }
            }
            catch
            {
                // Fallback: use machine name + user name hash
                var fallback = Environment.MachineName + Environment.UserName;
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(fallback));
                    var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
                    return $"{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
                }
            }
        }

        /// <summary>
        /// Gets the path where license file should be stored
        /// </summary>
        public static string GetLicenseFilePath()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appDir, LicenseFileName);
        }

        /// <summary>
        /// Gets the path for the hidden timestamp file (clock tampering detection)
        /// </summary>
        private static string GetTimestampFilePath()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.AppDataFolderName);
            Directory.CreateDirectory(appDataDir);
            return Path.Combine(appDataDir, TimestampFileName);
        }

        /// <summary>
        /// Gets the path for the tamper flag file
        /// </summary>
        private static string GetTamperFlagPath()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.AppDataFolderName);
            Directory.CreateDirectory(appDataDir);
            return Path.Combine(appDataDir, TamperFlagFileName);
        }

        /// <summary>
        /// Checks if app was previously flagged as tampered
        /// </summary>
        private static bool WasPreviouslyTampered()
        {
            return File.Exists(GetTamperFlagPath());
        }

        /// <summary>
        /// Self-destruct: Delete critical application files when tampering is detected
        /// </summary>
        private static void SelfDestruct()
        {
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                
                // Mark as tampered (persists even after reinstall attempt to same location)
                var tamperFlag = GetTamperFlagPath();
                File.WriteAllText(tamperFlag, DateTime.Now.ToString("o"));
                File.SetAttributes(tamperFlag, FileAttributes.Hidden | FileAttributes.System);

                // Schedule deletion of the executable after app exits
                // Using a batch file that waits then deletes
                var batchPath = Path.Combine(Path.GetTempPath(), $"cleanup_{Guid.NewGuid():N}.bat");
                var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "InspectionEditor.exe");
                
                var batchContent = $@"
@echo off
ping 127.0.0.1 -n 3 > nul
del /f /q ""{exePath}"" 2>nul
del /f /q ""{Path.Combine(appDir, "*.dll")}"" 2>nul
rmdir /s /q ""{Path.Combine(appDir, "runtimes")}"" 2>nul
del /f /q ""%~f0""
";
                File.WriteAllText(batchPath, batchContent);
                
                // Start the cleanup batch hidden
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = batchPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // Best effort - if we can't delete, at least we flagged it
            }
        }

        /// <summary>
        /// Tries to get the current date from the internet (for clock tampering detection)
        /// </summary>
        private static DateTime? GetNetworkTime()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                // Use a simple HEAD request to get server date header
                var request = new HttpRequestMessage(HttpMethod.Head, "https://www.google.com");
                var response = client.Send(request);
                
                if (response.Headers.Date.HasValue)
                {
                    return response.Headers.Date.Value.DateTime;
                }
            }
            catch
            {
                // Network unavailable - fall back to local checks
            }
            return null;
        }

        /// <summary>
        /// Checks if the system clock appears to have been tampered with
        /// </summary>
        private static bool IsClockTampered(out string reason)
        {
            reason = "";
            var now = DateTime.Now;
            var today = DateTime.Today;
            
            // Check 1: Try to get network time
            var networkTime = GetNetworkTime();
            if (networkTime.HasValue)
            {
                var drift = Math.Abs((networkTime.Value - now).TotalHours);
                if (drift > 24) // More than 24 hours off from network time
                {
                    reason = "System clock appears to be incorrect.";
                    return true;
                }
            }

            // Check 2: Compare against last known good date
            var timestampPath = GetTimestampFilePath();
            if (File.Exists(timestampPath))
            {
                try
                {
                    var content = File.ReadAllText(timestampPath);
                    if (DateTime.TryParse(content, out var lastRunDate))
                    {
                        // If current date is more than 1 day BEFORE last run, clock was rolled back
                        if (today < lastRunDate.AddDays(-1))
                        {
                            reason = "System clock appears to have been set backwards.";
                            return true;
                        }
                    }
                }
                catch
                {
                    // Ignore errors reading timestamp
                }
            }

            // Check 3: Look at recent file modifications on the system
            try
            {
                // Check Windows directory - files there are frequently updated
                var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var tempDir = Path.Combine(windowsDir, "Temp");
                if (Directory.Exists(tempDir))
                {
                    var recentFiles = Directory.GetFiles(tempDir, "*", SearchOption.TopDirectoryOnly);
                    foreach (var file in recentFiles.Take(10))
                    {
                        var fileTime = File.GetLastWriteTime(file);
                        // If files were modified "in the future", clock is wrong
                        if (fileTime > now.AddHours(1))
                        {
                            reason = "System clock appears to be incorrect.";
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors checking file times
            }

            return false;
        }

        /// <summary>
        /// Records the current date for future tampering detection
        /// </summary>
        private static void RecordValidationTimestamp()
        {
            try
            {
                var timestampPath = GetTimestampFilePath();
                File.WriteAllText(timestampPath, DateTime.Today.ToString("yyyy-MM-dd"));
                
                // Make the file hidden
                File.SetAttributes(timestampPath, FileAttributes.Hidden);
            }
            catch
            {
                // Ignore errors - non-critical
            }
        }

        /// <summary>
        /// Validates the license and returns detailed result
        /// </summary>
        public static LicenseValidationResult ValidateLicense()
        {
            var machineId = GetMachineId();
            var result = new LicenseValidationResult { MachineId = machineId };

            // Check if previously flagged as tampered
            if (WasPreviouslyTampered())
            {
                result.IsValid = false;
                result.WasTampered = true;
                result.Message = "This installation has been disabled. Please contact your administrator.";
                return result;
            }

            // Check for clock tampering
            if (IsClockTampered(out var tamperReason))
            {
                result.IsValid = false;
                result.WasTampered = true;
                result.Message = $"License validation failed: {tamperReason}";
                
                // SELF-DESTRUCT: Tampering detected
                SelfDestruct();
                
                return result;
            }

            var licensePath = GetLicenseFilePath();
            
            // Also check in app data folder as fallback
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.AppDataFolderName, LicenseFileName);
            var legacyAppDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RED", LicenseFileName);

            string? licenseContent = null;
            
            if (File.Exists(licensePath))
            {
                licenseContent = File.ReadAllText(licensePath);
            }
            else if (File.Exists(appDataPath))
            {
                licenseContent = File.ReadAllText(appDataPath);
            }
            else if (AppIdentity.IsDevBuild && File.Exists(legacyAppDataPath))
            {
                licenseContent = File.ReadAllText(legacyAppDataPath);
            }

            if (string.IsNullOrEmpty(licenseContent))
            {
                result.IsValid = false;
                result.Message = "No license file found. Please activate RED.";
                return result;
            }

            try
            {
                var license = JsonSerializer.Deserialize<LicenseInfo>(licenseContent);
                
                if (license == null)
                {
                    result.IsValid = false;
                    result.Message = "Invalid license file format.";
                    return result;
                }

                // Verify signature
                if (!VerifySignature(license))
                {
                    result.IsValid = false;
                    result.Message = "License signature is invalid.";
                    return result;
                }

                // Check machine ID
                if (!string.Equals(license.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsValid = false;
                    result.Message = "License is not valid for this machine.";
                    return result;
                }

                // Check expiration
                if (!DateTime.TryParse(license.ExpirationDate, out var expDate))
                {
                    result.IsValid = false;
                    result.Message = "Invalid expiration date in license.";
                    return result;
                }

                // Enforce maximum license duration of 90 days
                // If license spans more than 90 days from today, it's unauthorized
                var daysRemaining = (expDate.Date - DateTime.Today).Days;
                if (daysRemaining > 90)
                {
                    result.IsValid = false;
                    result.Message = "License duration exceeds maximum allowed (90 days). Please contact your administrator for a valid license.";
                    return result;
                }

                result.DaysRemaining = daysRemaining;
                result.IssuedTo = license.IssuedTo;

                if (daysRemaining < 0)
                {
                    result.IsValid = false;
                    result.Message = $"License expired on {expDate:MMMM d, yyyy}.";
                    return result;
                }

                // License is valid - record timestamp for future tampering detection
                RecordValidationTimestamp();

                result.IsValid = true;
                if (daysRemaining <= 7)
                {
                    result.Message = $"License expires in {daysRemaining} day{(daysRemaining == 1 ? "" : "s")}.";
                }
                else
                {
                    result.Message = $"License valid until {expDate:MMMM d, yyyy}.";
                }

                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Message = $"Error reading license: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Verifies the signature on a license
        /// </summary>
        private static bool VerifySignature(LicenseInfo license)
        {
            // Create the data string that was signed
            var dataToVerify = $"{license.MachineId}|{license.ExpirationDate}|{license.IssuedTo}|{PublicKey}";
            
            using (var sha256 = SHA256.Create())
            {
                var expectedHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(dataToVerify));
                var expectedSignature = Convert.ToBase64String(expectedHash);
                
                return string.Equals(license.Signature, expectedSignature, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Checks if we're in grace period (for showing warnings)
        /// </summary>
        public static bool IsInGracePeriod(int daysRemaining)
        {
            return daysRemaining >= 0 && daysRemaining <= 7;
        }
    }
}
