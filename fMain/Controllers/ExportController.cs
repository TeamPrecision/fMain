using ClosedXML.Excel;
using fMain.Services;
using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace fMain.Controllers;

[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly DatalogService _datalog;

    public ExportController(DatalogService datalog)
    {
        _datalog = datalog;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    [HttpGet("csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string? sn, [FromQuery] string? wo,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int limit = 500)
    {
        var rows = await FetchRows(sn, wo, from, to, limit);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ID,Work Order,Serial Number,Head,Start Time,End Time,Result,Plan Name,Plan Version");
        foreach (var r in rows)
            sb.AppendLine($"{r["id"]},{Esc(r["work_order"])},{Esc(r["serial_number"])},{r["head"]},{r["start_time"]},{r["end_time"]},{r["result"]},{Esc(r["plan_name"])},{Esc(r["plan_version"])}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"datalog_{DateTime.Now:yyyyMMdd_HHmm}.csv");
    }

    // ── Excel ─────────────────────────────────────────────────────────────────

    [HttpGet("xlsx")]
    public async Task<IActionResult> ExportExcel(
        [FromQuery] string? sn, [FromQuery] string? wo,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int limit = 500)
    {
        var rows = await FetchRows(sn, wo, from, to, limit);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Datalog");

        // Header row
        string[] cols = ["ID", "Work Order", "Serial Number", "Head", "Start Time", "End Time", "Result", "Plan Name", "Plan Version"];
        for (int c = 0; c < cols.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = cols[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            cell.Style.Font.FontColor = XLColor.White;
        }

        // Data rows
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            ws.Cell(r + 2, 1).Value = row["id"]?.ToString() ?? "";
            ws.Cell(r + 2, 2).Value = row["work_order"]?.ToString() ?? "";
            ws.Cell(r + 2, 3).Value = row["serial_number"]?.ToString() ?? "";
            ws.Cell(r + 2, 4).Value = row["head"]?.ToString() ?? "";
            ws.Cell(r + 2, 5).Value = row["start_time"]?.ToString() ?? "";
            ws.Cell(r + 2, 6).Value = row["end_time"]?.ToString() ?? "";
            ws.Cell(r + 2, 7).Value = row["result"]?.ToString() ?? "";
            ws.Cell(r + 2, 8).Value = row["plan_name"]?.ToString() ?? "";
            ws.Cell(r + 2, 9).Value = row["plan_version"]?.ToString() ?? "";

            var resultCell = ws.Cell(r + 2, 7);
            if (resultCell.GetString() == "PASS")
                resultCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D1FAE5");
            else if (resultCell.GetString() == "FAIL")
                resultCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"datalog_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    [HttpGet("pdf")]
    public async Task<IActionResult> ExportPdf(
        [FromQuery] string? sn, [FromQuery] string? wo,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int limit = 200)
    {
        var rows = await FetchRows(sn, wo, from, to, limit);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text("fMain — Test Datalog Report").FontSize(14).Bold();
                        col.Item().Text($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}   Records: {rows.Count}").FontSize(8).FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrEmpty(wo))  col.Item().Text($"Work Order: {wo}").FontSize(8);
                        if (!string.IsNullOrEmpty(sn))  col.Item().Text($"Serial: {sn}").FontSize(8);
                    });
                });

                page.Content().PaddingTop(8).Table(tbl =>
                {
                    tbl.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(40);   // ID
                        c.RelativeColumn(2);     // WO
                        c.RelativeColumn(2);     // SN
                        c.ConstantColumn(35);    // Head
                        c.RelativeColumn(2);     // Start
                        c.RelativeColumn(2);     // End
                        c.ConstantColumn(40);    // Result
                        c.RelativeColumn(2);     // Plan
                    });

                    // Header
                    tbl.Header(header =>
                    {
                        void HdrCell(string text) =>
                            header.Cell().Background(Colors.Blue.Medium).Padding(3)
                                .Text(text).FontColor(Colors.White).Bold().FontSize(8);
                        HdrCell("ID"); HdrCell("Work Order"); HdrCell("Serial Number"); HdrCell("Head");
                        HdrCell("Start Time"); HdrCell("End Time"); HdrCell("Result"); HdrCell("Plan");
                    });

                    // Rows
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        string result = row["result"]?.ToString() ?? "";
                        string bg = result == "PASS" ? Colors.Green.Lighten4 :
                                    result == "FAIL" ? Colors.Red.Lighten4   : Colors.White;

                        void DataCell(string? val) =>
                            tbl.Cell().Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                               .Padding(3).Text(val ?? "").FontSize(8);

                        DataCell(row["id"]?.ToString());
                        DataCell(row["work_order"]?.ToString());
                        DataCell(row["serial_number"]?.ToString());
                        DataCell(row["head"]?.ToString());
                        DataCell(row["start_time"]?.ToString());
                        DataCell(row["end_time"]?.ToString());
                        tbl.Cell().Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                           .Padding(3).Text(result).FontSize(8).Bold()
                           .FontColor(result == "PASS" ? Colors.Green.Darken2 :
                                      result == "FAIL" ? Colors.Red.Darken2 : Colors.Black);
                        DataCell(row["plan_name"]?.ToString());
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages();
                });
            });
        });

        var bytes = doc.GeneratePdf();
        return File(bytes, "application/pdf", $"datalog_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchRows(
        string? sn, string? wo, string? from, string? to, int limit)
    {
        DateTime? dtFrom = string.IsNullOrEmpty(from) ? null : DateTime.TryParse(from, out var f) ? f : null;
        DateTime? dtTo   = string.IsNullOrEmpty(to)   ? null : DateTime.TryParse(to,   out var t) ? t : null;
        var result = await _datalog.QueryAsync(sn, wo, dtFrom, dtTo, limit);
        return result as List<Dictionary<string, object?>> ?? [];
    }

    private static string Esc(object? val)
    {
        var s = val?.ToString() ?? "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
