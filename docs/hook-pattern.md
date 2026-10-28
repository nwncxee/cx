# The hook pattern

The whole mechanism in one page: install one hook, call the original through the
trampoline. This is the reference `FrameTapService` used to be (deleted in #158 --
the framework owns the frame present, so the example no longer installed).

`HookService.Install(symbol, handler)` resolves the symbol in `nwmain`, hands it to
funchook, and returns the **trampoline** -- a pointer you call to reach the original
function. Hooks are staged; `HookService.Commit()` installs all of them at once,
after every service's `Initialize` has run. For a symbol more than one service wants,
use `hooks.Chain(symbol, handler, priority)` instead -- funchook orphans stacked
single-hooks (#88).

```csharp
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Example;

public sealed unsafe class ExampleService : IExtenderService
{
    // GLRender::SwapBuffers(SDL_Window*) -- static (no `this`) + one arg (MSVC `CA` proved it).
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _original;
    private static bool _announced;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _original = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwapBuffers);
    }

    [UnmanagedCallersOnly]
    private static void OnSwapBuffers(IntPtr window)
    {
        if (!_announced)
        {
            _announced = true;
            _log?.Info("ExampleService: first frame presented -- hook is LIVE");
        }
        if (_original != null) _original(window); // let the game render
    }
}
```

## The rules the shape encodes

- **The handler's signature must match the target's ABI.** A C++ member function
  takes `this` as its first argument; a static one does not. Find the symbol and its
  mangled signature with `./sym.sh -m CNWSMessage::Handle...`, and verify the body in
  Ghidra before you commit to a shape -- never guess. By-value struct arguments are a
  known trap: the caller lowers them onto the stack rather than passing a pointer, so
  the handler must declare the lowered form, not the struct (`CResRef` is the one that
  bit us).
- **`[UnmanagedCallersOnly]` handlers must be static**, so everything they touch is
  static too -- the trampoline pointer, the log, any state. That is the one ergonomic
  tax of C#-in-a-native-callback; keep the static surface tiny.
- **Call the original, or don't, deliberately.** Calling it after your work is an
  "after" hook, before it an "before" hook, not at all is skip-original.
- **Hot hooks allocate nothing.** NativeAOT's GC runs on whatever thread the hook
  fired on. Frame, render and GL-stub hooks are hot; input and network hooks are cold.
