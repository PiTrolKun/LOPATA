using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutTreeTests
{
    private static readonly LetItOutTreeNode Root=new("root","","Тема","","theme","reported",5,[]);
    [TestMethod]
    public void TreePublicationIsAtomicDisabledAndStaleUpdatesCannotOverwriteAndDeletionCascades()
    {
        var directory=Path.Combine(Path.GetTempPath(),"lopata-tree-store-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);
            store.SaveContent(id,[new("user","Дата 22 ноября"),new("assistant","Уточним")],"Черновик");
            var sources=store.MemoryPairs(id);var original=store.Load(id)!;Assert.IsFalse(store.Tree(id).Enabled);
            var document=new LetItOutTreeDocument("LOPATA","test","let_it_out.topic_tree","test",id,1,1,DateTimeOffset.UtcNow,"Тема",[Root]);
            store.SetTreeEnabled(id,false);Assert.IsFalse(store.PublishTree(document,0,sources));Assert.IsNull(store.Tree(id).Document);
            store.SetTreeEnabled(id,true);Assert.IsTrue(store.PublishTree(document,0,sources));
            Assert.IsFalse(store.PublishTree(document,0,sources));
            var reopened=new LetItOutConversationStore(store.DatabasePath);Assert.AreEqual(1,reopened.Tree(id).Document!.Revision);
            Assert.AreEqual(original.Draft,reopened.Load(id)!.Draft);CollectionAssert.AreEqual(original.Messages.ToArray(),reopened.Load(id)!.Messages.ToArray());
            store.Delete(id);Assert.IsNull(store.Tree(id).Document);Assert.AreEqual(0,store.MemoryPairs(id).Count);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [TestMethod]
    public void CorrectionsReplaceExistingNodeInsteadOfAppendingAnotherVersionAsASecondFact()
    {
        var old=new LetItOutTreeNode("date","root","Дата","22 ноября","event","planned",4,[1]);
        var corrected=old with{Detail="29 ноября; 22-е было предварительно",Sources=[1,2]};
        var result=LetItOutTreePolicy.Apply([Root,old],new("Тема",[corrected],[]),2);
        Assert.AreEqual(2,result.Length);Assert.AreEqual(corrected,result.Single(n=>n.Id=="date"));
    }
    [TestMethod]
    public void InvalidCycleOrFutureSourceIsRejectedBeforePublishing()
    {
        var child=new LetItOutTreeNode("a","root","Факт","","event","reported",3,[1]);
        Assert.ThrowsExactly<InvalidDataException>(()=>LetItOutTreePolicy.Apply([Root,child],new("Тема",[child with{Sources=[2]}],[]),1));
        Assert.ThrowsExactly<InvalidDataException>(()=>LetItOutTreePolicy.Apply([Root,child],new("Тема",[child with{Parent="a"}],[]),1));
        Assert.ThrowsExactly<InvalidDataException>(()=>LetItOutTreePolicy.Apply([Root,child],new("Тема",[],["root"]),1));
        Assert.ThrowsExactly<InvalidDataException>(()=>LetItOutTreePolicy.Apply([Root,child],new("Тема",[child with{Sources=[]}],[]),1));
    }
}
