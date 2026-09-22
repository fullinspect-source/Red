using InspectionEditor.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>Fills known text fields; embedded forms permit only missing/blank values. Never flattens.</summary>
    public static class OrientationPdfForm
    {
        private static string? Clean(object? value)
        {
            string? text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrWhiteSpace(text) || text.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : text;
        }

        private static Dictionary<string, string> Mappings(InspectionFile inspection)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = inspection.Sections.SelectMany(s => s.Items).ToList();
            string? ItemValue(string name, string? number = null) =>
                Clean(items.FirstOrDefault(i => string.Equals(i.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))?.Value) ??
                (number == null ? null : Clean(items.FirstOrDefault(i => i.Number == number)?.Value));
            string? Extra(string key) => inspection.ExtensionData?.TryGetValue(key, out var value) == true ? Clean(value) : null;
            void Add(string? value, params string[] fields)
            {
                value = Clean(value);
                if (value != null) foreach (string field in fields) values[field] = value;
            }
            Add(inspection.Project, "Subdivision_HOI", "Subdivision_FPAF1");
            Add(Extra("Lot"), "InspectionLot_HOI", "InspectionLot_FPAF0");
            Add(Extra("Block"), "InspectionBlock_HOI");
            Add(inspection.Address, "Address1_HOI", "Address1_LND", "Address1_ATT");
            Add(ItemValue("Customer Name", "1.1") ?? Clean(inspection.Contact),
                "Customer Name_HOI", "Customer Name_LND", "Customer Name_ATT", "Customer Name_FPAF");
            Add(ItemValue("Customer Phone", "1.2") ?? Extra("ContactNumber"), "Customer Phone_HOI", "Customer Phone_FPAF");
            Add(ItemValue("Customer Email", "1.4"), "Customer Email_FPAF");
            foreach (string name in new[] { "Microwave Serial Number", "Stove/Oven Serial Number", "Dishwasher Serial Number", "Refrigerator Serial Number" })
                Add(ItemValue(name), name == "Stove/Oven Serial Number" ? name : name + "_HOI");
            // Keep the inspection's calendar date, not this computer's timezone or today's date.
            if (DateTimeOffset.TryParse(inspection.DateInspected, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                Add(date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), "Date_HOI", "Date_FPAF0", "Date_FPAF");
                Add(date.Day.ToString(CultureInfo.InvariantCulture), "InspectionDay_LND", "InspectionDay_ATT");
                Add(date.ToString("MMMM", CultureInfo.InvariantCulture), "InspectionMonth_LND", "InspectionMonth_ATT");
                Add(date.ToString("yy", CultureInfo.InvariantCulture), "InspectionYear_LND", "InspectionYear_ATT");
            }
            return values;
        }

        private static bool IsBlank(PdfItem? value)
        {
            if (value is PdfReference reference) value = reference.Value;
            return value switch
            {
                null or PdfNull => true,
                PdfString text => string.IsNullOrWhiteSpace(text.Value),
                PdfStringObject text => string.IsNullOrWhiteSpace(text.Value),
                _ => false // Unrecognized value types are prior data, not permission to replace them.
            };
        }

        public static byte[] Fill(byte[] template, InspectionFile inspection, bool blankOnly = false)
        {
            try
            {
                using var input = new MemoryStream(template, writable: false);
                using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
                if (document.PageCount == 0) throw new IOException("The Orientation template contains no pages.");
                // Stay at dictionary level: PDFsharp's typed TextField re-renders ALL fields with
                // Courier on save. Preserve original /DA, /DR, /Q, flags, widgets and unknown fields.
                var form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                var values = Mappings(inspection);
                bool changed = false;
                var visited = new HashSet<PdfDictionary>();
                void Visit(PdfArray? fields, string parentName, string parentType,
                    PdfItem? inheritedValue, bool parentFilled, int depth)
                {
                    if (fields == null) return;
                    if (depth > 64) throw new IOException("The Orientation template field hierarchy is invalid.");
                    foreach (var item in fields.Elements)
                    {
                        var field = (item is PdfReference reference ? reference.Value : item) as PdfDictionary;
                        if (field == null || !visited.Add(field)) throw new IOException("The Orientation template field hierarchy is invalid.");
                        string localName = field.Elements.GetString("/T");
                        string name = localName.Length == 0 ? parentName : parentName.Length == 0 ? localName : parentName + "." + localName;
                        string type = field.Elements.GetName("/FT");
                        if (type.Length == 0) type = parentType;
                        PdfItem? current = field.Elements.ContainsKey("/V") ? field.Elements["/V"] : inheritedValue;
                        bool filled = false;
                        if (localName.Length > 0 && type == "/Tx" && values.TryGetValue(name, out var mapped) &&
                            (!blankOnly || IsBlank(current)))
                        {
                            field.Elements.SetString("/V", mapped);
                            current = field.Elements["/V"];
                            filled = true;
                        }
                        // Unnamed widget children inherit their field's value; clear their appearance
                        // only when that field was filled and the widget has no conflicting prior value.
                        bool filledWidget = localName.Length == 0 && parentFilled && type == "/Tx" &&
                            (!field.Elements.ContainsKey("/V") || IsBlank(field.Elements["/V"]));
                        if (filled || filledWidget)
                        {
                            // Remove only stale text appearances for fields we fill. Standard PDF
                            // editors regenerate them using the template's original font/format.
                            field.Elements.Remove("/AP");
                            changed = true;
                        }
                        Visit(field.Elements.GetArray("/Kids"), name, type, current, filled || filledWidget, depth + 1);
                    }
                }
                Visit(form?.Elements.GetArray("/Fields"), "", "", null, false, 0);
                if (!changed) return template;
                form!.Elements.SetBoolean("/NeedAppearances", true);
                using var output = new MemoryStream();
                document.Save(output);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is not IOException)
            {
                throw new IOException("The exact Orientation template could not be read or filled. Restore the correct PDF in Inspections/Documents; no substitute was opened.", ex);
            }
        }
    }
}
