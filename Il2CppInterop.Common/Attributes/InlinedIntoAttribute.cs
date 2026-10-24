namespace Il2CppInterop.Common.Attributes;

// SPT addition, the game carries copies of this method inside other functions, and calls from those skip a patch on it
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
public class InlinedIntoAttribute : Attribute
{
    // The first few functions holding a copy, Count is all of them
    public readonly string[] Callers;

    public readonly int Count;

    // Calls the game still makes to the method itself
    public readonly int DirectCalls;

    // Set for a stub whose callers all inlined it, they call this method directly
    public readonly string? ForwardsTo;

    public InlinedIntoAttribute(string[] callers, int count, int directCalls, string? forwardsTo)
    {
        Callers = callers;
        Count = count;
        DirectCalls = directCalls;
        ForwardsTo = forwardsTo;
    }
}
