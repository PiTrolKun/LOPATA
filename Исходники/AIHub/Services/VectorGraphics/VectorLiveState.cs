using System.Text;

namespace AIHub.Services.VectorGraphics;

public enum VectorStreamKind { Reset, Reasoning, Svg }
public sealed record VectorStreamChunk(VectorStreamKind Kind, string Text);
internal sealed record VectorLiveSnapshot(int Epoch, string Reasoning, int ReasoningLength, int SvgLength, string? Answer, string Stage);

/// <summary>Transient stream state, independent of visual-tree lifetime. The model thread never waits for a renderer.</summary>
internal sealed class VectorLiveState
{
    private readonly object _gate = new();
    private readonly StringBuilder _reasoning = new(), _answer = new();
    private int _epoch;
    private string _stage = "Generating";
    internal int Epoch { get { lock (_gate) return _epoch; } }
    internal void Append(VectorStreamChunk chunk)
    {
        lock (_gate)
        {
            if (chunk.Kind == VectorStreamKind.Reset) { _reasoning.Clear(); _answer.Clear(); _stage = "Generating"; _epoch++; }
            else if (chunk.Kind == VectorStreamKind.Reasoning) _reasoning.Append(chunk.Text);
            else _answer.Append(chunk.Text);
        }
    }
    internal void SetStage(string stage) { lock (_gate) _stage = stage; }
    internal VectorLiveSnapshot Read(int epoch, int reasoningOffset, int svgLength, bool includeAnswer)
    {
        lock (_gate)
        {
            if (epoch != _epoch) reasoningOffset = 0;
            reasoningOffset = Math.Clamp(reasoningOffset, 0, _reasoning.Length);
            var answer = includeAnswer && (epoch != _epoch || svgLength != _answer.Length) ? _answer.ToString() : null;
            return new(_epoch, _reasoning.ToString(reasoningOffset, _reasoning.Length - reasoningOffset),
                _reasoning.Length, _answer.Length, answer, _stage);
        }
    }
}
