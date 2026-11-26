using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SmartFleetBE.Services;

// Server-generated reports share the API snapshot. XLSX cells are typed values,
// never formulas. PDF uses portable standard fonts and IDs rather than device names.
public static class AnalyticsExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static string Value(object? value) => value switch {
        null => "N/A", IFormattable number => number.ToString(null, Invariant), _ => value.ToString() ?? ""
    };
    private static List<object?[]> Summary(AnalyticsReport report)
    {
        List<object?[]> rows = [["Metric", "Value"], ["From (inclusive UTC+07)", report.Window.From.ToString("yyyy-MM-dd")],
            ["To (inclusive UTC+07)", report.Window.To.ToString("yyyy-MM-dd")], ["Generated UTC", report.Window.GeneratedAt.ToString("O")],
            ["Warehouse ID", report.Window.WarehouseId?.ToString() ?? "ALL"], ["Robot ID", report.Window.RobotId?.ToString() ?? "ALL"]];
        foreach (var (label, metrics) in new (string, object)[] { ("Fleet now", report.Fleet), ("Tasks", report.Tasks), ("Missions", report.Missions), ("Maintenance", report.Maintenance) })
            foreach (var property in metrics.GetType().GetProperties()) {
                if (property.GetValue(metrics) is IEnumerable<KeyValuePair<string,int>> counts)
                    rows.AddRange(counts.Select(c => new object?[] { $"{label}: Status {c.Key}", c.Value }));
                else rows.Add([$"{label}: {property.Name}", property.GetValue(metrics)]);
            }
        rows.Add(["Assignment utilization (%)", report.AssignmentUtilizationPercent]); return rows;
    }
    private static readonly string[] Definitions = [
        "Dates use UTC+07; end date is inclusive. Fleet and open counts are current snapshots.",
        "Task/mission status totals use records created in the selected period and their current statuses.",
        "CompletedInPeriod/daily completions use completion timestamps, regardless of creation date.",
        "Success rate = completed / (completed + failed) in the creation cohort; cancelled excluded.",
        "Completion duration = creation to completion for tasks completed in the period; N/A means no valid samples.",
        "Execution duration uses completed missions in the creation cohort with valid start/end timestamps.",
        "Assignment utilization = union of held assignment intervals / registered time in window, clipped at now.",
        "Utilization includes failed load holds; it is not measured motor motion, uptime or energy use.",
        "Inactive robots remain in historical utilization. Robot filter includes tasks ever assigned to that robot.",
        "Fleet available uses basic state checks; localization, route and payload eligibility depend on each task."
    ];
    public static byte[] Xlsx(AnalyticsReport report)
    {
        List<object?[]> utilization = [["Robot ID", "Robot code", "Warehouse ID", "Active", "Assignments overlapping window", "Completed missions in period", "Assigned seconds", "Registered seconds", "Utilization %"]];
        utilization.AddRange(report.RobotUtilization.Select(r => new object?[] { r.RobotId, r.RobotCode, r.WarehouseId, r.IsActive ? "Yes" : "No", r.Assignments, r.CompletedMissions, r.AssignedSeconds, r.RegisteredSeconds, r.UtilizationPercent }));
        List<object?[]> daily = [["Date UTC+07", "Created tasks", "Completed tasks"]];
        daily.AddRange(report.Daily.Select(d => new object?[] { d.Date.ToString("yyyy-MM-dd"), d.CreatedTasks, d.CompletedTasks }));
        var sheets = new[] { (Name: "Summary", Rows: Summary(report)), (Name: "Robot utilization", Rows: utilization),
            (Name: "Daily", Rows: daily), (Name: "Definitions", Rows: Definitions.Select(d => new object?[] { d }).ToList()) };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) {
            void Add(string name, string contents) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(contents); }
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            Add("[Content_Types].xml", new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                sheets.Select((_, i) => new XElement(types + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{i + 1}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))).ToString());
            Add("_rels/.rels", new XElement(package + "Relationships", new XElement(package + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", relations.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))).ToString());
            Add("xl/workbook.xml", new XElement(main + "workbook", new XAttribute(XNamespace.Xmlns + "r", relations), new XElement(main + "sheets", sheets.Select((s, i) => new XElement(main + "sheet", new XAttribute("name", s.Name), new XAttribute("sheetId", i + 1), new XAttribute(relations + "id", $"rId{i + 1}"))))).ToString());
            Add("xl/_rels/workbook.xml.rels", new XElement(package + "Relationships", sheets.Select((_, i) => new XElement(package + "Relationship", new XAttribute("Id", $"rId{i + 1}"), new XAttribute("Type", relations.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{i + 1}.xml")))).ToString());
            for (var s = 0; s < sheets.Length; s++) {
                var data = sheets[s].Rows.Select((row, i) => new XElement(main + "row", new XAttribute("r", i + 1), row.Select((v, c) => {
                    var cell = new XElement(main + "c", new XAttribute("r", $"{(char)('A' + c)}{i + 1}"));
                    if (v is int or long or double or decimal) cell.Add(new XElement(main + "v", Value(v)));
                    else { cell.Add(new XAttribute("t", "inlineStr")); cell.Add(new XElement(main + "is", new XElement(main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Value(v)))); }
                    return cell;
                })));
                Add($"xl/worksheets/sheet{s + 1}.xml", new XElement(main + "worksheet", new XElement(main + "sheetViews", new XElement(main + "sheetView", new XAttribute("workbookViewId", 0), new XElement(main + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("state", "frozen")))),
                    new XElement(main + "cols", Enumerable.Range(1, sheets[s].Rows.Max(r => r.Length)).Select(i => new XElement(main + "col", new XAttribute("min", i), new XAttribute("max", i), new XAttribute("width", i == 1 ? (s == 3 ? 120 : 46) : 24), new XAttribute("customWidth", 1)))), new XElement(main + "sheetData", data)).ToString());
            }
        }
        return output.ToArray();
    }
    public static byte[] Pdf(AnalyticsReport report)
    {
        var lines = Summary(report).Select(r => $"{Value(r[0]),-48} {Value(r[1])}").ToList();
        lines.AddRange(["", "ROBOT ASSIGNMENT UTILIZATION", "Robot ID / Warehouse       Assignments   Completed     Utilization %"]);
        lines.AddRange(report.RobotUtilization.Select(r => $"#{r.RobotId,-8} / #{r.WarehouseId,-8} {r.Assignments,10} {r.CompletedMissions,11} {Value(r.UtilizationPercent),17}"));
        lines.AddRange(["", "DAILY TASKS (UTC+07)", "Date          Created     Completed"]);
        lines.AddRange(report.Daily.Select(d => $"{d.Date:yyyy-MM-dd} {d.CreatedTasks,10} {d.CompletedTasks,13}"));
        lines.AddRange(["", "DEFINITIONS"]);
        foreach (var definition in Definitions) {
            var remaining = definition;
            while (remaining.Length > 83) {
                var split = remaining.LastIndexOf(' ', 83); if (split <= 0) split = 83;
                lines.Add(remaining[..split]); remaining = remaining[split..].TrimStart();
            }
            lines.Add(remaining);
        }
        var pages = lines.Chunk(45).ToList();
        var objects = new List<string> { "", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>" };
        var pageIds = new List<int>();
        string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        for (var i = 0; i < pages.Count; i++) {
            int page = objects.Count + 1, content = page + 1; pageIds.Add(page);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {content} 0 R >>");
            var commands = new StringBuilder("BT /F1 15 Tf 40 800 Td (SmartFleet Operational Report) Tj ET\nBT /F1 10 Tf 40 773 Td 15 TL\n");
            foreach (var line in pages[i]) commands.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            commands.Append($"ET\nBT /F1 9 Tf 40 42 Td (Page {i + 1} of {pages.Count} - Robot IDs identify devices; full codes in Excel.) Tj ET\n");
            var stream = commands.ToString(); objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = $"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(' ', pageIds.Select(i => $"{i} 0 R"))}] >>";
        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n"); var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = output.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return output.ToArray();
    }
}
