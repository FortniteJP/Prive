#pragma once

// Writes the first few access violations (and illegal-instruction faults) to our log: where the
// fault happened, what was touched, and every return address on the stack as module+offset.
// Observes only - the exception is always passed on to the game's own handling. Added after a
// crash (2026-09-11) whose WER report named no module and carried no stack.
namespace CrashLog {
    void Install();
}
