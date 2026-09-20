using System.Text.Json;

namespace AIHub.Services;

public sealed partial class LiteraryJellyStore
{
    public object ExportAudit()
    {
        using var db=Open(); using var tx=db.BeginTransaction();
        object[] Rows(string sql)
        {
            using var cmd=Command(db,tx,sql); using var reader=cmd.ExecuteReader(); var rows=new List<object>();
            while(reader.Read())
            {
                var row=new Dictionary<string,object?>();
                for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
                rows.Add(row);
            }
            return rows.ToArray();
        }
        var result=new { format="AIHub.Jelly.audit.v1", batches=Rows("SELECT * FROM batch ORDER BY rowid"),
            facts=Rows("SELECT * FROM fact ORDER BY rowid"), changes=Rows("SELECT * FROM change ORDER BY seq") };
        tx.Commit(); return result;
    }
    public void ConfirmEmpty(LiteraryJellyBatch batch)
    {
        if(batch.Facts.Length!=0) throw new InvalidOperationException("Only empty batches can be completed without fact approval.");
        Confirm(batch,[]);
        using var db=Open(); using var tx=db.BeginTransaction();
        LogChange(db,tx,batch.Id,"empty_batch_completed","",JsonSerializer.Serialize(new { batch.Id, batch.PartId })); tx.Commit();
    }
}
