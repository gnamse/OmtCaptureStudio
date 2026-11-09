namespace OmtCaptureStudio.Tests;

partial class Program
{
    /// <summary>The suite convention: throw on the first failed assertion with the fact as the message.</summary>
    static void Expect(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }
}
