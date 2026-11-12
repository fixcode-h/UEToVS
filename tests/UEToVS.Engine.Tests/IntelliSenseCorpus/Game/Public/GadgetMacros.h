// IntelliSense corpus: macros for Quick Info expansion.
#pragma once

#define GADGET_SCALE 2
#define GADGET_MUL(X, Y) ((X) * (Y) * GADGET_SCALE)
#define GADGET_NAME(N) TEXT(#N)
#define GADGET_CONCAT(A, B) A##B
#define GADGET_SELF GADGET_SELF + 1

/** Logs a gadget message. */
#define GADGET_LOG(Fmt, ...) UE_LOG(LogTemp, Log, Fmt, ##__VA_ARGS__)
