using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Manages user-specific data: saved comments, prefixes, suffixes, and templates.
    /// Each user has their own storage folder based on Windows username.
    /// </summary>
    public class UserDataService
    {
        private readonly string _userDataPath;
        private readonly string _userName;
        
        // Default trade prefixes (alphabetical) - using brackets
        public static List<string> DefaultPrefixes = new List<string>
        {
            "[builder]",
            "[cabinet]",
            "[concrete]",
            "[drywall]",
            "[electrician]",
            "[flooring]",
            "[framer]",
            "[hvac]",
            "[insulation]",
            "[landscaping]",
            "[mason]",
            "[painter]",
            "[plumber]",
            "[roofer]",
            "[siding]",
            "[tile]",
            "[window]"
        };

        // Default location suffixes (alphabetical) - using parentheses
        public static List<string> DefaultSuffixes = new List<string>
        {
            "(back)",
            "(downstairs)",
            "(front)",
            "(left)",
            "(located per plan)",
            "(rear)",
            "(right)",
            "(several places)",
            "(throughout)",
            "(upstairs)"
        };

        public UserDataService(string basePath)
        {
            // Get Windows username for per-user storage
            _userName = Environment.UserName ?? "default";
            _userDataPath = Path.Combine(basePath, "userdata", _userName);
            EnsureUserDataFolder();
            LoadCustomPrefixesSuffixes();
        }

        public string UserName => _userName;

        private void EnsureUserDataFolder()
        {
            if (!Directory.Exists(_userDataPath))
            {
                Directory.CreateDirectory(_userDataPath);
            }
        }

        private void LoadCustomPrefixesSuffixes()
        {
            // Load custom prefixes if file exists
            string prefixFile = Path.Combine(_userDataPath, "custom_prefixes.json");
            if (File.Exists(prefixFile))
            {
                try
                {
                    var custom = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(prefixFile));
                    if (custom != null)
                    {
                        foreach (var p in custom)
                        {
                            if (!DefaultPrefixes.Contains(p))
                            {
                                DefaultPrefixes.Add(p);
                            }
                        }
                        DefaultPrefixes = DefaultPrefixes.OrderBy(x => x).ToList();
                    }
                }
                catch { }
            }

            // Load custom suffixes if file exists
            string suffixFile = Path.Combine(_userDataPath, "custom_suffixes.json");
            if (File.Exists(suffixFile))
            {
                try
                {
                    var custom = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(suffixFile));
                    if (custom != null)
                    {
                        foreach (var s in custom)
                        {
                            if (!DefaultSuffixes.Contains(s))
                            {
                                DefaultSuffixes.Add(s);
                            }
                        }
                        DefaultSuffixes = DefaultSuffixes.OrderBy(x => x).ToList();
                    }
                }
                catch { }
            }
        }

        #region Custom Prefix/Suffix

        public void AddCustomPrefix(string prefix)
        {
            // Ensure brackets
            if (!prefix.StartsWith("[")) prefix = "[" + prefix;
            if (!prefix.EndsWith("]")) prefix = prefix + "]";
            prefix = prefix.ToLower();

            if (!DefaultPrefixes.Contains(prefix))
            {
                DefaultPrefixes.Add(prefix);
                DefaultPrefixes = DefaultPrefixes.OrderBy(x => x).ToList();
                SaveCustomPrefixes();
            }
        }

        public void AddCustomSuffix(string suffix)
        {
            // Ensure parentheses
            if (!suffix.StartsWith("(")) suffix = "(" + suffix;
            if (!suffix.EndsWith(")")) suffix = suffix + ")";
            suffix = suffix.ToLower();

            if (!DefaultSuffixes.Contains(suffix))
            {
                DefaultSuffixes.Add(suffix);
                DefaultSuffixes = DefaultSuffixes.OrderBy(x => x).ToList();
                SaveCustomSuffixes();
            }
        }

        public bool RemoveCustomPrefix(string prefix)
        {
            if (!prefix.StartsWith("[")) prefix = "[" + prefix;
            if (!prefix.EndsWith("]")) prefix = prefix + "]";
            prefix = prefix.ToLower();

            if (DefaultPrefixes.Remove(prefix))
            {
                SaveCustomPrefixes();
                return true;
            }
            return false;
        }

        public bool RemoveCustomSuffix(string suffix)
        {
            if (!suffix.StartsWith("(")) suffix = "(" + suffix;
            if (!suffix.EndsWith(")")) suffix = suffix + ")";
            suffix = suffix.ToLower();

            if (DefaultSuffixes.Remove(suffix))
            {
                SaveCustomSuffixes();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a prefix is a custom (user-added) one vs built-in.
        /// </summary>
        public bool IsCustomPrefix(string prefix)
        {
            var original = new List<string> { "[builder]", "[cabinet]", "[concrete]", "[drywall]", "[electrician]", "[flooring]", "[framer]", "[hvac]", "[insulation]", "[landscaping]", "[mason]", "[painter]", "[plumber]", "[roofer]", "[siding]", "[tile]", "[window]" };
            return !original.Contains(prefix.ToLower());
        }

        public bool IsCustomSuffix(string suffix)
        {
            var original = new List<string> { "(autofail)", "(back)", "(downstairs)", "(front)", "(left)", "(located per plan)", "(no progress)", "(rear)", "(right)", "(several places)", "(throughout)", "(upstairs)" };
            return !original.Contains(suffix.ToLower());
        }

        private void SaveCustomPrefixes()
        {
            // Save only non-default prefixes
            var original = new List<string> { "[builder]", "[cabinet]", "[concrete]", "[drywall]", "[electrician]", "[flooring]", "[framer]", "[hvac]", "[insulation]", "[landscaping]", "[mason]", "[painter]", "[plumber]", "[roofer]", "[siding]", "[tile]", "[window]" };
            var custom = DefaultPrefixes.Where(p => !original.Contains(p)).ToList();
            
            string filePath = Path.Combine(_userDataPath, "custom_prefixes.json");
            File.WriteAllText(filePath, JsonConvert.SerializeObject(custom, Formatting.Indented));
        }

        private void SaveCustomSuffixes()
        {
            var original = new List<string> { "(back)", "(downstairs)", "(front)", "(left)", "(located per plan)", "(rear)", "(right)", "(several places)", "(throughout)", "(upstairs)" };
            var custom = DefaultSuffixes.Where(s => !original.Contains(s)).ToList();
            
            string filePath = Path.Combine(_userDataPath, "custom_suffixes.json");
            File.WriteAllText(filePath, JsonConvert.SerializeObject(custom, Formatting.Indented));
        }

        #endregion

        #region Saved Comments

        public List<string> GetSavedComments(string inspectionCode, string itemNumber)
        {
            var allComments = LoadCommentsFile(inspectionCode);
            if (allComments.TryGetValue(itemNumber, out var comments))
            {
                return comments;
            }
            return new List<string>();
        }

        public void SaveComment(string inspectionCode, string itemNumber, string comment)
        {
            var allComments = LoadCommentsFile(inspectionCode);
            
            // Keep the full comment including trade prefix (but strip location suffixes)
            // This allows saved comments to remember which trade they belong to
            string prefix = ExtractPrefix(comment);
            string coreComment = StripPrefixAndSuffix(comment);
            
            if (string.IsNullOrWhiteSpace(coreComment))
                return;
            
            // Save with prefix if it had one
            string commentToSave = string.IsNullOrEmpty(prefix) 
                ? coreComment 
                : $"{prefix} {coreComment}";

            if (!allComments.ContainsKey(itemNumber))
            {
                allComments[itemNumber] = new List<string>();
            }

            if (!allComments[itemNumber].Contains(commentToSave))
            {
                allComments[itemNumber].Add(commentToSave);
                SaveCommentsFile(inspectionCode, allComments);
            }
        }

        public void RemoveComment(string inspectionCode, string itemNumber, string comment)
        {
            var allComments = LoadCommentsFile(inspectionCode);
            if (allComments.TryGetValue(itemNumber, out var comments))
            {
                comments.Remove(comment);
                SaveCommentsFile(inspectionCode, allComments);
            }
        }

        private Dictionary<string, List<string>> LoadCommentsFile(string inspectionCode)
        {
            string filePath = Path.Combine(_userDataPath, $"comments_{inspectionCode}.json");
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    return JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json) 
                           ?? new Dictionary<string, List<string>>();
                }
                catch
                {
                    return new Dictionary<string, List<string>>();
                }
            }
            return new Dictionary<string, List<string>>();
        }

        private static readonly object _fileWriteLock = new object();
        
        private void SaveCommentsFile(string inspectionCode, Dictionary<string, List<string>> comments)
        {
            string filePath = Path.Combine(_userDataPath, $"comments_{inspectionCode}.json");
            string json = JsonConvert.SerializeObject(comments, Formatting.Indented);
            lock (_fileWriteLock)
            {
                File.WriteAllText(filePath, json);
            }
        }

        #endregion

        #region Prefix/Suffix Helpers

        public static string ExtractPrefix(string comment)
        {
            if (string.IsNullOrEmpty(comment))
                return "";

            // Look for [bracket] prefix
            int start = comment.IndexOf('[');
            int end = comment.IndexOf(']');
            
            if (start == 0 && end > start)
            {
                return comment.Substring(start, end - start + 1).ToLower();
            }
            return "";
        }

        public static List<string> ExtractSuffixes(string comment)
        {
            var suffixes = new List<string>();
            if (string.IsNullOrEmpty(comment))
                return suffixes;

            // Find all (parentheses) at the END of the comment
            // Work backwards to find suffix-like patterns
            foreach (var suffix in DefaultSuffixes)
            {
                if (comment.ToLower().Contains(suffix.ToLower()))
                {
                    suffixes.Add(suffix);
                }
            }
            return suffixes;
        }

        public static string StripPrefixAndSuffix(string comment)
        {
            if (string.IsNullOrEmpty(comment))
                return "";

            string result = comment.Trim();

            // Strip prefix [trade]
            int bracketEnd = result.IndexOf(']');
            if (result.StartsWith("[") && bracketEnd > 0)
            {
                result = result.Substring(bracketEnd + 1).TrimStart();
            }

            // Strip ALL known suffixes (case-insensitive)
            foreach (var suffix in DefaultSuffixes)
            {
                // Remove suffix case-insensitively
                int idx = result.ToLower().IndexOf(suffix.ToLower());
                while (idx >= 0)
                {
                    result = result.Remove(idx, suffix.Length);
                    idx = result.ToLower().IndexOf(suffix.ToLower());
                }
            }

            // Strip timestamp suffixes like "(2/20/2026 8 AM)" or "(1/3/2025 12 PM)"
            // Pattern: (digits/digits/digits digits AM|PM)
            var timestampRegex = new System.Text.RegularExpressions.Regex(
                @"\(\d{1,2}/\d{1,2}/\d{4}\s+\d{1,2}\s*(?:AM|PM)\)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            result = timestampRegex.Replace(result, "");

            // Clean up whitespace
            result = result.Trim();
            while (result.Contains("  "))
            {
                result = result.Replace("  ", " ");
            }
            
            // Remove trailing periods if doubled
            while (result.EndsWith(".."))
            {
                result = result.Substring(0, result.Length - 1);
            }

            return result.Trim();
        }

        public static string BuildComment(string prefix, string coreText, List<string> suffixes)
        {
            string result = "";
            
            if (!string.IsNullOrEmpty(prefix))
            {
                result = prefix.ToLower() + " ";
            }

            result += coreText.Trim();

            if (suffixes != null && suffixes.Count > 0)
            {
                result += " " + string.Join(" ", suffixes);
            }

            return result.Trim();
        }

        #endregion

        #region Templates

        public Dictionary<string, string> GetTemplate(string inspectionCode)
        {
            string filePath = Path.Combine(_userDataPath, $"template_{inspectionCode}.json");
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                           ?? new Dictionary<string, string>();
                }
                catch
                {
                    return new Dictionary<string, string>();
                }
            }
            return new Dictionary<string, string>();
        }

        public void SaveTemplate(string inspectionCode, Dictionary<string, string> template)
        {
            string filePath = Path.Combine(_userDataPath, $"template_{inspectionCode}.json");
            string json = JsonConvert.SerializeObject(template, Formatting.Indented);
            lock (_fileWriteLock)
            {
                File.WriteAllText(filePath, json);
            }
        }

        public bool HasTemplate(string inspectionCode)
        {
            string filePath = Path.Combine(_userDataPath, $"template_{inspectionCode}.json");
            return File.Exists(filePath);
        }

        #endregion

        #region Trade Summary

        public static string GenerateTradeSummary(List<(string Number, string Name, string Comments)> items)
        {
            var grouped = items
                .Where(i => !string.IsNullOrEmpty(i.Comments) && i.Comments.StartsWith("["))
                .GroupBy(i => ExtractPrefix(i.Comments))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            var tradeSummaries = new List<string>();
            
            foreach (var trade in grouped)
            {
                var tradeItems = trade.ToList();
                
                // Format trade name: remove brackets, capitalize first letter
                string tradeName = trade.Key.Trim('[', ']');
                tradeName = char.ToUpper(tradeName[0]) + tradeName.Substring(1);
                
                // Group by unique comment text to consolidate duplicates
                var commentGroups = tradeItems
                    .Select(item => new {
                        Number = item.Number,
                        CommentText = StripPrefixAndSuffix(item.Comments)
                    })
                    .GroupBy(x => x.CommentText.ToLower().Trim())
                    .OrderBy(g => g.First().Number);

                var itemEntries = new List<string>();
                foreach (var commentGroup in commentGroups)
                {
                    var itemNumbers = commentGroup.Select(x => x.Number).ToList();
                    string displayText = commentGroup.First().CommentText;
                    
                    // Ensure first letter is capitalized
                    if (!string.IsNullOrEmpty(displayText))
                        displayText = char.ToUpper(displayText[0]) + displayText.Substring(1);
                    
                    string itemNums = itemNumbers.Count > 1 
                        ? string.Join(", ", itemNumbers) 
                        : itemNumbers[0];
                    
                    itemEntries.Add($"#{itemNums}: {displayText}");
                }

                // Format: "• FRAMER (2): #2.2: Comment one || #2.3: Comment two"
                tradeSummaries.Add($"• {tradeName.ToUpper()} ({tradeItems.Count}): {string.Join(" || ", itemEntries)}");
            }

            // || between items within a trade, |||||||| between trades
            return string.Join("  ||||||||  ", tradeSummaries);
        }

        #endregion
    }
}
