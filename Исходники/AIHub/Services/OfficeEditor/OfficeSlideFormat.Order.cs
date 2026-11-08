using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private static void OrderPptxShape(XElement shape, bool front)
    {
        var peers = shape.Parent!.Elements().Where(e => e.Name.LocalName is
            "sp" or "pic" or "grpSp" or "graphicFrame" or "cxnSp" or "contentPart" or "AlternateContent");
        MoveToEdge(shape, front ? peers.Last() : peers.First(), front);
    }

    private static void OrderOdpShape(XElement shape, bool front)
    {
        while (shape.Parent is {} wrapper && wrapper.Name == Draw+"a" && wrapper.Elements().Count(e => e.Name.Namespace == Draw) == 1)
            shape = wrapper;
        var peers = shape.Parent!.Elements().Where(e => e.Name.Namespace == Draw).Select((e,index) =>
            (Shape:e, Z:int.TryParse((string?)e.Attribute(Draw+"z-index"), out var z) ? z : index))
            .OrderBy(p => p.Z).Select(p => p.Shape).ToList();
        peers.Remove(shape);
        if (peers.Count == 0) return;
        MoveToEdge(shape, front ? peers[^1] : peers[0], front);
        peers.Insert(front ? peers.Count : 0, shape);
        for (int i = 0; i < peers.Count; i++) peers[i].SetAttributeValue(Draw+"z-index", i);
    }

    private static void MoveToEdge(XElement item, XElement edge, bool front)
    {
        if (item == edge) return;
        item.Remove();
        if (front) edge.AddAfterSelf(item); else edge.AddBeforeSelf(item);
    }
}
