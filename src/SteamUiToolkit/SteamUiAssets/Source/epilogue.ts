// The last fragment in the bundle, and the only thing in it.
//
// bridge.ts opens the IIFE and every other fragment is concatenated into it, so the value the
// injected script evaluates to has to be returned AFTER the last of them — a gate registers with a
// top-level call, and a return placed before those calls makes every one of them unreachable. That
// is not a hypothetical: it shipped, and it published a bridge whose registry was empty while the
// bootstrap patch still verified, so every gate reported "bridge unavailable" with nothing in the
// log naming why.
//
// Keeping the return here rather than in the builder's epilogue string keeps the result shape the
// bridge's own business; the builder only has to emit this file last and close the IIFE.
return installResult;
