namespace RustSharp.Syntax;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}
