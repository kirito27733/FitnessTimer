using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FitnessTimer.Core;

public sealed record RaceResult(string Batch, DateTimeOffset RecordedAt, int Lane, string Participant,
    long ElapsedTicks, int FiveCount, int FifteenCount)
{
    public string DateDisplay => RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string BaseDisplay => LaneTimer.Format(TimeSpan.FromTicks(ElapsedTicks));
    public int PenaltySeconds => FiveCount * 5 + FifteenCount * 15;
    public string TotalDisplay => LaneTimer.Format(TimeSpan.FromTicks(ElapsedTicks) + TimeSpan.FromSeconds(PenaltySeconds));
}

public static class ResultStore
{
    public static List<RaceResult> Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<List<RaceResult>>(File.ReadAllText(path)) ?? throw new InvalidDataException("成绩文件内容为空。") : [];
    public static void Save(string path, IEnumerable<RaceResult> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, path, true);
    }
    public static void Export(string path, IEnumerable<RaceResult> results)
    {
        static string Cell(string value)
        {
            // Spreadsheet applications must treat participant input as text, including formula prefixes.
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("批次,记录时间,赛道,选手,本批次名次,总排名,基础用时,5秒罚时次数,15秒罚时次数,罚时秒数,总用时");
        foreach (var ranked in ResultRanking.Calculate(results))
        {
            var r=ranked.Result;
            writer.WriteLine(string.Join(",", new[] { r.Batch, r.DateDisplay, r.Lane.ToString(CultureInfo.InvariantCulture), r.Participant, ranked.BatchRank.ToString(CultureInfo.InvariantCulture), ranked.OverallRank.ToString(CultureInfo.InvariantCulture), r.BaseDisplay, r.FiveCount.ToString(), r.FifteenCount.ToString(), r.PenaltySeconds.ToString(), r.TotalDisplay }.Select(Cell)));
        }
    }
}
