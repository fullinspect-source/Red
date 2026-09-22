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
    /// <summary>Prefills only known text fields in a fresh, exact builder template. Never flattens a form.</summary>
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

        public static byte[] Fill(byte[] template, InspectionFile inspection)
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
                void Visit(PdfArray? fields, string parentName, string parentType, string? inheritedValue, int depth)
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
                        string? value = localName.Length == 0 ? inheritedValue : null;
                        if (type == "/Tx" && values.TryGetValue(name, out var mapped)) value = mapped;
                        if (type == "/Tx" && value != null)
                        {
                            // /V lives on the field; unnamed widget children inherit it.
                            if (localName.Length > 0) field.Elements.SetString("/V", value);
                            // Remove only stale text appearances for fields we fill. Standard PDF
                            // editors regenerate them using the template's original font/format.
                            field.Elements.Remove("/AP");
                            changed = true;
                        }
                        Visit(field.Elements.GetArray("/Kids"), name, type, value, depth + 1);
                    }
                }
                Visit(form?.Elements.GetArray("/Fields"), "", "", null, 0);
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
