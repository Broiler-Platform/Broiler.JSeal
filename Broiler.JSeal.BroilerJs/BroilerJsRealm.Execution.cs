using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

internal partial class BroilerJsRealm
{
    // Explicit state lets callers use cached static delegates instead of allocating closures
    // on every property read. Translation happens before the realm and its pump are restored.
    private TResult Execute<TState, TResult>(TState state, Func<TState, TResult> operation)
    {
        using var scope = Enter();
        try
        {
            return operation(state);
        }
        catch (JSException guestException)
        {
            throw Translate(guestException);
        }
    }

    private void Execute<TState>(TState state, Action<TState> operation)
    {
        using var scope = Enter();
        try
        {
            operation(state);
        }
        catch (JSException guestException)
        {
            throw Translate(guestException);
        }
    }
}
