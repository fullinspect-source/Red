// Checklist search through common comments (owner 2026-10-24; program/features/F-024-comment-libraries/ACCEPTANCE.md,
// A-024-05 to A-024-16). The editor's left-panel search lists an item when its own text OR one of its common comments
// (all quick comments, INSPECT's company and personal lists, My saved comments) contains the search text.
//   dotnet run -p:Platform=x64
// Synthetic items and comments only; files go to a sandbox under %TEMP%. No network.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using InspectionEditor.Models;
using InspectionEditor.Services;

return Harness.Run();

static class Harness
{
    private static int _pass, _fail;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    private static Item NewItem(int id, string number, string name, string comments = "", object? value = null) =>
        new() { ItemId = id, Number = number, Name = name, Comments = comments, Value = value };

    // The classic tree's own-text rule, as MainWindow.PopulateTreeView applies it.
    private static bool LabelMatches(Item item, string filter)
    {
        string f = filter.ToLower();
        return item.Name?.ToLower().Contains(f) == true || item.Number?.ToLower().Contains(f) == true ||
               item.Comments?.ToLower().Contains(f) == true;
    }

    private static ChecklistSearchMatch MatchOf(Item item, string filter, ChecklistCommentIndex index) =>
        ChecklistCommentSearch.Match(LabelMatches(item, filter), item, filter, index);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InspectionEditor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("InspectionEditor.csproj not found above the harness");
    }

    // The body of one method (by its signature line) in a source file.
    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        return "";
    }

    public static int Run()
    {
        // Synthetic lists: item 5.6 "Receptacles" has five quick comments, the GFCI one fifth (outside the top three).
        var quick = new Dictionary<string, List<string>>
        {
            ["5.6"] = new() { "Cover plate missing", "Loose receptacle", "Reverse polarity", "Open ground", "[electrician] GFCI protection missing at kitchen island" },
            ["5.7"] = new() { "Switch loose" },
        };
        var saved = new Dictionary<string, List<string>> { ["7.2"] = new() { "Efflorescence at slab edge, rear" } };
        var company = new Dictionary<int, List<string>> { [1801] = new() { "Weep screed buried below grade" } };
        var inspectUser = new Dictionary<int, List<string>> { [1802] = new() { "Kick-out flashing absent at roof-wall" } };
        int loads = 0;
        IEnumerable<ChecklistComment> Load(Item item)
        {
            loads++;
            var list = new List<ChecklistComment>();
            if (saved.TryGetValue(item.Number ?? "", out var s)) list.AddRange(s.Select(t => new ChecklistComment(ChecklistCommentSource.Saved, t)));
            if (quick.TryGetValue(item.Number ?? "", out var q)) list.AddRange(q.Select(t => new ChecklistComment(ChecklistCommentSource.Quick, t)));
            if (company.TryGetValue(item.ItemId, out var c)) list.AddRange(c.Select(t => new ChecklistComment(ChecklistCommentSource.Company, t)));
            if (inspectUser.TryGetValue(item.ItemId, out var u)) list.AddRange(u.Select(t => new ChecklistComment(ChecklistCommentSource.InspectUser, t)));
            return list;
        }
        var index = new ChecklistCommentIndex(Load, "stamp-1");

        var receptacles = NewItem(1601, "5.6", "Receptacles");
        var gfciLabel = NewItem(1602, "5.8", "GFCI devices tested");
        var switches = NewItem(1603, "5.7", "Switches");
        var slab = NewItem(1701, "7.2", "Slab and foundation");
        var stucco = NewItem(1801, "8.1", "Stucco");
        var roof = NewItem(1802, "8.2", "Roof to wall");

        // A-024-05 label only
        Check("A-024-05", MatchOf(gfciLabel, "gfci", index) == ChecklistSearchMatch.Label &&
                          index.Matching(gfciLabel, "gfci").Count == 0,
            "an item whose own text has the search is listed as before, with no marker");

        // A-024-06 comment only, through the fifth quick comment
        Check("A-024-06", MatchOf(receptacles, "gfci", index) == ChecklistSearchMatch.CommentOnly &&
                          ChecklistCommentSearch.MarkerText == "🔍 in comments",
            "an item whose only match is a common comment (5th quick comment) is listed with the \"🔍 in comments\" marker");

        // A-024-07 both
        var gfciBoth = NewItem(1601, "5.6", "GFCI receptacles");
        Check("A-024-07", MatchOf(gfciBoth, "gfci", index) == ChecklistSearchMatch.Label &&
                          index.Matching(gfciBoth, "gfci").Count == 1,
            "an item matching by its own text and by a comment is listed without the marker; its comment still matches");

        // A-024-08 no match; no search
        Check("A-024-08", MatchOf(switches, "gfci", index) == ChecklistSearchMatch.None &&
                          index.Matching(switches, "gfci").Count == 0 &&
                          new[] { receptacles, switches, slab }.All(i => MatchOf(i, "", index) == ChecklistSearchMatch.Label) &&
                          index.Matching(receptacles, "  ").Count == 0,
            "no match hides the item; an empty search lists every item and matches no comment");

        // A-024-09 every list, case-insensitive
        Check("A-024-09", MatchOf(slab, "EFFLOR", index) == ChecklistSearchMatch.CommentOnly &&
                          index.Matching(slab, "efflor")[0].Source == ChecklistCommentSource.Saved &&
                          MatchOf(stucco, "weep", index) == ChecklistSearchMatch.CommentOnly &&
                          index.Matching(stucco, "weep")[0].Source == ChecklistCommentSource.Company &&
                          MatchOf(roof, "Kick-Out", index) == ChecklistSearchMatch.CommentOnly &&
                          index.Matching(roof, "kick-out")[0].Source == ChecklistCommentSource.InspectUser &&
                          MatchOf(receptacles, "gFcI", index) == ChecklistSearchMatch.CommentOnly &&
                          index.Matching(receptacles, "gfci")[0].Source == ChecklistCommentSource.Quick,
            "My saved comments, company comments, My INSPECT comments and every quick comment are searched, ignoring case");

        // A-024-10 matching list and matching-first order
        var dupIndex = new ChecklistCommentIndex(_ => new[]
        {
            new ChecklistComment(ChecklistCommentSource.Saved, "GFCI missing"),
            new ChecklistComment(ChecklistCommentSource.Quick, "Loose plate"),
            new ChecklistComment(ChecklistCommentSource.Quick, "gfci missing"),
            new ChecklistComment(ChecklistCommentSource.Company, "No GFCI at exterior"),
        }, "dup");
        var dupMatches = dupIndex.Matching(receptacles, "gfci");
        var ordered = ChecklistCommentSearch.MatchingFirst(new[] { "Loose plate", "GFCI missing", "Open ground", "No GFCI at exterior" }, "gfci");
        Check("A-024-10", dupMatches.Count == 2 && dupMatches[0].Text == "GFCI missing" && dupMatches[1].Text == "No GFCI at exterior" &&
                          ordered.SequenceEqual(new[] { "GFCI missing", "No GFCI at exterior", "Loose plate", "Open ground" }) &&
                          ChecklistCommentSearch.MatchingFirst(new[] { "b", "a" }, "").SequenceEqual(new[] { "b", "a" }),
            "the opened item's matching comments come first in list order, each once; the rest follow; nothing is dropped");

        // A-024-11 each item's lists are read once per index; a copied item shares its entry
        loads = 0;
        var once = new ChecklistCommentIndex(Load, "stamp-2");
        var items = new[] { receptacles, gfciLabel, switches, slab, stucco, roof };
        foreach (string search in new[] { "g", "gf", "gfc", "gfci", "e", "ef", "eff", "weep", "kick", "" })
            foreach (var item in items)
                once.HasMatch(item, search);
        once.HasMatch(NewItem(1601, "5.6", "Receptacles (copy)"), "gfci");
        Check("A-024-11", loads == items.Length && once.LoadedItems == items.Length,
            $"lists read {loads} times for {items.Length} items over 10 searches and a copied item");

        // A-024-12 a large report stays responsive while typing
        var bigItems = Enumerable.Range(0, 2500).Select(i => NewItem(10000 + i, $"{i / 50 + 1}.{i % 50 + 1}", $"Checklist item {i}")).ToList();
        var bigIndex = new ChecklistCommentIndex(item => Enumerable.Range(0, 40)
            .Select(k => new ChecklistComment((ChecklistCommentSource)(k % 4), $"Comment {k} for {item.Number}: deficiency noted, see photo {k}")), "big");
        var timings = new List<double>();
        string typed = "receptacle cover plate";
        int shown = 0;
        for (int n = 1; n <= typed.Length; n++)
        {
            string search = typed.Substring(0, n);
            var sw = Stopwatch.StartNew();
            shown = bigItems.Count(item => MatchOf(item, search, bigIndex) != ChecklistSearchMatch.None);
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }
        double firstKey = timings[0], worstAfter = timings.Skip(1).Max();
        Check("A-024-12", firstKey < 1500 && worstAfter < 100 && shown == 0,
            $"2,500 items x 40 comments: first key {firstKey:F0} ms (reads the lists), then at most {worstAfter:F1} ms per key (matching only)");

        // A-024-13 the real list files: saved comments, INSPECT libraries, quick comments; stamps follow file changes
        string root = Path.Combine(Path.GetTempPath(), "red-commentsearch-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(root, "Inspections", "Data");
            Directory.CreateDirectory(data);
            var users = new UserDataService(root);
            string savedStamp0 = users.SavedCommentsStamp("CPP");
            users.SaveComment("CPP", "7.2", "Efflorescence at slab edge, rear");
            var byItem = users.GetSavedCommentsByItem("CPP");
            string savedStamp1 = users.SavedCommentsStamp("CPP");

            string folder = Path.Combine(root, "Inspections");
            string libStamp0 = InspectCommentLibrary.Stamp(folder);
            File.WriteAllText(Path.Combine(data, "CompanyComments.cmt"),
                "{\"1801\":[{\"Selected\":false,\"Value\":\"Weep screed buried below grade\"}]}", new UTF8Encoding(false));
            string libStamp1 = InspectCommentLibrary.Stamp(folder);
            var companyAll = InspectCommentLibrary.All(folder, InspectCommentLibrary.Kind.Company);

            var quickFile = new Dictionary<string, List<QuickComment>>
            {
                ["CPP|5.6"] = Enumerable.Range(1, 6).Select(k => new QuickComment { Text = k == 6 ? "GFCI missing" : $"Comment {k}", Count = 10 - k }).ToList(),
                ["IER|5.6"] = new() { new QuickComment { Text = "Other type", Count = 50 } },
            };
            var byNumber = QuickCommentsService.SuggestionsByItem(quickFile, "CPP");

            Check("A-024-13", byItem.TryGetValue("7.2", out var mine) && mine.Any(c => c.Contains("Efflorescence")) &&
                              savedStamp0 != savedStamp1 && libStamp0 != libStamp1 &&
                              companyAll.TryGetValue("1801", out var co) && co.Single() == "Weep screed buried below grade" &&
                              byNumber.Count == 1 && byNumber["5.6"].Count == 6 && byNumber["5.6"][5] == "GFCI missing" &&
                              byNumber["5.6"][0] == "Comment 1",
                "saved comments and INSPECT libraries are read whole; every quick comment of the type (6 of 6, most used first); stamps change with the files");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // Source checks on the editor (MainWindow): where the rules are wired in.
        string pl = FindRoot();
        string main = File.ReadAllText(Path.Combine(pl, "MainWindow.xaml.cs"));
        string searchCode = File.ReadAllText(Path.Combine(pl, "MainWindow.CommentSearch.cs"));
        string groups = File.ReadAllText(Path.Combine(pl, "MainWindow.ItemGroups.cs"));
        string tree = Method(main, "private void PopulateTreeView(string filter = null)");
        string inline = Method(main, "private bool ShouldShowInlineItem(Item item, string? filter, bool isSCI)");
        string populateInline = Method(main, "private void PopulateInlineChecklist(string? filter = null)");

        // A-024-14 combined with OFI/REQ/INC: the search test comes first, the result filters still apply after it
        int treeSearch = tree.IndexOf("if (!matchesFilter && !commentOnlyMatch) continue;", StringComparison.Ordinal);
        int inlineSearch = inline.IndexOf("!MatchesThroughComments(item, filter))", StringComparison.Ordinal);
        bool treeOrder = treeSearch > 0 && treeSearch < tree.IndexOf("if (_ofiFilterActive)", treeSearch, StringComparison.Ordinal) &&
                         tree.IndexOf("if (_reqFilterActive && !IsCheckedByCompletionRules(item)) continue;", treeSearch, StringComparison.Ordinal) > treeSearch &&
                         tree.IndexOf("if (_incFilterActive && !IsIncompleteByCompletionRules(item)) continue;", treeSearch, StringComparison.Ordinal) > treeSearch;
        bool inlineOrder = inlineSearch > 0 && inline.IndexOf("if (_ofiFilterActive)", inlineSearch, StringComparison.Ordinal) > inlineSearch &&
                           inline.IndexOf("if (_reqFilterActive && !IsCheckedByCompletionRules(item))", inlineSearch, StringComparison.Ordinal) > inlineSearch &&
                           inline.IndexOf("if (_incFilterActive && !IsIncompleteByCompletionRules(item))", inlineSearch, StringComparison.Ordinal) > inlineSearch;
        bool gridCells = Method(groups, "private bool GridCellMatchesFilter(Item item, string? filter)").Contains("!MatchesThroughComments(item, filter)");
        Check("A-024-14", treeOrder && inlineOrder && gridCells,
            "classic tree, v2.1 rows and grid cells match through comments first; OFI, REQ and INC still filter the result");

        // A-024-15 the marker only on comment-only rows
        Check("A-024-15", tree.Contains("commentOnlyMatch = IsCommentOnlySearchMatch(item, matchesFilter, filter);") &&
                          Regex.IsMatch(tree, @"if \(commentOnlyMatch\)\s+AddCommentSearchMarker\(nameText\);") &&
                          Regex.IsMatch(Method(main, "private Grid CreateInlineItemHeader(Section section, Item item, bool isExpanded)"),
                              @"if \(IsInlineCommentOnlySearchMatch\(item\)\)\s+chips\.Children\.Add\(CreateCommentSearchBadge\(\)\);"),
            "the classic tree and the v2.1 rows add \"🔍 in comments\" only when the item matched through a comment alone");

        // A-024-16 the opened item: matching comments first and highlighted, one tap applies
        string loadSaved = Method(main, "private void LoadSavedComments()");
        string sidePanel = Method(main, "private UIElement CreateInlineSavedCommentsSidePanel(Item item)");
        bool rightPanel = loadSaved.IndexOf("AddMatchingCommentsToSavedPanel(matches);", StringComparison.Ordinal) > 0 &&
                          loadSaved.IndexOf("AddMatchingCommentsToSavedPanel(matches);", StringComparison.Ordinal) < loadSaved.IndexOf("foreach (var comment in saved)", StringComparison.Ordinal);
        bool side = sidePanel.IndexOf("AddInlineMatchingComments(list, item, matches);", StringComparison.Ordinal) > 0 &&
                    sidePanel.IndexOf("AddInlineMatchingComments(list, item, matches);", StringComparison.Ordinal) < sidePanel.IndexOf("foreach (string comment in saved)", StringComparison.Ordinal) &&
                    sidePanel.Contains("AddInspectCommentSections(list, item, matches);");
        bool quickSlots = Method(main, "private void UpdateQuickSuggestionsOverlay()").Contains("QuickCommentsMatchingFirst(_currentItem,") &&
                          Method(main, "private void UpdateQuickSuggestionsOverlay()").Contains("MarkQuickSuggestionMatches();") &&
                          Method(main, "private List<string> GetInlineQuickComments(Item item)").Contains("QuickCommentsMatchingFirst(item,");
        bool taps = Method(searchCode, "private void AddMatchingCommentsToSavedPanel(IReadOnlyList<ChecklistComment> matches)").Contains("button.Click += SavedCommentButton_Click;") &&
                    Method(searchCode, "private void AddInlineMatchingComments(Panel list, Item item, IReadOnlyList<ChecklistComment> matches)").Contains("button.Click += InlineInspectCommentButton_Click;") &&
                    searchCode.Contains("HighlightMatchingComment(button);");
        Check("A-024-16", rightPanel && side && quickSlots && taps,
            "the opened item lists its matching comments first, highlighted, in the right panel, the v2.1 comment drawer and the quick slots; a tap applies one");

        // A-024-17 one index per list build; per-item checks read no files
        string common = Method(searchCode, "private IEnumerable<ChecklistComment> CommonCommentsFor(Item item,");
        Check("A-024-17", Regex.IsMatch(tree, @"if \(hasFilter\)\s+RefreshCommentSearchIndex\(\);") &&
                          Regex.IsMatch(populateInline, @"if \(!string\.IsNullOrWhiteSpace\(filter\)\)\s+RefreshCommentSearchIndex\(\);") &&
                          common.Length > 0 && !common.Contains("GetSavedComments(") && !common.Contains("Entries(") && !common.Contains("GetSuggestions(") &&
                          !Method(main, "private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)").Contains("MessageBox"),
            "the comment index is refreshed once per list build; each item is checked from memory; no popups");

        // A-024-18 typing stays smooth on a slow list: one build after a pause; quick lists rebuild on every key as before
        string textChanged = Method(main, "private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)");
        string schedule = Method(searchCode, "private void ScheduleSearchRebuild()");
        Check("A-024-18", Regex.IsMatch(textChanged, @"if \(_lastChecklistBuildMs >= SlowChecklistBuildMs\)\s*\{\s*_checklistFilterGeneration\+\+;[^\n]*\s*ScheduleSearchRebuild\(\);\s*return;\s*\}\s*PopulateTreeView\(filter\);") &&
                          tree.Contains("_searchRebuildTimer?.Stop();") && tree.Contains("NoteChecklistBuilt(buildTimer);") &&
                          schedule.Contains("_searchRebuildTimer.Tick += (_, _) => FlushPendingSearchRebuild();") &&
                          Method(searchCode, "private void FlushPendingSearchRebuild()").Contains("PopulateTreeView(SearchFilterBox.Text);") &&
                          searchCode.Contains("SlowChecklistBuildMs = 150") && searchCode.Contains("TimeSpan.FromMilliseconds(300)"),
            "a list that took 150 ms or more to build waits for a 300 ms pause in typing; quicker lists rebuild on every key as before");

        // Review fixes (2026-10-24).
        // A-024-19 a quick comment the user also saved still takes a quick slot (source filter before texts are merged)
        var bothLists = new ChecklistCommentIndex(_ => new[]
        {
            new ChecklistComment(ChecklistCommentSource.Saved, "GFCI protection missing at kitchen island"),
            new ChecklistComment(ChecklistCommentSource.Quick, "Cover plate missing"),
            new ChecklistComment(ChecklistCommentSource.Quick, "GFCI protection missing at kitchen island"),
        }, "both");
        var quickOnly = bothLists.Matching(receptacles, "gfci", ChecklistCommentSource.Quick);
        var merged = bothLists.Matching(receptacles, "gfci");
        Check("A-024-19", quickOnly.Count == 1 && quickOnly[0].Source == ChecklistCommentSource.Quick &&
                          merged.Count == 1 && merged[0].Source == ChecklistCommentSource.Saved,
            "a matching quick comment that is also saved is still found among the quick comments");

        // A-024-20 damaged lists never stop the search
        bool noThrow = true;
        Dictionary<string, List<string>>? damaged = null;
        try
        {
            damaged = QuickCommentsService.SuggestionsByItem(new Dictionary<string, List<QuickComment>>
            {
                ["CPP|5.6"] = new() { null!, new QuickComment { Text = null! }, new QuickComment { Text = "GFCI missing", Count = 2 } },
            }, "CPP");
        }
        catch (Exception) { noThrow = false; }
        var throwing = new ChecklistCommentIndex(_ => throw new InvalidOperationException("damaged list"), "throws");
        bool indexSafe;
        try { indexSafe = !throwing.HasMatch(receptacles, "gfci") && throwing.Matching(receptacles, "gfci").Count == 0; }
        catch (Exception) { indexSafe = false; }
        string commonFor = Method(searchCode, "private IEnumerable<ChecklistComment> CommonCommentsFor(Item item,");
        Check("A-024-20", noThrow && damaged != null && damaged["5.6"].SequenceEqual(new[] { "GFCI missing" }) && indexSafe &&
                          commonFor.Contains("out var mine) && mine != null") &&
                          Regex.IsMatch(Method(searchCode, "private ChecklistCommentIndex? RefreshCommentSearchIndex()"), @"catch \(Exception ex\)\s*\{[^}]*DiagnosticLogService\.Log\("),
            "a null quick comment, a null saved list or a failing list reader leaves the search on the items' own text, with no crash");

        // A-024-21 the open item follows the search on screen; INSPECT entries tapped in the right panel are added the
        // INSPECT way; a tap away from the box builds a pending search; small reports are timed without open-time work;
        // the quick slots are unchanged without a search
        string matchedFor = Method(searchCode, "private IReadOnlyList<ChecklistComment> SearchMatchedComments(Item? item, ChecklistCommentSource? source = null)");
        string inspectTap = Method(searchCode, "private void RightPanelInspectEntry_Click(object sender, RoutedEventArgs e)");
        string quickFirst = Method(searchCode, "private List<string> QuickCommentsMatchingFirst(Item? item, IEnumerable<string> topSuggestions, int slots)");
        Check("A-024-21", matchedFor.Contains("string filter = _checklistSearchText;") &&
                          Method(searchCode, "private void MarkQuickSuggestionMatches()").Contains("string filter = _checklistSearchText;") &&
                          !Regex.IsMatch(main, @"ChecklistCommentSearch\.(Contains|MatchingFirst)\([^)]*SearchFilterBox\.Text") &&
                          Regex.IsMatch(tree, @"RefreshOpenItemCommentMatches\(\);[^\n]*\s+NoteChecklistBuilt\(buildTimer\);") &&
                          loadSaved.Contains("_rightPanelSearchText = _checklistSearchText;") &&
                          inspectTap.Contains("InspectCommentLibrary.Append(CommentsTextBox.Text, entry)") &&
                          searchCode.Contains("button.Click += RightPanelInspectEntry_Click;") &&
                          searchCode.Contains("SearchFilterBox.LostKeyboardFocus +=") &&
                          searchCode.Contains("DispatcherPriority.Loaded);") && !searchCode.Contains("DispatcherPriority.ContextIdle") &&
                          Regex.IsMatch(quickFirst, @"if \(string\.IsNullOrWhiteSpace\(_checklistSearchText\)\)\s+return top;"),
            "open item's comment lists follow the list's search; INSPECT taps use \"existing, entry\"; leaving the box builds the pending search; open-time work is not timed; no search keeps the slots as before");

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
