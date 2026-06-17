using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PDMLite
{
    // Minimal multi-sheet .xlsx writer built on System.IO.Compression only — no
    // NuGet / native dependency (a .xlsx is a ZIP of OOXML parts). Used by the
    // baseline viewer's "Export All" (one worksheet per released revision).
    //
    // Every cell is written as an inline string (xml:space="preserve" so leading
    // indentation in component names survives), which keeps the format simple and
    // robust; numbers therefore land as text, which is fine for a BOM export.
    public static class XlsxWriter
    {
        public sealed class Sheet
        {
            public string Name;                 // tab name (sanitised/truncated)
            public List<string[]> Rows = new List<string[]>();
            public Sheet(string name) { Name = name; }
            public void Add(params string[] cells) { Rows.Add(cells ?? new string[0]); }
            public void AddBlank() { Rows.Add(new string[0]); }
        }

        public static void Write(string path, List<Sheet> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                sheets = new List<Sheet> { new Sheet("Sheet1") };

            // Excel tab names: ≤31 chars, none of []:*?/\, and unique.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sheets)
                s.Name = UniqueSheetName(SanitizeSheetName(s.Name), usedNames);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                WriteEntry(zip, "_rels/.rels", RootRels());
                WriteEntry(zip, "xl/workbook.xml", Workbook(sheets));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                for (int i = 0; i < sheets.Count; i++)
                    WriteEntry(zip, "xl/worksheets/sheet" + (i + 1) + ".xml",
                        SheetXml(sheets[i]));
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(content);
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + i +
                    ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private static string Workbook(List<Sheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append("<sheet name=\"" + Esc(sheets[i].Name) + "\" sheetId=\"" +
                    (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId" + i +
                    "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" +
                    i + ".xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string SheetXml(Sheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                string[] cells = sheet.Rows[r] ?? new string[0];
                int rowNum = r + 1;
                sb.Append("<row r=\"" + rowNum + "\">");
                for (int col = 0; col < cells.Length; col++)
                {
                    string v = cells[col] ?? "";
                    sb.Append("<c r=\"" + ColLetter(col) + rowNum +
                        "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                    sb.Append(Esc(v));
                    sb.Append("</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        // 0 → A, 25 → Z, 26 → AA …
        private static string ColLetter(int index)
        {
            string s = "";
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                s = (char)('A' + rem) + s;
                index = (index - 1) / 26;
            }
            return s;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Sheet";
            foreach (char c in new[] { '[', ']', ':', '*', '?', '/', '\\' })
                name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length > 31) name = name.Substring(0, 31);
            if (name.Length == 0) name = "Sheet";
            return name;
        }

        private static string UniqueSheetName(string baseName, HashSet<string> used)
        {
            string name = baseName;
            int n = 2;
            while (used.Contains(name))
            {
                string suffix = " (" + n++ + ")";
                int keep = Math.Min(baseName.Length, 31 - suffix.Length);
                name = baseName.Substring(0, Math.Max(0, keep)) + suffix;
            }
            used.Add(name);
            return name;
        }
    }
}
