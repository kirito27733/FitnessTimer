using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace FitnessTimer.Core;

public static class BatchExcel
{
    public static readonly string[] Headers=["顺序","批次名称","1道姓名","2道姓名","3道姓名","4道姓名"];
    public static void Export(string path,IEnumerable<BatchCard> batches)
    {
        var cards=batches.ToList();foreach(var card in cards)card.Validate();
        using var workbook=new XLWorkbook();var sheet=workbook.AddWorksheet("批次名单");
        for(var i=0;i<Headers.Length;i++)sheet.Cell(1,i+1).Value=Headers[i];
        for(var i=0;i<cards.Count;i++)
        {
            sheet.Cell(i+2,1).Value=i+1;sheet.Cell(i+2,2).Value=cards[i].Name;
            var names=cards[i].Names();for(var lane=0;lane<4;lane++)sheet.Cell(i+2,lane+3).Value=names[lane];
        }
        sheet.Row(1).Style.Fill.BackgroundColor=XLColor.FromHtml("#203852");sheet.Row(1).Style.Font.FontColor=XLColor.White;
        sheet.Row(1).Style.Font.Bold=true;sheet.Style.Font.FontName="Microsoft YaHei";sheet.Style.Font.FontSize=11;
        sheet.Column(1).Width=9;sheet.Column(2).Width=28;sheet.Columns(3,6).Width=20;
        sheet.Rows(1,Math.Max(cards.Count+1,2)).Height=25;sheet.SheetView.FreezeRows(1);
        var range=sheet.Range(1,1,Math.Max(cards.Count+1,1),6);
        range.Style.Border.OutsideBorder=XLBorderStyleValues.Thin;range.Style.Border.InsideBorder=XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor=XLColor.FromHtml("#D9D9D9");range.Style.Border.InsideBorderColor=XLColor.FromHtml("#D9D9D9");
        if(cards.Count>0)range.SetAutoFilter();
        // Values are stored as text, including names beginning with '='; no formulas are emitted.
        workbook.SaveAs(path);
    }
    public static List<BatchCard> Import(string path)
    {
        if(new FileInfo(path).Length>20*1024*1024)throw new InvalidDataException("名单文件不能超过 20 MB。");
        using var workbook=new XLWorkbook(path);
        var sheet=workbook.Worksheets.FirstOrDefault(s=>s.Name=="批次名单")??workbook.Worksheets.FirstOrDefault()??throw new InvalidDataException("工作簿中没有工作表。");
        for(var c=1;c<=6;c++)
            if(sheet.Cell(1,c).HasFormula||sheet.Cell(1,c).GetString().Trim()!=Headers[c-1])throw new InvalidDataException("表头不匹配。请先导出名单或模板，保留第一行的六列表头。");
        var last=sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber()??1;
        if(last>5001)throw new InvalidDataException("每次最多导入 5000 个批次。");
        var rows=new List<(int Order,BatchCard Card)>();var used=new HashSet<int>();
        for(var r=2;r<=last;r++)
        {
            var cells=Enumerable.Range(1,6).Select(c=>sheet.Cell(r,c)).ToArray();
            if(cells.All(c=>c.IsEmpty()))continue;
            if(cells.Any(c=>c.HasFormula))throw new InvalidDataException($"第 {r} 行含公式。名单只接受直接填写的文字和顺序数字。");
            if(!int.TryParse(cells[0].GetString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var order)||order<=0||!used.Add(order))
                throw new InvalidDataException($"第 {r} 行顺序必须是唯一的正整数。");
            var card=new BatchCard { Name=cells[1].GetString().Trim(),Lane1=cells[2].GetString().Trim(),Lane2=cells[3].GetString().Trim(),Lane3=cells[4].GetString().Trim(),Lane4=cells[5].GetString().Trim() };
            try{card.Validate();}catch(InvalidDataException ex){throw new InvalidDataException($"第 {r} 行：{ex.Message}");}
            rows.Add((order,card));
        }
        if(rows.Count==0)throw new InvalidDataException("名单中没有批次，请填写后再导入。");
        return rows.OrderBy(r=>r.Order).Select(r=>r.Card).ToList();
    }
}
