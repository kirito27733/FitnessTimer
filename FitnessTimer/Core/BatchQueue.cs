using System.Collections.ObjectModel;

namespace FitnessTimer.Core;

public sealed class BatchQueue(ObservableCollection<BatchCard> cards)
{
    public ObservableCollection<BatchCard> Cards { get; }=cards;
    public Guid? CurrentId { get;private set; }
    private int fallbackNext;
    public BatchCard? Current => Cards.FirstOrDefault(c=>c.Id==CurrentId);
    public BatchCard? PeekNext()
    {
        var index=Current is { } current ? Cards.IndexOf(current)+1 : fallbackNext;
        return index>=0&&index<Cards.Count ? Cards[index] : null;
    }
    public void Load(BatchCard card)
    {
        if(!Cards.Contains(card))throw new ArgumentException("该卡片已不在名单中。");
        CurrentId=card.Id;fallbackNext=Cards.IndexOf(card)+1;Reindex();
    }
    public void Remove(BatchCard card)
    {
        var index=Cards.IndexOf(card);if(index<0)return;
        if(CurrentId==card.Id){CurrentId=null;fallbackNext=index;}
        else if(CurrentId==null&&index<fallbackNext)fallbackNext--;
        Cards.Remove(card);Reindex();
    }
    public void RemoveMany(IEnumerable<BatchCard> selected)
    {
        foreach(var card in selected.ToList())Remove(card);
    }
    public void Replace(IEnumerable<BatchCard> imported)
    {
        var snapshot=imported.ToList();Cards.Clear();foreach(var card in snapshot)Cards.Add(card);
        CurrentId=null;fallbackNext=0;Reindex();
    }
    public void Reindex()
    { for(var i=0;i<Cards.Count;i++){Cards[i].Order=i+1;Cards[i].IsCurrent=Cards[i].Id==CurrentId;} }
}
