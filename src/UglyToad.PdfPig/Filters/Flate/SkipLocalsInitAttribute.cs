#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Tells the compiler not to emit the flag that makes the runtime zero every local, including
    /// stack-allocated buffers, on entry to a method. The runtime attribute exists from .NET 5 on;
    /// the compiler accepts this definition for the older targets.
    /// </summary>
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event, Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : Attribute
    {
    }
}
#endif
