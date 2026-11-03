namespace FitnessTimer.Core;

public sealed record RankedRaceResult(RaceResult Result,int BatchRank,int OverallRank);

public static class ResultRanking
{
    public static IReadOnlyList<RankedRaceResult> Calculate(IEnumerable<RaceResult> results)
    {
        var rows=results.Select((result,index)=>(Result:result,Index:index)).ToArray();
        var batchRanks=new int[rows.Length];var overallRanks=new int[rows.Length];
        // Each saved heat has a shared timestamp; repeated batch names remain separate heats.
        foreach(var group in rows.GroupBy(r=>(r.Result.Batch,r.Result.RecordedAt)))
            Assign(group.Select(r=>r.Index),batchRanks);
        Assign(rows.Select(r=>r.Index),overallRanks);
        return rows.Select(r=>new RankedRaceResult(r.Result,batchRanks[r.Index],overallRanks[r.Index])).ToArray();

        long Score(int index)=>(rows[index].Result.ElapsedTicks+(long)rows[index].Result.PenaltySeconds*TimeSpan.TicksPerSecond)/(TimeSpan.TicksPerMillisecond*10);
        void Assign(IEnumerable<int> indices,int[] ranks)
        {
            var position=0;var rank=0;long? previous=null;
            foreach(var index in indices.OrderBy(Score))
            {
                position++;var score=Score(index);
                if(previous!=score)rank=position;
                ranks[index]=rank;previous=score;
            }
        }
    }
}
